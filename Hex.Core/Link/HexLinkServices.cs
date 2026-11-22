using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>A folder of the workspace, by the name the wire knows it by, and where it is here (never sent).</summary>
internal sealed record HexLinkFolder(string Name, string Path, HexLinkAuthority? Authority = null)
{
    public bool IncludeSystemFiles { get; init; }
    public string? FolderAuthorityId => Authority?.DeviceId;
    public long FolderAuthorityEpoch => Authority?.Epoch ?? 0;
    public HexLinkWorktreeGatePolicy? WorktreeGate { get; init; }
}

/// <summary>A repository of the workspace, its name, the branch it's on and the commit at its head, and where it is here (never sent).</summary>
internal sealed record HexLinkRepository(string Name, string Branch, string Head, string Path);

/// <summary>The workspace as a link describes it, its name, its folders and its repositories.</summary>
internal sealed record HexLinkWorkspace(string Name, IReadOnlyList<HexLinkFolder> Folders, IReadOnlyList<HexLinkRepository> Repositories)
{
    public static readonly HexLinkWorkspace Empty = new(string.Empty, [], []);
}

/// <summary>
/// What a fleet row carries beyond the hub's snapshot, which only the editor knows: whether the chat waits on the
/// person (an approval or a question) and the step of its plan it's doing.
/// </summary>
internal sealed record HexLinkChatExtra(bool Waiting, string? PlanStep);

/// <summary>
/// Where somebody is in a file, said by a device as its caret moves: the file as a wire path, its line and column (from
/// 0), and the other end of a selection when there is one. A null file is nowhere, the device closed it.
/// </summary>
internal sealed record HexLinkPresence(string? File, int Line, int Column, int? ToLine = null, int? ToColumn = null, int? PointerLine = null, int? PointerColumn = null, bool SharedDocument = false);

/// <summary>
/// Everything a link needs of the editor it's part of, which Hex.Dawn gives it. Called from the links' own threads,
/// never the editor's: what has to touch the editor returns a task and goes there itself (the queue the MCP calls
/// use), what's read off-thread (<see cref="Fleet"/>, <see cref="Workspace"/>) is kept current by the editor as an
/// immutable value it swaps in.
/// </summary>
internal interface IHexLinkServices
{
    /// <summary>This machine as other devices see it, its id the one its certificate proves.</summary>
    HexLinkDevice Device { get; }

    /// <summary>The workspace open now, swapped whole when it changes.</summary>
    HexLinkWorkspace Workspace { get; }

    HexLinkAuthorityStore? FolderAuthorities => null;
    IHexLinkPublicationService? Publication => null;
    HexLinkWorktreeGatePolicy? WorktreeGate(HexLinkFolder folder) => null;

    /// <summary>The workspace's current files.exclude patterns.</summary>
    HexExcludes Excludes => HexExcludes.Usual;

    /// <summary>Where the authenticated device can dial this editor after the connection closes.</summary>
    JsonObject RedialAdvertisement() => HexLinkRedial.Advertisement(0, []);

    /// <summary>Renames an unchanged file within its current directory.</summary>
    Task RenameCase(string from, string to, string expected, CancellationToken cancel) =>
        Task.Run(() => HexLinkTree.RenameCase(from, to, expected), cancel);

    /// <summary>Where working-tree stat and hash caches survive a reconnect.</summary>
    string? TreeCacheDirectory => null;

    Task<string?> CompareAndReplace(string full, string wire, string? baseHash, Stream? source, long length,
        string? expectedHash, CancellationToken cancel, bool? executable) =>
        HexLinkFiles.CompareAndReplace(full, wire, baseHash, source, length, expectedHash, cancel, executable);

    /// <summary>Applies a linked write or deletion only if the local file still matches its base hash.</summary>
    Task<string?> CompareAndReplace(string full, string wire, string? baseHash, byte[]? bytes, CancellationToken cancel, bool? executable = null) =>
        HexLinkFiles.CompareAndReplace(full, wire, baseHash, bytes, cancel, executable);

    Task<string?> CompareAndReplacePacked(string full, string wire, string? baseHash, byte[] bytes,
        CancellationToken cancel, bool? executable, HexLinkFiles.PackDirectories directories) =>
        CompareAndReplace(full, wire, baseHash, bytes, cancel, executable);

