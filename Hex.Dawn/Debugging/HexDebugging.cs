using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

using Hex.Core.Projects;

using Horizon.Core.Threading;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// Debugging C#, VS Code's way and VS Code's keys. F9 (or a click in the gutter) puts a breakpoint on a line of a .cs file,
// F5 builds the project the file is in (the workspace's when there's one) and runs it under netcoredbg (Samsung's, MIT,
// a debugger that talks the Debug Adapter Protocol, the engine's DebugSession), and where it stops the file comes up with
// the line lit and the Debug side has the stack and the variables. F5 goes on, F10 steps over, F11 in, Shift+F11 out,
// Shift+F5 stops it, Ctrl+Shift+F5 starts it again. What it writes goes in the output. netcoredbg is looked for where the
// settings say (debugger_path), next to the exe (netcoredbg/netcoredbg.exe), in the settings folder and on the PATH, and
// where it's nowhere the first F5 installs it into the settings folder and goes on. Microsoft's own debugger comes with
// VS Code's C# extension and is only allowed in Microsoft's editors, so not that.
internal sealed partial class HexDebugging
{
    private readonly DawnContext context;

    /// <summary>
    /// The Run menu's debugging, on VS Code's keys, which go while typing (F5 itself is RunOrPlay's, and F11 is Zen
    /// Mode's while nothing has stopped), the breakpoints of every .cs file opened and the Debug side.
    /// </summary>
    public HexDebugging(DawnContext with)
    {
        context = with;
        variables = new HexDebugVariables(key => context.Strings.Said(key),
            e => context.Strings.Said("debugging.could_not_ask", ("why", e.Message)), () => context.Panels.Dim,
            debugWork.Enqueue, () => context.Panels.RefreshSide(HexSide.Debug), () =>
            {
                context.Panels.RefreshRows();
                tipTree?.Refresh();
            });
        SweepProgramOutputs();
        context.Documents.Opened += PrepareBreakpoints;

        // A closed file's text let go of, this kept every .cs ever opened and all of its editor alive
        context.Documents.Closed += closed => following.Remove(closed.Editor.Document);
        context.Panels.AddSide(HexSide.Debug, context.Strings.Said("debugging.debug"), DebugSide);
        UICommands commands = context.Commands;
        commands.Add("Run/debug", context.Strings.Said("debugging.start_debugging"), DebugOrContinue, "F5", "debug-alt").IsVisible = () => !context.Documents.Current.InDesigner && !DebugStopped && SupportsDebugging(context.Documents.Current.Path);
        commands.Add("Run/debug", context.Strings.Said("debugging.continue"), DebugOrContinue, "F5", "debug-continue").IsVisible = () => DebugStopped;
        commands.Add("Run/debug", context.Strings.Said("debug_bar.pause"), ContinueOrPause, icon: "debug-pause")
            .IsVisible = () => debugging is { State: DebugState.Running };
        var hexLocals = commands.Add("Run/debug", context.Strings.Said("debugging.hexadecimal_locals"), ToggleHexadecimalLocals, icon: "symbol-numeric");
        hexLocals.IsVisible = () => DebugStopped && debugging?.Can("supportsValueFormattingOptions") == true;
        hexLocals.IsChecked = () => hexadecimalLocals;
        commands.Add("Run/debug", context.Strings.Said("debugging.show_current_frame"), ShowCurrentFrame, icon: "debug-stackframe-focused")
            .IsVisible = CanShowCurrentFrame;
        commands.Add("Run/debug", context.Strings.Said("debugging.disassembly"), ShowDisassembly, icon: "code")
            .IsVisible = CanShowDisassembly;
        commands.Add("Run/debug", context.Strings.Said("debugging.memory"), ShowMemory, icon: "file-binary").IsVisible = CanShowMemory;
        commands.Add("Run/debug", context.Strings.Said("debugging.modules"), ShowModules, icon: "library").IsVisible = CanShowModules;
        commands.Add("Run/debug", context.Strings.Said("debugging.modules_refresh"), RefreshModules, icon: "refresh").IsVisible = () => modulesShown && CanShowModules() && debugging?.CanRequestModules == true;
        commands.Add("Run/debug", context.Strings.Said("debugging.modules_filter"), FilterModules, icon: "filter").IsVisible = () => modulesShown && CanShowModules();
        commands.Add("Run/debug", context.Strings.Said("debugging.memory_refresh"), RefreshMemory, icon: "refresh").IsVisible = CanPageMemory;
        commands.Add("Run/debug", context.Strings.Said("debugging.memory_previous"), () => PageMemory(-1), icon: "arrow-left").IsVisible = CanPageMemory;
        commands.Add("Run/debug", context.Strings.Said("debugging.memory_next"), () => PageMemory(1), icon: "arrow-right").IsVisible = CanPageMemory;
        commands.Add("Run/debug", context.Strings.Said("debugging.copy_call_stack"), CopyCallStack, icon: "copy")
            .IsVisible = () => DebugStopped && stoppedFrames.Count > 0;
        commands.Add("Run/debug", context.Strings.Said("debugging.run_to_cursor"), RunToCursor, "Ctrl+F10", "debug-continue", UIKeyUse.BoundWhileTyping).IsEnabled = () => DebugStopped && !IsDebuggerSourcePath(context.Documents.Current.Path);
        var setNext = commands.Add("Run/debug", context.Strings.Said("debugging.set_next_statement"), SetNextStatement, "Ctrl+Shift+F10", "debug-stackframe-focused", UIKeyUse.BoundWhileTyping);
        setNext.IsVisible = () => debugging?.CanGoto == true;
        setNext.IsEnabled = CanSetNextStatement;
        commands.Add("Run/debug", context.Strings.Said("debugging.step_over"), StepOver, "F10", "debug-step-over", UIKeyUse.BoundWhileTyping).IsEnabled = () => DebugStopped;
        commands.Add("Run/debug", context.Strings.Said("debugging.step_into"), StepIn, "F11", "debug-step-into").IsEnabled = () => DebugStopped;
        commands.Add("Run/debug", context.Strings.Said("debugging.step_out"), StepOut, "Shift+F11", "debug-step-out", UIKeyUse.BoundWhileTyping).IsEnabled = () => DebugStopped;
        var stopDebugging = commands.Add("Run/debug", context.Strings.Said("debugging.stop_debugging"), StopDebugging, "Shift+F5", "debug-stop", UIKeyUse.BoundWhileTyping);
        stopDebugging.IsEnabled = () => Debugging || Tasks?.RunGoing == true;
        stopDebugging.IsVisible = () => debugging?.TerminatesDebuggeeOnStop != false;
        commands.Add("Run/debug", context.Strings.Said("debugging.detach"), StopDebugging, "Shift+F5", "debug-disconnect", UIKeyUse.BoundWhileTyping)
            .IsVisible = () => debugging?.TerminatesDebuggeeOnStop == false;
        commands.Add("Run/debug", context.Strings.Said("debugging.restart_debugging"), RestartDebugging, "Ctrl+Shift+F5", "debug-restart", UIKeyUse.BoundWhileTyping).IsEnabled = () => Debugging && restart is not null;
        commands.Add("Run/debug", context.Strings.Said("debugging.attach"), AttachToProcess, icon: "plug").IsEnabled = () => !Debugging;
        var justMyCode = commands.Add("Run/debug", context.Strings.Said("debugging.just_my_code"), () =>
        {
            if (Debugging) return;
            context.Settings.Values.DebugJustMyCode = !context.Settings.Values.DebugJustMyCode;
            context.Settings.Save();
        }, icon: "code");
        justMyCode.IsChecked = () => context.Settings.Values.DebugJustMyCode;
        justMyCode.IsEnabled = () => !Debugging;
        commands.Add("Run/debug", context.Strings.Said("debugging.select_framework"), SelectDebugFramework, icon: "debug-alt")
            .IsVisible = () => !Debugging && Tasks?.ChosenRun()?.Task is null && (IsCSharp(context.Documents.Current.Path)
                || Tasks?.ChosenRun()?.Project?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true);
        commands.Add("Run/debug", context.Strings.Said("debugging.toggle_breakpoint"), ToggleBreakpointHere, "F9", "debug-breakpoint", UIKeyUse.BoundWhileTyping).IsVisible = () => CanUseBreakpointCommands() && (!IsDisassemblyPath(context.Documents.Current.Path) || CanToggleInstructionHere());
        commands.Add("Run/debug", context.Strings.Said("debugging.add_function_breakpoint"), AddFunctionBreakpoint, icon: "debug-breakpoint-function")
            .IsEnabled = () => !building;
        commands.Add("Run/debug", context.Strings.Said("debugging.enable_disable_breakpoint"), ToggleBreakpointEnabledHere, "Ctrl+F9", "circle", UIKeyUse.BoundWhileTyping).IsVisible = () => CanUseBreakpointCommands() && (!IsDisassemblyPath(context.Documents.Current.Path) || CanToggleInstructionHere());
        commands.Add("Run/debug", context.Strings.Said("debugging.breakpoint_settings"), BreakpointSettingsHere, icon: "debug-breakpoint-conditional").IsVisible = () => CanUseBreakpointCommands() && !IsDisassemblyPath(context.Documents.Current.Path) && !IsDebuggerSourcePath(context.Documents.Current.Path);
        commands.Add("Run/debug", context.Strings.Said("debugging.install_netcoredbg"), InstallNetcoredbg, icon: "cloud-download").IsVisible = () => NetcoredbgAsset() is not null && Netcoredbg() is null;
        AddWatchCommands();
        commands.Add("Run/debug", context.Strings.Said("debugging.exception_settings"), ExceptionSettings, icon: "debug-breakpoint")
            .IsVisible = () => debugging?.ExceptionFilters.Count > 0;
        commands.Add("Run/debug", context.Strings.Said("debugging.cmake_configure"), ConfigureCMake, icon: "tools").IsVisible = () => CanConfigureCMake;
        commands.Add("Run/debug", context.Strings.Said("debugging.cmake_build"), BuildCMake, icon: "tools").IsVisible = () => CanConfigureCMake;
        commands.Add("Run/debug", context.Strings.Said("debugging.cmake_select_preset"), SelectCMakePreset, icon: "tools").IsVisible = () => CanConfigureCMake;
        commands.Add("Run/debug", context.Strings.Said("debugging.cmake_choose_preset"), () =>
        {
            if (context.Documents.Current.Path is { } file && CMakeProject.RootOf(file, context.Workspace.Roots) is { } root)
            {
                cmakeTargets.Remove(root);
                StartDebuggingCMake(file, root, preset: string.Empty);
            }
        }, icon: "debug-alt").IsVisible = () => !Debugging && IsNativeSource(context.Documents.Current.Path);
        commands.Add("Run/debug", context.Strings.Said("debugging.cmake_choose_target"), () =>
        {
            if (context.Documents.Current.Path is { } file && CMakeProject.RootOf(file, context.Workspace.Roots) is { } root)
            {
                cmakeTargets.Remove(root);
                StartDebuggingCMake(file, root);
            }
        }, icon: "debug-alt").IsVisible = () => !Debugging && IsNativeSource(context.Documents.Current.Path);
    }

