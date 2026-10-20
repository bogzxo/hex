using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// Debugging C#, VS Code's way and VS Code's keys. F9 (or a click in the gutter) puts a breakpoint on a line of a .cs file,
// F5 builds the project the file is in (the workspace's when there's one) and runs it under netcoredbg (Samsung's, MIT,
// a debugger that talks the Debug Adapter Protocol, the engine's DebugSession), and where it stops the file comes up with
// the line lit and the Debug side has the stack and the variables. F5 goes on, F10 steps over, F11 in, Shift+F11 out,
// Shift+F5 stops it, Ctrl+Shift+F5 starts it again. What it writes goes in the output. netcoredbg is looked for where the
// settings say (debugger_path), next to the exe (netcoredbg/netcoredbg.exe), in the settings folder and on the PATH.
// Microsoft's own debugger comes with VS Code's C# extension and is only allowed in Microsoft's editors, so not that.
internal sealed partial class HexScene
{
    // The breakpoints, by the file's whole path, the lines counted from 0. An editor of the file is handed the set
    private readonly Dictionary<string, SortedSet<int>> breakpoints = new(StringComparer.OrdinalIgnoreCase);

    private DebugSession? debugging;
    private string debuggedProject = string.Empty;

    // Where it stopped, the frames of the stack and the one looked at
    private IReadOnlyList<DebugFrame> stoppedFrames = [];
    private int stoppedFrame;

    // Work done off the editor's thread (the build, the launch, the variables asked for) handed back to it
    private readonly ConcurrentQueue<Action> debugWork = new();
    private bool building;

    /// <summary>Whether a program is being debugged, from the launch to its end.</summary>
    private bool Debugging => debugging is { State: not DebugState.Ended } || building;

    /// <summary>Whether it's stopped somewhere, where the steps mean something.</summary>
    private bool DebugStopped => debugging is { State: DebugState.Stopped };

    private static bool IsCSharp(string? path) => path is not null && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /* Breakpoints */

    /// <summary>A file's editor shown its breakpoints and given the gutter to put them down with, for a .cs file.</summary>
    private void PrepareBreakpoints(HexDocument prepared)
    {
        if (!IsCSharp(prepared.Path))
            return;

        string full = Path.GetFullPath(prepared.Path!);
        prepared.Editor.Breakpoints = BreakpointsOf(full);
        prepared.Editor.OnGutterPressed = line => ToggleBreakpoint(full, line);
    }

    private SortedSet<int> BreakpointsOf(string full)
    {
        if (!breakpoints.TryGetValue(full, out SortedSet<int>? lines))
            breakpoints[full] = lines = [];
        return lines;
    }

    /// <summary>F9, a breakpoint on the caret's line put down or taken up.</summary>
    private void ToggleBreakpointHere()
    {
        if (!document.IsText || !IsCSharp(document.Path))
        {
            Say("breakpoints go in .cs files", error: true);
            return;
        }

        ToggleBreakpoint(Path.GetFullPath(document.Path!), code.Document.Caret.Line);
    }

    private void ToggleBreakpoint(string full, int line)
    {
        SortedSet<int> lines = BreakpointsOf(full);
        if (!lines.Remove(line))
            lines.Add(line);

        debugging?.SetBreakpoints(full, lines);
        if (Side == HexSide.Debug)
            treeDirty = true;
    }

    /* Starting and stopping */

    /// <summary>F5, on from where it stopped, or the project built and started under the debugger.</summary>
    private void DebugOrContinue()
    {
        if (DebugStopped)
        {
            debugging!.Continue();
            return;
        }

        if (Debugging)
        {
            Say("it's running, Shift+F5 stops it");
            return;
        }

        if (ProjectsToDebug() is not { Count: > 0 } projects)
        {
            Say("there's no .csproj to debug in the workspace or over this file", error: true);
            return;
        }

        if (projects.Count == 1)
        {
            StartDebugging(projects[0]);
            return;
        }

        ChooseFrom("Debug which project", "More than one in the workspace, which to build and run under the debugger",
            [.. projects.Select(project => (Relative(project), (Action)(() => StartDebugging(project))))]);
    }

