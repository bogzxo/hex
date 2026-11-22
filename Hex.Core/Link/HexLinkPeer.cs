using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// An agent of the fleet on another machine as a row of what this one shows of it: its id there, what it's called,
/// its agent and model, the chat that started it and how deep, its state in a word, the end of its last reply, how
/// long its turn took and what it cost, its version (which moves when it does), whether it waits on the person there
/// and the step of its plan it's doing.
/// </summary>
internal sealed record HexLinkRow(int Id, string Name, string Agent, string? Model, int? Parent, int Depth, string State, string Reply,
    double Seconds, double Cost, int Version, bool Waiting, string? PlanStep)
{
    public IReadOnlyList<HexAgentApprovalStatus> Approvals { get; init; } = [];
    public string? StarterReceipt { get; init; }
}

/// <summary>
/// The other machine as this one sees it, what the UI and this machine's tools use: who it is, what it lets this one do
/// (<see cref="Granted"/>, kept current as it changes), its workspace, its fleet mirrored as rows, its chats mirrored as
/// <see cref="HexAgentChat"/>s, its files, its tools, where its person is, and its repositories by bundle.
/// <para>
/// Everything it hears is the other machine's data, read field by field with limits, never trusted: a field of the
/// wrong kind is passed over, a count beyond reason refuses the message, and nothing it says throws out of the reader.
/// What arrives off the editor's thread for a chat waits in a queue <see cref="Pump"/> drains on it.
/// </para>
/// </summary>
internal sealed partial class HexLinkPeer : IDisposable
{
    // The most rows of a fleet taken, and the most of a reply, a name or a step
    private const int MOST_FLEET = 2000, MOST_REPLY = 4000, MOST_NAME = 200;

    private readonly HexLinkSession session;
    private readonly HexLinkDevice me;

    public HexLinkPeer(HexLinkSession session, HexLinkDevice me)
    {
        this.session = session;
        this.me = me;
        Device = session.Connection.Device;
        presenceSend = new HexLinkCoalesce(TimeSpan.FromMilliseconds(500), SendPresence);
    }

    /// <summary>The session it asks over.</summary>
    public HexLinkSession Session => session;

    /// <summary>The device at the other end, its id the proved one, its name and the rest as it said in link/hello.</summary>
    public HexLinkDevice Device { get; private set; }

    /// <summary>What the other machine lets this one do there.</summary>
    public HexLinkRights Granted => granted;
    private volatile HexLinkRights granted;

    /// <summary>The other machine's workspace, its folders and repositories by name (their paths are empty, they're not this machine's).</summary>
    public HexLinkWorkspace Workspace { get; private set; } = HexLinkWorkspace.Empty;

    internal IReadOnlyList<string> ChosenSyncFolders { get; private set; } = [];
    internal IReadOnlyList<HexLinkSyncPause> PausedSyncFolders { get; private set; } = [];
    internal Action? SyncPausesChanged { get; set; }

    /// <summary>The protocol the other end speaks.</summary>
    public int Protocol { get; private set; }

    /// <summary>The fleet there, as of the last fleet/get or fleet/changed, by id order. Swapped whole, read from any thread.</summary>
    public IReadOnlyList<HexLinkRow> Fleet { get; private set; } = [];

    /// <summary>The version of the hub there the fleet is of.</summary>
    public long FleetVersion { get; private set; }

    /// <summary>Where the other machine's person is, null for nowhere or not said.</summary>
    public HexLinkPresence? Presence { get; private set; }

    /// <summary>
    /// Called when anything shown of the other machine moved (its fleet, what it grants, where its person is, a mirrored
    /// chat patched by <see cref="Pump"/>), on whatever thread it moved on. Whoever draws it marks itself dirty.
    /// </summary>
    public Action? Changed { get; set; }

    internal event Action? RightsChanged;
    internal event Action? ViewRestored;
    internal event Action? AddressesMoved;
    private int viewRevision;

    public IReadOnlyList<HexLinkEndpoint> RedialAddresses { get; private set; } = [];

    private void TakeAddresses(JsonObject? said)
    {
        if (said is null)
            return;
        RedialAddresses = HexLinkRedial.AdvertisedBy(said, session.Connection.Address);
        AddressesMoved?.Invoke();
    }

    /// <summary>The link's gone.</summary>
    public bool IsClosed => session.IsClosed;

