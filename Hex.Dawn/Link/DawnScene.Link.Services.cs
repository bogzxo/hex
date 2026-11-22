using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    /// <summary>
    /// What a link needs of the editor (IHexLinkServices), over this scene. The links call it from their own threads:
    /// what's read there is a value the editor swaps in whole (the workspace, the fleet's extras, the hub's snapshot),
    /// and what has to touch the editor (a chat read, a question answered, a file written into an open tab) is posted to
    /// the editor's thread and handed back when it's run.
    /// </summary>
    private sealed class LinkServices(DawnScene hex) : IHexLinkServices
    {
        private volatile HexLinkWorkspace workspace = HexLinkWorkspace.Empty;
        private volatile IReadOnlyDictionary<int, HexLinkChatExtra> extras = new Dictionary<int, HexLinkChatExtra>();

        public HexLinkDevice Device => hex.linkIdentity?.Device ?? new HexLinkDevice(string.Empty, hex.LinkNameWanted);

        public HexLinkWorkspace Workspace
        {
            get => workspace;
            set => workspace = value;
        }

        public HexLinkDocuments Documents { get; } = new();

        public HexHubSnapshot Fleet => hex.hub.Snapshot;

        public IReadOnlyDictionary<int, HexLinkChatExtra> FleetExtras
        {
            get => extras;
            set => extras = value;
        }

        public Task<JsonObject?> Chat(int id, Func<HexAgentChat, JsonObject?> read) =>
            hex.OnEditorForLink(() => hex.ChatById(id) is { } chat ? read(chat) : null);

        // The tools as a client that isn't a chat's sees them; HexLinkAllowed keeps to the few a device may call
        public IMcpTools Tools => hex.McpTools;

        // As the question card's button does it
        public Task<bool> Answer(int chat, string answer) => hex.OnEditorForLink(() =>
            hex.ChatById(chat) is { } asked && asked.FirstPending(HexAgentEntryKind.Question) is { } question && asked.Reply(question, answer));

        // The disk always, whole or not at all, since the hash handed back says what's on it now (the working-tree sync
        // takes it as what both sides agreed), and an open tab of it made what it says as one step of its undo, changes
        // nobody saved a step back (DawnScene.LinkSync's LinkWriteLocal)
        public Task WriteFile(string fullPath, byte[] content) => hex.LinkWriteLocal(fullPath, content);

        public Task DeleteFile(string fullPath) => Task.Run(() => File.Delete(fullPath));

        public HexExcludes Excludes => HexExcludes.Of(hex.settings);

        public Task RenameCase(string from, string to, string expected, CancellationToken cancel) =>
            hex.LinkRenameCase(from, to, expected, cancel);

        public string? TreeCacheDirectory => hex.linkData is { } data ? Path.Combine(data, LINK_SYNC_STATES) : null;

        public Task<string?> CompareAndReplace(string full, string wire, string? baseHash, Stream? source,
            long length, string? expectedHash, CancellationToken cancel, bool? executable) =>
            hex.LinkCompareAndReplaceStream(full, wire, baseHash, source, length, expectedHash, cancel, executable);

        public Task<string?> CompareAndReplace(string full, string wire, string? baseHash, byte[]? bytes, CancellationToken cancel, bool? executable = null) =>
            hex.LinkCompareAndReplace(full, wire, baseHash, bytes, cancel, executable);

        public Task<string?> CompareAndReplacePacked(string full, string wire, string? baseHash, byte[] bytes,
            CancellationToken cancel, bool? executable, HexLinkFiles.PackDirectories directories) =>
            hex.LinkCompareAndReplace(full, wire, baseHash, bytes, cancel, executable, directories);

        public void Presence(HexLinkDevice device, HexLinkPresence? presence) => hex.PostLink(() =>
        {
            hex.linkPresence[device.Id] = new LinkPresenceSeen(device, presence, DateTime.UtcNow);
            hex.linkDirty = true;
        });

        // A card for the person, Apply, Keep as File or Discard, their answer going back (DawnScene.LinkGit)
        public Task<string> PatchOffered(HexLinkDevice device, string title, string diff) => hex.LinkPatchOffered(device, title, diff);

        public void Audit(string what, HexLinkDevice who, string detail)
        {
            hex.linkAudit?.Write(what, who.Id, detail);
            // A Steer request breathes the status item a while, so the person sees another machine is driving this one
            if (what == "steer")
                hex.LinkSteered();
        }

        // What the prediction side bound (DawnScene.LinkPredict), off the editor's thread
        public bool ServesPredictions => hex.LinkPredictServes();

        public IAsyncEnumerable<string> Predict(HexLinkPrediction request, CancellationToken cancel) => hex.LinkPredictMake(request, cancel);
    }

    internal async Task<string?> LinkCompareAndReplace(string full, string wire, string? baseHash, byte[]? bytes,
        CancellationToken cancel, bool? executable = null) =>
        LinkCompareAndReplace(full, wire, baseHash, bytes, cancel, executable, directories: null);

    private async Task<string?> LinkCompareAndReplace(string full, string wire, string? baseHash, byte[]? bytes,
        CancellationToken cancel, bool? executable, HexLinkFiles.PackDirectories? directories)
    {
        LinkTabStamp before = await LinkTabForCheckedWrite(full, wire, cancel);

        string? hash = await HexLinkFiles.CompareAndReplace(full, wire, baseHash, bytes, cancel, executable, directories).ConfigureAwait(false);
        if (bytes is not null && HexLinkFiles.Decode(bytes) is { } decoded &&
            await OnEditorForLink(() => LinkTakeIntoTab(full, decoded.Text, before)).ConfigureAwait(false))
        {
            throw new HexLinkException(HexLinkErrors.CONFLICT, $"{wire} was edited while the linked write was applying.",
                new JsonObject { ["hash"] = hash, ["editor_dirty"] = true });
        }
        return hash;
    }

    // Bytes as text when they're UTF-8 (a byte order mark let go of), null when they aren't, which goes to the disk as they are
    private static string? LinkText(byte[] content)
    {
        try
        {
            int skip = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content, skip, content.Length - skip);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
