using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// One link's answering half: what the device at the other end asks of this machine, answered from the editor's
/// services. Every request is checked against what the device may do here, read from the connection afresh each time,
/// so a right taken away in Link's window holds from its next request. Steer and Edit requests, and refusals, go to
/// the audit log. The asking half is <see cref="HexLinkPeer"/>; <see cref="HexLink"/> puts the two over one session.
/// </summary>
internal sealed partial class HexLinkHost : IDisposable
{
    /// <summary>The protocol's version, said in link/hello.</summary>
    public const int PROTOCOL = 1;

    // The most fleet/changed a second, and the same for each watched chat
    private static readonly TimeSpan FLEET_GAP = TimeSpan.FromMilliseconds(200), CHAT_GAP = TimeSpan.FromMilliseconds(200);

    private readonly HexLinkSession session;
    private readonly IHexLinkServices services;
    private readonly HexLinkCoalesce fleet;
    private volatile bool fleetAsked;

    public HexLinkHost(HexLinkSession session, IHexLinkServices services)
    {
        this.session = session;
        this.services = services;
        fleet = new HexLinkCoalesce(FLEET_GAP, SendFleet);
        InstallFilePacks();
    }

    /// <summary>Who's asking, as the connection proved it.</summary>
    public HexLinkDevice Device => session.Connection.Device;

    /// <summary>What the device may do here now.</summary>
    public HexLinkRights Rights => session.Connection.Rights;

    /// <summary>Where the device said it is, as it said it (presence/set), for <see cref="HexLink"/> to pass on.</summary>
    public Action<HexLinkHost, HexLinkPresence?>? PresenceSaid { get; set; }

    /// <summary>Whether a connected device supplies its own presence and therefore ignores relays about it.</summary>
    public Func<string, bool>? DirectlyLinked { get; set; }

    /// <summary>A request from the other end answered, or refused with why. What the session calls.</summary>
    public Task<JsonNode?> Handle(string method, JsonObject parameters, CancellationToken cancel) => method switch
    {
        "link/hello" => Task.FromResult<JsonNode?>(Hello()),
        "fleet/get" => Task.FromResult<JsonNode?>(FleetGet()),
        "chat/get" => ChatGet(parameters),
        "chat/watch" => ChatWatch(parameters),
        "chat/unwatch" => Task.FromResult<JsonNode?>(ChatUnwatch(parameters)),
        "chat/answer" => ChatAnswer(parameters),
        "tools/list" => Task.FromResult<JsonNode?>(ToolsList()),
        "tools/call" => ToolsCall(parameters, cancel),
        "doc/join" or "doc/poll" or "doc/edit" => Task.FromResult<JsonNode?>(DocumentRequest(method, parameters)),
        "fs/list" => Task.FromResult<JsonNode?>(FsList(parameters)),
        "fs/read" => Task.FromResult<JsonNode?>(FsRead(parameters)),
        "fs/write" => FsWrite(parameters, cancel),
        "fs/watch" => Task.FromResult<JsonNode?>(FsWatch(parameters)),
        "fs/unwatch" => Task.FromResult<JsonNode?>(FsUnwatch(parameters)),
        "fs/delete" => FsDeleteChecked(parameters, cancel),
        "sync/pack/read" => ReadFilePack(parameters, cancel),
        "sync/manifest" => SyncManifest(parameters, cancel),
        "sync/watch" => SyncWatch(parameters, cancel),
        "sync/unwatch" => Task.FromResult<JsonNode?>(SyncUnwatch(parameters)),
        "sync/flush" => SyncFlush(parameters),
        "git/bundle" => GitBundle(parameters, cancel),
        "patch/offer" => PatchOffer(parameters),
        "predict/start" => PredictStart(parameters),
        _ => throw new HexLinkException(HexLinkErrors.METHOD_NOT_FOUND, $"A link has no {(method.Length > 60 ? method[..60] : method)}.")
    };

    /// <summary>A notification from the other end taken, true when it was this half's (presence), false to pass it to the peer.</summary>
    public bool Told(string method, JsonObject parameters)
    {
        if (method == "predict/cancel")
            return PredictCancel(parameters);
        if (method == "git/cancel")
        {
            GitBundleCancel(parameters);
            return true;
        }
        if (method != "presence/set")
            return false;

        // A device that may not see here doesn't get to say where it is either
        if (!HexLinkAllowed.Has(Rights, HexLinkRights.View))
            return true;

        HexLinkPresence? presence = PresenceOf(parameters);
        if (HexLinkData.Object(parameters, "device") is { } passedOn)
        {
            // Another device's, passed on by the one at the other end: shown as said, never passed on again
            string id = HexLinkData.String(passedOn, "id", 80) ?? string.Empty;
            if (id.Length == 0 || id == services.Device.Id || id == Device.Id || DirectlyLinked?.Invoke(id) == true)
                return true;
            services.Presence(new HexLinkDevice(id, HexLinkData.String(passedOn, "name", 80) ?? id), presence);
            return true;
        }

        services.Presence(Device, presence);
        PresenceSaid?.Invoke(this, presence);
        return true;
    }