    /// <summary>The workspace's tasks, whose run tasks Shift+F5 stops when nothing is being debugged.</summary>
    internal HexTasks? Tasks { get; set; }

    /// <summary>Whether F5 installs netcoredbg when it isn't there, which the self test doesn't want it going off to GitHub for.</summary>
    internal bool InstallsItself { get; set; } = true;

    // The breakpoints, by the file's whole path, the lines counted from 0. An editor of the file is handed the set
    internal readonly Dictionary<string, SortedSet<int>> breakpoints = new(StringComparer.OrdinalIgnoreCase);

    private DebugSession? debugging;
    private string debuggedProject = string.Empty;

    // What starts what was debugged last again, Ctrl+Shift+F5
    private Action? restart;

    // Where it stopped, the frames of the stack and the one looked at
    internal IReadOnlyList<DebugFrame> stoppedFrames = [];
    internal int stoppedFrame;

    // Work done off the editor's thread (the build, the launch, the variables asked for) handed back to it
    private readonly ConcurrentQueue<Action> debugWork = new();
    private bool building;
    private CancellationTokenSource? debugBuild;

    private void CancelDebugBuild()
    {
        debugBuildGeneration++;
        attachingProcess = false;
        var pending = debugBuild;
        debugBuild = null;
        pending?.Cancel();
    }

    // Both the worker and its queued UI callbacks belong to this attempt.
    private void PostBuild(CancellationToken cancel, Action action) => debugWork.Enqueue(() =>
    {
        if (!cancel.IsCancellationRequested)
            action();
    });

    /// <summary>Whether a program is being debugged, from the launch to its end.</summary>
    internal bool Debugging => debugging is { State: not DebugState.Ended } || building || testAttempt is not null || ctestDebugRun is not null || pythonTestDebugRun is not null;

    /// <summary>Whether it's stopped somewhere, where the steps mean something.</summary>
    internal bool DebugStopped => debugging is { State: DebugState.Stopped };
    internal bool DebugRunning => debugging is { State: DebugState.Running };

    internal static bool IsCSharp(string? path) => path is not null && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /* Breakpoints */

