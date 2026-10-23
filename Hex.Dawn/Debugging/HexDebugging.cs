using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

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
// settings say (debugger_path), next to the exe (netcoredbg/netcoredbg.exe), in the settings folder and on the PATH.
// Microsoft's own debugger comes with VS Code's C# extension and is only allowed in Microsoft's editors, so not that.
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
        SweepProgramOutputs();
        context.Documents.Opened += PrepareBreakpoints;
        context.Panels.AddSide(HexSide.Debug, context.Strings.Said("debugging.debug"), DebugSide);
        UICommands commands = context.Commands;
        commands.Add("Run/debug", context.Strings.Said("debugging.start_debugging"), DebugOrContinue, "F5", "debug-alt").IsVisible = () => !context.Documents.Current.InDesigner && !DebugStopped && (IsCSharp(context.Documents.Current.Path) || IsD(context.Documents.Current.Path));
        commands.Add("Run/debug", context.Strings.Said("debugging.continue"), DebugOrContinue, "F5", "debug-continue").IsVisible = () => DebugStopped;
        commands.Add("Run/debug", context.Strings.Said("debugging.step_over"), StepOver, "F10", "debug-step-over", UIKeyUse.BoundWhileTyping).IsEnabled = () => DebugStopped;
        commands.Add("Run/debug", context.Strings.Said("debugging.step_into"), StepIn, "F11", "debug-step-into").IsEnabled = () => DebugStopped;
        commands.Add("Run/debug", context.Strings.Said("debugging.step_out"), StepOut, "Shift+F11", "debug-step-out", UIKeyUse.BoundWhileTyping).IsEnabled = () => DebugStopped;
        commands.Add("Run/debug", context.Strings.Said("debugging.stop_debugging"), StopDebugging, "Shift+F5", "debug-stop", UIKeyUse.BoundWhileTyping).IsEnabled = () => Debugging;
        commands.Add("Run/debug", context.Strings.Said("debugging.restart_debugging"), RestartDebugging, "Ctrl+Shift+F5", "debug-restart", UIKeyUse.BoundWhileTyping).IsEnabled = () => Debugging;
        commands.Add("Run/debug", context.Strings.Said("debugging.toggle_breakpoint"), ToggleBreakpointHere, "F9", "debug-breakpoint", UIKeyUse.BoundWhileTyping).IsVisible = () => !context.Documents.Current.InDesigner && context.Documents.Current.IsText;
        commands.Add("Run/debug", context.Strings.Said("debugging.install_netcoredbg"), InstallNetcoredbg, icon: "cloud-download").IsVisible = () => NetcoredbgAsset() is not null && Netcoredbg() is null;
        AddWatchCommands();
    }

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

    /// <summary>Whether a program is being debugged, from the launch to its end.</summary>
    internal bool Debugging => debugging is { State: not DebugState.Ended } || building;

    /// <summary>Whether it's stopped somewhere, where the steps mean something.</summary>
    internal bool DebugStopped => debugging is { State: DebugState.Stopped };

    internal static bool IsCSharp(string? path) => path is not null && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /* Breakpoints */

    /// <summary>A file's editor shown its breakpoints and given the gutter to put them down with, for a .cs or a .d file.</summary>
    private void PrepareBreakpoints(HexDocument prepared)
    {
        if (!IsCSharp(prepared.Path) && !IsD(prepared.Path))
            return;

        string full = Path.GetFullPath(prepared.Path!);
        prepared.Editor.Breakpoints = BreakpointsOf(full);
        prepared.Editor.OnGutterPressed = line => ToggleBreakpoint(full, line);

        // Once for each document, opened again it's the same one
        UITextDocument document = prepared.Editor.Document;
        if (following.Add(document))
            document.LinesMoved += (at, by) => MoveBreakpoints(full, document, at, by);
    }

    // The documents whose breakpoints follow their lines already
    private readonly HashSet<UITextDocument> following = [];

    /// <summary>
    /// Lines put in or taken out of a file, its breakpoints moved along with the lines they were on, the way VS Code's
    /// are. A line opened above one by Enter at its start used to leave the breakpoint on the blank line.
    /// </summary>
    private void MoveBreakpoints(string full, UITextDocument document, UITextPosition at, int by)
    {
        if (by == 0 || !breakpoints.TryGetValue(full, out SortedSet<int>? lines) || lines.Count == 0)
            return;

        // Put in at the start of a line (nothing but its indent before), the line itself went down with the rest
        bool fromStart = by > 0 && at.Line < document.LineCount && string.IsNullOrWhiteSpace(document.LineAt(at.Line));
        var moved = new SortedSet<int>();
        foreach (int line in lines)
        {
            if (by > 0)
                moved.Add(line > at.Line || (line == at.Line && fromStart) ? line + by : line);
            else
                moved.Add(line > at.Line ? line + by : line > at.Line + by ? at.Line + by : line);
        }

        if (moved.SetEquals(lines))
            return;

        lines.Clear();
        lines.UnionWith(moved);
        debugging?.SetBreakpoints(full, lines);
        context.Panels.RefreshSide(HexSide.Debug);
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
        if (!shown.IsText || (!IsCSharp(shown.Path) && !IsD(shown.Path)))
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

        debugging?.SetBreakpoints(full, lines);
        context.Panels.RefreshSide(HexSide.Debug);
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
    /// The projects F5 could mean. The one over the file on screen first and alone when there is one, the first .csproj
    /// going up from its folder, otherwise every one in the workspace.
    /// </summary>
    private List<string> ProjectsToDebug()
    {
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

    /// <summary>
    /// netcoredbg downloaded from its newest release and unpacked into Hex's own folder of things (where it's looked for
    /// first), on a card while it goes. It said where to get it and left the rest to whoever read that.
    /// </summary>
    internal void InstallNetcoredbg()
    {
        if (NetcoredbgAsset() is not { } asset)
        {
            context.Windows.OpenInBrowser($"https://github.com/{NETCOREDBG_REPO}/releases");
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
            if (item.State == WorkState.Succeeded && Netcoredbg() is { } there)
                context.Notices.Say(context.Strings.Said("debugging.netcoredbg_installed", ("path", there)));
            else
                foreach (string said in item.Output)
                    context.Notices.Note(said, error: true);
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

    /// <summary>Builds a project, then runs what it made under the debugger with the breakpoints there are, off the editor's thread.</summary>
    private void StartDebugging(string project)
    {
        if (Netcoredbg() is not { } debugger)
        {
            context.Notices.Say(NoDebugger, error: true);
            return;
        }

        SaveAllCode();
        building = true;
        debuggedProject = project;
        restart = () => StartDebugging(project);
        context.Notices.Note(context.Strings.Said("debugging.building_to_debug", ("project", Path.GetFileName(project))));
        context.Panels.ShowOutput();
        context.Panels.ShowSide(HexSide.Debug);
        context.Notices.Say(context.Strings.Said("debugging.building", ("project", Path.GetFileName(project))));

        var breakpointsNow = breakpoints.Where(pair => pair.Value.Count > 0).ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<int>)pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(() => BuildAndLaunch(project, debugger, breakpointsNow));
    }

    private async Task BuildAndLaunch(string project, string debugger, Dictionary<string, IReadOnlyCollection<int>> breakpointsNow)
    {
        string folder = Path.GetDirectoryName(project)!;
        var (built, buildSaid) = await Dotnet(folder, "build", project, "-c", "Debug", "-nologo", "-v", "q");
        if (!ReportBuild(buildSaid, folder, built, Path.GetFileName(project)))
            return;

        var (_, target) = await Dotnet(folder, "msbuild", project, "-getProperty:TargetPath", "-p:Configuration=Debug", "-nologo");
        string program = target.Trim();
        if (!File.Exists(program))
        {
            debugWork.Enqueue(() =>
            {
                building = false;
                context.Notices.Say(context.Strings.Said("debugging.no_program", ("project", Path.GetFileName(project)), ("program", program)), error: true);
            });
            return;
        }

        await LaunchUnder(DebugSession.Start(debugger, ["--interpreter=vscode"]), program, folder, breakpointsNow, DebugSession.CORECLR);
    }

    /// <summary>
    /// What a build for debugging said, its lines in the output and what it says is wrong in the Problems panel and under
    /// the code the way a build task's is (they were lines in the output nobody looked at), and a build that failed said
    /// so. Whether it built.
    /// </summary>
    private bool ReportBuild(string said, string folder, int built, string name, string? builtFile = null)
    {
        foreach (string line in said.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            debugWork.Enqueue(() => context.Notices.Note(line.TrimEnd('\r')));

        var problems = new List<TerminalProblem>();
        var reader = new TerminalProblemReader(TerminalProblemMatcher.Shipped, problems.Add);
        reader.Feed(said.ReplaceLineEndings("\n") + "\n\n\n");
        // A link that failed says where in no file, its errors are put on the file that was built instead, the way Visual
        // Studio lists a linker's errors. "entry point must be defined" was only ever in the output
        if (built != 0 && builtFile is not null && !problems.Any(problem => !problem.Warning))
        {
            foreach (string line in LinkErrorsIn(said))
                problems.Add(new TerminalProblem(builtFile, 1, 1, line, false, "link"));
        }

        bool listed = problems.Any(problem => !problem.Warning);
        debugWork.Enqueue(() =>
        {
            context.Terminals.ClearProblems();
            foreach (TerminalProblem problem in problems)
                context.Terminals.AddProblem(Path.IsPathRooted(problem.File) ? problem.File : Path.Combine(folder, problem.File), problem.Line, problem.Column, problem.Message, problem.Warning);
            if (built != 0 && listed)
                context.Panels.ShowProblems();
        });

        if (built == 0)
            return true;

        debugWork.Enqueue(() =>
        {
            building = false;
            context.Panels.RefreshSide(HexSide.Debug);
            context.Notices.Say(context.Strings.Said(listed ? "debugging.did_not_build_problems" : "debugging.did_not_build", ("project", name)), error: true);
        });
        return false;
    }

    // A linker's errors as dmd and MSVC's link say them
    private static readonly System.Text.RegularExpressions.Regex LinkError = new(@"^(?:Error: .+|.*\berror LNK\d+: .+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The first few lines of what a build said that are a linker's errors, each once.</summary>
    internal static IEnumerable<string> LinkErrorsIn(string said) =>
        said.Split('\n').Select(line => line.Trim()).Where(line => LinkError.IsMatch(line)).Distinct().Take(5);

    /// <summary>A program launched under a debugger that was started (null for one that wasn't), with the breakpoints there are.</summary>
    private async Task LaunchUnder(DebugSession? session, string program, string folder, Dictionary<string, IReadOnlyCollection<int>> breakpointsNow, string kind, string? output = null)
    {
        if (session is null)
        {
            debugWork.Enqueue(() =>
            {
                building = false;
                context.Notices.Say(context.Strings.Said("debugging.debugger_did_not_start"), error: true);
            });
            return;
        }

        debugWork.Enqueue(() => (debugging, endedByException, debuggingKind) = (session, false, kind));
        bool launched = await session.Launch(program, [], folder, breakpointsNow, kind, output);
        debugWork.Enqueue(() =>
        {
            building = false;
            if (launched)
            {
                context.Notices.Say(context.Strings.Said("debugging.debugging", ("program", Path.GetFileName(program))));
                context.Panels.ShowSide(HexSide.Debug);
            }
            else
            {
                context.Notices.Say(context.Strings.Said("debugging.launch_failed", ("why", $"{session.Failed}")), error: true);
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
    private void SaveAllCode() => context.Documents.SaveAll(open => open.IsText && open.Path is not null && (open.Modified || open.CodeEdited));

    /// <summary>Shift+F5, the program and the debugger ended.</summary>
    internal void StopDebugging()
    {
        if (debugging is null)
            return;

        debugging.Stop();
        context.Notices.Say(context.Strings.Said("debugging.stopping"));
    }

    /// <summary>Ctrl+Shift+F5, stopped and started again.</summary>
    internal void RestartDebugging()
    {
        if (restart is not { } again)
            return;

        EndDebugging();
        again();
    }

    internal void StepOver() => debugging?.StepOver();

    internal void StepIn() => debugging?.StepIn();

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

        // What the program wrote, while it goes and for a moment after
        ReadProgramOutput();
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
                    // netcoredbg says 0 for a program its unhandled exception ended, it's said what ended it instead
                    string ended = Path.GetFileNameWithoutExtension(debuggedProject);
                    context.Notices.Say(endedByException && happened.ExitCode <= 0
                        ? context.Strings.Said("debugging.ended_exception", ("project", ended))
                        : happened.ExitCode >= 0
                            ? context.Strings.Said("debugging.ended_code", ("project", ended), ("code", happened.ExitCode.ToString()))
                            : context.Strings.Said("debugging.ended", ("project", ended)));
                    EndDebugging();
                    return;
            }
        }
    }

    private void Stopped(DebugEvent happened)
    {
        stoppedFrames = happened.Frames;
        stoppedFrame = 0;
        stops++;
        frameNames.Clear();
        // The reason alone for the usual stops, lldb's text after it said it again ("step, step in")
        string why = happened.Text;
        int comma = why.IndexOf(", ", StringComparison.Ordinal);
        string reason = comma >= 0 ? why[..comma] : why;
        if (reason is "breakpoint" or "step" or "pause" or "entry" or "goto" or "function breakpoint" or "data breakpoint")
            why = reason;
        if (why.StartsWith("exception", StringComparison.Ordinal))
            endedByException = true;
        string? dWhat = DExceptionOf(stoppedFrames);
        dStoppedOn = dWhat;

        // The frame of the program's own code nearest the top, the debugger's steps into the framework go past that
        for (int i = 0; i < stoppedFrames.Count; i++)
        {
            if (OwnCode(stoppedFrames[i]))
            {
                stoppedFrame = i;
                break;
            }
        }

        ShowFrame(stoppedFrame);
        // A step says only where it got to, there's no news in having stepped. A D exception says what kind it was
        string said = why == "step" ? "debugging.stepped_to" : dWhat is not null ? "debugging.d_stopped" : "debugging.stopped_at";
        context.Notices.Say(stoppedFrames.Count > 0 && stoppedFrames[stoppedFrame].File is { } at
            ? context.Strings.Said(said, ("why", why), ("what", dWhat ?? string.Empty), ("file", Path.GetFileName(at)), ("line", (stoppedFrames[stoppedFrame].Line + 1).ToString()))
            : context.Strings.Said("debugging.stopped", ("why", dWhat ?? why)));
        context.Panels.ShowSide(HexSide.Debug);
        context.Redraw();
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

        context.Documents.OpenAt(file, frame.Line, frame.Column);
        if (context.Documents.Current is { Path: { } open } shown && string.Equals(Path.GetFullPath(open), Path.GetFullPath(file), PlatformPaths.Comparison))
            shown.Editor.ExecutionLine = frame.Line;
        variablesShown = null;
        context.Panels.RefreshSide(HexSide.Debug);
    }

    private void ClearExecutionLine()
    {
        foreach (HexDocument open in context.Documents.All)
            open.Editor.ExecutionLine = -1;
    }

    internal void EndDebugging()
    {
        ForgetProgramOutput();
        DebugSession? ending = debugging;
        debugging = null;
        building = false;
        stoppedFrames = [];
        variablesShown = null;
        ClearExecutionLine();
        context.Panels.RefreshSide(HexSide.Debug);
        if (ending is not null)
            _ = Task.Run(ending.Dispose);
    }

    /* The Debug side, the stack, the variables and the breakpoints */

    // The variables of the frame looked at, asked for once a stop and kept, null while they're coming
    private List<UITreeItem>? variablesShown;
    private int variablesFor = -1;

    private List<UITreeItem> DebugSide()
    {
        var items = new List<UITreeItem>();

        if (building)
            items.Add(NoteItem(context.Strings.Said("debugging.building_side", ("project", Path.GetFileName(debuggedProject)))));
        else if (debugging is null)
            items.Add(NoteItem(context.Strings.Said("debugging.nothing_running")));
        else if (!DebugStopped)
            items.Add(NoteItem(context.Strings.Said("debugging.running", ("project", Path.GetFileNameWithoutExtension(debuggedProject)))));

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

            var variables = new UITreeItem(context.Strings.Said("debugging.variables"), "symbol-variable") { Expandable = true, Expanded = true };
            if (variablesShown is null || variablesFor != stoppedFrame)
            {
                variables.Children = [NoteItem(context.Strings.Said("debugging.asking"))];
                AskVariables(stoppedFrames[stoppedFrame].Id, stoppedFrame);
            }
            else
            {
                variables.Children = variablesShown;
            }

            items.Add(variables);
        }

        if (watches.Count > 0 || DebugStopped)
            items.Add(WatchSection());

        var points = new UITreeItem(context.Strings.Said("debugging.breakpoints"), "debug-breakpoint") { Expandable = true, Expanded = true, Children = [] };
        foreach (var (file, lines) in breakpoints.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (int line in lines)
            {
                string at = file;
                int on = line;
                points.Children.Add(new UITreeItem($"{Path.GetFileName(file)}:{line + 1}", "debug-breakpoint")
                {
                    IconColor = context.Panels.Error,
                    Detail = context.Workspace.Relative(file),
                    Tag = new SideTag(Pressed: () => context.Documents.OpenAt(at, on, 0), Menu: _ => ToggleBreakpoint(at, on))
                });
            }
        }

        if (points.Children.Count == 0)
            points.Children.Add(NoteItem(context.Strings.Said("debugging.breakpoints_hint")));
        items.Add(points);
        return items;
    }

    // A row that only says something
    private UITreeItem NoteItem(string text) => new(text) { Color = context.Panels.Dim };

    /// <summary>
    /// Whether a frame is in a file of the program's own that's there to show, in the workspace or open. druntime's
    /// entrypoint.d is on the disk too, with the compiler, and it isn't anybody's code to step through.
    /// </summary>
    private bool OwnCode(DebugFrame frame) =>
        frame.File is { } file && File.Exists(file)
        && (context.Workspace.Roots.Any(root => Path.GetFullPath(file).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, PlatformPaths.Comparison))
            || context.Documents.All.Any(open => open.Path is { } path && PlatformPaths.Same(path, file)));

    // A frame of the stack a row, pressed it's the one looked at
    private UITreeItem FrameItem(int index)
    {
        DebugFrame frame = stoppedFrames[index];
        return new UITreeItem(FrameName(frame), index == stoppedFrame ? "debug-stackframe-focused" : "debug-stackframe-dot")
        {
            Detail = frame.File is { } file && OwnCode(frame) ? $"{Path.GetFileName(file)}:{frame.Line + 1}" : string.Empty,
            Color = OwnCode(frame) ? null : context.Panels.Dim,
            Tag = new SideTag(Pressed: () => ShowFrame(index))
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
        if (!frame.Name.StartsWith("0x", StringComparison.Ordinal) || !OwnCode(frame))
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
                // The first scope's variables as they are (the locals), the rest folded under their names. lldb-dap says
                // every global of the program and the registers too, and flat they buried the locals under __imp_ rows
                IReadOnlyList<DebugScope> scopes = await session.Scopes(frameId);
                for (int i = 0; i < scopes.Count; i++)
                {
                    if (i > 0)
                    {
                        rows.Add(Expanding(new UITreeItem(scopes[i].Name, "symbol-namespace") { Expandable = true }, session, scopes[i].Reference));
                        continue;
                    }

                    // Not the compiler's own (dmd's __r103 and __key104 for a foreach), nobody wrote those
                    foreach (DebugVariable variable in await session.Variables(scopes[i].Reference))
                    {
                        if (!variable.Name.StartsWith("__", StringComparison.Ordinal))
                            rows.Add(VariableItem(session, variable));
                    }
                }
            }
            catch (Exception e)
            {
                rows.Add(new UITreeItem(context.Strings.Said("debugging.could_not_ask", ("why", e.Message))) { Color = context.Panels.Dim });
            }

            debugWork.Enqueue(() =>
            {
                if (variablesFor != frameIndex)
                    return;
                variablesShown = rows.Count > 0 ? rows : [new UITreeItem(context.Strings.Said("debugging.nothing_in_scope")) { Color = context.Panels.Dim }];
                context.Panels.RefreshSide(HexSide.Debug);
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

        return variable.Reference > 0 ? Expanding(item, session, variable.Reference) : item;
    }

    // A row whose children are the variables of a reference (a variable's, a scope's), asked for as it's opened
    private UITreeItem Expanding(UITreeItem item, DebugSession session, int reference)
    {
        item.Tag = new SideTag(Expanding: opened =>
        {
            if (opened.Children is not null)
                return;
            opened.Children = [new UITreeItem(context.Strings.Said("debugging.asking")) { Color = context.Panels.Dim }];
            _ = Task.Run(async () =>
            {
                var inside = new List<UITreeItem>();
                try
                {
                    foreach (DebugVariable child in await session.Variables(reference))
                        inside.Add(VariableItem(session, child));
                }
                catch (Exception)
                {
                    // gone on since, nothing to show
                }

                debugWork.Enqueue(() =>
                {
                    opened.Children = inside;
                    context.Panels.RefreshRows();
                });
            });
        });

        return item;
    }
}
