using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The terminal for an agent, all of it rather than the focused tab. Its tabs listed, opened with a shell and a folder,
// picked, renamed, closed and interrupted, and a command run in one and waited for until it's done. Done is when a
// marker the command echoes after itself comes out with the exit code in it, the one way that works in every shell,
// marks or none. What comes back is what that command printed and nothing above it, cut in the middle when it's long
// since the start and the end of a build's output are what anybody reads.
internal sealed partial class DawnScene
{
    private IEnumerable<HexMcpTool> TerminalTools() =>
    [
        new(new McpTool("hex_terminal", "Terminal tabs",
            "The terminal's tabs. list says each (number, title, shell, running or ended, which is current) and the shells a new one can be, new opens one (a shell by name, a folder), select, rename, close and interrupt (Ctrl+C) act on one by its number or title, the current one without.",
            McpSchema.Object([
                ("action", McpSchema.OneOf("What to do.", "list", "new", "select", "rename", "close", "interrupt")),
                ("tab", McpSchema.String("The tab, its number from 1 or part of its title.")),
                ("shell", McpSchema.String("For new, the shell by part of its name, as list says them.")),
                ("folder", McpSchema.String("For new, the folder it starts in.")),
                ("title", McpSchema.String("For rename, the new title."))], "action")),
            TerminalTabs),

        new(new McpTool("hex_terminal_run", "Run in the terminal",
            "Run a command in a terminal tab (the current one, or a new shell when none is up) and wait until it's done, then give back what it printed and its exit code. Long output is cut in the middle. For a command that never ends (a server, a watcher) use hex_terminal_send.",
            McpSchema.Object([
                ("command", McpSchema.String("The command line.")),
                ("tab", McpSchema.String("The tab, its number from 1 or part of its title.")),
                ("timeout", McpSchema.Number("Seconds to wait at the most, 120 by default. A command still going then is left running.")),
                ("max_chars", McpSchema.Integer("The most of its output handed back, 6000 by default."))], "command")),
            TerminalRun, Timeout: 610.0f, TakesCaller: true)
    ];

    // Helper method for the tab an agent named, by number from 1 or part of its title, the current one when it named none
    private TerminalTab? McpTab(string? named)
    {
        if (string.IsNullOrWhiteSpace(named))
            return terminalPanel.Current;
        if (int.TryParse(named, out int number))
            return number >= 1 && number <= terminalPanel.Tabs.Count ? terminalPanel.Tabs[number - 1] : null;
        return terminalPanel.Tabs.FirstOrDefault(tab => tab.Title.Contains(named, StringComparison.OrdinalIgnoreCase));
    }

    private string McpTabLine(TerminalTab tab) =>
        $"{terminalPanel.IndexOf(tab) + 1}. {tab.Title}{(tab == terminalPanel.Current ? " (current)" : string.Empty)}  {tab.Profile?.Name ?? "shell"}, {(tab.FocusedView?.Process is { IsRunning: true } ? "running" : "ended")}";

