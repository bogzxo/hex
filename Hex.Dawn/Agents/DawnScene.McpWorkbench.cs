using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// The tools for the rest of the editor. The command palette's commands and running one, the terminal of the panel, the
// tasks, the settings, waiting for something to happen, and the self test, run in a Hex of its own since it drives the
// editor about and writes files, which a Hex somebody's working in is no place for.
internal sealed partial class DawnScene
{
    private IEnumerable<HexMcpTool> WorkbenchTools() =>
    [
        new(new McpTool("hex_commands", "List commands",
            "List the commands of the command palette (Ctrl+Shift+P) that can run right now, with their menu and keys.",
            McpSchema.Object([("find", McpSchema.String("Only commands whose name contains this."))]), ReadOnly: true),
            Commands),

        new(new McpTool("hex_run_command", "Run a command",
            "Run a command of the command palette by its name (as hex_commands lists it, \"File: Save\" or just \"Save\").",
            McpSchema.Object([("name", McpSchema.String("The command."))], "name")),
            RunCommand),

        new(new McpTool("hex_terminal_read", "Read the terminal",
            "Read the text of the terminal in the panel under the editor (the focused one, or a task's tab).",
            McpSchema.Object([
                ("lines", McpSchema.Integer("The last this many lines, 60 by default.")),
                ("scrollback", McpSchema.Flag("Include what has scrolled off the top."))]), ReadOnly: true),
            TerminalRead),

        new(new McpTool("hex_terminal_send", "Type into the terminal",
            "Send a line to the terminal in the panel (opening it with a shell if none is up) and give back what it shows after a moment.",
            McpSchema.Object([
                ("text", McpSchema.String("What to send.")),
                ("enter", McpSchema.Flag("Press Enter after it, true by default.")),
                ("wait", McpSchema.Number("Seconds to wait before reading the screen, 1 by default."))], "text")),
            TerminalSend, Timeout: 60.0f),

        new(new McpTool("hex_run_task", "Run a task",
            "Run a task of the workspace's tasks.hor by name (build, test, run...), in its terminal tab. Without a name, list the tasks.",
            McpSchema.Object([("name", McpSchema.String("The task."))])),
            RunTask),

        new(new McpTool("hex_task_output", "Task output",
            "Read what a task printed in its terminal tab, and whether it is still going or how it ended.",
            McpSchema.Object([
                ("name", McpSchema.String("The task.")),
                ("lines", McpSchema.Integer("The last this many lines, 200 by default."))], "name"), ReadOnly: true),
            TaskOutput),

        new(new McpTool("hex_settings_get", "Read settings",
            "Read Hex's settings, one by its key with what it does, or all of them (optionally narrowed by a word).",
            McpSchema.Object([
                ("key", McpSchema.String("A setting's key, like minimap or tab_size.")),
                ("find", McpSchema.String("Only settings whose key or title contains this."))]), ReadOnly: true),
            SettingsGet),

        new(new McpTool("hex_settings_set", "Change a setting",
            "Change one of Hex's settings by its key, the way its row in the settings window would. Kept in the user's settings file.",
            McpSchema.Object([
                ("key", McpSchema.String("The setting's key.")),
                ("value", McpSchema.Any("The value, a boolean, number or string as the setting is."))], "key", "value")),
            SettingsSet),

        new(new McpTool("hex_wait_for", "Wait for",
            "Wait until something is true, Hex idle, a file open, a dialog up or gone, some text on screen or gone, a status bar message, problems listed (for a file), a task finished, with a timeout.",
            McpSchema.Object([
                ("condition", McpSchema.OneOf("What to wait for.", "idle", "file_open", "dialog", "no_dialog", "text", "text_gone", "status", "problems", "task_done")),
                ("value", McpSchema.String("The file, text or message, for the ones that need it (a file for problems is optional).")),
                ("timeout", McpSchema.Number("Seconds, 10 by default."))], "condition")),
            WaitFor),

        new(new McpTool("hex_selftest", "Run self tests",
            "Run some of Hex's own self test suites (code, layout, workspace, terminal...) in a separate Hex window and report passes and failures. Takes minutes.",
            McpSchema.Object([
                ("suites", McpSchema.String("Suite names separated by spaces, \"list\" to list them.")),
                ("timeout", McpSchema.Number("Seconds to give it, 600 by default."))], "suites")),
            null, RunAway: SelfTestRun)
    ];