    /// <summary>
    /// Says hello: this machine's device and protocol, answered with the other's, what it grants and its workspace. The
    /// first thing asked on a link.
    /// </summary>
    public async Task Hello(CancellationToken cancel = default)
    {
        JsonObject said = await AskObject("link/hello", new JsonObject { ["protocol"] = HexLinkHost.PROTOCOL, ["device"] = HexLinkData.DeviceJson(me) }, cancel);
        Protocol = HexLinkData.Int(said, "protocol") ?? 0;
        if (Protocol < HexLinkHost.PROTOCOL)
            throw new HexLinkException(HexLinkErrors.INVALID_REQUEST, $"The other Hex speaks link protocol {Protocol}, this one {HexLinkHost.PROTOCOL}.");
        Device = HexLinkData.DeviceOf(HexLinkData.Object(said, "device"), session.Connection.Device);
        // Its name is its own data, shown everywhere, so cleaned first (HexLinkNames)
        Device = Device with { Name = HexLinkNames.Clean(Device.Name) ?? HexLinkNames.Clean(session.Connection.Device.Name) ?? Device.Id[..Math.Min(8, Device.Id.Length)] };
        granted = HexLinkData.RightsOf(said, "granted");
        Workspace = WorkspaceOf(HexLinkData.Object(said, "workspace"));
        TakeAddresses(HexLinkData.Object(said, "redial"));
        TakeSyncChoices(said);
        TakeServes(said);
        Changed?.Invoke();
    }

    private static HexLinkWorkspace WorkspaceOf(JsonObject? said)
    {
        if (said is null)
            return HexLinkWorkspace.Empty;
        return new HexLinkWorkspace(HexLinkData.String(said, "name", MOST_NAME) ?? string.Empty,
            [.. HexLinkData.Objects(said, "folders", 64).Where(folder => HexLinkData.String(folder, "name", MOST_NAME) is { Length: > 0 })
                  .Select(folder => new HexLinkFolder(HexLinkData.String(folder, "name", MOST_NAME)!, string.Empty,
                      HexLinkAuthority.Of(HexLinkData.Object(folder, "authority"))))],
            [.. HexLinkData.Objects(said, "repositories", 64).Where(each => HexLinkData.String(each, "name", MOST_NAME) is { Length: > 0 })
                .Select(each => new HexLinkRepository(HexLinkData.String(each, "name", MOST_NAME)!, HexLinkData.String(each, "branch", MOST_NAME) ?? string.Empty,
                    HexLinkData.String(each, "head", 64) ?? string.Empty, string.Empty))]);
    }

    internal async ValueTask ToldAsync(string method, JsonObject parameters)
    {
        if (!await PredictTold(method, parameters))
            Told(method, parameters);
    }

    /// <summary>
    /// A notification from the other end that's this half's to take: the fleet, a chat, a file, a bundle's chunks, what
    /// it grants. The session calls it on its dispatch task.
    /// </summary>
    public void Told(string method, JsonObject parameters)
    {
        try
        {
            switch (method)
            {
                case "fleet/changed":
                    if (HexLinkAllowed.Has(Granted, HexLinkRights.View))
                        TakeFleet(parameters);
                    break;
                case "chat/changed":
                    if (HexLinkAllowed.Has(Granted, HexLinkRights.View))
                        ChatChanged(parameters);
                    break;
                case "fs/changed":
                    if (HexLinkAllowed.Has(Granted, HexLinkRights.View) && HexLinkData.String(parameters, "path", HexLinkFiles.MOST_PATH) is { } path)
                    {
                        if (fileWatches.TryGetValue(path, out string? was))
                            fileWatches.TryUpdate(path, HexLinkData.String(parameters, "hash", 64), was);
                        FileChanged?.Invoke(path, HexLinkData.String(parameters, "hash", 64));
                    }
                    break;
                case "git/chunk":
                    BundleChunk(parameters);
                    break;
                case "git/done":
                    BundleDone(parameters);
                    break;
                case "link/name":
                    TakeName(parameters);
                    break;
                case "workspace/changed":
                    PokeWorkspaceRefresh();
                    break;
                case "link/addresses":
                    TakeAddresses(parameters);
                    break;
                case "link/rights":
                    TakeRights(HexLinkData.RightsOf(parameters, "granted"));
                    break;
                case "sync/status":
                    TakeSyncReport(parameters);
                    break;
                default:
                    if (HexLinkAllowed.Has(Granted, HexLinkRights.View))
                        TreeTold(method, parameters);
                    break;
            }
        }
        catch (Exception)
        {
            // Whatever it was, it was the other machine's to get right, the link goes on
        }
    }