    /// <summary>
    /// The projects F5 could mean. The one over the file on screen first and alone when there is one, the first .csproj
    /// going up from its folder, otherwise every one in the workspace.
    /// </summary>
    private List<string> ProjectsToDebug()
    {
        if (document.Path is { } path)
        {
            for (string? folder = Path.GetDirectoryName(Path.GetFullPath(path)); folder is not null; folder = Path.GetDirectoryName(folder))
            {
                string[] here = Directory.GetFiles(folder, "*.csproj");
                if (here.Length > 0)
                    return [here[0]];
                if (WorkspaceRoots().Any(root => string.Equals(Path.GetFullPath(root).TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), PlatformPaths.Comparison)))
                    break;
            }
        }

        var found = new List<string>();
        foreach (string root in WorkspaceRoots())
            FindProjects(root, found, 0);
        return found;
    }

    private static void FindProjects(string folder, List<string> found, int depth)
    {
        if (depth > 5 || found.Count > 40)
            return;

        try
        {
            found.AddRange(Directory.GetFiles(folder, "*.csproj"));
            foreach (string inside in Directory.GetDirectories(folder))
            {
                string name = Path.GetFileName(inside);
                if (name is "bin" or "obj" or "node_modules" || name.StartsWith('.'))
                    continue;
                FindProjects(inside, found, depth + 1);
            }
        }
        catch (Exception)
        {
            // a folder that can't be read has no projects as far as this goes
        }
    }

    /// <summary>Where netcoredbg is, null when it's nowhere.</summary>
    private string? Netcoredbg()
    {
        string exe = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        if (settings.DebuggerPath.Length > 0 && File.Exists(settings.DebuggerPath))
            return settings.DebuggerPath;

        foreach (string folder in new[] { AppContext.BaseDirectory, TerminalFiles.UserFolder })
        {
            string there = Path.Combine(folder, "netcoredbg", exe);
            if (File.Exists(there))
                return there;
        }

        // Started from the Dock or a launcher, the PATH a terminal has (ProcessEnvironment.ImportLoginShell)
        Horizon.Core.Processes.ProcessEnvironment.WaitForLoginShell();
        foreach (string folder in Horizon.Core.Processes.ProcessEnvironment.Path().Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string there = Path.Combine(folder.Trim('"'), exe);
            if (File.Exists(there))
                return there;
        }

        return null;
    }

    // What to say when there's no netcoredbg, where to get one for this machine. Its releases (github.com/Samsung/netcoredbg)
    // have Windows, Linux (x64 and arm64) and Intel Macs, an Apple silicon Mac builds it from its source
    private static string NoDebugger =>
        OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "netcoredbg isn't anywhere Hex looks, and its releases have nothing for Apple silicon: build it from github.com/Samsung/netcoredbg and say where in the settings (debugger_path)"
            : $"netcoredbg isn't anywhere Hex looks, get it from github.com/Samsung/netcoredbg/releases and put it in a netcoredbg folder next to {(OperatingSystem.IsWindows() ? "Hex.exe" : "Hex")} or on the PATH, or say where in the settings (debugger_path)";

    /// <summary>Builds a project, then runs what it made under the debugger with the breakpoints there are, off the editor's thread.</summary>
    private void StartDebugging(string project)
    {
        if (Netcoredbg() is not { } debugger)
        {
            Say(NoDebugger, error: true);
            return;
        }

        SaveAllCode();
        building = true;
        debuggedProject = project;
        Note($"building {Path.GetFileName(project)} to debug it");
        ShowPanel(OUTPUT_PAGE);
        Say($"building {Path.GetFileName(project)}");

        var breakpointsNow = breakpoints.Where(pair => pair.Value.Count > 0).ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<int>)pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(() => BuildAndLaunch(project, debugger, breakpointsNow));
    }

    private async Task BuildAndLaunch(string project, string debugger, Dictionary<string, IReadOnlyCollection<int>> breakpointsNow)
    {
        string folder = Path.GetDirectoryName(project)!;
        var (built, buildSaid) = await Dotnet(folder, "build", project, "-c", "Debug", "-nologo", "-v", "q");
        foreach (string line in buildSaid.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            debugWork.Enqueue(() => Note(line.TrimEnd('\r')));

        if (built != 0)
        {
            debugWork.Enqueue(() =>
            {
                building = false;
                Say($"{Path.GetFileName(project)} didn't build, see the output", error: true);
            });
            return;
        }

        var (_, target) = await Dotnet(folder, "msbuild", project, "-getProperty:TargetPath", "-p:Configuration=Debug", "-nologo");
        string program = target.Trim();
        if (!File.Exists(program))
        {
            debugWork.Enqueue(() =>
            {
                building = false;
                Say($"couldn't find what {Path.GetFileName(project)} builds ({program})", error: true);
            });
            return;
        }

        DebugSession? session = DebugSession.Start(debugger, ["--interpreter=vscode"]);
        if (session is null)
        {
            debugWork.Enqueue(() =>
            {
                building = false;
                Say("netcoredbg wouldn't start", error: true);
            });
            return;
        }

        debugWork.Enqueue(() => debugging = session);
        bool launched = await session.Launch(program, [], folder, breakpointsNow);
        debugWork.Enqueue(() =>
        {
            building = false;
            if (launched)
            {
                Say($"debugging {Path.GetFileName(program)}, Shift+F5 stops it");
                ShowSide(HexSide.Debug);
            }
            else
            {
                Say($"the debugger couldn't start it, {session.Failed}", error: true);
            }
        });
    }

    // dotnet with some arguments in a folder, its exit code and what it wrote
    private static async Task<(int Exit, string Said)> Dotnet(string folder, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await output + await errors);
        }
        catch (Exception e)
        {
            return (-1, e.Message);
        }
    }

    // Every file of code with changes saved, the build reads them off the disk
    private void SaveAllCode()
    {
        HexDocument was = document;
        foreach (HexDocument open in documents.ToList())
        {
            if (open.IsText && open.Path is not null && (open.Modified || open.CodeEdited))
            {
                Show(open);
                Save();
            }
        }

        if (documents.Contains(was))
            Show(was);
    }

    /// <summary>Shift+F5, the program and the debugger ended.</summary>
    private void StopDebugging()
    {
        if (debugging is null)
            return;

        debugging.Stop();
        Say("stopping it");
    }

    /// <summary>Ctrl+Shift+F5, stopped and started again.</summary>
    private void RestartDebugging()
    {
        string project = debuggedProject;
        if (project.Length == 0)
            return;

        EndDebugging();
        StartDebugging(project);
    }

    private void StepOver() => debugging?.StepOver();

    private void StepIn() => debugging?.StepIn();

    private void StepOut() => debugging?.StepOut();

    /* What happened, every tick */

    private void UpdateDebugging()
    {
        while (debugWork.TryDequeue(out Action? work))
            work();

        if (debugging is null)
            return;

        while (debugging?.TryTake(out DebugEvent happened) == true)
        {
            switch (happened.Kind)
            {
                case DebugEventKind.Stopped:
                    Stopped(happened);
                    break;
                case DebugEventKind.Continued:
                    ClearExecutionLine();
                    stoppedFrames = [];
                    if (Side == HexSide.Debug) treeDirty = true;
                    break;
                case DebugEventKind.Output:
                    int tab = happened.Text.IndexOf('\t');
                    foreach (string line in happened.Text[(tab + 1)..].TrimEnd('\r', '\n').Split('\n'))
                        Note(line.TrimEnd('\r'));
                    break;
                case DebugEventKind.Ended:
                    Say(happened.ExitCode >= 0 ? $"{Path.GetFileNameWithoutExtension(debuggedProject)} ended, {happened.ExitCode}" : $"{Path.GetFileNameWithoutExtension(debuggedProject)} ended");
                    EndDebugging();
                    return;
            }
        }
    }

    private void Stopped(DebugEvent happened)
    {
        stoppedFrames = happened.Frames;
        stoppedFrame = 0;

        // The frame of the program's own code nearest the top, the debugger's steps into the framework go past that
        for (int i = 0; i < stoppedFrames.Count; i++)
        {
            if (stoppedFrames[i].File is { } file && File.Exists(file))
            {
                stoppedFrame = i;
                break;
            }
        }

        ShowFrame(stoppedFrame);
        Say($"stopped, {happened.Text}{(stoppedFrames.Count > 0 && stoppedFrames[stoppedFrame].File is { } at ? $" at {Path.GetFileName(at)}:{stoppedFrames[stoppedFrame].Line + 1}" : string.Empty)}");
        left.Show(true);
        Side = HexSide.Debug;
        treeDirty = true;
        Engine.WindowManager.RequestFrames();
    }

    /// <summary>A frame of the stack looked at, its file open at its line, lit.</summary>
    private void ShowFrame(int index)
    {
        if (index < 0 || index >= stoppedFrames.Count)
            return;

        stoppedFrame = index;
        DebugFrame frame = stoppedFrames[index];
        ClearExecutionLine();
        if (frame.File is not { } file || !File.Exists(file))
            return;

        OpenAt(file, frame.Line, frame.Column);
        if (document.Path is { } open && string.Equals(Path.GetFullPath(open), Path.GetFullPath(file), PlatformPaths.Comparison))
            document.Editor.ExecutionLine = frame.Line;
        variablesShown = null;
        treeDirty = true;
    }

    private void ClearExecutionLine()
    {
        foreach (HexDocument open in documents)
            open.Editor.ExecutionLine = -1;
    }

    private void EndDebugging()
    {
        DebugSession? ending = debugging;
        debugging = null;
        building = false;
        stoppedFrames = [];
        variablesShown = null;
        ClearExecutionLine();
        if (Side == HexSide.Debug)
            treeDirty = true;
        if (ending is not null)
            _ = Task.Run(ending.Dispose);
    }

    /* The Debug side, the stack, the variables and the breakpoints */

    // The variables of the frame looked at, asked for once a stop and kept, null while they're coming
    private List<UITreeItem>? variablesShown;
    private int variablesFor = -1;

    private void RebuildDebugSide()
    {
        UseSideTree(true);
        treeTitle.Text = "DEBUG";
        var items = new List<UITreeItem>();

        if (building)
            items.Add(NoteItem($"building {Path.GetFileName(debuggedProject)}...", DimColor));
        else if (debugging is null)
            items.Add(NoteItem("nothing running, F5 builds and debugs the project", DimColor));
        else if (!DebugStopped)
            items.Add(NoteItem($"{Path.GetFileNameWithoutExtension(debuggedProject)} is running", DimColor));

        if (DebugStopped && stoppedFrames.Count > 0)
        {
            var stack = new UITreeItem("Call stack", "debug-stackframe") { Expandable = true, Expanded = true, Children = [] };
            for (int i = 0; i < stoppedFrames.Count; i++)
            {
                DebugFrame frame = stoppedFrames[i];
                int index = i;
                stack.Children.Add(new UITreeItem(frame.Name, i == stoppedFrame ? "debug-stackframe-focused" : "debug-stackframe-dot")
                {
                    Detail = frame.File is { } file ? $"{Path.GetFileName(file)}:{frame.Line + 1}" : string.Empty,
                    Color = frame.File is { } there && File.Exists(there) ? null : DimColor,
                    Tag = new SideTag(Pressed: () => ShowFrame(index))
                });
            }

            items.Add(stack);

            var variables = new UITreeItem("Variables", "symbol-variable") { Expandable = true, Expanded = true };
            if (variablesShown is null || variablesFor != stoppedFrame)
            {
                variables.Children = [NoteItem("asking...", DimColor)];
                AskVariables(stoppedFrames[stoppedFrame].Id, stoppedFrame);
            }
            else
            {
                variables.Children = variablesShown;
            }

            items.Add(variables);
        }

        var points = new UITreeItem("Breakpoints", "debug-breakpoint") { Expandable = true, Expanded = true, Children = [] };
        foreach (var (file, lines) in breakpoints.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (int line in lines)
            {
                string at = file;
                int on = line;
                points.Children.Add(new UITreeItem($"{Path.GetFileName(file)}:{line + 1}", "debug-breakpoint")
                {
                    IconColor = ErrorColor,
                    Detail = Relative(file),
                    Tag = new SideTag(Pressed: () => OpenAt(at, on, 0), Menu: _ => ToggleBreakpoint(at, on))
                });
            }
        }

        if (points.Children.Count == 0)
            points.Children.Add(NoteItem("F9 or a click in the gutter of a .cs file", DimColor));
        items.Add(points);

        sideTree.Items = items;
    }

    // The scopes of a frame and the variables of each, off the editor's thread, handed back as rows
    private void AskVariables(int frameId, int frameIndex)
    {
        if (debugging is not { } session)
            return;

        variablesFor = frameIndex;
        variablesShown = null;
        _ = Task.Run(async () =>
        {
            var rows = new List<UITreeItem>();
            try
            {
                foreach (DebugScope scope in await session.Scopes(frameId))
                {
                    foreach (DebugVariable variable in await session.Variables(scope.Reference))
                        rows.Add(VariableItem(session, variable));
                }
            }
            catch (Exception e)
            {
                rows.Add(new UITreeItem($"couldn't ask, {e.Message}") { Color = DimColor });
            }

            debugWork.Enqueue(() =>
            {
                if (variablesFor != frameIndex)
                    return;
                variablesShown = rows.Count > 0 ? rows : [new UITreeItem("nothing in scope") { Color = DimColor }];
                if (Side == HexSide.Debug) treeDirty = true;
            });
        });
    }

    // A variable a row, what's inside it asked for as it's opened
    private UITreeItem VariableItem(DebugSession session, DebugVariable variable)
    {
        var item = new UITreeItem($"{variable.Name} = {variable.Value}", "symbol-variable")
        {
            Detail = variable.Type ?? string.Empty,
            Tooltip = $"{variable.Name} = {variable.Value}",
            Expandable = variable.Reference > 0
        };

        if (variable.Reference > 0)
        {
            item.Tag = new SideTag(Expanding: opened =>
            {
                if (opened.Children is not null)
                    return;
                opened.Children = [new UITreeItem("asking...") { Color = DimColor }];
                _ = Task.Run(async () =>
                {
                    var inside = new List<UITreeItem>();
                    try
                    {
                        foreach (DebugVariable child in await session.Variables(variable.Reference))
                            inside.Add(VariableItem(session, child));
                    }
                    catch (Exception)
                    {
                        // gone on since, nothing to show
                    }

                    debugWork.Enqueue(() =>
                    {
                        opened.Children = inside;
                        sideTree.Refresh();
                    });
                });
            });
        }

        return item;
    }

    /// <summary>
    /// The Run menu's debugging, on VS Code's keys, which go while typing. F5 itself is RunOrPlay's, and F11 is Zen
    /// Mode's while nothing has stopped (BuildQuiet).
    /// </summary>
    private void AddDebuggingCommands()
    {
        commands.Add("Run/debug", "Start Debugging", DebugOrContinue, "F5", "debug-alt").IsVisible = () => !LayoutTools && !DebugStopped && IsCSharp(document.Path);
        commands.Add("Run/debug", "Continue", DebugOrContinue, "F5", "debug-continue").IsVisible = () => DebugStopped;
        commands.Add("Run/debug", "Step Over", StepOver, "F10", "debug-step-over", HexKeyUse.BoundWhileTyping).IsEnabled = () => DebugStopped;
        commands.Add("Run/debug", "Step Into", StepIn, "F11", "debug-step-into").IsEnabled = () => DebugStopped;
        commands.Add("Run/debug", "Step Out", StepOut, "Shift+F11", "debug-step-out", HexKeyUse.BoundWhileTyping).IsEnabled = () => DebugStopped;
        commands.Add("Run/debug", "Stop Debugging", StopDebugging, "Shift+F5", "debug-stop", HexKeyUse.BoundWhileTyping).IsEnabled = () => Debugging;
        commands.Add("Run/debug", "Restart Debugging", RestartDebugging, "Ctrl+Shift+F5", "debug-restart", HexKeyUse.BoundWhileTyping).IsEnabled = () => Debugging;
        commands.Add("Run/debug", "Toggle Breakpoint", ToggleBreakpointHere, "F9", "debug-breakpoint", HexKeyUse.BoundWhileTyping).IsVisible = () => !LayoutTools && IsCSharp(document.Path);
    }
}