    /// <summary>Folders this device is already responsible for syncing with this peer.</summary>
    IReadOnlyList<string> ChosenSyncFolders(string deviceId) => [];

    /// <summary>Folders explicitly stopped for this peer.</summary>
    IReadOnlyList<HexLinkSyncPause> PausedSyncFolders(string deviceId) => [];
    void AuthorityChanged() { }

    /// <summary>Buffers explicitly shared by the host editor, across all its connections.</summary>
    HexLinkDocuments? Documents => null;

    // Cross-version self-test fixtures exercise the pre-release protocol.
    bool LegacyDocumentFixture => false;

    /// <summary>Checks the folder authority and opens an eligible live document across links.</summary>
    Task<HexLinkDocumentRoom> OpenSharedDocument(string path, string full, HexLinkDevice caller, bool automatic, CancellationToken cancel) =>
        Task.FromResult(Documents?.Rooms.Find(path) ?? throw HexLinkException.NotFound("No documents are shared."));

    HexLinkPeer? SharedDocumentPeer(string id) => null;

    bool OwnsSharedDocument(string wire) => true;

    /// <summary>The fleet as of the hub's last pump (HexAgentHub.Snapshot), read off the editor's thread as hex_agent_wait does.</summary>
    HexHubSnapshot Fleet { get; }

    /// <summary>What the fleet's rows carry besides, by chat id, refreshed by the editor when the hub pumps. Empty is fine.</summary>
    IReadOnlyDictionary<int, HexLinkChatExtra> FleetExtras { get; }

    /// <summary>
    /// Runs <paramref name="read"/> on the editor's thread with the chat of an id and hands back what it made, null when
    /// there's no such chat. The link writes the chat (HexAgentChatStore) and works out what changed inside it, where
    /// the chat can't change under it.
    /// </summary>
    Task<JsonObject?> Chat(int id, Func<HexAgentChat, JsonObject?> read);

    /// <summary>The tools, called with no calling chat. Only those HexLinkAllowed lets through are ever called.</summary>
    IMcpTools Tools { get; }

    /// <summary>A chat's question card answered as the person would, false when it has none waiting.</summary>
    Task<bool> Answer(int chat, string answer);

    /// <summary>
    /// A file of the workspace written as hex_write writes it: an open tab with unsaved changes is changed in the editor
    /// (undo kept), otherwise the disk. The link has checked the path and the conflict already. Writes the disk, a
    /// temp file beside it moved over it, unless the editor says otherwise.
    /// </summary>
    Task WriteFile(string fullPath, byte[] content) => Task.Run(() => HexLinkFiles.WriteAtomic(fullPath, content));

    /// <summary>
    /// A file of the workspace deleted (the working-tree sync's fs/delete), the path and the conflict checked already.
    /// The disk unless the editor says otherwise (a tab of it told it's gone).
    /// </summary>
    Task DeleteFile(string fullPath) => Task.Run(() => File.Delete(fullPath));

    /// <summary>Where a device is in a file now (null when it's said nowhere or it went), for the editor to show.</summary>
    void Presence(HexLinkDevice device, HexLinkPresence? presence);

    /// <summary>
    /// Changes offered by a device, a title and a diff, for the person to apply, keep as a file or throw away. Answers
    /// "applied", "kept" or "discarded" once they've chosen.
    /// </summary>
    Task<string> PatchOffered(HexLinkDevice device, string title, string diff);

    /// <summary>A line for the audit log: what was done or refused, by which device, and on what.</summary>
    void Audit(string what, HexLinkDevice who, string detail);

    /// <summary>
    /// Whether this machine would make predictions for a device that may ask (its share setting on and a model it can
    /// reach). Read off the editor's thread on every predict/start and hello, so it's cheap. Not, unless the editor says.
    /// </summary>
    bool ServesPredictions => false;

    /// <summary>
    /// A prediction made with this machine's model, its text as it comes. Throws a <see cref="HexLinkException"/> with
    /// <see cref="HexLinkErrors.PREDICTION_FAILED"/> (and data.status) when the model couldn't, from the call or the
    /// enumeration. Called off the editor's thread and must never wait on it: somebody is typing at the other end.
    /// </summary>
    IAsyncEnumerable<string> Predict(HexLinkPrediction request, CancellationToken cancel) => HexLinkPrediction.NotServed();
}