    private void TakeRights(HexLinkRights rights)
    {
        bool couldView = HexLinkAllowed.Has(Granted, HexLinkRights.View);
        granted = rights;
        bool canView = HexLinkAllowed.Has(rights, HexLinkRights.View);
        if (canView != couldView)
        {
            int revision = Interlocked.Increment(ref viewRevision);
            if (canView)
                _ = Task.Run(() => RestoreView(revision));
            else
            {
                Fleet = [];
                FleetVersion = 0;
                Tools = [];
                Workspace = HexLinkWorkspace.Empty;
                ChosenSyncFolders = [];
                PausedSyncFolders = [];
                remoteSyncReport = [];
            }
        }
        RightsChanged?.Invoke();
        Changed?.Invoke();
    }

    private async Task RestoreView(int revision)
    {
        bool StillAllowed() => revision == Volatile.Read(ref viewRevision) && HexLinkAllowed.Has(Granted, HexLinkRights.View);
        async Task Attempt(Func<Task> watch)
        {
            if (!StillAllowed())
                return;
            try { await watch().ConfigureAwait(false); }
            catch (Exception e) when (e is HexLinkException or IOException or OperationCanceledException or TimeoutException) { }
        }

        await Attempt(async () =>
        {
            await RefreshWorkspace(session.Closing).ConfigureAwait(false);
            if (StillAllowed())
                ViewRestored?.Invoke();
        });
        await Task.WhenAll(
            Attempt(async () =>
            {
                JsonObject fleet = await AskObject("fleet/get", null, session.Closing).ConfigureAwait(false);
                if (StillAllowed())
                    TakeFleet(fleet);
            }),
            Attempt(() => ListTools(session.Closing)),
            Attempt(() => RestoreChatWatches(Attempt, StillAllowed)),
            Attempt(() => RestoreFileWatches(Attempt, StillAllowed)),
            Attempt(() => RestoreTreeWatches(Attempt, StillAllowed))).ConfigureAwait(false);
    }

    private readonly ConcurrentDictionary<string, Dictionary<string, HexLinkTreeChange>> treeWatchFiles = new(StringComparer.Ordinal);

    private void RememberTree(string folder, HexLinkManifest manifest)
    {
        var files = new Dictionary<string, HexLinkTreeChange>(manifest.Files.Count, StringComparer.Ordinal);
        foreach (HexLinkTreeFile file in manifest.Files)
            files.TryAdd(file.Path, TreeChange(file));
        treeWatchFiles[folder] = files;
    }

    private static HexLinkTreeChange TreeChange(HexLinkTreeFile file) =>
        new(file.Path, file.Hash, Text: file.Text, Eol: file.Eol, Unavailable: file.Unavailable);

    private void RememberTreeChanges(string folder, IReadOnlyList<HexLinkTreeChange> changes)
    {
        if (!treeWatchFiles.TryGetValue(folder, out var files))
            return;
        lock (files)
        {
            foreach (HexLinkTreeChange change in changes)
            {
                if (change.Deleted)
                    files.Remove(change.Path);
                else
                    files[change.Path] = change;
            }
        }
    }

    private async Task RestoreTreeWatches(Func<Func<Task>, Task> attempt, Func<bool> stillAllowed)
    {
        foreach (var (folder, callback) in trees)
        {
            await attempt(async () =>
            {
                Dictionary<string, HexLinkTreeChange> before = new(StringComparer.Ordinal);
                if (treeWatchFiles.TryGetValue(folder, out var files))
                {
                    lock (files)
                        before = new(files, StringComparer.Ordinal);
                }
                HexLinkManifest? manifest = await OpenTreeWatch(folder, callback, session.Closing, basis: null, restoring: true);
                if (manifest is null || !stillAllowed() || trees.GetValueOrDefault(folder) != callback)
                    return;

                var changed = new List<HexLinkTreeChange>();
                foreach (HexLinkTreeFile file in manifest.Files)
                {
                    HexLinkTreeChange current = TreeChange(file);
                    if (!before.Remove(file.Path, out HexLinkTreeChange? previous) || previous != current)
                        changed.Add(current);
                }
                foreach (string missing in before.Keys)
                {
                    if (!stillAllowed())
                        return;
                    try { await Read(missing, session.Closing); }
                    catch (HexLinkException e) when (e.Code == HexLinkErrors.NOT_FOUND)
                    {
                        changed.Add(new HexLinkTreeChange(missing, null, Deleted: true));
                    }
                    catch (HexLinkException) { }
                }
                if (stillAllowed() && changed.Count > 0)
                    callback(changed);
            });
        }
    }

    /// <summary>Where the other machine's person is, as its host half heard it (presence/set).</summary>
    public void Heard(HexLinkPresence? presence)
    {
        Presence = presence;
        Changed?.Invoke();
    }

