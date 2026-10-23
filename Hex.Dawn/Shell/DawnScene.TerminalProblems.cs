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

    private void AddTerminalProblem(string path, int line, int column, string message, bool warning)
    {
        // A path said relative to where the shell was is taken from the terminal's folder
        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(terminalPanel.FocusedView?.WorkingDirectory ?? TerminalFolder(), path));

        // A compiler's code after what it says, the way a language server says it (CS1061: what, read as what (CS1061)),
        // so it sits in its own column in Problems and the build's word on a line is the server's said once
        message = CodeAfter(message);
        var problem = new HexProblem(full, Math.Max(0, line - 1), Math.Max(0, column - 1), message, warning);
        if (terminalProblems.Contains(problem))
            return;

        terminalProblems.Add(problem);
        problemsDirty = true;
        terminalProblemsChanged = true;
    }

    private void ClearTerminalProblems()
    {
        if (terminalProblems.Count == 0)
            return;

        terminalProblems.Clear();
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

    /// <summary>What the terminal said about a document, underlined in its code with the rest of what's wrong with it.</summary>
    private IReadOnlyList<UICodeDiagnostic>? TerminalDiagnosticsOf(HexDocument of)
    {
        if (terminalProblems.Count == 0 || of.Path is not { } path)
            return null;

        List<UICodeDiagnostic>? found = null;
        foreach (HexProblem problem in terminalProblems)
        {
            if (PlatformPaths.Same(problem.Path, path))
                (found ??= []).Add(new UICodeDiagnostic(problem.Line, problem.Column, WordLength(of.Editor.Text, problem.Line, problem.Column), problem.Text, problem.Warning));
        }

        return found;
    }

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

    /// <summary>Every tick, the files that are open told again what the terminal says about them, once that changed.</summary>
    private void UpdateTerminalProblems()
    {
        // A task whose shell said it was done, after the problems it printed were read (they come before the prompt)
        for (int i = taskWatches.Count - 1; i >= 0; i--)
        {
            var (view, before, finished) = taskWatches[i];
            if (!view.IsRunning)
            {
                taskWatches.RemoveAt(i);
            }
            else if (view.Screen.Finished > before && view.Screen.AtPrompt)
            {
                taskWatches.RemoveAt(i);
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
