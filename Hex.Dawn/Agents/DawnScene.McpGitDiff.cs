using System.Text;
using System.Text.Json.Nodes;
using Hex.Core.Git;

namespace Hex.Dawn;

// What changed in a file or folder by git's own diff, staged and not, for an agent checking its work or another's
// without running git in a shell. Read-only and bounded, a cut answer says so and how much there was.
internal sealed partial class DawnScene
{
    private IEnumerable<HexMcpTool> GitDiffTools() =>
    [
        new(new McpTool("hex_git_diff", "Git diff of a path",
            "Git's own unified diff of a file or folder, read-only: what isn't staged (working tree against the index), what is (index against HEAD), or both, each under its own heading. Cut at max_chars, saying so. Use it in place of git diff in a shell.",
            McpSchema.Object([
                ("path", McpSchema.String("The file or folder, inside the workspace or whole.")),
                ("view", McpSchema.OneOf("unstaged, staged, or both (the default).", "unstaged", "staged", "both")),
                ("context", McpSchema.Integer("Lines of context around each hunk, 3 by default.")),
                ("max_chars", McpSchema.Integer("Stay under this many characters, the agent's profile says how many when it isn't said (8000 usually)."))], "path"), ReadOnly: true),
            null, RunAway: GitDiffTool, NeedsEditor: false)
    ];

    private Task<McpToolResult> GitDiffTool(JsonObject args, CancellationToken cancel)
    {
        string asked = args.String("path") ?? string.Empty;
        string view = args.String("view") ?? "both";
        int context = args.Integer("context") ?? 3;
        int budget = HexAgentProfiles.BudgetFor(McpServer.CallingClient.Value, args.Integer("max_chars"));
        string path = McpPath(asked);
        return Task.Run(() =>
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return McpToolResult.Error($"There's no file or folder {asked}.");
            if (GitRepository.Find(path) is not { } repository)
                return McpToolResult.Error($"{asked} isn't in a git repository.");

            var said = new StringBuilder();
            foreach (bool staged in view switch { "staged" => new[] { true }, "unstaged" => [false], _ => [false, true] })
            {
                cancel.ThrowIfCancellationRequested();
                string? diff = repository.Diff(path, staged, context);
                if (diff is null)
                    return McpToolResult.Error($"git wouldn't diff {asked}.");
                said.Append($"## {(staged ? "staged" : "unstaged")} (git diff{(staged ? " --cached" : string.Empty)} -- {repository.Relative(path)})\n");
                said.Append(diff.Length == 0 ? "No changes.\n" : diff.EndsWith('\n') ? diff : diff + "\n");
            }

            return McpToolResult.Text(Bounded(said.ToString(), budget));
        }, cancel);
    }

    /// <summary>A diff kept under the budget at a line's end, a last line saying it was cut and how much there was.</summary>
    internal static string Bounded(string text, int budget)
    {
        if (text.Length <= budget)
            return text;
        int cut = text.LastIndexOf('\n', Math.Max(0, budget - 120));
        cut = cut > 0 ? cut + 1 : Math.Max(0, budget - 120);
        return text[..cut] + $"[truncated: {cut} of {text.Length} characters shown, narrow path, pick one view or raise max_chars]";
    }
}