    partial void GitBundleCancel(JsonObject parameters);

    /// <summary>A presence read off the wire, null for nowhere.</summary>
    public static HexLinkPresence? PresenceOf(JsonObject said)
    {
        if (HexLinkData.String(said, "file", HexLinkFiles.MOST_PATH) is not { Length: > 0 } file)
            return null;
        JsonObject? pointer = HexLinkData.Object(said, "pointer");
        int? pointerLine = HexLinkData.Int(pointer, "line"), pointerColumn = HexLinkData.Int(pointer, "column");
        if (pointerLine is not >= 0 || pointerColumn is not >= 0)
            pointerLine = pointerColumn = null;
        JsonObject? selection = HexLinkData.Object(said, "selection");
        return new HexLinkPresence(file, Math.Max(0, HexLinkData.Int(said, "line") ?? 0), Math.Max(0, HexLinkData.Int(said, "column") ?? 0),
            HexLinkData.Int(selection, "line") is { } toLine && toLine >= 0 ? toLine : null,
            HexLinkData.Int(selection, "column") is { } toColumn && toColumn >= 0 ? toColumn : null,
            pointerLine, pointerColumn);
    }

    /// <summary>A presence as the wire says it.</summary>
    public static JsonObject PresenceJson(HexLinkPresence? presence)
    {
        var said = new JsonObject { ["file"] = presence?.File };
        if (presence is null)
            return said;
        said["line"] = presence.Line;
        said["column"] = presence.Column;
        if (presence.ToLine is { } toLine && presence.ToColumn is { } toColumn)
            said["selection"] = new JsonObject { ["line"] = toLine, ["column"] = toColumn };
        if (presence.PointerLine is { } line && presence.PointerColumn is { } column)
            said["pointer"] = new JsonObject { ["line"] = line, ["column"] = column };
        return said;
    }

    /// <summary>
    /// Another device's presence passed on to the one at this link's end, when it may see here. Said with whose it is,
    /// which tells the other end it's passed on and not to pass it on again.
    /// </summary>
    public void PassOn(HexLinkDevice from, HexLinkPresence? presence)
    {
        if (!HexLinkAllowed.Has(Rights, HexLinkRights.View) || from.Id == Device.Id)
            return;
        JsonObject said = PresenceJson(presence);
        said["device"] = new JsonObject { ["id"] = from.Id, ["name"] = from.Name };
        session.Tell("presence/set", said);
    }

    /// <summary>What the device may do here changed (Link's window), told to it at once.</summary>
    public void RightsMoved()
    {
        session.Tell("link/rights", new JsonObject { ["granted"] = HexLinkData.Words(Rights) });
        if (!HexLinkAllowed.Has(Rights, HexLinkRights.View))
            StopWatching();
        ServesMoved();
    }

    // What's asked of a device's right for a method, refused (and logged) when it hasn't it
    private void Need(HexLinkRights right, string what)
    {
        if (HexLinkAllowed.Has(Rights, right))
            return;
        string needs = HexLinkData.Word(right);
        services.Audit("refused", Device, $"{what}, it needs {needs}");
        throw HexLinkException.NotAllowed(needs);
    }

    private JsonObject Hello()
    {
        HexLinkRights rights = Rights;
        HexLinkWorkspace workspace = HexLinkAllowed.Has(rights, HexLinkRights.View) ? services.Workspace : HexLinkWorkspace.Empty;
        return new JsonObject
        {
            ["protocol"] = PROTOCOL,
            ["device"] = HexLinkData.DeviceJson(services.Device),
            ["granted"] = HexLinkData.Words(rights),
            ["serves"] = Serves(),
            ["workspace"] = new JsonObject
            {
                ["name"] = workspace.Name,
                ["folders"] = new JsonArray([.. workspace.Folders.Select(folder => (JsonNode)new JsonObject { ["name"] = folder.Name })]),
                ["repositories"] = new JsonArray([.. workspace.Repositories.Select(repository => (JsonNode)new JsonObject
                {
                    ["name"] = repository.Name,
                    ["branch"] = repository.Branch,
                    ["head"] = repository.Head
                })])
            }
        };
    }

    // The fleet as of the hub's last pump, and fleet/changed from now on
    private JsonObject FleetGet()
    {
        Need(HexLinkRights.View, "fleet/get");
        fleetAsked = true;
        return Fleet();
    }

    /// <summary>The hub moved (Dawn calls it when the hub pumps and the snapshot changed), sent on when the device asked for the fleet.</summary>
    public void FleetMoved()
    {
        if (fleetAsked)
            fleet.Poke();
    }

