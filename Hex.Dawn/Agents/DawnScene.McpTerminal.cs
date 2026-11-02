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
            TerminalRun, Timeout: 610.0f)
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

            if (!bottom.Shown || bottom.Selected != CONSOLE_PAGE)
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
        Match done = Regex.Match(text, Regex.Escape(marker) + @"_(-?\d+)_");
        int typed = text.IndexOf(marker, StringComparison.Ordinal);

        // What was typed wrapped right through the marker, so where the output starts isn't known, the lines before
        // the code have to do
        if (typed < 0 || (done.Success && typed == done.Index))
        {
            if (!done.Success)
                return (string.Empty, null);
            string[] before = text[..done.Index].Split('\n');
            return (string.Join('\n', before.Skip(Math.Max(0, before.Length - 41)).SkipLast(1).Select(line => line.TrimEnd())).Trim('\n'), int.Parse(done.Groups[1].Value));
        }

        // A long command wraps, the output starts after the line with the end of what was typed on it
        int from = text.IndexOf('\n', typed);
        if (from < 0)
            return (string.Empty, null);

        if (!done.Success || done.Index <= typed)
            return (text[(from + 1)..].TrimEnd(), null);

        int to = text.LastIndexOf('\n', done.Index);
        string output = to > from ? text[(from + 1)..to] : string.Empty;
        return (string.Join('\n', output.Split('\n').Select(line => line.TrimEnd())).Trim('\n'), int.Parse(done.Groups[1].Value));
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
        if (command.Length == 0)
        {
            yield return McpToolResult.Error("Say the command line in command.");
            yield break;
        }

        TerminalTab? tab = McpTab(arguments.String("tab"));
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
            yield return McpToolResult.Error("No shell is running in that tab, hex_terminal new opens one.");
            yield break;
        }

        string marker = $"__HEX_{Guid.NewGuid().ToString("N")[..8]}";
        view.Send(McpMarked(command, McpShellOf(tab.Profile), marker) + "\r");

        long until = System.Environment.TickCount64 + (long)(Math.Clamp(arguments.Number("timeout") ?? 120.0, 1.0, 600.0) * 1000.0);
        (string Output, int? Exit) ran = (string.Empty, null);
        while (System.Environment.TickCount64 < until)
        {
            ran = McpRunOutput(view.Screen.AllText(), marker);
            if (ran.Exit is not null || view.Process is not { IsRunning: true })
                break;
            yield return null;
        }

        int most = Math.Clamp(arguments.Integer("max_chars") ?? 6000, 400, 200_000);
        string output = McpMiddleCut(ran.Output, most);
        yield return McpToolResult.Text(ran.Exit is { } exit
            ? $"Exit {exit} in \"{tab.Title}\".\n{output}"
            : $"Still going in \"{tab.Title}\" after the timeout, left running (hex_terminal interrupt stops it). So far:\n{output}");
    }
}