    private IEnumerable<McpToolResult?> TerminalTabs(JsonObject arguments)
    {
        string action = arguments.String("action") ?? "list";
        if (action == "new")
        {
            TerminalProfile? profile = null;
            if (arguments.String("shell") is { Length: > 0 } shell && (profile = terminalPanel.Profiles.FirstOrDefault(each => each.Name.Contains(shell, StringComparison.OrdinalIgnoreCase))) is null)
            {
                yield return McpToolResult.Error($"There's no shell called {shell}, they are {string.Join(", ", terminalPanel.Profiles.Select(each => each.Name))}.");
                yield break;
            }

            if (!PanelOpen || bottom.Selected != CONSOLE_PAGE)
                ShowPanel(CONSOLE_PAGE);
            TerminalTab made = terminalPanel.NewTab(profile, arguments.String("folder") is { } folder ? McpPath(folder) : null);
            long giveUp = System.Environment.TickCount64 + 15_000;
            while (System.Environment.TickCount64 < giveUp && made.FocusedView?.Process is not { IsRunning: true })
                yield return null;

            yield return McpToolResult.Text($"Opened {McpTabLine(made)}.");
            yield break;
        }

        if (action == "list")
        {
            var said = new StringBuilder(terminalPanel.Tabs.Count == 0 ? "No terminal tabs are open.\n" : $"{terminalPanel.Tabs.Count} tabs.\n");
            foreach (TerminalTab tab in terminalPanel.Tabs)
                said.Append(McpTabLine(tab)).Append('\n');
            said.Append($"Shells for new, {string.Join(", ", terminalPanel.Profiles.Select(each => each.Name))}.\n");
            yield return McpToolResult.Text(said.ToString());
            yield break;
        }

        if (McpTab(arguments.String("tab")) is not { } chosen)
        {
            yield return McpToolResult.Error($"There's no tab {arguments.String("tab") ?? "open"}. hex_terminal list says them.");
            yield break;
        }

        switch (action)
        {
            case "select":
                ShowPanel(CONSOLE_PAGE);
                terminalPanel.Select(chosen);
                yield return null;
                yield return McpToolResult.Text($"Now on {McpTabLine(chosen)}.");
                break;

            case "rename":
                terminalPanel.Rename(chosen, arguments.String("title"));
                yield return McpToolResult.Text($"Renamed, {McpTabLine(chosen)}.");
                break;

            case "close":
                string was = chosen.Title;
                terminalPanel.CloseTab(chosen);
                yield return null;
                yield return McpToolResult.Text($"Closed {was}, {terminalPanel.Tabs.Count} tabs left.");
                break;

            default:
                chosen.FocusedView?.Send("\x03");
                for (int settle = 0; settle < 10; settle++)
                    yield return null;
                yield return McpToolResult.Text($"Sent Ctrl+C to {chosen.Title}. It shows:\n{(chosen.FocusedView is { } view ? McpTerminalText(view, 15, scrollback: false) : string.Empty)}");
                break;
        }
    }

    /// <summary>
    /// What a shell is by its command, which decides how the marker's written. Git Bash, WSL and the rest of the sh
    /// family, PowerShell, or cmd, PowerShell when nothing says (it's what Windows starts).
    /// </summary>
    internal static string McpShellOf(TerminalProfile? profile)
    {
        string command = $"{profile?.Command} {profile?.Name}".ToLowerInvariant();
        if (command.Contains("cmd"))
            return "cmd";
        if (command.Contains("bash") || command.Contains("wsl") || command.Contains("zsh") || command.Contains("/sh") || command.Contains("fish"))
            return "sh";
        if (command.Contains("pwsh") || command.Contains("powershell"))
            return "pwsh";
        return OperatingSystem.IsWindows() ? "pwsh" : "sh";
    }

    /// <summary>
    /// Git told not to page what it prints, wherever a statement of the line starts with it. A pager waits on a key nobody
    /// presses and the call never sees its exit (git diff did). Put in the line rather than the shell's environment, which
    /// is the person's own terminal and keeps its pager for them.
    /// </summary>
    internal static string McpNoPager(string command) =>
        GitPaged().Replace(command, "$1git --no-pager$2");

    // A statement of a command line that starts with git and hasn't said --no-pager
    [GeneratedRegex(@"(^|[;&|(]\s*)git(\s+)(?!--no-pager)")]
    private static partial Regex GitPaged();

    /// <summary>A command line with a marker echoed after it that says its exit code, the way the shell writes that.</summary>
    internal static string McpMarked(string command, string shell, string marker) => shell switch
    {
        "cmd" => $"{command} & call echo {marker}_%^errorlevel%_",
        "pwsh" => $"{command}; echo \"{marker}_$(if ($?) {{ 0 }} elseif ($LASTEXITCODE) {{ $LASTEXITCODE }} else {{ 1 }})_\"",
        _ => $"{command}; echo \"{marker}_$?_\""
    };

