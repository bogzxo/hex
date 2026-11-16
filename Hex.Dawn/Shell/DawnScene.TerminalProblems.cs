namespace Hex.Dawn;

// What the terminal put in the Problems panel, a compiler's errors read off its output by a problem matcher (the
// engine's TerminalProblems) or put there by a plugin with terminal.problem. Kept apart from the file's own and from F8's,
// shown after them, cleared when the terminal asks or when a new run of the same program starts saying things. A file
// that's open has them underlined in its code as well, the way a build's errors are in Visual Studio, until the build
// runs again or the file is saved (what the build said of it is old news then, its server and its check say it now).
internal sealed partial class DawnScene
{
    private readonly List<HexProblem> terminalProblems = [];

    private static readonly System.Text.RegularExpressions.Regex BuildCode = new(@"^(?<code>[A-Z]+\d+): (?<said>.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>What a compiler says with its code after it (CS1061: what, as what (CS1061)), the way a language server says it.</summary>
    internal static string CodeAfter(string message) =>
        BuildCode.Match(message) is { Success: true } coded ? $"{coded.Groups["said"].Value} ({coded.Groups["code"].Value})" : message;

    // Whether the files that are open have to be told again what the terminal says about them
    private bool terminalProblemsChanged;

    private void AddTerminalProblem(string path, int line, int column, string message, bool warning, bool read = false)
    {
        // A path said relative to where the shell was is taken from the terminal's folder
        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(terminalPanel.FocusedView?.WorkingDirectory ?? TerminalFolder(), path));

        // One read off the output whose file isn't there is half a path the console broke at its edge that couldn't be
        // put back together, a row nobody can go to. What has no folder and no extension to it (MSBUILD, CSC) is the tool
        // that said it and stays
        if (read && !File.Exists(full) && !Directory.Exists(full) && (path.IndexOfAny(['/', '\\', '.']) >= 0))
            return;

        // A compiler's code after what it says, the way a language server says it (CS1061: what, read as what (CS1061)),
        // so it sits in its own column in Problems and the build's word on a line is the server's said once
        message = CodeAfter(message);
        var problem = new HexProblem(full, Math.Max(0, line - 1), Math.Max(0, column - 1), message, warning);

        // A build says its warnings as it goes and again at the end, they're the one problem
        if (terminalProblems.Any(was => was.Line == problem.Line && was.Column == problem.Column && was.Warning == problem.Warning && was.Text == problem.Text && PlatformPaths.Same(was.Path, problem.Path)))
            return;

        terminalProblems.Add(problem);
        if (OpenDocument(full) is { } open)
            builtVersions.TryAdd(open, open.Editor.Document.Version);
        problemsDirty = true;
        terminalProblemsChanged = true;
    }

    private void ClearTerminalProblems()
    {
        if (terminalProblems.Count == 0)
            return;

        terminalProblems.Clear();
        builtVersions.Clear();
        problemsDirty = true;
        terminalProblemsChanged = true;
    }

    /// <summary>A file was saved, what the terminal said about it goes.</summary>
    private void ForgetTerminalProblemsOf(HexDocument saved)
    {
        if (saved.Path is not { } path || terminalProblems.RemoveAll(problem => PlatformPaths.Same(problem.Path, path)) == 0)
            return;

        problemsDirty = true;
        terminalProblemsChanged = true;
    }

    /// <summary>
    /// What the terminal said about a document, underlined in its code with the rest of what's wrong with it. What its
    /// server says too on the same line with the same code is the server's, the build's word on it is left out. The two
    /// were listed one under the other, and the build's stayed after the server's had gone with the mistake.
    /// </summary>
    private IReadOnlyList<UICodeDiagnostic>? TerminalDiagnosticsOf(HexDocument of)
    {
        if (terminalProblems.Count == 0 || of.Path is not { } path)
            return null;

        IReadOnlyList<UICodeDiagnostic>? live = languages.ServerDiagnosticsOf(of);
        List<UICodeDiagnostic>? found = null;
        foreach (HexProblem problem in terminalProblems)
        {
            if (PlatformPaths.Same(problem.Path, path) && !SaidLive(live, problem))
                (found ??= []).Add(new UICodeDiagnostic(problem.Line, problem.Column, WordLength(of.Editor.Text, problem.Line, problem.Column), problem.Text, problem.Warning));
        }

        return found;
    }