    /// <summary>The fleet asked for, and kept current from now on by fleet/changed.</summary>
    public async Task<IReadOnlyList<HexLinkRow>> WatchFleet(CancellationToken cancel = default)
    {
        TakeFleet(await AskObject("fleet/get", null, cancel));
        return Fleet;
    }

    private void TakeFleet(JsonObject said)
    {
        if (said["rows"] is JsonArray all && all.Count > MOST_FLEET)
            return;
        var rows = new List<HexLinkRow>();
        foreach (JsonObject row in HexLinkData.Objects(said, "rows", MOST_FLEET))
        {
            if (HexLinkData.Int(row, "id") is not { } id)
                continue;
            rows.Add(new HexLinkRow(id, HexLinkData.String(row, "name", MOST_NAME) ?? string.Empty, HexLinkData.String(row, "agent", MOST_NAME) ?? string.Empty,
                HexLinkData.String(row, "model", MOST_NAME), HexLinkData.Int(row, "parent"), Math.Clamp(HexLinkData.Int(row, "depth") ?? 0, 0, 64),
                HexLinkData.String(row, "state", MOST_NAME) ?? string.Empty, HexLinkData.String(row, "reply", MOST_REPLY) ?? string.Empty,
                Math.Max(0, HexLinkData.Double(row, "seconds") ?? 0), Math.Max(0, HexLinkData.Double(row, "cost") ?? 0), HexLinkData.Int(row, "version") ?? 0,
                HexLinkData.Bool(row, "waiting") == true, HexLinkData.String(row, "plan_step", 1000))
            {
                StarterReceipt = HexLinkData.String(row, "starter_receipt", 32),
                Approvals = [.. HexLinkData.Objects(row, "approvals", 16)
                    .Where(each => HexLinkData.Int(each, "request") is > 0 && HexLinkData.String(each, "title", 300) is { Length: > 0 })
                    .Select(each => new HexAgentApprovalStatus(HexLinkData.Int(each, "request")!.Value,
                        HexLinkData.String(each, "title", 300)!, HexLinkData.String(each, "detail", 600) ?? string.Empty))]
            });
        }

        Fleet = rows;
        FleetVersion = HexLinkData.Long(said, "version") ?? FleetVersion;
        Changed?.Invoke();
    }

    /// <summary>The tools the other machine lets this one call, as of the last <see cref="ListTools"/>.</summary>
    public IReadOnlyList<McpTool> Tools { get; private set; } = [];

    /// <summary>The tools the other machine lets this one call, asked for again.</summary>
    public async Task<IReadOnlyList<McpTool>> ListTools(CancellationToken cancel = default)
    {
        JsonObject said = await AskObject("tools/list", null, cancel);
        Tools = [.. HexLinkData.Objects(said, "tools", 200).Where(tool => HexLinkData.String(tool, "name", 100) is { Length: > 0 }).Select(tool =>
            new McpTool(HexLinkData.String(tool, "name", 100)!, HexLinkData.String(tool, "title", MOST_NAME) ?? string.Empty,
                HexLinkData.String(tool, "description", 4000) ?? string.Empty, (JsonObject)(HexLinkData.Object(tool, "inputSchema") ?? []).DeepClone(),
                HexLinkData.Bool(HexLinkData.Object(tool, "annotations"), "readOnlyHint") == true))];
        return Tools;
    }

    // The most content items and characters of a tool's answer taken
    private const int MOST_CONTENT = 64, MOST_CONTENT_TEXT = 4 * 1024 * 1024;

    /// <summary>
    /// One of the other machine's tools called, its answer as it gave it (text and pictures). A refusal (a right this
    /// machine wasn't granted, a tool never let over a link) is thrown as the <see cref="HexLinkException"/> it was.
    /// </summary>
    public async Task<McpToolResult> Call(string tool, JsonObject arguments, CancellationToken cancel = default, TimeSpan? timeout = null,
        int? starter = null, string? receipt = null)
    {
        var request = new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() };
        if (starter is > 0)
            request["starter_chat"] = starter;
        if (receipt is not null)
            request["starter_receipt"] = receipt;
        JsonObject said = await AskObject("tools/call", request, cancel, timeout);
        var result = new McpToolResult { IsError = HexLinkData.Bool(said, "isError") == true };
        foreach (JsonObject item in HexLinkData.Objects(said, "content", MOST_CONTENT))
        {
            switch (HexLinkData.String(item, "type", 20))
            {
                case "text":
                    result.AddText(HexLinkData.String(item, "text", MOST_CONTENT_TEXT) ?? string.Empty);
                    break;
                case "image" when HexLinkData.String(item, "data", MOST_CONTENT_TEXT) is { } data:
                    try
                    {
                        result.AddImage(Convert.FromBase64String(data), HexLinkData.String(item, "mimeType", 60) ?? "image/png");
                    }
                    catch (FormatException)
                    {
                    }

                    break;
            }
        }