    /// <summary>A file's editor shown its breakpoints and given the gutter to put them down with, for a .cs or a .d file.</summary>
    private void PrepareBreakpoints(HexDocument prepared)
    {
        if (!SupportsDebugging(prepared.Path))
            return;

        ReadKeptBreakpoints();
        string full = Path.GetFullPath(prepared.Path!);
        prepared.Editor.Breakpoints = BreakpointsOf(full);
        prepared.Editor.BreakpointMarks = MarksOf(full);
        prepared.Editor.OnGutterPressed = line => ToggleBreakpoint(full, line);

        // A right click on the strip of breakpoints is the breakpoint's menu, anywhere else the code's as it was
        Action<System.Numerics.Vector2>? codeMenu = prepared.Editor.OnContextMenu;
        prepared.Editor.OnContextMenu = point =>
        {
            if (!GutterMenu(prepared, full, point))
                codeMenu?.Invoke(point);
        };

        // Once for each document, opened again it's the same one
        UITextDocument document = prepared.Editor.Document;
        if (following.Add(document))
            document.LineChanged += change => MoveBreakpoints(full, change);
    }

    // The documents whose breakpoints follow their lines already
    private readonly HashSet<UITextDocument> following = [];
    private readonly Dictionary<string, HexBreakpointTracking> breakpointTracking = new(PlatformPaths.Comparer);

    private Dictionary<int, HexBreakpoint> VisibleBreakpoints(string full) =>
        BreakpointsOf(full).ToDictionary(line => line, line => SettingsOf(full, line));

    private void MoveBreakpoints(string full, UITextLineChange change)
    {
        if (!breakpoints.TryGetValue(full, out SortedSet<int>? lines) || lines.Count == 0)
        {
            breakpointTracking.Remove(full);
            return;
        }
        if (!breakpointTracking.TryGetValue(full, out var tracking))
            breakpointTracking[full] = tracking = new();
        var moved = tracking.Apply(VisibleBreakpoints(full), change);
        breakpointSettings[full] = moved.Where(pair => !pair.Value.Plain).ToDictionary();
        lines.Clear();
        lines.UnionWith(moved.Keys);
        BreakpointsChanged(full);
    }

    private SortedSet<int> BreakpointsOf(string full)
    {
        if (!breakpoints.TryGetValue(full, out SortedSet<int>? lines))
            breakpoints[full] = lines = [];
        return lines;
    }

    /// <summary>F9, a breakpoint on the caret's line put down or taken up.</summary>
    internal void ToggleBreakpointHere()
    {
        HexDocument shown = context.Documents.Current;
        if (IsDebuggerSourcePath(shown.Path)) { ToggleSourceBreakpoint(shown, shown.Editor.Document.Caret.Line); return; }
        if (IsDisassemblyPath(shown.Path)) { ToggleInstruction(shown, shown.Editor.Document.Caret.Line); return; }
        if (!shown.IsText || (!SupportsDebugging(shown.Path)))
        {
            context.Notices.Say(context.Strings.Said("debugging.breakpoints_cs_only"), error: true);
            return;
        }

        ToggleBreakpoint(Path.GetFullPath(shown.Path!), shown.Editor.Document.Caret.Line);
    }

    internal void ToggleBreakpoint(string full, int line)
    {
        SortedSet<int> lines = BreakpointsOf(full);
        if (!lines.Remove(line))
            lines.Add(line);

        BreakpointsChanged(full);
    }

    /// <summary>The settings of the breakpoint on the caret's line, its menu where the pointer is, one put down first.</summary>
    internal void BreakpointSettingsHere()
    {
        HexDocument shown = context.Documents.Current;
        if (!shown.IsText || (!SupportsDebugging(shown.Path)))
        {
            context.Notices.Say(context.Strings.Said("debugging.breakpoints_cs_only"), error: true);
            return;
        }

        BreakpointMenu(Path.GetFullPath(shown.Path!), shown.Editor.Document.Caret.Line);
    }

    /* Starting and stopping */

    /// <summary>F5, on from where it stopped, or the project built and started under the debugger.</summary>
    internal void DebugOrContinue()
    {
        if (DebugStopped)
        {
            GoOn();
            return;
        }

        if (Debugging)
        {
            context.Notices.Say(context.Strings.Said("debugging.already_running"));
            return;
        }

        // A run task picked beside the Run button that's a dotnet run of a project is that project debugged, the way F5 has it
        if (Tasks?.ChosenRun() is { Task: { } task } && DotnetRunLine.Of(task.Command, task.WorkingFolder()) is { } run)
        {
            DebugRun(run, task.WorkingFolder(), go => Tasks.RunFirstThen(task, go));
            return;
        }

        if (IsPython(context.Documents.Current.Path))
        {
            StartDebuggingPython(context.Documents.Current.Path!);
            return;
        }

        if (IsNativeSource(context.Documents.Current.Path))
        {
            StartDebuggingNative(context.Documents.Current.Path!);
            return;
        }

        // D is built with -g and run under lldb-dap
        if (IsD(context.Documents.Current.Path))
        {
            StartDebuggingD(context.Documents.Current.Path!);
            return;
        }

        if (ProjectsToDebug() is not { Count: > 0 } projects)
        {
            context.Notices.Say(context.Strings.Said("debugging.no_project"), error: true);
            return;
        }

        if (projects.Count == 1)
        {
            StartDebugging(projects[0]);
            return;
        }

        context.Windows.Choose(context.Strings.Said("debugging.which_project"), context.Strings.Said("debugging.which_project_text"),
            [.. projects.Select(project => (context.Workspace.Relative(project), (Action)(() => StartDebugging(project))))]);
    }

    /// <summary>
    /// A project picked to run beside the Run button built and started under the debugger, or said to be going already.
    /// F5 with a project picked debugs it whatever file is on screen.
    /// </summary>
    internal void DebugProject(string project)
    {
        if (Debugging)
            context.Notices.Say(context.Strings.Said("debugging.already_running"));
        else
            StartDebugging(project);
    }

    /// <summary>
    /// A run task's dotnet run debugged, its project with the arguments, the configuration and the launch profile it
    /// says, in the folder the task runs in, once <paramref name="first"/> has done what the task depends on. Said to be
    /// going already while something is.
    /// </summary>
    internal void DebugRun(DotnetRunLine run, string folder, Action<Action> first)
    {
        if (Debugging)
            context.Notices.Say(context.Strings.Said("debugging.already_running"));
        else
            first(() => StartDebugging(run.Project, run, folder));
    }

