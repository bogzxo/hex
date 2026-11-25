using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed record ClaimInput(HexLinkClaimScope Scope, HexLinkClaimDraft Draft);
    private sealed record ClaimPlanCache(HexAgentPlan Plan, IReadOnlyList<HexLinkClaimFolder> Folders, ClaimInput[] Inputs);
    private sealed record ClaimEditorStamp(string Path, long Version, long LastEdit);
    private Dictionary<int, ClaimPlanCache> linkClaimPlans { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }
    private Dictionary<HexDocument, ClaimEditorStamp> linkClaimEditors { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }
    private Dictionary<(HexLinkClaimScope Scope, HexLinkClaimKey Key), HexLinkClaimDraft> linkExplicitClaims { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }
    private Dictionary<string, ClaimInput> linkPrivateClaims { get => LazyInitializer.EnsureInitialized(ref field, static () => new(StringComparer.Ordinal)); }
    private Dictionary<(int Chat, string Step), (long Utc, long Monotonic)> linkClaimEtas { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }
    private HexLink? linkClaimSession;
    private float linkClaimWait;
    private int linkClaimsSeenVersion;

    private HexLinkWorkspace LinkProvisionExport(string? selection)
    {
        if (linkServices is not { } services)
            return HexLinkWorkspace.Empty;
        foreach (HexLinkFolder folder in services.Workspace.Folders.Where(folder => selection is null || folder.Name == selection))
        {
            if (services.FolderAuthorities.Of(folder) is null)
                services.FolderAuthorities.Bind(folder, new HexLinkAuthority(services.Device.Id, 1), HexLinkAuthorityProvenance.Provisioned);
        }
        return services.Workspace;
    }

    private void LinkBindProvisionedFolders(HexLinkPeer source, IReadOnlyList<HexLinkFolder> folders)
    {
        if (linkServices is not { } services)
            throw new InvalidOperationException("Link stopped before the folder authority could be kept.");
        foreach (HexLinkFolder folder in folders)
        {
            HexLinkAuthority authority = folder.Authority
                ?? source.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, folder.Name))?.Authority
                ?? new HexLinkAuthority(source.Device.Id, 1);
            services.FolderAuthorities.Bind(folder, authority, HexLinkAuthorityProvenance.Provisioned);
        }
    }

    private void SetLinkClaimSession(HexLink? current)
    {
        if (linkClaimSession == current)
            return;
        linkClaimSession = current;
        linkClaimsIdle = false;
        linkExplicitClaims.Clear();
        linkPrivateClaims.Clear();
        linkClaimPlans.Clear();
        linkClaimEditors.Clear();
        linkClaimEtas.Clear();
    }

    // What the last look at the claims found: nothing to claim, and what it was looked at from (see UpdateLinkClaims)
    private bool linkClaimsIdle;
    private int linkClaimsSignature;

    private void UpdateLinkClaims(float dt)
    {
        if (link is not { } current || (linkClaimWait -= dt) > 0)
            return;
        // With nobody linked there is nobody to tell and nobody racing for a claim: looked at a quarter as often, and at once
        // (LinkJoined moves the link version) when somebody links
        linkClaimWait = LinkPeers.Count == 0 && linkClaimsSeenVersion == LinkVersion ? 2.0f : 0.5f;
        linkClaimsSeenVersion = LinkVersion;
        SetLinkClaimSession(current);

        HexLinkClaimExchange exchange = current.Claims;
        exchange.RefreshFolders();
        if (exchange.Folders.Count == 0)
            return;

        // Nothing to claim last time and nothing that claims anything moved since (no tab typed in or saved, no claim made, no
        // plan going): the answer is the same empty one, so it's not worked out again. Time only takes claims away.
        bool planning = false;
        foreach (HexAgentChat chat in agentChats)
        {
            if (chat.Plan is { Unfinished: true })
            {
                planning = true;
                break;
            }
        }

        var seen = new HashCode();
        foreach (HexDocument tab in documents)
        {
            if (tab is not { IsText: true, Path: { } tabPath, DiffOf: null })
                continue;
            seen.Add(tab);
            seen.Add(tab.Modified);
            seen.Add(tab.Editor.Document.Version);
            seen.Add(tabPath);
            seen.Add(LinkLiveOf(tab)?.LocalEditVersion);
        }
        seen.Add(linkExplicitClaims.Count);
        seen.Add(linkPrivateClaims.Count);
        seen.Add(linkMcpEdits.Count);
        seen.Add(linkEditingReleased.Count);
        seen.Add(exchange.Folders);
        int claimSignature = seen.ToHashCode();
        if (linkClaimsIdle && !planning && claimSignature == linkClaimsSignature)
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
            // A shared document changes as the others type into it: only a change made here is this device editing
            LinkLiveDocument? live = LinkLiveOf(tab);
            if (live is not null && !tab.Modified)
                live.LocalEditVersion = 0;
            long version = live is null ? tab.Editor.Document.Version : live.LocalEditVersion;
            linkClaimEditors.TryGetValue(tab, out ClaimEditorStamp? was);
            long edit = was?.LastEdit ?? 0;
            bool typed = live is null || live.LocalEditVersion != 0;
            if (tab.Modified && typed && (was is null || was.Version != version || was.Path != path))
                edit = now;
            if (was is null || was.Path != path || was.Version != version || was.LastEdit != edit)
                linkClaimEditors[tab] = new ClaimEditorStamp(path, version, edit);
            bool held = tab.Modified && typed;
            if (held && LinkEditingClaimLapsed(path, edit, now))
                held = false;
            // Released by hand: not claimed again until the buffer changes again or is saved
            if (linkEditingReleased.TryGetValue(path, out long releasedAt))
            {
                if (tab.Modified && version == releasedAt)
                {
                    held = false;
                    edit = 0;
                }
                else
                    linkEditingReleased.Remove(path);
            }

            if ((held || edit > 0 && now - edit < 5_000) && ClaimPathOf(exchange.Folders, path, false) is { } shared)
                Add(shared.Scope, new HexLinkClaimDraft(new HexLinkClaimKey(shared.Relative, false, null, HexLinkClaimKind.Editing),
                    System.Environment.UserName));
        }
        // A stamp only matters while its buffer's claim can lapse (the lease and a few seconds); older ones are let go
        if (linkMcpEdits.Count > 0)
        {
            foreach (var (path, stamp) in linkMcpEdits.ToArray())
            {
                if (now - stamp > 10 * 60_000)
                    linkMcpEdits.Remove(path);
            }
        }

        if (linkClaimEditors.Count > 0)
        {
            foreach (HexDocument stale in linkClaimEditors.Keys.Where(tab => !documents.Contains(tab)).ToArray())
                linkClaimEditors.Remove(stale);
        }

        foreach (HexAgentChat chat in agentChats)
        {
            if (chat.Plan is not { Unfinished: true } plan)
                continue;
            foreach (ClaimInput entry in PlanClaims(chat, plan, exchange.Folders))
                Add(entry.Scope, entry.Draft);
        }
        if (linkClaimPlans.Count > 0)
        {
            foreach (int stale in linkClaimPlans.Keys.Where(id => ChatById(id)?.Plan is not { Unfinished: true }).ToArray())
                linkClaimPlans.Remove(stale);
        }

        if (exchange.Update(drafts))
            linkDirty = true;
        linkClaimsIdle = drafts.Count == 0 && !planning;
        linkClaimsSignature = claimSignature;

        void Add(HexLinkClaimScope scope, HexLinkClaimDraft draft)
        {
            if (!drafts.TryGetValue(scope, out var entries))
                drafts[scope] = entries = [];
            entries.TryAdd(draft.Key, draft);
        }
    }

    // Editing claims the person let go of by hand, by path, with the version of the buffer they were let go at
    private Dictionary<string, long> linkEditingReleased { get => LazyInitializer.EnsureInitialized(ref field, static () => new(PlatformPaths.Comparer)); }

    private Dictionary<string, long> linkMcpEdits { get => LazyInitializer.EnsureInitialized(ref field, static () => new(PlatformPaths.Comparer)); }

    /// <summary>
    /// Whether the editing claim of a modified tab has lapsed: its last change came from an MCP edit (the guard saw the
    /// call just before the buffer changed) and nothing has touched it for a whole lease. A tab the person typed in
    /// keeps its claim for as long as it is unsaved.
    /// </summary>
    internal static bool LinkEditingClaimLapsed(long mcpStamp, long lastEdit, long now) =>
        mcpStamp > 0 && lastEdit - mcpStamp is >= -1_000 and < 3_000 && now - lastEdit >= HexLinkClaims.LEASE_MS;

    private bool LinkEditingClaimLapsed(string path, long lastEdit, long now) =>
        linkMcpEdits.TryGetValue(path, out long stamp) && LinkEditingClaimLapsed(stamp, lastEdit, now);

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
            if (!HexLinkClaims.ValidPath(relative, folderPrefix) ||
                HexLinkIgnore.Matches(HexLinkIgnore.ForRoot(owner.Folder.Path).Read(), relative, folderPrefix))
                return null;
            return (owner.Scope, relative, folderPrefix);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal IReadOnlyList<HexLinkClaim> LinkClaimsAt(string absolutePath)
    {
        if (link?.Claims is not { } exchange)
            return [];
        long now = System.Environment.TickCount64;
        return ClaimPathOf(exchange.Folders, absolutePath, false) is { } path ? exchange.At(path.Scope, path.Relative, false, now) : [];
    }

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
        string[] checkedPaths = [.. paths];
        foreach (string path in checkedPaths)
        {
            blockers.AddRange(LinkClaimsAt(path).Where(LinkClaimBlocks(actor, chat)));
        }

        if (blockers.Count == 0)
        {
            // Remember which files MCP edits touched, so the editing claim they make can lapse once the buffer is idle.
            long stamp = System.Environment.TickCount64;
            foreach (string path in checkedPaths)
                linkMcpEdits[path] = stamp;
            return null;
        }
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

    /// <summary>
    /// Whether a claim stops a write by <paramref name="actor"/> (a device id) from <paramref name="chat"/>: any other
    /// device's does, and this device's own only when another of its chats made it. What this device's own editor or user
    /// claims (no chat) never blocks its own agents, the notes still say who has it.
    /// </summary>
    internal static Func<HexLinkClaim, bool> LinkClaimBlocks(string actor, int? chat) =>
        claim => actor != claim.DeviceId || (claim.ChatId is not null && claim.ChatId != chat);

    private static string ClaimHolder(HexLinkClaim claim) => claim.ChatId is { } id
        ? $"#{id} '{claim.Holder}' on {claim.DeviceName}" : $"{claim.Holder} on {claim.DeviceName}";

    // One zone for every time said to an agent, local with its offset
    internal static string ClaimTime(DateTimeOffset time) => time.ToLocalTime().ToString("HH:mm:ss zzz", CultureInfo.InvariantCulture);

    internal static string ClaimAge(HexLinkClaim claim) =>
        (TimeSpan.FromMilliseconds(Math.Max(0, System.Environment.TickCount64 - claim.SinceTick)) is var age && age.TotalMinutes >= 1
            ? $"claimed {(int)age.TotalMinutes} min ago" : $"claimed {(int)age.TotalSeconds} s ago") + $", since {ClaimTime(claim.Since)}";

    /// <summary>What an agent is told when the file it changed is claimed by another device or held here, empty when neither.</summary>
    private string LinkEditNotes(string path)
    {
        var notes = new StringBuilder();
        if (LinkHeldFile(path) is { } held)
            notes.Append($" The file is held on this device since {ClaimTime(held.HeldAt)}{(held.HasIncoming ? $" and {held.IncomingFrom ?? "another device"} changed it too, see hex_link_hold update" : string.Empty)}, hex_link_hold submit sends it.");
        if (LinkRemoteHolderOf(path) is { } holder)
            notes.Append(' ' + Said("link.held_by_notice", ("device", holder.Name)));
        string? local = linkServices?.Device.Id;
        foreach (HexLinkClaim claim in LinkClaimsAt(path).Where(claim => claim.DeviceId != local).Take(3))
            notes.Append($" Claimed by {ClaimHolder(claim)} ({claim.Kind.ToString().ToLowerInvariant()}, {ClaimAge(claim)}).");
        return notes.ToString();
    }

    private static string ClaimPlan(HexLinkClaim claim)
    {
        string plan = claim.PlanStep is { Length: > 0 } step ? $"Its step \"{step}\" is {claim.PlanState ?? "open"}. " : string.Empty;
        if (claim.Eta is not { } eta)
            return plan + "ETA unknown.";
        return plan + $"ETA {ClaimTime(eta)}.";
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
            foreach (HexLinkClaim claim in exchange.At(entry.Scope, entry.Draft.Key.Path, entry.Draft.Key.Prefix, now))
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
        IReadOnlyList<HexLinkClaim> claims = exchange.List(System.Environment.TickCount64);
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
                ("action", McpSchema.OneOf("What to do, list by default.", "list", "claim", "release", "status")),
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
        if (action == "status")
        {
            string folders = string.Join('\n', exchange.Folders.Select(folder =>
                $"{folder.Scope.Folder}#{folder.Scope.Identity[..Math.Min(8, folder.Scope.Identity.Length)]}: "
                + (folder.PairedWith.Length > 0 ? $"synced with {string.Join('/', folder.PairedWith.Split('\n').Select(id => id[..Math.Min(8, id.Length)]))}"
                    : $"authority {folder.AuthorityId[..Math.Min(8, folder.AuthorityId.Length)]}")
                + (folder.Folder.Authority is { } pin ? $", pinned {pin.DeviceId[..Math.Min(8, pin.DeviceId.Length)]}" : ", not pinned")));
            string chosen = linkServices is null ? string.Empty : string.Join('\n', LinkPeers.Select(peer =>
                $"{peer.Device.Name}: we chose {string.Join('/', linkServices.ChosenSyncFolders(peer.Device.Id))}"));
            string routes = string.Join('\n', exchange.RouteStatus());
            yield return McpToolResult.Text($"Claim folders:\n{(folders.Length > 0 ? folders : "none")}"
                + $"\nSync choices:\n{(chosen.Length > 0 ? chosen : "none")}\nRoutes:\n{(routes.Length > 0 ? routes : "none")}"
                + $"\nExplicit claims here: {linkExplicitClaims.Count}. Board: {exchange.Board.List(System.Environment.TickCount64).Count}.");
            yield break;
        }
        if (action == "list")
        {
            if (supplied.Length > 0 && target is null)
            {
                yield return McpToolResult.Error("This path has no shared folder authority.");
                yield break;
            }
            long now = System.Environment.TickCount64;
            var claims = target is { } want ? exchange.At(want.Scope, want.Relative, want.Prefix, now) : exchange.List(now);
            yield return McpToolResult.Text(claims.Count == 0 ? "No current claims."
                : string.Join('\n', claims.Take(500).Select(claim => $"{claim.Wire}{(claim.Prefix ? "/" : string.Empty)}: {ClaimHolder(claim)}, {claim.Kind.ToString().ToLowerInvariant()}, {ClaimAge(claim)}. {ClaimPlan(claim)}"))
                    + (claims.Count > 500 ? $"\n{claims.Count - 500} more claims. Use path to narrow." : string.Empty)
                    + (exchange.Overflow ? "\nThe claim table reached its capacity. Some claims are missing." : string.Empty));
            yield break;
        }

        if (CallerOf(args) is not { } id || ChatById(id) is not { } chat)
        {
            yield return McpToolResult.Error("Only a Hex agent chat can claim or release, this call isn't from one.");
            yield break;
        }
        if (target is not { } requested)
        {
            yield return McpToolResult.Error(ClaimRefusal(exchange, supplied));
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

        string? refused = LinkClaimRival(exchange, requested.Scope, requested.Relative, requested.Prefix, id, supplied);
        if (refused is not null)
        {
            yield return McpToolResult.Error(refused);
            yield break;
        }

        long? eta = args.Integer("eta_seconds") is >= 0 and <= 43200 and var seconds
            ? System.Environment.TickCount64 + seconds * 1000L : null;
        linkExplicitClaims[(requested.Scope, key)] = new HexLinkClaimDraft(key, ChatName(chat.Id), EtaTick: eta);
        linkClaimWait = 0;
        yield return McpToolResult.Text($"Claimed {supplied}. Release it when finished. Other holders keep their claims.");
    }

    // An explicit claim is refused when another device's editing claim or explicit claim already covers the path, so two
    // sides cannot both take it. The check reads the claim table every device converges on; the refusal names the holder.
    private string? LinkClaimRival(HexLinkClaimExchange exchange, HexLinkClaimScope scope, string relative, bool prefix, int chat, string supplied)
    {
        string? local = linkServices?.Device.Id;
        HexLinkClaim? rival = exchange.At(scope, relative, prefix, System.Environment.TickCount64).FirstOrDefault(claim =>
            claim.Kind != HexLinkClaimKind.Private && (claim.DeviceId != local || claim.ChatId is not null && claim.ChatId != chat)
            && (claim.Kind == HexLinkClaimKind.Editing || claim.PlanStep is null));
        return rival is null ? null : $"{supplied} is already claimed by {ClaimHolder(rival)} ({rival.Kind.ToString().ToLowerInvariant()}, {ClaimAge(rival)}). "
            + $"Wait for release or {ClaimContact(rival)} then try again.";
    }

    // Exactly which condition a path failed to meet for a claim, in the order they're checked
    private string ClaimRefusal(HexLinkClaimExchange exchange, string supplied)
    {
        HexLinkWorkspace workspace = linkServices?.Workspace ?? HexLinkWorkspace.Empty;
        string names = string.Join(", ", workspace.Folders.Select(folder => folder.Name));
        if (string.IsNullOrWhiteSpace(supplied))
            return "Say the path to claim.";
        string input = supplied.Trim().Replace('\\', '/');
        HexLinkFolder? inside = null;
        if (Path.IsPathFullyQualified(input))
            inside = workspace.Folders.Where(folder => PlatformPaths.IsUnder(Path.GetFullPath(input), folder.Path)).MaxBy(folder => folder.Path.Length);
        else
        {
            string first = input.Split('/')[0];
            inside = workspace.Folders.FirstOrDefault(folder => PlatformPaths.Comparer.Equals(folder.Name, first)) ?? workspace.Folders.FirstOrDefault();
        }
        if (inside is null)
            return $"{supplied} is in no folder of this workspace (folders: {(names.Length == 0 ? "none" : names)}).";
        if (exchange.Folders.All(each => !PlatformPaths.Comparer.Equals(each.Scope.Folder, inside.Name)))
        {
            string chosen = linkServices is null ? string.Empty : string.Join(", ", LinkPeers.Select(peer =>
                $"{peer.Device.Name}: {(linkServices.ChosenSyncFolders(peer.Device.Id).Count == 0 ? "no folder synced" : string.Join('/', linkServices.ChosenSyncFolders(peer.Device.Id)))}, "
                + $"its folders {(peer.Workspace.Folders.Count == 0 ? "none" : string.Join('/', peer.Workspace.Folders.Select(folder => folder.Name)))}"));
            return $"{inside.Name} has no claim scope: it has no pinned authority"
                + $" ({(inside.Authority is null ? "none" : "pinned")}) and no linked device keeps a folder of that name in sync with it"
                + $" ({(chosen.Length == 0 ? "no device is linked" : chosen)}). Sync it with Link: Sync Folder, or hex_link_authority it.";
        }
        return $"{supplied} is a path claims can't cover (ignored, hidden or not inside {inside.Name}).";
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
            HexLinkClaim? claim = links.Claims.List(System.Environment.TickCount64).FirstOrDefault(claim =>
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

        Task<McpToolResult> sent = LinkCall(peer, tool, message, linkCancel?.Token ?? default, caller: null);
        while (!sent.IsCompleted)
            yield return null;
        yield return sent.IsCompletedSuccessfully ? sent.Result : McpToolResult.Error(sent.Exception?.GetBaseException().Message ?? "The message was cancelled.");
    }
}
