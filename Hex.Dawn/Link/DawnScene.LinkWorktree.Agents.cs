using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed class PrivateAgentScope(DawnScene scene, Guid id, string root, HexLinkFolder shared)
    {
        public Guid Id { get; } = id;
        public string Root { get; } = root;
        public HexLinkFolder Shared { get; } = shared;
        public HexCodeIndex Index => LazyInitializer.EnsureInitialized(ref field, () => scene.LinkPrivateAgentIndex(this));
    }

    private ConcurrentDictionary<int, PrivateAgentScope> privateAgentRoots { get => LazyInitializer.EnsureInitialized(ref field, static () => new()); }

    private const string LINK_PRIVATE_STORAGE_UNAVAILABLE = "No private Link storage is available outside the shared folders.";

    private string LinkProfileDataRoot => options.SelfTest
        ? Path.Combine(Path.GetTempPath(), "hex-link-selftest-data") : PlatformPaths.DataRoot;

    private string? LinkPrivateStoragePath => HexLinkPrivateStorage.Choose(
        linkData.Length > 0 ? linkData : Path.Combine(HexData.Root, LINK_FOLDER),
        LinkProfileDataRoot,
        linkServices?.Workspace.Folders ?? []);

    private string LinkPrivateStorage => LinkPrivateStoragePath ?? throw new IOException(LINK_PRIVATE_STORAGE_UNAVAILABLE);

    private string? LinkAgentFolder(HexAgentChat chat)
    {
        if (chat.LinkDraft is not { } id)
            return explorer.workspace.Folders.FirstOrDefault() ?? Directory.GetCurrentDirectory();
        if (LinkPrivateStoragePath is not { } storage)
            return null;
        string root = Path.Combine(storage, "drafts", id.ToString("N"), "tree");
        if (!File.Exists(Path.Combine(root, ".git")) || HexLinkPrivateWorktree.RecordedFolder(storage, id) is not { } shared)
            return null;
        privateAgentRoots.GetOrAdd(chat.Id, _ => new PrivateAgentScope(this, id, root, shared));
        return root;
    }

    private sealed record LinkPrivateAgentStart(string? Folder, Guid? Inherited, string Family, string[] Repositories);
    private Dictionary<HexAgentChat, CancellationTokenSource> privateAgentStarting { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }

    private string? LinkPrivateAgentRequest(JsonObject args, out LinkPrivateAgentStart? prepare)
    {
        prepare = null;
        string? mode = args.String("worktree");
        Guid? parentDraft = CallerOf(args) is { } caller ? ChatById(caller)?.LinkDraft : null;
        if (mode != "private" && (mode == "live" || parentDraft is null))
            return null;
        if (LinkPrivateStoragePath is not { } storage)
            return LINK_PRIVATE_STORAGE_UNAVAILABLE;

        string? folder = args.String("folder") ?? (parentDraft is { } inherited
            ? HexLinkPrivateWorktree.RecordedFolder(storage, inherited)?.Name : null);
        if (args["repositories"] is { } extra && (extra is not JsonArray rows || rows.Count > 64 ||
            rows.Any(row => row is not JsonValue value || !value.TryGetValue(out string? path) || string.IsNullOrWhiteSpace(path))))
            return "List up to 64 shared repository paths to add to the private worktree.";
        string[] chosen = args["repositories"] is JsonArray repos
            ? [.. repos.Select(repo => repo!.GetValue<string>().TrimEnd('/'))] : [];
        string family = HexLinkAllowed.AgentCall(args) is { } remote
            ? $"device:{remote.Device.Id}:{remote.Chat?.ToString() ?? args.String("agent") ?? "default"}"
            : $"local:{CallerOf(args)?.ToString() ?? args.String("agent") ?? "default"}";
        prepare = new LinkPrivateAgentStart(folder, parentDraft, family, chosen);
        return null;
    }

    private void LinkPrepareAgent(HexAgentChat chat, LinkPrivateAgentStart request, Action launch)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(linkCancel?.Token ?? CancellationToken.None);
        privateAgentStarting.Add(chat, stop);
        chat.LinkDraft = Guid.NewGuid(); // A failed or stopped private setup must never launch in the live workspace.
        chat.LinkPreparation = "preparing private worktree";
        chat.Note("Preparing private worktree. The first gate in a fresh tree runs a full build.");
        _ = Prepare();

        async Task Prepare()
        {
            string? failure;
            try
            {
                failure = await LinkCreateAgentDraft(chat, request.Folder, stop.Token, request, progress =>
                    PostLink(() =>
                    {
                        if (!stop.IsCancellationRequested && agentsStarting.Contains(chat))
                        {
                            chat.LinkPreparation = progress.Describe();
                            chat.Note(char.ToUpperInvariant(chat.LinkPreparation[0]) + chat.LinkPreparation[1..]);
                            PublishAgentStatuses();
                        }
                    }));
            }
            catch (Exception error)
            {
                failure = error.Message;
            }
            PostLink(() =>
            {
                privateAgentStarting.Remove(chat);
                bool cancelled = stop.IsCancellationRequested;
                stop.Dispose();
                chat.LinkPreparation = null;
                if (!agentsStarting.Contains(chat))
                    return;
                if (failure is null && !cancelled)
                    launch();
                else
                {
                    agentsStarting.Remove(chat);
                    chat.Error(cancelled ? "Private worktree preparation was cancelled." : failure!);
                    chat.Done();
                }
                PublishAgentStatuses();
            });
        }
    }

    private void LinkStopAgentPreparation(HexAgentChat chat)
    {
        if (privateAgentStarting.TryGetValue(chat, out var stop))
        {
            stop.Cancel();
            chat.LinkPreparation = null;
            chat.Note("Private worktree preparation stopped.");
        }
    }

    private async Task<string?> LinkCreateAgentDraft(HexAgentChat chat, string? folder, CancellationToken cancel,
        LinkPrivateAgentStart? request = null, Action<HexLinkWorktreeProgress>? progress = null)
    {
        if (linkServices is not { } services)
            return Said("link.off");
        IReadOnlyList<HexLinkFolder> folders = services.Workspace.Folders;
        HexLinkFolder? shared = string.IsNullOrWhiteSpace(folder) ? folders.FirstOrDefault() :
            folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, folder) ||
                (Path.IsPathFullyQualified(folder) && PlatformPaths.Same(each.Path, folder)));
        if (shared is null)
            return folders.Count == 0 ? "There are no shared folders for the private worktree." :
                "Select a shared Git folder for the private worktree. Shared folders:\n" +
                string.Join('\n', folders.Select(each => $"{each.Name}: {each.Path}"));

        string storage = LinkPrivateStorage;
        Guid id = chat.LinkDraft ?? Guid.NewGuid();
        if (request is not null)
        {
            if (request.Inherited is { } inherited &&
                HexLinkPrivateWorktree.RecordedFolder(storage, inherited) is { } original &&
                PlatformPaths.Same(original.Path, shared.Path))
                id = inherited;
            else
            {
                string identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(shared.Path));
                if (PlatformPaths.Comparison == StringComparison.OrdinalIgnoreCase)
                    identity = identity.ToUpperInvariant();
                byte[] key = System.Text.Encoding.UTF8.GetBytes(request.Family + "\n" + identity);
                id = new Guid(System.Security.Cryptography.SHA256.HashData(key).AsSpan(0, 16));
            }
        }
        chat.LinkDraft = id;
        try
        {
            HexLinkPrivateWorktree draft = await LinkStartPrivateWorktree(services, shared, id, cancel,
                request?.Repositories, progress);
            cancel.ThrowIfCancellationRequested();
            privateAgentRoots[chat.Id] = new PrivateAgentScope(this, id, draft.Root, draft.SharedFolder);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HexLinkException or OperationCanceledException)
        {
            return error.Message;
        }
    }

    private HexCodeIndex LinkPrivateAgentIndex(PrivateAgentScope scope)
    {
        return new HexCodeIndex(() => [scope.Root, .. (linkPrivateDrafts.TryGetValue(scope.Id, out var draft)
            ? draft.MaterializedRepositories.Where(path => path.Length > 0).Select(path => Path.Combine(scope.Root, path))
            : [])])
        {
            Scope = index =>
            {
                HexIndexSnapshot workspace = Harness.Current();
                var files = new List<HexIndexedFile>(index.Files.Length);
                foreach (HexIndexedFile file in index.Files)
                {
                    string relative = Path.GetRelativePath(scope.Root, file.Path).Replace('\\', '/');
                    files.Add(relative == file.Relative ? file : AtRelative(file, relative));
                }

                linkPrivateDrafts.TryGetValue(scope.Id, out var draft);
                foreach (HexIndexedFile file in workspace.Files)
                {
                    if (!PlatformPaths.IsUnder(file.Path, scope.Shared.Path))
                    {
                        files.Add(file);
                        continue;
                    }
                    string relative = Path.GetRelativePath(scope.Shared.Path, file.Path).Replace('\\', '/');
                    if (draft is not null && !PrivateRepositoryPresent(draft, relative) &&
                        index.File(Path.Combine(scope.Root, relative)) is null)
                        files.Add(AtRelative(file, relative));
                }
                return new HexIndexSnapshot(index.Generation, [scope.Root, .. workspace.Roots], [.. files]);
            }
        };

        static HexIndexedFile AtRelative(HexIndexedFile file, string relative) =>
            new(file.Path, relative, file.Length, file.Written, file.LineCount, file.Symbols, file.FromServer, () => file.Lines);
    }

    internal HexCodeIndex LinkAgentIndex(int? caller) =>
        caller is { } id && privateAgentRoots.TryGetValue(id, out var scope) ? scope.Index : Harness;

    private string? LinkPrivateAgentRoot(int? caller) =>
        caller is { } id && privateAgentRoots.TryGetValue(id, out var scope) ? scope.Root : null;

    private static string LinkPrivateRelativePath(string asked, HexLinkFolder shared)
    {
        string path = asked.Replace('\\', '/');
        return path.StartsWith(shared.Name + "/", PlatformPaths.Comparison) ? path[(shared.Name.Length + 1)..] : path;
    }

    private static string LinkPrivateJail(string root, string full) =>
        HexLinkFiles.Resolve([new HexLinkFolder("private", root)],
            "private/" + Path.GetRelativePath(root, full).Replace('\\', '/'));

    internal Task<string?>? LinkPreparePrivateAgentPath(int caller, string tool, JsonObject arguments, CancellationToken cancel)
    {
        if (tool is not ("hex_edit" or "hex_write" or "hex_open" or "hex_read") ||
            !privateAgentRoots.TryGetValue(caller, out var scope))
            return null;

        string? absolute = arguments.String("path");
        if (absolute is not null && !PlatformPaths.IsUnder(absolute, scope.Root))
            return null;
        if (absolute is not null && tool != "hex_read" &&
            linkPrivateDrafts.TryGetValue(scope.Id, out var draft) &&
            PrivateRepositoryPresent(draft, Path.GetRelativePath(scope.Root, absolute).Replace('\\', '/')))
            return null;

        return Task.Run(Prepare, cancel);

        async Task<string?> Prepare()
        {
            try
            {
                string? path = absolute;
                if (path is null && tool == "hex_read" && arguments.String("symbol") is { Length: > 0 } symbol)
                {
                    var matches = HexCodeIndex.FindSymbols(scope.Index.VisibleSnapshot(), symbol);
                    if (matches.Count > 0 && matches[0].Score >= 90 &&
                        PlatformPaths.IsUnder(matches[0].File.Path, scope.Shared.Path))
                        path = Path.Combine(scope.Root, matches[0].File.Relative);
                }
                if (path is null)
                    return null;

                var opened = await LinkOpenStoredDraft(scope.Id, cancel);
                await LinkMaterializePrivatePath(opened, Path.GetRelativePath(scope.Root, path).Replace('\\', '/'), cancel);
                arguments["path"] = LinkPrivateJail(scope.Root, path);
                if (tool == "hex_read")
                {
                    // An edit or a sparse checkout can create a declaration before the index would normally look again.
                    var info = new FileInfo(path);
                    var indexed = scope.Index.Latest.File(path);
                    if (info.Exists && (indexed is null || indexed.Length != info.Length || indexed.Written != info.LastWriteTimeUtc.Ticks))
                        scope.Index.Look(force: true);
                }
                return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or HexLinkException)
            {
                return error.Message;
            }
        }
    }

    internal string? LinkScopePrivateAgentCall(int caller, string tool, JsonObject arguments)
    {
        if (!privateAgentRoots.TryGetValue(caller, out var scope))
            return null;
        if (!Directory.Exists(scope.Root))
            return "This agent's private worktree was discarded.";
        if (tool is "hex_link_authority" or "hex_link_worktree_gate")
            return "A person on the folder authority manages publish gate settings and authority transfers.";

        if (tool is "hex_edit" or "hex_write" or "hex_open" or "hex_read")
        {
            if (arguments.String("path") is not { Length: > 0 } asked)
                return tool == "hex_read" ? null : "Supply a path in the private worktree.";
            try
            {
                if (Path.IsPathFullyQualified(asked))
                {
                    string full = Path.GetFullPath(asked);
                    if (PlatformPaths.IsUnder(full, scope.Root))
                        arguments["path"] = LinkPrivateJail(scope.Root, full);
                    else if (PlatformPaths.IsUnder(full, scope.Shared.Path))
                        arguments["path"] = LinkPrivateJail(scope.Root, Path.Combine(scope.Root, Path.GetRelativePath(scope.Shared.Path, full)));
                    else if (tool == "hex_read" && harnessRoots.FirstOrDefault(root => PlatformPaths.IsUnder(full, root)) is { } other)
                        arguments["path"] = LinkPrivateJail(other, full);
                    else
                        return "The path is outside this agent's private worktree.";
                }
                else
                {
                    string normalized = asked.Replace('\\', '/');
                    string relative = LinkPrivateRelativePath(normalized, scope.Shared);
                    if (tool == "hex_read" && relative.Length == normalized.Length && relative.IndexOf('/') is > 0 and var slash &&
                        harnessRoots.FirstOrDefault(root => !PlatformPaths.Same(root, scope.Shared.Path) &&
                            PlatformPaths.Comparer.Equals(WorkspacePaths.FolderName(root), relative[..slash])) is { } other)
                    {
                        arguments["path"] = HexLinkFiles.Resolve([new HexLinkFolder("workspace", other)], "workspace/" + relative[(slash + 1)..]);
                    }
                    else
                    {
                        arguments["path"] = HexLinkFiles.Resolve([new HexLinkFolder("private", scope.Root)], "private/" + relative);
                    }
                }
            }
            catch (Exception error) when (error is ArgumentException or HexLinkException or IOException or UnauthorizedAccessException)
            {
                return error.Message;
            }
        }
        else if (tool is "hex_files" or "hex_symbols" or "hex_search" or "hex_skim" or "hex_refs")
        {
            foreach (string key in tool switch
            {
                "hex_files" => new[] { "query", "path" },
                "hex_symbols" => ["path"],
                "hex_skim" => ["paths"],
                _ => ["glob"]
            })
                if (arguments.String(key) is { } paths)
                    arguments[key] = string.Join(',', paths.Split(',').Select(path => LinkPrivateRelativePath(path.Trim(), scope.Shared)));
        }
        else if (tool is "hex_edit_symbol" or "hex_rename" or "hex_fix" or "hex_save" or "hex_type" or "hex_key" or "hex_click" or "hex_workspace" or "hex_run_command" ||
                 (tool != "hex_terminal_run" && tool.StartsWith("hex_terminal", StringComparison.Ordinal)) || tool.StartsWith("hex_git", StringComparison.Ordinal) ||
                 tool.StartsWith("hex_dev_", StringComparison.Ordinal))
        {
            return "This tool targets the shared editor or workspace. Use the agent's tools in its private worktree.";
        }

        return null;
    }
}