    private Task SendFleet()
    {
        if (fleetAsked && HexLinkAllowed.Has(Rights, HexLinkRights.View))
            session.Tell("fleet/changed", Fleet());
        return Task.CompletedTask;
    }

    // The most of an agent's last reply a fleet row carries
    private const int MOST_REPLY = 2000;

    private JsonObject Fleet()
    {
        HexHubSnapshot snapshot = services.Fleet;
        IReadOnlyDictionary<int, HexLinkChatExtra> extras = services.FleetExtras;
        var rows = new JsonArray();
        foreach (HexHubRow row in snapshot.Rows.Values.OrderBy(row => row.Id))
        {
            HexLinkChatExtra? extra = extras.GetValueOrDefault(row.Id);
            rows.Add((JsonNode)new JsonObject
            {
                ["id"] = row.Id,
                ["name"] = row.Shown,
                ["agent"] = row.Agent,
                ["model"] = row.Model,
                ["parent"] = row.Parent,
                ["depth"] = row.Depth,
                ["state"] = row.State,
                ["reply"] = row.Reply.Length > MOST_REPLY ? "…" + row.Reply[^MOST_REPLY..] : row.Reply,
                ["seconds"] = row.Seconds,
                ["cost"] = row.Cost,
                ["version"] = row.Version,
                ["waiting"] = extra?.Waiting ?? false,
                ["plan_step"] = extra?.PlanStep
            });
        }

        return new JsonObject { ["version"] = snapshot.Version, ["rows"] = rows };
    }

    private JsonObject ToolsList()
    {
        Need(HexLinkRights.View, "tools/list");
        return new JsonObject
        {
            ["tools"] = new JsonArray([.. HexLinkAllowed.Tools(Rights, services.Tools.Tools).Select(tool => (JsonNode)tool.Describe(McpProtocol.Latest))])
        };
    }

    // A tool called the way MCP's tools/call is, behind the allow list and the device's rights
    private async Task<JsonNode?> ToolsCall(JsonObject parameters, CancellationToken cancel)
    {
        string name = HexLinkData.Need(parameters, "name", 100);
        if (HexLinkAllowed.Needs(name) is not { } right)
        {
            services.Audit("refused", Device, $"tool {name}, never over a link");
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, $"{name} is never reachable over a link.", new JsonObject { ["needs"] = "never" });
        }

        Need(right, $"tool {name}");
        if (services.Tools.Tools.FirstOrDefault(tool => tool.Name == name) is not { } tool)
            throw HexLinkException.NotFound($"This Hex has no tool called {name}.");

        JsonObject arguments = HexLinkAllowed.Stripped((JsonObject)(HexLinkData.Object(parameters, "arguments") ?? []).DeepClone());
        arguments = HexLinkAllowed.Stripped(McpSchema.Normalize(tool.InputSchema, arguments));
        if (McpSchema.Check(tool.InputSchema, arguments) is { } wrong)
            return McpToolResult.Error($"{name} can't take those arguments. {wrong}").ToJson();

        if (right != HexLinkRights.View)
            services.Audit(HexLinkData.Word(right), Device, $"tool {name} {Cut(arguments.ToJsonString(), 300)}");

        try
        {
            McpToolResult result = await services.Tools.Call(name, arguments, cancel);
            return result.ToJson();
        }
        catch (OperationCanceledException) when (!session.IsClosed)
        {
            return McpToolResult.Error($"{name} was cancelled.").ToJson();
        }
        catch (Exception e) when (e is not HexLinkException and not OperationCanceledException)
        {
            return McpToolResult.Error($"{name} went wrong, {e.Message}").ToJson();
        }
    }

    // A patch offered, a card for the person here, the answer they gave
    private async Task<JsonNode?> PatchOffer(JsonObject parameters)
    {
        Need(HexLinkRights.View, "patch/offer");
        string title = HexLinkData.String(parameters, "title", 200) ?? "Changes";
        string diff = HexLinkData.Need(parameters, "diff", HexLinkFiles.MOST_READ);
        services.Audit("patch", Device, $"offered {title}, {diff.Length} characters");
        string answer = await services.PatchOffered(Device, title, diff);
        return new JsonObject { ["answer"] = answer is "applied" or "kept" ? answer : "discarded" };
    }

    private static string Cut(string text, int most) => text.Length > most ? text[..most] + "..." : text;

    /// <summary>Everything that runs for the link stopped, its watchers and timers.</summary>
    public void Dispose()
    {
        fleet.Dispose();
        StopWatching();
        StopPredicting();
    }

    // The watches of chats and files let go of, when the link goes or may no longer see
    private void StopWatching()
    {
        fleetAsked = false;
        foreach (var (_, watched) in chats)
            watched.Coalesce.Dispose();
        chats.Clear();
        Interlocked.Exchange(ref files, null)?.Dispose();
        StopTrees();
    }

    // The chats the device has open and what was last sent of each
    private readonly ConcurrentDictionary<int, WatchedChat> chats = new();
}
