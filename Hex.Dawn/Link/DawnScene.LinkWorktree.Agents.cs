using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private readonly ConcurrentDictionary<int, string> privateAgentRoots = new();

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
        if (!File.Exists(Path.Combine(root, ".git")) || !File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "state.json")))
            return null;
        privateAgentRoots[chat.Id] = root;
        return root;
    }

    private sealed record LinkPrivateAgentStart(string? Folder, Guid? Inherited, string Family, string[] Repositories);
    private readonly Dictionary<HexAgentChat, CancellationTokenSource> privateAgentStarting = [];

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
        HexLinkFolder? shared = folder is { Length: > 0 }
            ? services.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, folder))
            : services.Workspace.Folders.FirstOrDefault();
        if (shared is null)
            return "Select a shared Git folder for the private worktree.";

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
            privateAgentRoots[chat.Id] = draft.Root;
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HexLinkException or OperationCanceledException)
        {
            return error.Message;
        }
    }

    internal Task<string?>? LinkPreparePrivateAgentPath(int caller, string tool, JsonObject arguments, CancellationToken cancel)
    {
        if (tool is not ("hex_edit" or "hex_write" or "hex_open" or "hex_read") ||
            !privateAgentRoots.TryGetValue(caller, out string? root) || arguments.String("path") is not { } absolute ||
            !Guid.TryParseExact(Path.GetFileName(Path.GetDirectoryName(root)), "N", out Guid id))
            return null;
        string path = Path.GetRelativePath(root, absolute).Replace('\\', '/');
        if (linkPrivateDrafts.TryGetValue(id, out var draft) && PrivateRepositoryPresent(draft, path))
            return null;

        return Prepare();

        async Task<string?> Prepare()
        {
            try
            {
                var opened = await LinkOpenStoredDraft(id, cancel);
                await LinkMaterializePrivatePath(opened, path, cancel);
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
        if (!privateAgentRoots.TryGetValue(caller, out string? root))
            return null;
        if (!Directory.Exists(root))
            return "This agent's private worktree was discarded.";
        if (tool is "hex_link_authority" or "hex_link_worktree_gate")
            return "A person on the folder authority manages publish gate settings and authority transfers.";

        if (tool is "hex_edit" or "hex_write" or "hex_open" or "hex_read")
        {
            if (arguments.String("path") is not { Length: > 0 } asked)
                return "Supply a path in the private worktree.";
            string full;
            try
            {
                full = Path.GetFullPath(asked, root);
                if (!PlatformPaths.IsUnder(full, root))
                    return "The path is outside this agent's private worktree.";
                string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                arguments["path"] = HexLinkFiles.Resolve([new HexLinkFolder("private", root)], "private/" + relative);
            }
            catch (Exception error) when (error is ArgumentException or HexLinkException or IOException)
            {
                return error.Message;
            }
        }
        else if (tool is "hex_edit_symbol" or "hex_rename" or "hex_fix" or "hex_save" or "hex_type" or "hex_key" or "hex_click" or "hex_workspace" or "hex_run_command" ||
                 tool.StartsWith("hex_terminal", StringComparison.Ordinal) || tool.StartsWith("hex_git", StringComparison.Ordinal) ||
                 tool.StartsWith("hex_dev_", StringComparison.Ordinal))
        {
            return "This tool targets the shared editor or workspace. Use the agent's tools in its private worktree.";
        }

        return null;
    }
}