    /// <summary>
    /// What a command printed out of the terminal's whole text, between the line it was typed on (the first with the
    /// marker in it) and the marker it echoed with its exit code, null with no code while it's still going.
    /// </summary>
    internal static (string Output, int? Exit) McpRunOutput(string all, string marker)
    {
        string text = all.Replace("\r", string.Empty);
        int done = McpExitAt(text, marker, out int code);
        int typed = text.IndexOf(marker, StringComparison.Ordinal);

        // What was typed wrapped right through the marker, so where the output starts isn't known, the lines before
        // the code have to do
        if (typed < 0 || (done >= 0 && typed == done))
        {
            if (done < 0)
                return (string.Empty, null);
            string[] before = text[..done].Split('\n');
            return (string.Join('\n', before.Skip(Math.Max(0, before.Length - 41)).SkipLast(1).Select(line => line.TrimEnd())).Trim('\n'), code);
        }

        // A long command wraps, the output starts after the line with the end of what was typed on it
        int from = text.IndexOf('\n', typed);
        if (from < 0)
            return (string.Empty, null);

        if (done < 0 || done <= typed)
            return (text[(from + 1)..].TrimEnd(), null);

        int to = text.LastIndexOf('\n', done);
        string output = to > from ? text[(from + 1)..to] : string.Empty;
        return (string.Join('\n', output.Split('\n').Select(line => line.TrimEnd())).Trim('\n'), code);
    }

    /// <summary>
    /// Whether McpRunOutput would find the command done, its exit code come out after the marker, with nothing cut out or
    /// joined. Asked every tick a command runs, where the whole of its output was made again each time to find it wasn't.
    /// </summary>
    internal static bool McpRunEnded(string all, string marker)
    {
        string text = all.Replace("\r", string.Empty);
        int done = McpExitAt(text, marker, out _);
        if (done < 0)
            return false;
        int typed = text.IndexOf(marker, StringComparison.Ordinal);
        return typed == done || text.IndexOf('\n', typed) >= 0;
    }

    /// <summary>
    /// Where the marker first comes out with an exit code after it, marker_0_ or marker_-1_, -1 while it hasn't. What the
    /// command line echoes it with (_%^errorlevel%_, _$?_) has no number there and isn't taken. A regex made for each
    /// marker did this, parsed anew for every command.
    /// </summary>
    private static int McpExitAt(string text, string marker, out int code)
    {
        code = 0;
        for (int at = text.IndexOf(marker, StringComparison.Ordinal); at >= 0; at = text.IndexOf(marker, at + 1, StringComparison.Ordinal))
        {
            int start = at + marker.Length + 1;
            if (start > text.Length || text[start - 1] != '_')
                continue;
            int digits = start < text.Length && text[start] == '-' ? start + 1 : start;
            int end = digits;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
                end++;
            if (end == digits || end >= text.Length || text[end] != '_')
                continue;
            code = int.Parse(text.AsSpan(start, end - start), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture);
            return at;
        }

        return -1;
    }

    /// <summary>Long output cut in the middle, its start and end kept and how much went said in between.</summary>
    internal static string McpMiddleCut(string output, int most)
    {
        if (output.Length <= most)
            return output;

        int half = Math.Max(200, most / 2 - 60);
        string start = output[..half], end = output[^half..];
        int cut = output.Length - 2 * half;
        int lines = output.AsSpan(half, cut).Count('\n');
        return $"{start}\n... {cut} characters ({lines} lines) cut from the middle ...\n{end}";
    }

