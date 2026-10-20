namespace Hex.Dawn;

// What the terminal put in the Problems panel, a compiler's errors read off its output by a problem matcher (the
// engine's TerminalProblems) or put there by a plugin with terminal.problem. Kept apart from the file's own and from F8's,
// shown after them, cleared when the terminal asks or when a new run of the same program starts saying things.
internal sealed partial class DawnScene
{
    private readonly List<HexProblem> terminalProblems = [];

    private void AddTerminalProblem(string path, int line, int column, string message, bool warning)
    {
        // A path said relative to where the shell was is taken from the terminal's folder
        string full = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(terminalPanel.FocusedView?.WorkingDirectory ?? TerminalFolder(), path));
        var problem = new HexProblem(full, Math.Max(0, line - 1), Math.Max(0, column - 1), message, warning);
        if (terminalProblems.Contains(problem))
            return;

        terminalProblems.Add(problem);
        problemsDirty = true;
    }

    private void ClearTerminalProblems()
    {
        if (terminalProblems.Count == 0)
            return;

        terminalProblems.Clear();
        problemsDirty = true;
    }
}