    /// <summary>Every tool, in the order tools/list says them.</summary>
    internal List<HexMcpTool> McpToolList() => [.. ScreenTools(), .. EditorTools(), .. WorkbenchTools(), .. NotebookTools()];

    /// <summary>A command as a line, its menu, its name and its keys.</summary>
    private static string McpCommandLine(UIChoice choice) =>
        (choice.Quiet.Length > 0 ? $"{choice.Quiet}: " : string.Empty) + Plain(choice.Text) + (choice.Keys.Length > 0 ? $"  ({choice.Keys})" : string.Empty);

    private IEnumerable<McpToolResult?> Commands(JsonObject arguments)
    {
        string? find = arguments.String("find");
        var lines = PaletteChoices().Select(McpCommandLine).Where(line => find is null || line.Contains(find, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        yield return McpToolResult.Text($"{lines.Count} commands.\n{string.Join('\n', lines)}");
    }

    private IEnumerable<McpToolResult?> RunCommand(JsonObject arguments)
    {
        string name = Plain(arguments.String("name") ?? string.Empty);
        List<UIChoice> all = PaletteChoices();

        // Exactly as listed, then by its name alone, then the only one that has it in its name
        UIChoice? chosen = all.Find(choice => McpCommandLine(choice).Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? all.Find(choice => $"{choice.Quiet}: {Plain(choice.Text)}".Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? all.Find(choice => Plain(choice.Text).Equals(name, StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            var near = all.Where(choice => McpCommandLine(choice).Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (near.Count == 1)
                chosen = near[0];
            else
            {
                yield return McpToolResult.Error(near.Count == 0
                    ? $"No command called \"{name}\" can run right now. hex_commands lists the ones that can."
                    : $"\"{name}\" could be {near.Count} commands, say which.\n{string.Join('\n', near.Take(20).Select(McpCommandLine))}");
                yield break;
            }
        }

        long saidBefore = saidAt;
        chosen.Picked();
        for (int settle = 0; settle < 4; settle++)
            yield return null;

        string said = saidAt != saidBefore && messageItem.Text.Length > 0 ? $" Hex said \"{Plain(messageItem.Text)}\"." : string.Empty;
        string dialog = chrome.Dialog is { IsOpen: true } ? " A dialog is up, hex_ui_tree shows it." : string.Empty;
        yield return McpToolResult.Text($"Ran {McpCommandLine(chosen)}.{said}{dialog}");
    }

    /// <summary>The last lines of a terminal's text, the scrollback too when asked.</summary>
    private static string McpTerminalText(TerminalView view, int lines, bool scrollback)
    {
        string all = scrollback ? view.Screen.AllText() : view.Screen.Text();
        string[] split = all.Replace("\r", string.Empty).TrimEnd().Split('\n');
        return string.Join('\n', split.Skip(Math.Max(0, split.Length - lines)).Select(line => line.TrimEnd()));
    }

    private IEnumerable<McpToolResult?> TerminalRead(JsonObject arguments)
    {
        if (terminalPanel.FocusedView is not { } view)
        {
            yield return McpToolResult.Error("No terminal is open in the panel. hex_terminal_send opens one.");
            yield break;
        }

        string title = terminalPanel.Current?.Title ?? "terminal";
        yield return McpToolResult.Text($"The terminal \"{title}\" ({(view.Process is { IsRunning: true } ? "running" : "ended")}):\n"
            + McpTerminalText(view, Math.Clamp(arguments.Integer("lines") ?? 60, 1, 5000), arguments.Flag("scrollback")));
    }

    private IEnumerable<McpToolResult?> TerminalSend(JsonObject arguments)
    {
        // The page brought up, which starts a shell the first time, waited for until the shell's there
        if (!bottom.Shown || bottom.Selected != CONSOLE_PAGE || terminalPanel.FocusedView is null)
            ShowPanel(CONSOLE_PAGE);

        long giveUp = System.Environment.TickCount64 + 15_000;
        while (System.Environment.TickCount64 < giveUp && terminalPanel.FocusedView is not { Process.IsRunning: true })
            yield return null;

        if (terminalPanel.FocusedView is not { } view)
        {
            yield return McpToolResult.Error("No terminal came up in the panel.");
            yield break;
        }

        string text = arguments.String("text") ?? string.Empty;
        view.Send(arguments.Flag("enter", true) ? text + "\r" : text);
        long until = System.Environment.TickCount64 + (long)(Math.Clamp(arguments.Number("wait") ?? 1.0, 0.0, 30.0) * 1000.0);
        while (System.Environment.TickCount64 < until)
            yield return null;

        yield return McpToolResult.Text($"Sent it to \"{terminalPanel.Current?.Title ?? "terminal"}\". It shows:\n{McpTerminalText(view, 40, scrollback: false)}");
    }

    private IEnumerable<McpToolResult?> RunTask(JsonObject arguments)
    {
        List<HexTask> all = tasks.Tasks();
        string? name = arguments.String("name");
        if (name is null)
        {
            yield return McpToolResult.Text(all.Count == 0
                ? "The workspace has no tasks.hor, or nothing in it."
                : $"{all.Count} tasks.\n{string.Join('\n', all.Select(task => $"{task.Name}  ({task.Command}, in {task.WorkingFolder()}{(task.Group.Length > 0 ? $", {task.Group}" : string.Empty)})"))}");
            yield break;
        }

        if (all.Find(task => task.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } found)
        {
            yield return McpToolResult.Error($"There's no task called {name}. The tasks are {(all.Count == 0 ? "none" : string.Join(", ", all.Select(task => task.Name)))}.");
            yield break;
        }

        tasks.Run(found);
        for (int settle = 0; settle < 4; settle++)
            yield return null;
        yield return McpToolResult.Text($"Running {found.Name} ({found.Command}) in its terminal tab. hex_task_output reads it, hex_wait_for or hex_task_output again to see it end.");
    }

    private IEnumerable<McpToolResult?> TaskOutput(JsonObject arguments)
    {
        string name = arguments.String("name") ?? string.Empty;
        HexTask? found = tasks.Tasks().Find(task => task.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        string? tag = found is null ? null : HexTasks.TabOf(found);
        TerminalTab? tab = tag is null ? null : terminalPanel.Tabs.FirstOrDefault(each => Equals(each.Tag, tag));
        if (found is null || tab?.FocusedView is not { } view)
        {
            yield return McpToolResult.Error(found is null ? $"There's no task called {name}." : $"{name} hasn't run since Hex started, or its tab was closed.");
            yield break;
        }

        string state = tasks.Exits.TryGetValue(tag!, out int exit) ? (exit == 0 ? "It ended fine (exit 0)." : $"It failed (exit {exit}).") : "It's still going.";
        yield return McpToolResult.Text($"{found.Name}, {state}\n{McpTerminalText(view, Math.Clamp(arguments.Integer("lines") ?? 200, 1, 5000), scrollback: true)}");
    }

    /// <summary>The settings an agent can read and change, the editor's, its text's and the terminal's. Not Windows's own.</summary>
    private List<ISettingsSource> McpSettingsSources()
    {
        var sources = new List<ISettingsSource> { new EditorSource(this) };
        sources.AddRange(settingSources.Select(added => added.Source()));
        sources.Add(new TerminalSettingsSource(terminals, Hex.Shell.EditorSettingsCatalog.THEME));
        return sources;
    }

    private static string McpValue(IRuntimeValue? value) => value switch
    {
        null => "(nothing)",
        BooleanValue flag => flag.Value ? "true" : "false",
        NumberValue number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        StringValue text => JsonValue.Create(text.Value).ToJsonString(),
        _ => value.ToString() ?? string.Empty
    };

    private IEnumerable<McpToolResult?> SettingsGet(JsonObject arguments)
    {
        var said = new StringBuilder();
        string? key = arguments.String("key"), find = arguments.String("find");
        foreach (ISettingsSource source in McpSettingsSources())
        {
            foreach (UISetting setting in source.Settings)
            {
                if (setting.ButtonsOnly)
                    continue;

                if (key is not null)
                {
                    if (setting.Key != key)
                        continue;

                    string choices = setting.Choices is { Length: > 0 } listed ? $"\nOne of {string.Join(", ", listed)}." : string.Empty;
                    yield return McpToolResult.Text($"{setting.Key} = {McpValue(source.Get(setting.Key))} ({setting.Kind}, {source.Section} section, comes as {McpValue(source.Default(setting.Key))}"
                        + $"{(source.IsModified(setting.Key) ? ", changed" : string.Empty)}).\n{setting.Title}. {Plain(setting.Help)}{choices}");
                    yield break;
                }

                if (find is not null && !setting.Key.Contains(find, StringComparison.OrdinalIgnoreCase) && !setting.Title.Contains(find, StringComparison.OrdinalIgnoreCase))
                    continue;

                said.Append($"{setting.Key} = {McpValue(source.Get(setting.Key))}  ({setting.Title}{(source.IsModified(setting.Key) ? ", changed" : string.Empty)})\n");
            }
        }

        yield return key is not null
            ? McpToolResult.Error($"There's no setting {key}. hex_settings_get with find looks for one.")
            : McpToolResult.Text(said.Length > 0 ? said.ToString() : "No setting matches.");
    }

    private IEnumerable<McpToolResult?> SettingsSet(JsonObject arguments)
    {
        string key = arguments.String("key") ?? string.Empty;
        if (key == "mcp_server")
        {
            yield return McpToolResult.Error("mcp_server is the door you came in by, it's only changed by hand.");
            yield break;
        }

        foreach (ISettingsSource source in McpSettingsSources())
        {
            if (source.Settings.FirstOrDefault(setting => setting.Key == key && !setting.ButtonsOnly) is not { } setting)
                continue;

            JsonNode? given = arguments["value"];
            IRuntimeValue? value = (setting.Kind, given?.GetValueKind()) switch
            {
                (UIFormKind.Flag, JsonValueKind.True or JsonValueKind.False) => Values.Bool(given!.GetValue<bool>()),
                (UIFormKind.Number or UIFormKind.Whole, JsonValueKind.Number) => new NumberValue((float)given!.GetValue<double>()),
                (UIFormKind.Text or UIFormKind.Choice, JsonValueKind.String) => new StringValue(given!.GetValue<string>()),
                _ => null
            };

            if (value is null)
            {
                yield return McpToolResult.Error($"{key} is a {setting.Kind} setting, give it {(setting.Kind switch { UIFormKind.Flag => "true or false", UIFormKind.Number or UIFormKind.Whole => "a number", UIFormKind.Text or UIFormKind.Choice => "a string", _ => "a value hex_settings_set can't make, change it in the settings window" })}.");
                yield break;
            }

            if (setting.Kind == UIFormKind.Choice && setting.Choices is { Length: > 0 } choices && !choices.Contains(((StringValue)value).Value))
            {
                yield return McpToolResult.Error($"{key} is one of {string.Join(", ", choices)}.");
                yield break;
            }

            source.Set(key, value);
            settingsView?.Refresh();
            yield return null;
            yield return McpToolResult.Text($"{key} = {McpValue(source.Get(key))} now.");
            yield break;
        }

        yield return McpToolResult.Error($"There's no setting {key}. hex_settings_get with find looks for one.");
    }

    private IEnumerable<McpToolResult?> WaitFor(JsonObject arguments)
    {
        string condition = arguments.String("condition") ?? "idle";
        string value = arguments.String("value") ?? string.Empty;
        if (condition is "file_open" or "text" or "text_gone" or "status" or "task_done" && value.Length == 0)
        {
            yield return McpToolResult.Error($"Waiting for {condition} needs a value.");
            yield break;
        }

        string? path = condition == "file_open" ? McpPath(value) : null;
        int quietTicks = 0;
        bool Met()
        {
            switch (condition)
            {
                case "idle":
                    quietTicks = Working || UIKeyboard.Room < 256 ? 0 : quietTicks + 1;
                    return quietTicks >= 3;
                case "file_open":
                    return document.Path is { } now && PlatformPaths.Same(Path.GetFullPath(now), path!);
                case "dialog":
                    return chrome.Dialog is { IsOpen: true } || SettingsWindow is not null;
                case "no_dialog":
                    return chrome.Dialog is not { IsOpen: true };
                case "text":
                    return McpFindText(value, out _) is not null;
                case "text_gone":
                    return McpFindText(value, out _) is null;
                case "status":
                    return Plain(messageItem.Text).Contains(value, StringComparison.OrdinalIgnoreCase);
                case "task_done":
                    return tasks.Tasks().Find(task => task.Name.Equals(value, StringComparison.OrdinalIgnoreCase)) is { } ran && tasks.Exits.ContainsKey(HexTasks.TabOf(ran));
                case "problems":
                    GatherProblems();
                    return value.Length == 0 ? problemList.Count > 0 : problemList.Any(problem => Path.GetFileName(problem.Path).Equals(Path.GetFileName(value), StringComparison.OrdinalIgnoreCase) || problem.Path.Contains(value, StringComparison.OrdinalIgnoreCase));
                default:
                    return true;
            }
        }

        var waited = Stopwatch.StartNew();
        double timeout = Math.Clamp(arguments.Number("timeout") ?? 10.0, 0.1, 600.0);
        while (!Met())
        {
            if (waited.Elapsed.TotalSeconds > timeout)
            {
                yield return McpToolResult.Error($"{condition}{(value.Length > 0 ? $" \"{value}\"" : string.Empty)} wasn't true after {timeout:0.#} seconds.");
                yield break;
            }

            yield return null;
        }

        yield return McpToolResult.Text($"{condition}{(value.Length > 0 ? $" \"{value}\"" : string.Empty)} after {waited.Elapsed.TotalSeconds:0.00} seconds.");
    }

    /// <summary>
    /// The self test of a few suites in a Hex of its own, the way --selftest runs it, and what it printed boiled down to
    /// the tally and what failed. Its window comes up and goes again by itself. Off the editor's thread start to end.
    /// </summary>
    private static async Task<McpToolResult> SelfTestRun(JsonObject arguments, CancellationToken cancel)
    {
        string[] suites = (arguments.String("suites") ?? string.Empty).Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (suites.Length == 0)
            return McpToolResult.Error("Say which suites, \"list\" lists them.");

        bool list = suites is ["list"];
        var start = new ProcessStartInfo(System.Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Hex.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };

        start.ArgumentList.Add("--selftest");
        foreach (string suite in list ? ["nothing-of-that-name"] : suites)
            start.ArgumentList.Add(suite);
        start.ArgumentList.Add("--exit");
        start.Environment["HORIZON_AUDIO"] = "off";

        var lines = new List<string>();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, line) => { if (line.Data is { } said) lock (lines) lines.Add(said); };
        process.ErrorDataReceived += (_, line) => { if (line.Data is { } said) lock (lines) lines.Add(said); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(arguments.Number("timeout") ?? 600.0, 10.0, 3600.0)));
        bool ended = true;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            ended = false;
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
        }

        string[] said;
        lock (lines)
            said = [.. lines.Where(line => line.StartsWith("[Hex self-test]", StringComparison.Ordinal)).Select(line => line["[Hex self-test]".Length..].Trim())];

        if (list)
            return McpToolResult.Text($"The suites are\n{string.Join('\n', said.SkipWhile(line => !line.StartsWith("there is no", StringComparison.Ordinal)).Skip(1))}");

        string[] failed = [.. said.Where(line => line.StartsWith("FAIL", StringComparison.Ordinal) || line.StartsWith("step threw", StringComparison.Ordinal))];
        string tally = said.LastOrDefault(line => line.Contains("checks passed", StringComparison.Ordinal)) ?? "no tally, it didn't get to the end";
        var report = new StringBuilder($"{string.Join(' ', suites)}: {tally}{(ended ? $", exit code {process.ExitCode}" : ", stopped at the timeout")}.\n");
        foreach (string line in said.Where(line => line.EndsWith(" s", StringComparison.Ordinal) && line.Contains(" took ", StringComparison.Ordinal)))
            report.Append(line).Append('\n');
        if (failed.Length > 0)
            report.Append($"{failed.Length} failed\n").AppendJoin('\n', failed);

        return ended && process.ExitCode == 0 ? McpToolResult.Text(report.ToString()) : McpToolResult.Error(report.ToString());
    }
}
