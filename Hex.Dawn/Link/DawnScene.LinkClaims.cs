using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed record ClaimInput(HexLinkClaimScope Scope, HexLinkClaimDraft Draft);
    private sealed record ClaimPlanCache(HexAgentPlan Plan, IReadOnlyList<HexLinkClaimFolder> Folders, ClaimInput[] Inputs);
    private sealed record ClaimEditorStamp(string Path, long Version, long LastEdit);
    private readonly Dictionary<int, ClaimPlanCache> linkClaimPlans = [];
    private readonly Dictionary<HexDocument, ClaimEditorStamp> linkClaimEditors = [];
    private readonly Dictionary<(HexLinkClaimScope Scope, HexLinkClaimKey Key), HexLinkClaimDraft> linkExplicitClaims = [];
    private readonly Dictionary<string, ClaimInput> linkPrivateClaims = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Chat, string Step), (long Utc, long Monotonic)> linkClaimEtas = [];
    private HexLink? linkClaimSession;
    private float linkClaimWait;

    private HexLinkWorkspace LinkProvisionExport(string? selection)
    {
        if (linkServices is not { } services)
            return HexLinkWorkspace.Empty;
        foreach (HexLinkFolder folder in services.Workspace.Folders.Where(folder => selection is null || folder.Name == selection))
        {
            if (services.FolderAuthorities.Of(folder) is null)
                services.FolderAuthorities.Bind(folder, new HexLinkAuthority(services.Device.Id, 1));
        }
        return services.Workspace;
    }

    private void LinkBindProvisionedFolders(HexLinkPeer source, IReadOnlyList<HexLinkFolder> folders)
    {
        if (linkServices is not { } services)
            throw new InvalidOperationException("Link stopped before the folder authority could be kept.");
        foreach (HexLinkFolder folder in folders)
            services.FolderAuthorities.Bind(folder, folder.Authority ?? new HexLinkAuthority(source.Device.Id, 1));
    }

    private void UpdateLinkClaims(float dt)
    {
        if (link is not { } current || (linkClaimWait -= dt) > 0)
            return;
        linkClaimWait = 0.5f;
        if (linkClaimSession != current)
        {
            linkClaimSession = current;
            linkExplicitClaims.Clear();
            linkPrivateClaims.Clear();
            linkClaimPlans.Clear();
            linkClaimEditors.Clear();
            linkClaimEtas.Clear();
        }

        HexLinkClaimExchange exchange = current.Claims;
        exchange.RefreshFolders();
        if (exchange.Folders.Count == 0)
            return;

        long now = System.Environment.TickCount64;
        var drafts = new Dictionary<HexLinkClaimScope, Dictionary<HexLinkClaimKey, HexLinkClaimDraft>>();
        foreach (var (key, draft) in linkExplicitClaims)
            Add(key.Scope, draft);
        foreach (ClaimInput claim in linkPrivateClaims.Values)
            Add(claim.Scope, claim.Draft);

        foreach (HexDocument tab in documents)
        {
            if (tab is not { IsText: true, Path: { } path, DiffOf: null })
                continue;
            long version = tab.Editor.Document.Version;
            linkClaimEditors.TryGetValue(tab, out ClaimEditorStamp? was);
            long edit = was?.LastEdit ?? 0;
            if (tab.Modified && (was is null || was.Version != version || was.Path != path))
                edit = now;
            linkClaimEditors[tab] = new ClaimEditorStamp(path, version, edit);
            if ((tab.Modified || edit > 0 && now - edit < 5_000) && ClaimPathOf(exchange.Folders, path, false) is { } shared)
                Add(shared.Scope, new HexLinkClaimDraft(new HexLinkClaimKey(shared.Relative, false, null, HexLinkClaimKind.Editing),
                    System.Environment.UserName));
        }
        foreach (HexDocument stale in linkClaimEditors.Keys.Where(tab => !documents.Contains(tab)).ToArray())
            linkClaimEditors.Remove(stale);

        foreach (HexAgentChat chat in agentChats)
        {
            if (chat.Plan is not { Unfinished: true } plan)
                continue;
            foreach (ClaimInput entry in PlanClaims(chat, plan, exchange.Folders))
                Add(entry.Scope, entry.Draft);
        }
        foreach (int stale in linkClaimPlans.Keys.Where(id => ChatById(id)?.Plan is not { Unfinished: true }).ToArray())
            linkClaimPlans.Remove(stale);

        if (exchange.Update(drafts))
            linkDirty = true;

        void Add(HexLinkClaimScope scope, HexLinkClaimDraft draft)
        {
            if (!drafts.TryGetValue(scope, out var entries))
                drafts[scope] = entries = [];
            entries.TryAdd(draft.Key, draft);
        }
    }

    private ClaimInput[] PlanClaims(HexAgentChat chat, HexAgentPlan plan, IReadOnlyList<HexLinkClaimFolder> folders)
    {
        if (linkClaimPlans.TryGetValue(chat.Id, out ClaimPlanCache? cached)
            && ReferenceEquals(cached.Plan, plan) && ReferenceEquals(cached.Folders, folders))
            return cached.Inputs;

        var claims = new List<ClaimInput>();
        foreach (HexPlanItem step in plan.Items.Where(step => step.Open && step.Files is not null).OrderBy(step => step.State != HexPlanState.Doing))
        {
            foreach (string named in step.Files!)
            {
                if (ClaimPathOf(folders, named, named.EndsWith('/') || named.EndsWith('\\')) is not { } path)
                    continue;
                var key = new HexLinkClaimKey(path.Relative, path.Prefix, chat.Id, HexLinkClaimKind.Planned);
                claims.Add(new ClaimInput(path.Scope, new HexLinkClaimDraft(key, ChatName(chat.Id), OneLine(step.Text, 120),
                    step.State.ToString().ToLowerInvariant(), ClaimEta(chat.Id, step))));
            }
        }

        ClaimInput[] found = [.. claims];
        linkClaimPlans[chat.Id] = new ClaimPlanCache(plan, folders, found);
        return found;
    }

    private long? ClaimEta(int chatId, HexPlanItem step)
    {
        if (step.EtaAt <= 0)
            return null;
        var key = (chatId, step.Id ?? step.Text);
        if (!linkClaimEtas.TryGetValue(key, out var was) || was.Utc != step.EtaAt)
            linkClaimEtas[key] = was = (step.EtaAt,
                System.Environment.TickCount64 + Math.Max(0, step.EtaAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        return was.Monotonic;
    }

    private static (HexLinkClaimScope Scope, string Relative, bool Prefix)? ClaimPathOf(IReadOnlyList<HexLinkClaimFolder> folders,
        string supplied, bool prefix)
    {
        if (folders.Count == 0 || string.IsNullOrWhiteSpace(supplied))
            return null;

        try
        {
            string input = supplied.Trim().Replace('\\', '/'), full;
            if (Path.IsPathFullyQualified(input))
                full = Path.GetFullPath(input);
            else
            {
                int slash = input.IndexOf('/');
                string first = slash < 0 ? input : input[..slash];
                HexLinkClaimFolder? named = folders.FirstOrDefault(folder => PlatformPaths.Comparer.Equals(folder.Scope.Folder, first));
                if (named is not null && (slash >= 0 || prefix))
                    full = Path.GetFullPath(Path.Combine(named.Folder.Path, slash < 0 ? string.Empty : input[(slash + 1)..]));
                else
                    full = Path.GetFullPath(Path.Combine(folders[0].Folder.Path, input));
            }

            HexLinkClaimFolder? owner = folders.Where(folder => PlatformPaths.IsUnder(full, folder.Folder.Path))
                .MaxBy(folder => folder.Folder.Path.Length);
            if (owner is null)
                return null;

            string relative = HexExcludes.Relative(owner.Folder.Path, full);
            bool folderPrefix = prefix || relative.Length == 0;
            return HexLinkClaims.ValidPath(relative, folderPrefix) ? (owner.Scope, relative, folderPrefix) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    internal IReadOnlyList<HexLinkClaim> LinkClaimsAt(string absolutePath)
    {
        if (link?.Claims is not { } exchange)
            return [];
        long now = System.Environment.TickCount64;
        return ClaimPathOf(exchange.Folders, absolutePath, false) is { } path ? exchange.Board.At(path.Scope, path.Relative, false, now) : [];
    }

    internal int LinkClaimCount(string deviceId) => link?.Claims.Board.List(System.Environment.TickCount64).Count(claim => claim.DeviceId == deviceId) ?? 0;

    internal bool LinkOpenClaimDocument(HexLinkClaim claim)
    {
        if (claim.Prefix)
            return false;
        HexLinkPeer? peer = link?.PeerOf(claim.DeviceId) ?? link?.PeerOf(claim.Via);
        if (peer is null || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
            return false;
        LinkDocumentJoin(peer, claim.Wire);
        return true;
    }

    internal void LinkPrivateClaim(string folder, int? chatId, string publishId, bool held) => PostLink(() =>
    {
        if (!held)
        {
            linkPrivateClaims.Remove(publishId);
            return;
        }

        if (link?.Claims.Folders.FirstOrDefault(item => item.Scope.Folder == folder) is not { } shared)
            return;
        string holder = chatId is { } id && ChatById(id) is { } chat ? ChatName(chat.Id) : System.Environment.UserName;
        linkPrivateClaims[publishId] = new ClaimInput(shared.Scope,
            new HexLinkClaimDraft(new HexLinkClaimKey(string.Empty, true, chatId, HexLinkClaimKind.Private), holder, "Publishing a private work tree"));
    });

    private static (string Name, JsonObject Schema) ClaimOverride() =>
        ("override_claim", McpSchema.Flag("Proceed after coordinating with the claim holder. The override is audited."));

    private McpToolResult? LinkClaimGuard(JsonObject args, IEnumerable<string> paths)
    {
        if (link is null)
            return null;
        int? chat = McpServer.CallingChat.Value;
        HexLinkDevice? inbound = HexLinkClaimCaller.Of(args);
        string actor = inbound?.Id ?? linkServices?.Device.Id ?? string.Empty;
        var blockers = new List<HexLinkClaim>();
        foreach (string path in paths)
        {
            blockers.AddRange(LinkClaimsAt(path).Where(claim => actor != claim.DeviceId || chat is null || claim.ChatId != chat));
        }

        if (blockers.Count == 0)
            return null;
        if (args.Flag("override_claim"))
        {
            linkAudit?.Write("claim override", actor,
                $"chat {chat?.ToString(CultureInfo.InvariantCulture) ?? "external"}, {string.Join(", ", blockers.Select(claim => $"{claim.Wire} by {claim.DeviceId}/{claim.ChatId}").Distinct().Take(20))}");
            return null;
        }

        HexLinkClaim first = blockers[0];
        return McpToolResult.Error($"Claimed file {first.Wire} by {ClaimHolder(first)} ({first.Kind.ToString().ToLowerInvariant()}, {ClaimAge(first)}). "
            + $"{ClaimPlan(first)} Wait for release, {ClaimContact(first)} or work in a private work tree. "
            + "Use override_claim=true only after coordination. Overrides are audited.");
    }

    private static string ClaimHolder(HexLinkClaim claim) => claim.ChatId is { } id
        ? $"#{id} '{claim.Holder}' on {claim.DeviceName}" : $"{claim.Holder} on {claim.DeviceName}";

    private static string ClaimAge(HexLinkClaim claim) =>
        TimeSpan.FromMilliseconds(Math.Max(0, System.Environment.TickCount64 - claim.SinceTick)) is var age
        && age.TotalMinutes >= 1 ? $"claimed {(int)age.TotalMinutes} min ago" : $"claimed {(int)age.TotalSeconds} s ago";

    private static string ClaimPlan(HexLinkClaim claim)
    {
        string plan = claim.PlanStep is { Length: > 0 } step ? $"Its step \"{step}\" is {claim.PlanState ?? "open"}. " : string.Empty;
        if (claim.Eta is not { } eta)
            return plan + "ETA unknown.";
        return plan + $"ETA {eta.ToLocalTime():HH:mm:ss}.";
    }

    private string ClaimContact(HexLinkClaim claim)
    {
        if (claim.ChatId is not { } chat)
            return "coordinate with the person who holds it,";
        object to = claim.DeviceId == linkServices?.Device.Id ? chat : $"{claim.DeviceId}:{chat}";
        return "coordinate with hex_agent_message " + new JsonObject
        {
            ["to"] = to is int id ? JsonValue.Create(id) : JsonValue.Create((string)to),
            ["kind"] = "question",
            ["text"] = "Can you let me know when this file is free?"
        }.ToJsonString() + ",";
    }

    private IEnumerable<string> LinkPlanOverlaps(HexAgentChat chat, HexAgentPlan plan)
    {
        if (link?.Claims is not { } exchange)
            yield break;
        string? localId = linkServices?.Device.Id;
        long now = System.Environment.TickCount64;
        var overlap = new Dictionary<(string Device, int? Chat, string? Step), (HexLinkClaim Claim, HashSet<string> Files)>();
        foreach (ClaimInput entry in PlanClaims(chat, plan, exchange.Folders))
        {
            foreach (HexLinkClaim claim in exchange.Board.At(entry.Scope, entry.Draft.Key.Path, entry.Draft.Key.Prefix, now))
            {
                if (claim.DeviceId == localId)
                    continue;
                var key = (claim.DeviceId, claim.ChatId, claim.PlanStep);
                if (!overlap.TryGetValue(key, out var group))
                    overlap[key] = group = (claim, new HashSet<string>(StringComparer.Ordinal));
                group.Files.Add(entry.Draft.Key.Path);
            }
        }

        foreach (var (claim, files) in overlap.Values)
            yield return $"Also named by {ClaimHolder(claim)} ({claim.Kind.ToString().ToLowerInvariant()}): {string.Join(", ", files)}. "
                + $"{ClaimPlan(claim)} Agree who goes first with {ClaimContact(claim).TrimEnd(',')} and take other work meanwhile.";
    }

    private string LinkAgentClaimFiles(JsonObject args)
    {
        if (link?.Claims is not { } exchange)
            return string.Empty;
        HexLinkDevice? inbound = HexLinkClaimCaller.Of(args);
        string? local = linkServices?.Device.Id;
        IReadOnlyList<HexLinkClaim> claims = exchange.Board.List(System.Environment.TickCount64);
        var groups = claims.Where(claim => claim.ChatId is not null && claim.DeviceId != local
            && (inbound is null || exchange.Subscribed(inbound.Id, claim.Scope))).GroupBy(claim => (claim.DeviceId, claim.ChatId));
        var text = new StringBuilder();
        foreach (var group in groups)
        {
            HexLinkClaim first = group.First();
            text.Append(ClaimHolder(first)).Append('\n').Append("  files ")
                .Append(string.Join(", ", group.Select(claim => claim.Wire + (claim.Prefix ? "/" : string.Empty)).Distinct().Take(8))).Append('\n');
        }
        return text.Length > 0 ? "\nLinked chats with claims:\n" + text : string.Empty;
    }

    private IEnumerable<HexMcpTool> LinkClaimTools() =>
    [
        new(new McpTool("hex_link_claims", "Coordinate edits with linked devices",
            "List current claims or take and release one advisory claim for the calling chat. Agents must coordinate before editing another holder's path.",
            McpSchema.Object([
                ("action", McpSchema.OneOf("What to do, list by default.", "list", "claim", "release")),
                ("path", McpSchema.String("A shared file or directory path. Required for claim and release. Optional list filter.")),
                ("prefix", McpSchema.Flag("The path is a directory and includes all descendants.")),
                ("eta_seconds", McpSchema.Integer("Optional estimate until you finish, in seconds."))
            ])), LinkClaimTool, NeedsEditor: false, TakesCaller: true, Alongside: true)
    ];

    private IEnumerable<McpToolResult?> LinkClaimTool(JsonObject args)
    {
        if (link?.Claims is not { } exchange)
        {
            yield return McpToolResult.Error("Link is off.");
            yield break;
        }
        exchange.RefreshFolders();
        string action = args.String("action") ?? "list", supplied = args.String("path") ?? string.Empty;
        var target = ClaimPathOf(exchange.Folders, supplied, args.Flag("prefix"));
        if (action == "list")
        {
            if (supplied.Length > 0 && target is null)
            {
                yield return McpToolResult.Error("This path has no shared folder authority.");
                yield break;
            }
            long now = System.Environment.TickCount64;
            var claims = target is { } want ? exchange.Board.At(want.Scope, want.Relative, want.Prefix, now) : exchange.Board.List(now);
            yield return McpToolResult.Text(claims.Count == 0 ? "No current claims."
                : string.Join('\n', claims.Take(500).Select(claim => $"{claim.Wire}{(claim.Prefix ? "/" : string.Empty)}: {ClaimHolder(claim)}, {claim.Kind.ToString().ToLowerInvariant()}, {ClaimAge(claim)}. {ClaimPlan(claim)}"))
                    + (claims.Count > 500 ? $"\n{claims.Count - 500} more claims. Use path to narrow." : string.Empty)
                    + (exchange.Overflow ? "\nThe claim table reached its capacity. Some claims are missing." : string.Empty));
            yield break;
        }

        if (target is not { } requested || CallerOf(args) is not { } id || ChatById(id) is not { } chat)
        {
            yield return McpToolResult.Error("Use a Hex agent chat and a path in a folder that was shared or provisioned.");
            yield break;
        }

        var key = new HexLinkClaimKey(requested.Relative, requested.Prefix, id, HexLinkClaimKind.Planned);
        if (action == "release")
        {
            bool removed = linkExplicitClaims.Remove((requested.Scope, key));
            linkClaimWait = 0;
            yield return McpToolResult.Text(removed ? $"Released {supplied}." : $"No explicit claim of yours for {supplied}.");
            yield break;
        }

        if (!linkExplicitClaims.ContainsKey((requested.Scope, key)) && linkExplicitClaims.Count >= HexLinkClaims.DEVICE_LIMIT)
        {
            yield return McpToolResult.Error("This device's explicit claim table is full.");
            yield break;
        }

        long? eta = args.Integer("eta_seconds") is >= 0 and <= 43200 and var seconds
            ? System.Environment.TickCount64 + seconds * 1000L : null;
        linkExplicitClaims[(requested.Scope, key)] = new HexLinkClaimDraft(key, ChatName(chat.Id), EtaTick: eta);
        linkClaimWait = 0;
        yield return McpToolResult.Text($"Claimed {supplied}. Release it when finished. Other holders keep their claims.");
    }

    private IEnumerable<McpToolResult?> LinkClaimAgentMessage(JsonObject args)
    {
        if (HexLinkClaimCaller.Of(args) is not null || link is not { } links)
        {
            yield return McpToolResult.Error("Use the remote form from your own Hex. Link accepts only one relay hop.");
            yield break;
        }

        string device = args.String("device") ?? string.Empty;
        int? chat = args.Integer("to");
        if (args.String("to") is { } compound)
        {
            int colon = compound.LastIndexOf(':');
            if (colon > 0 && int.TryParse(compound[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            {
                device = compound[..colon];
                chat = id;
            }
        }
        if (chat is not > 0 || device.Length == 0 || string.IsNullOrWhiteSpace(args.String("text")))
        {
            yield return McpToolResult.Error("Give to as device:chat, or give device and a numeric to. Include a message.");
            yield break;
        }

        var message = new JsonObject { ["to"] = chat, ["kind"] = args.String("kind") ?? "finding", ["text"] = args.String("text") };
        HexLinkPeer? peer = LinkPeerOf(links.Peers, device, out string? why);
        string tool = "hex_agent_message";
        if (peer is null)
        {
            HexLinkClaim? claim = links.Claims.Board.List(System.Environment.TickCount64).FirstOrDefault(claim =>
                claim.ChatId == chat && (claim.DeviceId.Equals(device, StringComparison.OrdinalIgnoreCase)
                    || claim.DeviceName.Equals(device, StringComparison.OrdinalIgnoreCase)));
            if (claim is not null && links.PeerOf(claim.Via) is { } authority)
            {
                peer = authority;
                tool = "hex_link_call";
                message = new JsonObject { ["device"] = claim.DeviceId, ["tool"] = "hex_agent_message", ["arguments"] = message };
            }
        }
        if (peer is null || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Steer))
        {
            yield return McpToolResult.Error(peer is null ? why ?? "The holder has no live route." : $"You need Steer on {peer.Device.Name} to send this message.");
            yield break;
        }

        Task<McpToolResult> sent = LinkCall(peer, tool, message, linkCancel?.Token ?? default);
        while (!sent.IsCompleted)
            yield return null;
        yield return sent.IsCompletedSuccessfully ? sent.Result : McpToolResult.Error(sent.Exception?.GetBaseException().Message ?? "The message was cancelled.");
    }
}
