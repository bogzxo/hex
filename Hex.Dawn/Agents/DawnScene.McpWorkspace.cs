using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

// An agent putting folders in Hex's workspace and taking them out, as the person does with Add Folder to Workspace. A
// project of another repository it needs to read (an engine beside the app, a library it builds against) comes into the
// harness that way, indexed and searched like the rest, rather than read through the shell around it.
internal sealed partial class DawnScene
{
    private IEnumerable<HexMcpTool> WorkspaceTools() =>
    [
        new(new McpTool("hex_workspace", "Workspace folders",
            "The folders of Hex's workspace, and putting one in or taking one out. list says them. add puts a folder in, or the folder of a project or solution file given (a .csproj, .sln, package.json), so its files are indexed and Hex's tools reach them (hex_search, hex_read, hex_symbols). remove takes one out, nothing on disk is touched. Add what you need to read outside the workspace rather than reading it through the shell.",
            McpSchema.Object([
                ("action", McpSchema.OneOf("list (the default), add or remove.", "list", "add", "remove")),
                ("path", McpSchema.String("The folder, or a project file in it, whole or from the first folder of the workspace."))])),
            WorkspaceTool)
    ];

    private IEnumerable<McpToolResult?> WorkspaceTool(JsonObject args)
    {
        string action = args.String("action") ?? "list";
        IReadOnlyList<string> folders = explorer.workspace.Folders;
        if (action == "list")
        {
            yield return McpToolResult.Text(WorkspaceList());
            yield break;
        }

        string asked = (args.String("path") ?? string.Empty).Trim().Trim('"');
        if (asked.Length == 0)
        {
            yield return McpToolResult.Error("Say the folder in path.");
            yield break;
        }

        string path = Path.IsPathRooted(asked) || folders.Count == 0 ? asked : Path.Combine(folders[0], asked);
        path = Path.GetFullPath(path);
        // A project or solution file stands for the folder it's in
        if (File.Exists(path))
            path = Path.GetDirectoryName(path)!;
        string? inWorkspace = folders.FirstOrDefault(folder => PlatformPaths.Same(folder, path));

        if (action == "add")
        {
            if (!Directory.Exists(path))
            {
                yield return McpToolResult.Error($"There's no folder {path}.");
                yield break;
            }

            if (inWorkspace is not null)
            {
                yield return McpToolResult.Text($"{path} is in the workspace already.\n\n{WorkspaceList()}");
                yield break;
            }

            explorer.AddFolder(path);
            yield return McpToolResult.Text($"Added {path}, its files are indexed as Hex gets to them.\n\n{WorkspaceList()}");
            yield break;
        }

        if (action == "remove")
        {
            if (inWorkspace is null)
            {
                yield return McpToolResult.Error($"{path} isn't a folder of the workspace.\n\n{WorkspaceList()}");
                yield break;
            }

            // The last one taken out would leave the harness and every agent with nothing to work in
            if (folders.Count == 1)
            {
                yield return McpToolResult.Error("That's the workspace's only folder, Hex keeps it.");
                yield break;
            }

            explorer.RemoveFolder(inWorkspace);
            yield return McpToolResult.Text($"Took {inWorkspace} out of the workspace, nothing on disk was touched.\n\n{WorkspaceList()}");
            yield break;
        }

        yield return McpToolResult.Error("action is list, add or remove.");
    }

    // The workspace's folders, a line each
    private string WorkspaceList()
    {
        var said = new StringBuilder("The workspace's folders:\n");
        foreach (string folder in explorer.workspace.Folders)
            said.Append("- ").Append(folder).Append('\n');
        return said.ToString().TrimEnd();
    }
}