    /// <summary>
    /// The projects F5 could mean. The project picked beside the Run button when it's a .csproj, the one over the file on
    /// screen first and alone when there is one, the first .csproj going up from its folder, otherwise every one in the
    /// workspace.
    /// </summary>
    private List<string> ProjectsToDebug()
    {
        if (context.Workspace.RunTarget is { Kind: HexRunTarget.PROJECT, Path: var picked } && picked.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(picked))
            return [picked];

        if (context.Documents.Current.Path is { } path)
        {
            for (string? folder = Path.GetDirectoryName(Path.GetFullPath(path)); folder is not null; folder = Path.GetDirectoryName(folder))
            {
                string[] here = Directory.GetFiles(folder, "*.csproj");
                if (here.Length > 0)
                    return [here[0]];
                if (context.Workspace.Roots.Any(root => string.Equals(Path.GetFullPath(root).TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), PlatformPaths.Comparison)))
                    break;
            }
        }

        var found = new List<string>();
        foreach (string root in context.Workspace.Roots)
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
    internal string? Netcoredbg()
    {
        string exe = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        if (context.Settings.Values.DebuggerPath.Length > 0 && File.Exists(context.Settings.Values.DebuggerPath))
            return context.Settings.Values.DebuggerPath;

        foreach (string folder in new[] { Hex.Core.HexHost.ProgramFolder, TerminalFiles.UserFolder })
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
    private string NoDebugger =>
        OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? context.Strings.Said("debugging.no_debugger_apple")
            : context.Strings.Said("debugging.no_debugger", ("exe", OperatingSystem.IsWindows() ? "Hex.exe" : "Hex"));

    // Whether it was said that C# runs without the debugger, said the first time only
    private bool saidNoDebugger;

    /// <summary>
    /// F5 on C# without netcoredbg, it runs without being debugged, which is said the first time on a card that installs
    /// it where Hex looks for it.
    /// </summary>
    internal void SayRunWithoutDebugger()
    {
        if (saidNoDebugger)
            return;

        saidNoDebugger = true;
        context.Notices.Note(context.Strings.Said("debugging.running_without", ("why", NoDebugger)));
        if (NetcoredbgAsset() is not null)
            context.Notices.Notify(context.Strings.Said("debugging.running_without_card"), false, (context.Strings.Said("debugging.install_netcoredbg"), InstallNetcoredbg));
    }

    private const string NETCOREDBG_REPO = "Samsung/netcoredbg";

    /// <summary>
    /// The file of netcoredbg's releases for this machine, null for one it has none for (an Apple silicon Mac builds it
    /// from its source).
    /// </summary>
    internal static string? NetcoredbgAsset()
    {
        bool arm = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
        if (OperatingSystem.IsWindows())
            return arm ? null : "netcoredbg-win64.zip";
        if (OperatingSystem.IsLinux())
            return arm ? "netcoredbg-linux-arm64.tar.gz" : "netcoredbg-linux-amd64.tar.gz";
        if (OperatingSystem.IsMacOS())
            return arm ? null : "netcoredbg-osx-amd64.tar.gz";
        return null;
    }

    // Whether an install F5 started by itself didn't go, after which F5 runs C# without the debugger rather than trying again
    private bool installFailed;

    /// <summary>
    /// F5 on C# with no netcoredbg anywhere, it's installed first with its card up and then F5 goes on into the debug
    /// session, the way Visual Studio has its debugger there from the start. It said C# ran without the debugger and
    /// left the install to a button. True while that's what happens, false when it can't be (no release for this machine,
    /// the self test, or an install that didn't go), and F5 runs the program without it then.
    /// </summary>
    internal bool InstallingFirst(Action then)
    {
        if (!InstallsItself || installFailed || NetcoredbgAsset() is null || Netcoredbg() is not null)
            return false;

        // F5 again while it installs has it go on once, from where the first F5 was
        if (context.Work.Find("netcoredbg") is { } going)
        {
            context.Work.Renew(going);
            context.Notices.Say(context.Strings.Said("debugging.still_installing"));
            return true;
        }

        context.Notices.Say(context.Strings.Said("debugging.installing_first"));
        InstallNetcoredbg(installed =>
        {
            installFailed |= !installed;
            then();
        });
        return true;
    }

    /// <summary>
    /// netcoredbg downloaded from its newest release and unpacked into Hex's own folder of things (where it's looked for
    /// first), on a card while it goes. It said where to get it and left the rest to whoever read that.
    /// </summary>
    internal void InstallNetcoredbg() => InstallNetcoredbg(null);

    /// <summary>The same, and whether it's there now said to whoever asked once it's done.</summary>
    private void InstallNetcoredbg(Action<bool>? done)
    {
        if (NetcoredbgAsset() is not { } asset)
        {
            context.Windows.OpenInBrowser($"https://github.com/{NETCOREDBG_REPO}/releases");
            done?.Invoke(false);
            return;
        }

        string into = TerminalFiles.UserFolder;
        context.Work.Start("netcoredbg", context.Strings.Said("debugging.installing_netcoredbg"), async work =>
        {
            // Straight from the newest release's file, GitHub's API allows sixty questions an hour and Hex's version checks
            // had used them, the download doesn't ask it anything
            string url = $"https://github.com/{NETCOREDBG_REPO}/releases/latest/download/{asset}";

            string download = Path.Combine(Path.GetTempPath(), asset);
            await HexReleases.Download(url, download, progress => work.Report(progress, context.Strings.Said("debugging.downloading", ("asset", asset))), work.Token).ConfigureAwait(false);
            work.Say(context.Strings.Said("debugging.unpacking"));
            Unpack(download, into);
            File.Delete(download);
        }, item =>
        {
            string? there = item.State == WorkState.Succeeded ? Netcoredbg() : null;
            if (there is not null)
                context.Notices.Say(context.Strings.Said("debugging.netcoredbg_installed", ("path", there)));
            else
                foreach (string said in item.Output)
                    context.Notices.Note(said, error: true);
            done?.Invoke(there is not null);
        });
    }

    /// <summary>A zip or a .tar.gz unpacked into a folder, what's there written over.</summary>
    internal static void Unpack(string archive, string into)
    {
        Directory.CreateDirectory(into);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(archive, into, overwriteFiles: true);
            return;
        }

        using FileStream file = File.OpenRead(archive);
        using var unzipped = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        System.Formats.Tar.TarFile.ExtractToDirectory(unzipped, into, overwriteFiles: true);
    }

