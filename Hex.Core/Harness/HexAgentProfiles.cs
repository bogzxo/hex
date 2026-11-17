namespace Hex.Core;

/// <summary>
/// An agent system as an extension says it is with hex.agent, Claude Code or Codex or whatever's next. It's picked for a
/// connection by what the client calls itself in its initialize, any of <paramref name="Clients"/> being in that name.
/// </summary>
/// <param name="Extension">The id of the extension that said it, which takes it away again when it's switched off.</param>
/// <param name="Budget">The characters a harness answer stays under for it when a call doesn't say, 0 for the usual.</param>
/// <param name="Instructions">What its agent is told as it connects, after what Hex tells every agent.</param>
internal sealed record HexAgentProfile(string Id, string Name, string[] Clients, int Budget, string Instructions, string Extension);

/// <summary>
/// The agent profiles there are, said by extensions on the editor's thread and read from the pipe's as clients connect
/// and call, so it's swapped whole under a lock and read without one.
/// </summary>
internal static class HexAgentProfiles
{
    /// <summary>What a harness answer stays under when neither the call nor a profile says.</summary>
    public const int USUAL_BUDGET = 8000;

    /// <summary>
    /// What an agent the Agents panel runs is told in its system prompt, where it weighs more than the server's
    /// instructions do. Agents left to themselves reached for the shell out of habit and read whole files to find one
    /// method, so this says which of Hex's tools does each of those better. The shell stays theirs, it's only second.
    /// No apostrophes in it, Codex takes it as a TOML literal string.
    /// </summary>
    public const string HARNESS_RULES =
        "You are running inside Hex, a code editor, and its harness is connected to you as the MCP server named hex. "
        + "The harness answers from a live index of the workspace, ranked, as path:line, in a fraction of the tokens and time a shell command or a whole file takes. "
        + "Reach for it first, the shell is still yours for whatever it does not cover.\n"
        + "- finding files, hex_files rather than ls, find, dir, Get-ChildItem or Glob\n"
        + "- searching text, hex_search rather than grep, rg, findstr, Select-String or Grep\n"
        + "- where a name is declared or used, hex_symbols and hex_refs\n"
        + "- what files or folders are, hex_skim and hex_map rather than opening them\n"
        + "- reading code, hex_read with a symbol for one method or class, whole files only when about to edit them\n"
        + "- builds, tests and other commands, hex_terminal_run, which waits and gives back the exit code and what was printed\n"
        + "- catching up on what changed, hex_changes with the generation the last answer ended with\n"
        + "- more hands, hex_agent_spawn and hex_agent_wait start and wait for other agents of any system and model\n"
        + "- coordinating, keep the judgement yourself and start cheaper agents to explore, implement and test. hex_agent_wait wakes on their messages as well as when they finish, "
        + "hex_agent_send steers one as it works, hex_agent_message passes what one found to the others, and those you start report to you the same way\n"
        + "When a subagent of your own does the searching, tell it to use these tools too.";

    private static readonly Lock changing = new();
    private static volatile HexAgentProfile[] all = [];

    public static IReadOnlyList<HexAgentProfile> All => all;

    /// <summary>A profile said, one with the same id put in its place (where it was, so the panel's chips don't move about).</summary>
    public static void Set(HexAgentProfile profile)
    {
        lock (changing)
            all = all.Any(each => each.Id == profile.Id) ? [.. all.Select(each => each.Id == profile.Id ? profile : each)] : [.. all, profile];
    }

    /// <summary>Every profile an extension said taken away, as it's switched off or read again.</summary>
    public static void Forget(string extension)
    {
        lock (changing)
            all = [.. all.Where(each => each.Extension != extension)];
    }

    /// <summary>The profile for a client by what it said it was ("claude-code 2.1.0"), null for one nobody said.</summary>
    public static HexAgentProfile? For(string? client)
    {
        if (string.IsNullOrEmpty(client))
            return null;

        return all.FirstOrDefault(profile => profile.Clients.Any(name => name.Length > 0 && client.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The budget for a call, what it asked for, the client's profile's, or the usual.</summary>
    public static int BudgetFor(string? client, int? asked) =>
        Math.Clamp(asked ?? (For(client) is { Budget: > 0 } profile ? profile.Budget : USUAL_BUDGET), 400, 200_000);

    /// <summary>What a client is told as it connects, Hex's own and its profile's after it.</summary>
    public static string InstructionsFor(string? client, string usual) =>
        For(client) is { Instructions.Length: > 0 } profile ? $"{usual}\n\n{profile.Name}: {profile.Instructions}" : usual;
}