        return result;
    }

    /// <summary>Something said to an agent there (hex_agent_send).</summary>
    public Task<McpToolResult> Send(int chat, string text, CancellationToken cancel = default) =>
        Call("hex_agent_send", new JsonObject { ["id"] = chat, ["text"] = text }, cancel);

    /// <summary>An agent there stopped (hex_agent_stop).</summary>
    public Task<McpToolResult> Stop(int chat, CancellationToken cancel = default) =>
        Call("hex_agent_stop", new JsonObject { ["id"] = chat }, cancel);

    /// <summary>An agent there restarted (hex_agent_restart), told <paramref name="text"/> as it starts again when said.</summary>
    public Task<McpToolResult> Restart(int chat, string? text = null, CancellationToken cancel = default)
    {
        var arguments = new JsonObject { ["id"] = chat };
        if (text is not null)
            arguments["text"] = text;
        return Call("hex_agent_restart", arguments, cancel);
    }

    /// <summary>An approval an agent there asked answered (hex_agent_approve), the oldest waiting unless <paramref name="request"/> says which.</summary>
    public Task<McpToolResult> Approve(int chat, int? request, bool allow, CancellationToken cancel = default)
    {
        var arguments = new JsonObject { ["id"] = chat, ["allow"] = allow };
        if (request is { } number and > 0)
            arguments["request"] = number;
        return Call("hex_agent_approve", arguments, cancel);
    }

    /// <summary>A question an agent there asked answered as the person there would (chat/answer), false when none was waiting.</summary>
    public async Task<bool> Answer(int chat, string answer, CancellationToken cancel = default) =>
        HexLinkData.Bool(await AskObject("chat/answer", new JsonObject { ["id"] = chat, ["answer"] = answer }, cancel), "answered") == true;

    /// <summary>Changes offered to the other machine, a title and a diff; its person's answer, applied, kept or discarded.</summary>
    public async Task<string> OfferPatch(string title, string diff, CancellationToken cancel = default) =>
        HexLinkData.String(await AskObject("patch/offer", new JsonObject { ["title"] = title, ["diff"] = diff }, cancel, TimeSpan.FromHours(1)), "answer", 20) switch
        {
            "applied" => "applied",
            "kept" => "kept",
            _ => "discarded"
        };

    // Where this machine's person is, the latest of it, sent at most twice a second
    private readonly HexLinkCoalesce presenceSend;
    private HexLinkPresence? presenceNow;
    private string presenceSent = string.Empty;

    /// <summary>Where this machine's person is now, told to the other machine at most twice a second (the latest of it).</summary>
    public void SetPresence(HexLinkPresence? presence)
    {
        presenceNow = presence;
        presenceSend.Poke();
    }

    private async Task SendPresence()
    {
        JsonObject said = HexLinkHost.PresenceJson(presenceNow);
        string key = said.ToJsonString();
        if (key != presenceSent)
        {
            presenceSent = key;
            await session.TellAsync("presence/set", said);
        }
    }

    // An answer that has to be an object, anything else the other end's mistake
    private async Task<JsonObject> AskObject(string method, JsonObject? parameters, CancellationToken cancel, TimeSpan? timeout = null) =>
        await AskWithRelay(method, parameters, cancel, timeout) as JsonObject
            ?? throw new HexLinkException(HexLinkErrors.INVALID_REQUEST, $"The other Hex answered {method} with something that isn't an object.");

    private Task<JsonNode?> AskWithRelay(string method, JsonObject? parameters, CancellationToken cancel, TimeSpan? timeout = null)
    {
        if (HexLinkAllowed.RelayOrigin.Value is { } origin)
        {
            parameters ??= [];
            parameters["relay"] = new JsonObject { ["origin"] = origin.Id, ["name"] = origin.Name, ["hops"] = 1 };
        }
        return session.Ask(method, parameters, cancel, timeout);
    }

    public void Dispose()
    {
        workspaceRefresh?.Dispose();
        syncReports?.Dispose();
        presenceSend.Dispose();
        foreach (var (_, receiving) in bundles)
            receiving.Fail("the link closed");
        lock (mirrors)
            mirrors.Clear();
    }
}