    /// <summary>
    /// Builds a project, then runs what it made under the debugger with the breakpoints there are, off the editor's
    /// thread. A run task's dotnet run says the arguments, the configuration, the profile and the folder, null for the
    /// project's own (the launch profile picked, the configuration beside the Run button).
    /// </summary>
    private void StartDebugging(string project, DotnetRunLine? run = null, string? runFolder = null)
    {
        if (Netcoredbg() is not { } debugger)
        {
            if (!InstallingFirst(() => StartDebugging(project, run, runFolder)))
                context.Notices.Say(NoDebugger, error: true);
            return;
        }

        SaveAllCode();
        CancelDebugBuild();
        var attempt = new CancellationTokenSource();
        debugBuild = attempt;
        building = true;
        conditionFailures.Clear();
        debuggedProject = project;
        restart = () => StartDebugging(project, run, runFolder);
        context.Notices.Note(context.Strings.Said("debugging.building_to_debug", ("project", Path.GetFileName(project))));
        context.Panels.ShowOutput();
        context.Panels.ShowSide(HexSide.Debug);
        context.Notices.Say(context.Strings.Said("debugging.building", ("project", Path.GetFileName(project))));

        var breakpointsNow = BreakpointsNow();
        string? profile = run?.Profile ?? context.Workspace.LaunchProfileOf(project);
        string configuration = run?.Configuration ?? context.Workspace.Configuration;
        _ = Task.Run(async () =>
        {
            try
            {
                string? framework = run?.Framework;
                if (framework is null)
                {
                    var choice = await DebugFramework(project, configuration, run?.RuntimeOptions, attempt.Token);
                    framework = choice.Selected;
                    if (framework is null && choice.Offered.Count > 1)
                    {
                        ChooseDebugFramework(project, configuration, run, runFolder, choice.Offered, attempt.Token);
                        return;
                    }
                }
                await BuildAndLaunch(project, debugger, breakpointsNow, profile, configuration, run, runFolder, framework, attempt.Token);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
            catch (Exception e)
            {
                PostBuild(attempt.Token, () =>
                {
                    building = false;
                    context.Notices.Say(context.Strings.Said("debugging.launch_failed", ("why", e.Message)), error: true);
                });
            }
            finally
            {
                debugWork.Enqueue(() =>
                {
                    if (ReferenceEquals(debugBuild, attempt))
                        debugBuild = null;
                    attempt.Dispose();
                });
            }
        });
    }

    private async Task BuildAndLaunch(string project, string debugger, Dictionary<string, IReadOnlyCollection<DebugBreakpoint>> breakpointsNow, string? picked,
        string configuration, DotnetRunLine? run, string? runFolder, string? framework, CancellationToken cancel)
    {
        string folder = Path.GetDirectoryName(project)!;

        // Hex debugging Hex builds into the folder it runs from, its locked files are moved aside first (HexSelfBuild)
        List<string> moved = [];
        try
        {
            var (_, output) = await DotnetCli.Run(folder, DotnetDebugTarget.PropertyArguments(project, configuration, framework, "OutDir,TargetPath", run?.RuntimeOptions), TimeSpan.FromMinutes(2), cancel);
            var target = DotnetEvaluation.Parse(project, output);
            if (target.OutputFolder.Length > 0 && HexSelfBuild.IsRunningFolder(target.OutputFolder))
                moved = HexSelfBuild.MoveAside(HexSelfBuild.RunningFolder);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException)
        {
            // Not knowing where it builds to, it builds and says what went wrong if it was over Hex
        }

        int built;
        string buildSaid;
        try
        {
            cancel.ThrowIfCancellationRequested();
            (built, buildSaid) = await DotnetCli.Run(folder,
                DotnetDebugTarget.BuildArguments(project, configuration, framework, run?.RuntimeOptions), TimeSpan.FromMinutes(30), cancel);
        }
        finally
        {
            HexSelfBuild.PutBack(moved);
        }
        cancel.ThrowIfCancellationRequested();
        if (!ReportBuild(buildSaid, folder, built, Path.GetFileName(project), project, cancel))
            return;

        var (_, targetPath) = await DotnetCli.Run(folder, DotnetDebugTarget.PropertyArguments(project, configuration, framework, "TargetPath,RuntimeIdentifier,RunCommand,RunArguments", run?.RuntimeOptions), TimeSpan.FromMinutes(2), cancel);
        var evaluated = DotnetEvaluation.Parse(project, targetPath);
        string assembly = evaluated.TargetPath;
        if (!File.Exists(assembly))
        {
            PostBuild(cancel, () =>
            {
                building = false;
                context.Notices.Say(context.Strings.Said("debugging.no_program", ("project", Path.GetFileName(project)), ("program", assembly)), error: true);
            });
            return;
        }

        // With what the launch profile picked says, the one Ctrl+F5 runs it with, and --window for a Hex so it doesn't hand
        // itself to this one
        // A task's dotnet run hands the program what's after its --, runs it in the task's folder, and has no profile with
        // --no-launch-profile, the way dotnet run itself would
        DotnetLaunchProfile? profile = run is { NoProfile: true } ? null : DotnetLaunch.For(project, picked);
        var launchTarget = DotnetDebugTarget.LaunchTarget(evaluated, run?.RuntimeOptions);
        string program = launchTarget.Program == "dotnet" ? LanguageServerProgram.Locate("dotnet") ?? launchTarget.Program : launchTarget.Program;
        List<string> applicationArguments = run?.Arguments is { } given ? [.. given] : DotnetLaunch.ArgumentsFor(assembly, profile, HexSelfBuild.RunningExe);
        List<string> arguments = [.. launchTarget.PrefixArguments, .. applicationArguments];
        string runIn = runFolder ?? profile?.WorkingFolder(project) ?? folder;
        var environment = run is not null ? run.RuntimeEnvironment(profile) : profile?.RuntimeEnvironment();
        cancel.ThrowIfCancellationRequested();
        await StartUnder(DebugSession.Start(debugger, ["--interpreter=vscode"]), Path.GetFileName(assembly), DebugSession.CORECLR,
            started => started.Launch(program, arguments, runIn, breakpointsNow, DebugSession.CORECLR, environment: environment), cancel);
    }

    /// <summary>
    /// What a build for debugging said, its lines in the output and what it says is wrong in the Problems panel and under
    /// the code the way a build task's is (they were lines in the output nobody looked at), and a build that failed said
    /// so. Whether it built.
    /// </summary>
    private bool ReportBuild(string said, string folder, int built, string name, string? builtFile = null, CancellationToken cancel = default)
    {
        foreach (string line in said.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            PostBuild(cancel, () => context.Notices.Note(line.TrimEnd('\r')));

        var problems = HexBuildProblems.Read(said, built, builtFile);
        bool listed = problems.Any(problem => !problem.Warning);
        PostBuild(cancel, () => HexBuildProblems.Show(context, problems, folder, built != 0));

        if (built == 0)
            return true;

        PostBuild(cancel, () =>
        {
            building = false;
            context.Panels.RefreshSide(HexSide.Debug);
            context.Notices.Say(context.Strings.Said(listed ? "debugging.did_not_build_problems" : "debugging.did_not_build", ("project", name)), error: true);
        });
        return false;
    }

    // Build failures without a source location: linker symbols and compiler driver
    // options/toolchain setup. Keep the tool prefix so ordinary output is not an error.
    private static readonly System.Text.RegularExpressions.Regex LinkError = new(@"^(?:Error: .+|.*\berror LNK\d+: .+|(?:clang(?:\+\+|-cl)?|gcc|g\+\+|cc|c\+\+|ld(?:\.lld|\.gold)?|lld(?:-link)?|collect2)(?:-\d+(?:\.\d+)*)?(?:\.exe)?: (?:fatal )?error: .+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The first few lines of what a build said that are a linker's errors, each once.</summary>
    internal static IEnumerable<string> LinkErrorsIn(string said) =>
        said.Split('\n').Select(line => line.Trim()).Where(line => LinkError.IsMatch(line)).Distinct().Take(5);

    /// <summary>A program launched under a debugger that was started (null for one that wasn't), with the breakpoints there are.</summary>
    private Task LaunchUnder(DebugSession? session, string program, string folder, Dictionary<string, IReadOnlyCollection<DebugBreakpoint>> breakpointsNow, string kind, string? output = null, IReadOnlyList<string>? arguments = null) =>
        StartUnder(session, Path.GetFileName(program), kind, started => started.Launch(program, arguments ?? [], folder, breakpointsNow, kind, output));

    /// <summary>
    /// A debug session that was started (null for one that wasn't) made the one there is, and set going by a launch or
    /// an attach, what it's debugging said once it goes.
    /// </summary>
    private async Task StartUnder(DebugSession? session, string what, string kind, Func<DebugSession, Task<bool>> start, CancellationToken cancel = default)
    {
        if (session is null)
        {
            PostBuild(cancel, () =>
            {
                building = false;
                context.Notices.Say(context.Strings.Said("debugging.debugger_did_not_start"), error: true);
            });
            return;
        }

        if (cancel.IsCancellationRequested)
        {
            session.Dispose();
            return;
        }
        using var stop = cancel.Register(session.Stop);
        var exceptionPreferences = new Dictionary<string, bool>(context.Settings.Values.ExceptionBreakpoints, StringComparer.Ordinal);
        session.JustMyCode = context.Settings.Values.DebugJustMyCode;
        session.BreakOnException = filter => exceptionPreferences.GetValueOrDefault(kind + "/" + filter.Id, filter.Default);
        session.InitialFunctionBreakpoints = Volatile.Read(ref activeFunctionBreakpoints);

        // A condition and a tracepoint's expressions go the same way as a watch's, the names of the file they're in known.
        // Set before the launch, the breakpoints it's given are sent without their conditions then, Hex checks them
        HexDebugEvaluator? through = kind == DebugSession.CORECLR ? new HexDebugEvaluator(session.EvaluateSaying, context.Strings.Said, session.Variables) : null;
        if (through is not null)
            session.Evaluates = (expression, frame) => through.Evaluate(expression, frame.Id, ScopeOf(frame.File));

        PostBuild(cancel, () =>
        {
            (debugging, endedByException, debuggingKind, stoppingByHand) = (session, false, kind, false);
            evaluator = through;
        });
        bool launched = !cancel.IsCancellationRequested && await start(session);
        if (cancel.IsCancellationRequested)
        {
            session.Dispose();
            return;
        }
        PostBuild(cancel, () =>
        {
            building = false;
            if (launched)
            {
                context.Notices.Say(context.Strings.Said("debugging.debugging", ("program", what)));
                context.Panels.ShowSide(HexSide.Debug);
            }
            else
            {
                context.Notices.Say(context.Strings.Said(session.TerminatesDebuggeeOnStop ? "debugging.launch_failed" : "debugging.attach_failed", ("why", $"{session.Failed}")), error: true);
            }
        });
    }

    // Every file of code with changes saved, the build reads them off the disk
    private void SaveAllCode() => context.Documents.SaveAll(open => open.IsText && open.Path is not null && (open.Modified || open.CodeEdited));

    /// <summary>
    /// Shift+F5, the program and the debugger ended. With nothing being debugged it's a program run without the debugger
    /// that's stopped (Ctrl+F5, a run task), or a test run waiting for its debugger, the way Visual Studio's Stop is.
    /// </summary>
    internal void StopDebugging()
    {
        FinishCTestDebug(ctestDebugRun);
        if (debugBuild is not null)
        {
            CancelDebugBuild();
            building = false;
            context.Panels.RefreshSide(HexSide.Debug);
            context.Notices.Say(context.Strings.Said("debugging.stopping"));
        }
        bool stoppedTest = StopTestRun();
        if (debugging is null)
        {
            FinishPythonTestDebug(pythonTestDebugRun);
            if (!stoppedTest)
                Tasks?.StopRuns();
            return;
        }

        stoppingByHand = true;
        debugging.Stop();
        context.Notices.Say(context.Strings.Said("debugging.stopping"));
    }

    /// <summary>Ctrl+Shift+F5, stopped and started again.</summary>
    internal void RestartDebugging()
    {
        if (restart is not { } again)
            return;

        // A test run being debugged is ended with its host, or the new one would run beside it
        CancelTestRun(finish: false);
        EndDebugging();
        again();
    }

    internal void StepOver() { if (InstructionStepping) debugging?.StepInstructionOver(); else debugging?.StepOver(); }

    internal void StepIn() { if (InstructionStepping) debugging?.StepInstructionIn(); else debugging?.StepIn(); }

    internal void StepOut() => debugging?.StepOut();

    /// <summary>
    /// On from where it stopped. Stopped on a D exception there's nowhere to go on to, druntime leaves an exception to a
    /// debugger that's there and halts, so the program is ended and what ended it said. F5 stopped on it again for ever.
    /// </summary>
    private void GoOn()
    {
        if (dStoppedOn is { } what)
        {
            string ended = Path.GetFileNameWithoutExtension(debuggedProject);
            StopDebugging();
            context.Notices.Say(context.Strings.Said("debugging.d_ended", ("project", ended), ("what", what)), error: true);
            return;
        }

        debugging!.Continue();
    }

    // What kind of D exception it's stopped on, null while it isn't
    private string? dStoppedOn;

    /// <summary>The debug bar's first button, on from where it stopped, or a pause where it is while it runs.</summary>
    internal void ContinueOrPause()
    {
        if (DebugStopped)
            GoOn();
        else if (debugging is { State: DebugState.Running } running)
            running.Pause();
    }

    /* What happened, every tick */

    /// <summary>Every tick, what the debugger and the work off the editor's thread had to say.</summary>
    internal void Update()
    {
        while (debugWork.TryDequeue(out Action? work))
            work();
        UpdateModuleView();

        // What the program wrote, while it goes and for a moment after
        ReadProgramOutput();
        KeepTip();
        if (debugging is null)
            return;

        while (debugging?.TryTake(out DebugEvent happened) == true)
        {
            switch (happened.Kind)
            {
                case DebugEventKind.BreakpointsChanged:
                    RefreshInstructionMarks();
                    RefreshSourceBreakpointMarks();
                    context.Panels.RefreshSide(HexSide.Debug);
                    break;
                case DebugEventKind.Stopped:
                    Stopped(happened);
                    break;
                case DebugEventKind.Continued:
                    if (!tipPinned)
                        CloseTip();
                    variables.Reset();
                    dStoppedOn = null;
                    ClearExecutionLine();
                    stoppedFrames = [];
                    context.Panels.RefreshSide(HexSide.Debug);
                    break;
                case DebugEventKind.Output:
                    // lldb-dap's own console talk (how to use its console, the binary it set, the process it attached to)
                    // is VS Code's debug console's, not the program's, and it buried what the program wrote
                    int tab = happened.Text.IndexOf('\t');
                    if (debuggingKind != DebugSession.CORECLR && happened.Text.AsSpan(0, Math.Max(0, tab)).SequenceEqual("console"))
                        break;
                    foreach (string line in happened.Text[(tab + 1)..].TrimEnd('\r', '\n').Split('\n'))
                        context.Notices.Note(line.TrimEnd('\r'));
                    break;
                case DebugEventKind.Ended:
                    SayEnded(happened);
                    FinishCTestDebug(ctestDebugRun, !stoppingByHand && !endedByException && happened.Text.Length == 0
                        ? ctestDebugRun?.Test?.DebugResult(happened.ExitCode, ctestDebugRun.Time.Elapsed.TotalSeconds) : null);
                    EndDebugging();
                    return;
            }
        }
    }

    private void Stopped(DebugEvent happened)
    {
        if (GoOnFromHostStop(happened))
            return;

        variables.Reset();
        stoppedFrames = happened.Frames;
        stoppedFrame = 0;
        stops++;
        LoadThreads();
        frameNames.Clear();
        showingDisassembly &= IsDisassemblyPath(context.Documents.Current.Path);
        // The reason alone for the usual stops, lldb's text after it said it again ("step, step in")
        string why = happened.Text;
        int comma = why.IndexOf(", ", StringComparison.Ordinal);
        string reason = comma >= 0 ? why[..comma] : why;
        if (reason is "breakpoint" or "step" or "pause" or "entry" or "goto" or "function breakpoint" or "data breakpoint")
            why = reason;
        if (why.StartsWith("exception", StringComparison.Ordinal))
        {
            endedByException = happened.Exception?.Unhandled ?? true;
            stoppedOn = happened.Exception;
            SayException(happened.Exception);
        }
        else
        {
            stoppedOn = null;
        }
        string? dWhat = DExceptionOf(stoppedFrames);
        dStoppedOn = dWhat;

        // The frame of the program's own code nearest the top, the debugger's steps into the framework go past that
        for (int i = 0; !showingDisassembly && i < stoppedFrames.Count; i++)
        {
            if (OwnCode(stoppedFrames[i]))
            {
                stoppedFrame = i;
                break;
            }
        }

        ShowFrame(stoppedFrame);
        // A step says only where it got to, there's no news in having stepped. A D exception says what kind it was, and a
        // .NET one its type and message
        string said = why == "step" ? "debugging.stepped_to" : dWhat is not null ? "debugging.d_stopped" : stoppedOn is not null ? "debugging.exception_at" : "debugging.stopped_at";
        string what = dWhat ?? (stoppedOn is { } thrown ? $"{thrown.Type}, {thrown.Message}" : string.Empty);
        if (happened.ConditionFailed is { } failed)
            context.Notices.Say(ConditionFailed(failed), error: true);
        else
            context.Notices.Say(stoppedFrames.Count > 0 && stoppedFrames[stoppedFrame].File is { } at
                ? context.Strings.Said(said, ("why", why), ("what", what), ("file", Path.GetFileName(at)), ("line", (stoppedFrames[stoppedFrame].Line + 1).ToString()))
                : context.Strings.Said("debugging.stopped", ("why", what.Length > 0 ? what : why)), error: stoppedOn is not null);
        context.Panels.ShowSide(HexSide.Debug);
        context.Redraw();
    }

    private bool CanShowCurrentFrame() => DebugStopped && stoppedFrame >= 0 && stoppedFrame < stoppedFrames.Count
        && (stoppedFrames[stoppedFrame].Source is {} source && debugging?.OwnsSource(source) == true
            || stoppedFrames[stoppedFrame].File is {} file && File.Exists(file));

    private void ShowCurrentFrame()
    {
        if (CanShowCurrentFrame()) { showingDisassembly = showingMemory = false; ShowFrame(stoppedFrame); }
    }

    /// <summary>A frame of the stack looked at, its file open at its line, lit.</summary>
    private void ShowFrame(int index)
    {
        if (index < 0 || index >= stoppedFrames.Count)
            return;

        disassemblyRequest++;
        memoryRequest++;
        debuggerSourceRequest++;
        memoryAsking = false;
        RetireFrameTips();
        variables.Reset();
        stoppedFrame = index;
        DebugFrame frame = stoppedFrames[index];
        ClearExecutionLine();
        if (showingMemory && IsCurrentMemoryView() && memoryInput is not null && CanShowMemory())
            ReadMemoryView(memoryInput, memoryOffset, stops == memoryReferenceStop ? memoryReference : null);
        else if (CanShowDisassembly() && showingDisassembly && IsDisassemblyPath(context.Documents.Current.Path))
            ShowDisassembly();
        else if (frame.Source is {} source && debugging?.OwnsSource(source) == true)
            ShowDebuggerSource(frame, source);
        else if (CanShowDisassembly() && (frame.File is null || !File.Exists(frame.File)))
            ShowDisassembly();
        else if (frame.File is { } file && File.Exists(file))
        {
            showingDisassembly = false;
            context.Documents.OpenAt(file, frame.Line, frame.Column);
            if (context.Documents.Current is { Path: { } open } shown && string.Equals(Path.GetFullPath(open), Path.GetFullPath(file), PlatformPaths.Comparison))
                shown.Editor.ExecutionLine = frame.Line;
        }
        RefreshPinnedTip();
        context.Panels.RefreshSide(HexSide.Debug);
    }

    private void ClearExecutionLine()
    {
        foreach (HexDocument open in context.Documents.All)
            open.Editor.ExecutionLine = -1;
    }

    internal void EndDebugging() => EndDebugging(terminate: false);

    private void EndDebugging(bool terminate)
    {
        var pythonTests = pythonTestDebugRun;
        FinishPythonTestDebug(pythonTests, cleanup: false);
        FinishCTestDebug(ctestDebugRun);
        CancelDebugBuild();
        CloseTip();
        variables.Reset();
        evaluator = null;
        stoppedOn = null;
        scopes.Clear();
        globalUsings.Clear();
        ForgetProgramOutput();
        DebugSession? ending = debugging;
        ClearInstructionView();
        ClearMemoryView();
        ClearModuleView();
        ClearDebuggerSources();
        debugging = null;
        showingDisassembly = false;
        disassemblyRequest++;
        building = false;
        stoppedFrames = [];
        ClearExecutionLine();
        context.Panels.RefreshSide(HexSide.Debug);
        if (ending is not null || pythonTests is not null)
            _ = Task.Run(async () =>
            {
                try { if (terminate && ending is not null) await ending.StopAsync().ConfigureAwait(false); }
                finally { ending?.Dispose(); DeletePythonTestFiles(pythonTests); }
            });
    }

    /* The Debug side, the stack, the variables and the breakpoints */

    // The variables of the frame looked at, asked for once a stop and kept, null while they're coming
    private readonly HexDebugVariables variables;

    private List<UITreeItem> DebugSide()
    {
        var items = new List<UITreeItem>();

        if (building)
            items.Add(NoteItem(attachingProcess ? context.Strings.Said("debugging.attaching", ("process", debuggedProject))
                : context.Strings.Said("debugging.building_side", ("project", Path.GetFileName(debuggedProject)))));
        else if (debugging is null)
            items.Add(NoteItem(context.Strings.Said("debugging.nothing_running")));
        else if (!DebugStopped)
            items.Add(NoteItem(context.Strings.Said("debugging.running", ("project", Path.GetFileNameWithoutExtension(debuggedProject)))));

        if (DebugStopped && stoppedOn is { } thrown)
            items.Add(ExceptionSection(thrown));

        if (DebugStopped && threads.Count > 0) items.Add(ThreadSection());

        if (DebugStopped && stoppedFrames.Count > 0)
        {
            var stack = new UITreeItem(context.Strings.Said("debugging.call_stack"), "debug-stackframe") { Expandable = true, Expanded = true, Children = [] };
            for (int i = 0; i < stoppedFrames.Count;)
            {
                // A run of frames with no code of the program's (the runtime's, the system's) folded into one row, the
                // way VS Code folds external code. Six rows of druntime and kernel32 buried the two that mattered
                int run = i;
                while (run < stoppedFrames.Count && !OwnCode(stoppedFrames[run]))
                    run++;

                if (run - i >= 2)
                {
                    var external = new UITreeItem(context.Strings.Said("debugging.external_code", ("frames", HexText.Count("frame", run - i))), "debug-stackframe-dot")
                    {
                        Color = context.Panels.Dim,
                        Expandable = true,
                        Children = []
                    };
                    for (int j = i; j < run; j++)
                        external.Children.Add(FrameItem(j));
                    stack.Children.Add(external);
                    i = run;
                    continue;
                }

                stack.Children.Add(FrameItem(i));
                i++;
            }

            items.Add(stack);

            DebugSession frameSession = debugging!;
            bool formatHex = hexadecimalLocals;
            var locals = new UITreeItem(context.Strings.Said("debugging.variables"), "symbol-variable")
            {
                Expandable = true, Expanded = true,
                Children = variables.Rows(debugging!, stoppedFrames[stoppedFrame].Id, frameSession.Scopes, reference => frameSession.Variables(reference, formatHex), frameSession.CanSetVariable ? EditVariable : null, formatHex)
            };
            items.Add(locals);
        }

        if (watches.Count > 0 || DebugStopped)
            items.Add(WatchSection());
        if (ImmediateSection() is { } evaluated)
            items.Add(evaluated);

        items.Add(BreakpointsSection());
        if (modulesShown && debugging is { State: not DebugState.Ended }) items.Add(ModulesSection());
        return items;
    }

    // A row that only says something
    private UITreeItem NoteItem(string text) => HexNotes.Note(text, context.Panels.Dim);

    /// <summary>
    /// Whether a frame is in a file of the program's own that's there to show, in the workspace or open. druntime's
    /// entrypoint.d is on the disk too, with the compiler, and it isn't anybody's code to step through.
    /// </summary>
    private bool OwnCode(DebugFrame frame) =>
        frame.Source is {} source && debugging?.OwnsSource(source) == true && source.PresentationHint != "deemphasize"
        || frame.File is { } file && File.Exists(file)
        && (context.Workspace.Roots.Any(root => Path.GetFullPath(file).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, PlatformPaths.Comparison))
            || context.Documents.All.Any(open => open.Path is { } path && PlatformPaths.Same(path, file)));

    // A frame of the stack a row, pressed it's the one looked at
    private UITreeItem FrameItem(int index)
    {
        DebugFrame frame = stoppedFrames[index];
        var session = debugging;
        int stop = stops;
        return new UITreeItem(FrameName(frame), index == stoppedFrame ? "debug-stackframe-focused" : "debug-stackframe-dot")
        {
            Detail = OwnCode(frame) && frame.Source is {} source ? $"{DebuggerSourceName(source)}:{frame.Line + 1}"
                : frame.File is { } file && OwnCode(frame) ? $"{Path.GetFileName(file)}:{frame.Line + 1}" : string.Empty,
            Color = OwnCode(frame) ? null : context.Panels.Dim,
            Tag = new SideTag(Pressed: () =>
            {
                if (DebugStopped && ReferenceEquals(debugging, session) && stops == stop) ShowFrame(index);
            })
        };
    }

    // The names of the frames of this stop worked out from their code, by the frame's id
    private readonly Dictionary<int, string> frameNames = [];

    /// <summary>
    /// What a frame is called, the debugger's name, or for one it named by its address (lldb with dmd's PDBs, which name
    /// main and nothing else) the function of its own file it's in, out of the outline.
    /// </summary>
    private string FrameName(DebugFrame frame)
    {
        if (!frame.Name.StartsWith("0x", StringComparison.Ordinal) || !OwnCode(frame) || frame.Source is not null)
            return frame.Name;
        if (frameNames.TryGetValue(frame.Id, out string? named))
            return named;

        string text;
        try
        {
            text = context.Documents.All.FirstOrDefault(open => open.Path is { } path && PlatformPaths.Same(path, frame.File!))?.Editor.Text ?? File.ReadAllText(frame.File!);
        }
        catch (IOException)
        {
            return frame.Name;
        }

        // The last function that starts at or above its line, the one the line is in
        string? function = null;
        foreach (OutlineEntry entry in HexOutline.Outline(text, frame.File))
        {
            if (entry.Line > frame.Line)
                break;
            if (entry.Icon == "symbol-method")
                function = entry.Name;
        }

        return frameNames[frame.Id] = function ?? frame.Name;
    }

    // Which debugger the session is under, DebugSession's kind of it
    private string debuggingKind = DebugSession.CORECLR;

    // Whether what it stopped on last was an exception, what ended the program when the debugger says it ended well
    private bool endedByException;

}
