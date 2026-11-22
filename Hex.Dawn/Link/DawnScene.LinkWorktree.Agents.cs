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
        if (!File.Exists(Path.Combine(root, ".git")))
            return null;
        privateAgentRoots[chat.Id] = root;
        return root;
    }

    private IEnumerable<McpToolResult?> LinkPrivateAgentStartup(HexAgentChat chat, JsonObject args)
    {
        string? mode = args.String("worktree");
        Guid? parentDraft = CallerOf(args) is { } caller ? ChatById(caller)?.LinkDraft : null;
        if (mode != "private" && (mode == "live" || parentDraft is null))
            yield break;

        if (LinkPrivateStoragePath is not { } storage)
        {
            yield return McpToolResult.Error(LINK_PRIVATE_STORAGE_UNAVAILABLE);
            yield break;
        }
        string? folder = args.String("folder") ?? (parentDraft is { } inherited
            ? HexLinkPrivateWorktree.RecordedFolder(storage, inherited)?.Name : null);
        Task<string?> setup = LinkCreateAgentDraft(chat, folder, linkCancel?.Token ?? CancellationToken.None);
        while (!setup.IsCompleted)
            yield return null;
        if (setup.GetAwaiter().GetResult() is { } why)
            yield return McpToolResult.Error(why);
    }

    private async Task<string?> LinkCreateAgentDraft(HexAgentChat chat, string? folder, CancellationToken cancel)
    {
        if (linkServices is not { } services)
            return Said("link.off");
        HexLinkFolder? shared = folder is { Length: > 0 }
            ? services.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, folder))
            : services.Workspace.Folders.FirstOrDefault();
        if (shared is null)
            return "Select a shared Git folder for the private worktree.";

        Guid id = Guid.NewGuid();
        try
        {
            HexLinkPrivateWorktree draft = await LinkStartPrivateWorktree(services, shared, id, cancel);
            linkPrivateDrafts[id] = draft;
            chat.LinkDraft = id;
            privateAgentRoots[chat.Id] = draft.Root;
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HexLinkException or OperationCanceledException)
        {
            return error.Message;
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