    // What a compiler calls a problem, the code in brackets at the end of what it says (CS0029), empty for none
    private static readonly System.Text.RegularExpressions.Regex CodeAtEnd = new(@"\((?<code>[A-Za-z]+\d+)\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The code a problem is known by, what's in brackets at the end of what it says (CS0029), empty when it has none.</summary>
    internal static string CodeOf(string message) => CodeAtEnd.Match(message) is { Success: true } coded ? coded.Groups["code"].Value : string.Empty;

    /// <summary>Whether a server says what a build said, on the same line with the same code (or the same words, for a problem with no code).</summary>
    internal static bool SaidLive(IReadOnlyList<UICodeDiagnostic>? live, HexProblem problem)
    {
        if (live is not { Count: > 0 })
            return false;

        string code = CodeOf(problem.Text);
        foreach (UICodeDiagnostic diagnostic in live)
        {
            if (diagnostic.Line == problem.Line && (code.Length > 0 ? CodeOf(diagnostic.Message) == code : diagnostic.Message == problem.Text))
                return true;
        }

        return false;
    }

    // The version each open file was at as the build spoke of it (or its server first did, for one opened after), to
    // tell a word on the text the build saw from one on text changed since
    private readonly Dictionary<HexDocument, int> builtVersions = [];

    /// <summary>
    /// A file's server said what's wrong with it. When it says nothing is, the build's errors in it are older than that
    /// and go, they'd stay listed until the next build after the mistake was put right. Once the file's been changed
    /// since and the server's had the change, a build error the server no longer has on its line with its code goes
    /// too, the way Visual Studio drops one as IntelliSense agrees. A CS1503 stayed after its line was fixed because
    /// the file still had other errors. Warnings stay, a build's analyzers say things the server never does.
    /// </summary>
    private void ServerSaidOf(HexDocument of, IReadOnlyList<UICodeDiagnostic> found)
    {
        if (terminalProblems.Count == 0 || of.Path is not { } path)
            return;

        // A file opened after the build is as the build saw it the first time its server speaks
        int version = of.Editor.Document.Version;
        bool edited = false;
        if (!builtVersions.TryGetValue(of, out int built))
            builtVersions[of] = version;
        else
            edited = built != version && !languages.ChangePending(of);
        bool clean = !found.Any(diagnostic => !diagnostic.Warning);
        if (terminalProblems.RemoveAll(problem => PlatformPaths.Same(problem.Path, path) && BuildErrorGone(problem, found, clean, edited)) == 0)
            return;

        problemsDirty = true;
        terminalProblemsChanged = true;
    }

    /// <summary>
    /// Whether a build's problem in a file is over by what its server said, an error once the server says nothing's
    /// wrong, or once the file was changed and the server doesn't say it on its line with its code any more.
    /// </summary>
    internal static bool BuildErrorGone(HexProblem problem, IReadOnlyList<UICodeDiagnostic> found, bool clean, bool edited) =>
        !problem.Warning && (clean || edited && !SaidLive(found, problem));

    /// <summary>
    /// How long the word at a place in some text is, the rest of the line from it for a place on no word (a string, a
    /// bracket), one letter for a place past the end of its line.
    /// </summary>
    internal static int WordLength(string text, int line, int column)
    {
        int at = 0;
        for (int row = 0; row < line && at >= 0; row++)
            at = text.IndexOf('\n', at) is var next and >= 0 ? next + 1 : -1;
        if (at < 0)
            return 1;

        int end = text.IndexOf('\n', at) is var close and >= 0 ? close : text.Length;
        int from = at + column;
        if (from >= end)
            return 1;

        int to = from;
        while (to < end && (char.IsLetterOrDigit(text[to]) || text[to] == '_'))
            to++;
        return to > from ? to - from : Math.Max(1, text.AsSpan(from, end - from).TrimEnd().Length);
    }

    // The tasks running in a terminal, the count of commands it had done as it was sent, and who wants to know it ended
    private readonly List<(TerminalView View, int Before, Action<int, string> Finished)> taskWatches = [];

    /// <summary>A command sent to a terminal is watched until the shell says it's done, then how it ended and what it wrote are said.</summary>
    private void WatchTask(TerminalView view, Action<int, string>? finished)
    {
        if (finished is not null)
            taskWatches.Add((view, view.Screen.Finished, finished));
    }

    /// <summary>
    /// Called as a terminal's shell ends, true to keep its pane. A task that ends the shell itself (exit 3 in its
    /// command) keeps its tab with what it printed and says how it ended, the way VS Code does. The tab went the moment
    /// the shell did and took the output with it, and hex_task_output said the task had never run.
    /// </summary>
    private bool TaskShellEnded(TerminalView view, int code)
    {
        int at = taskWatches.FindIndex(watch => watch.View == view);
        if (at < 0)
            return false;

        Action<int, string> finished = taskWatches[at].Finished;
        taskWatches.RemoveAt(at);
        NoteTaskEnded(code);
        finished(code, view.Screen.LastOutput());
        view.Feed($"\r\n\u001b[2m{Said("terminal_page.task_ended", ("code", code.ToString()))}\u001b[0m\r\n");
        return true;
    }

    /// <summary>Every tick, the files that are open told again what the terminal says about them, once that changed.</summary>
    private void UpdateTerminalProblems()
    {
        // A task whose shell said it was done, after the problems it printed were read (they come before the prompt)
        for (int i = taskWatches.Count - 1; i >= 0; i--)
        {
            var (view, before, finished) = taskWatches[i];
            // A shell that ended is TaskShellEnded's business, unless its tab is gone already and nothing will hear of it
            if (!view.IsRunning)
            {
                if (!terminalPanel.Tabs.Any(tab => tab.Views.Contains(view)))
                    taskWatches.RemoveAt(i);
            }
            else if (view.Screen.Finished > before && view.Screen.AtPrompt)
            {
                taskWatches.RemoveAt(i);
                NoteTaskEnded(view.Screen.LastExit);
                finished(view.Screen.LastExit, view.Screen.LastOutput());
            }
        }

        if (!terminalProblemsChanged)
            return;

        terminalProblemsChanged = false;
        foreach (HexDocument open in documents)
        {
            if (open.IsText)
                ShowDiagnostics(open);
        }
    }
}