    private IEnumerable<McpToolResult?> TerminalRun(JsonObject arguments)
    {
        string command = (arguments.String("command") ?? string.Empty).Trim();

        // A read or a search of the workspace by the shell, turned back as the agent's own shell is, or an agent kept
        // off grep and cat by its guard ran them here instead (Codex did, through this very tool)
        string? privateRoot = LinkPrivateAgentRoot(CallerOf(arguments));
        string[] roots = privateRoot is null ? [.. explorer.workspace.Folders] : [privateRoot, .. explorer.workspace.Folders];
        if (HexHarnessGuard.ForCommand(command, roots) is { } instead)
        {
            yield return McpToolResult.Error($"This reads the workspace the long way, Hex answers it from its index, use {instead}. hex_terminal_run is for builds, tests, git and anything outside the workspace.");
            yield break;
        }

        if (command.Length == 0)
        {
            yield return McpToolResult.Error("Say the command line in command.");
            yield break;
        }

        if (privateRoot is not null && arguments.String("tab") is { Length: > 0 })
        {
            yield return McpToolResult.Error("A private worktree command starts in its own shell. Leave tab unset.");
            yield break;
        }

        TerminalTab? tab = privateRoot is null ? McpTab(arguments.String("tab")) : terminalPanel.NewTab(workingDirectory: privateRoot);
        if (privateRoot is not null)
        {
            long giveUp = System.Environment.TickCount64 + 15_000;
            while (System.Environment.TickCount64 < giveUp && tab?.FocusedView?.Process is not { IsRunning: true })
                yield return null;
            // The tab is the agent's own: a command left running in it can be read, sent to and interrupted by name
            if (tab is not null && CallerOf(arguments) is { } owner)
                LinkRememberPrivateTab(owner, tab.Title);
        }
        if (arguments.String("tab") is { Length: > 0 } named && tab is null)
        {
            yield return McpToolResult.Error($"There's no tab {named}. hex_terminal list says them.");
            yield break;
        }

        if (tab is null)
        {
            ShowPanel(CONSOLE_PAGE);
            long giveUp = System.Environment.TickCount64 + 15_000;
            while (System.Environment.TickCount64 < giveUp && terminalPanel.FocusedView is not { Process.IsRunning: true })
                yield return null;
            tab = terminalPanel.Current;
        }

        if (tab?.FocusedView is not { Process.IsRunning: true } view)
        {
            if (privateRoot is not null && tab is not null)
                terminalPanel.CloseTab(tab);
            yield return McpToolResult.Error("No shell is running in that tab, hex_terminal new opens one.");
            yield break;
        }

        string marker = $"__HEX_{Guid.NewGuid().ToString("N")[..8]}";
        // Whatever was left on the shell's line is cleared first, Escape in PowerShell and cmd and Ctrl+U in the others.
        // A key that landed in the terminal before went in front of the command (".git", "wgit") and broke it
        string shell = McpShellOf(tab.Profile);
        view.Send(shell == "sh" ? "\u0015" : "\u001b");
        view.Send(McpMarked(McpNoPager(command), shell, marker) + "\r");

        long until = System.Environment.TickCount64 + (long)(Math.Clamp(arguments.Number("timeout") ?? 120.0, 1.0, 600.0) * 1000.0);
        // Each tick only asks whether it's done, what it printed is cut out of the text once, as the text was when the wait ended
        string seen = string.Empty;
        while (System.Environment.TickCount64 < until)
        {
            seen = view.Screen.AllText();
            if (McpRunEnded(seen, marker) || view.Process is not { IsRunning: true })
                break;
            yield return null;
        }
        (string Output, int? Exit) ran = McpRunOutput(seen, marker);

        int most = Math.Clamp(arguments.Integer("max_chars") ?? 6000, 400, 200_000);
        string output = McpMiddleCut(ran.Output, most);
        if (privateRoot is not null && (ran.Exit is not null || view.Process is not { IsRunning: true }))
            terminalPanel.CloseTab(tab);
        yield return McpToolResult.Text(ran.Exit is { } exit
            ? $"Exit {exit} in \"{tab.Title}\".\n{output}"
            : $"Still going in \"{tab.Title}\" after the timeout, left running (hex_terminal interrupt stops it). So far:\n{output}");
    }
}
