using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

// The tools for files and their code, what a developer reads off the editor without a screenshot. Opening a file at a
// line, the tabs, the text and the caret, putting the selection somewhere, what's wrong, the outline, finding across the
// workspace, and the language features, where a name's declared, what it is and where it's used. Lines and columns are
// 1 based here, as an agent reads them in a file, the editor's own are 0 based.
internal sealed partial class DawnScene
{
    // The most of a file's text the editor tool hands back at once
    private const int MCP_MOST_TEXT = 200_000;

    private IEnumerable<HexMcpTool> EditorTools() =>
    [
        new(new McpTool("hex_open", "Open a file",
            "Open a file in Hex (a path, absolute or relative to the first workspace folder), optionally at a line and column, and make it the current tab.",
            McpSchema.Object([
                ("path", McpSchema.String("The file.")),
                ("line", McpSchema.Integer("The line to put the caret on, 1 is the first.")),
                ("column", McpSchema.Integer("The column, 1 is the first."))], "path")),
            OpenFile),

        new(new McpTool("hex_documents", "Open tabs",
            "List the open tabs, which one is current and which have changes that aren't saved.", McpSchema.Empty(), ReadOnly: true),
            Documents),

        new(new McpTool("hex_editor", "Current file",
            "Read the current tab's text (or lines of it), with its path, language, caret and selection.",
            McpSchema.Object([
                ("start_line", McpSchema.Integer("First line to read, 1 is the first.")),
                ("end_line", McpSchema.Integer("Last line to read, inclusive.")),
                ("numbers", McpSchema.Flag("Put the line number in front of each line."))]), ReadOnly: true),
            EditorText),

        new(new McpTool("hex_set_selection", "Move the caret",
            "Put the caret in the current file at a line and column, or select from there to an end line and column. It is scrolled into sight.",
            McpSchema.Object([
                ("line", McpSchema.Integer("Line, 1 is the first.")),
                ("column", McpSchema.Integer("Column, 1 is the first, 1 by default.")),
                ("end_line", McpSchema.Integer("Where the selection ends, the caret goes there.")),
                ("end_column", McpSchema.Integer("Column the selection ends at."))], "line")),
            SetSelection),

        new(new McpTool("hex_problems", "Problems",
            "List what Hex says is wrong (the Problems panel), errors and warnings with file, line, column and message.",
            McpSchema.Object([
                ("path", McpSchema.String("Only this file's.")),
                ("errors_only", McpSchema.Flag("Leave the warnings out."))]), ReadOnly: true),
            Problems),

        new(new McpTool("hex_outline", "Outline",
            "List what the current file declares (functions, types, values) with their lines, the way the outline shows them.", McpSchema.Empty(), ReadOnly: true),
            Outline),

        new(new McpTool("hex_find_in_files", "Find in files",
            "Search the workspace's folders for text or a regex and list the places, file, line, column and the line itself.",
            McpSchema.Object([
                ("query", McpSchema.String("What to look for.")),
                ("regex", McpSchema.Flag("The query is a regular expression.")),
                ("match_case", McpSchema.Flag("Upper and lower case count.")),
                ("whole_word", McpSchema.Flag("Only whole words.")),
                ("folder", McpSchema.String("Search this folder instead of the workspace's.")),
                ("most", McpSchema.Integer("At most this many places, 200 by default."))], "query"), ReadOnly: true),
            FindInFilesTool, Timeout: 60.0f),

        new(new McpTool("hex_definition", "Go to definition",
            "Go to where the name at a place in the current file is declared (F12), through HIDL or the file's language server, and say where that is.",
            McpSchema.Object([
                ("line", McpSchema.Integer("Line of the name, the caret's by default.")),
                ("column", McpSchema.Integer("Column of the name."))])),
            Definition, Timeout: 20.0f),

        new(new McpTool("hex_symbol_info", "What a name is",
            "Say what the name at a place in the current file is, the hover text HIDL or the language server gives.",
            McpSchema.Object([
                ("line", McpSchema.Integer("Line of the name, the caret's by default.")),
                ("column", McpSchema.Integer("Column of the name."))]), ReadOnly: true),
            SymbolInfo, Timeout: 20.0f),

        new(new McpTool("hex_references", "Find references",
            "List everywhere the name at a place in the current file is used (Shift+F12), file, line and the line itself.",
            McpSchema.Object([
                ("line", McpSchema.Integer("Line of the name, the caret's by default.")),
                ("column", McpSchema.Integer("Column of the name."))]), ReadOnly: true),
            References, Timeout: 30.0f)
    ];

    /// <summary>A path an agent gave, whole, a relative one taken from the first folder of the workspace.</summary>
    private string McpPath(string path)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);
        string root = explorer.workspace.Folders.FirstOrDefault() ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(root, path));
    }

    private string McpWhere(HexDocument shown) => shown.Path ?? $"{shown.Name} (not saved anywhere yet)";

    private string McpCaret() =>
        $"Ln {code.Document.Caret.Line + 1}, Col {code.Document.Caret.Column + 1}" + (code.Document.HasSelection
            ? $", selected from {code.Document.SelectionStart.Line + 1}:{code.Document.SelectionStart.Column + 1} to {code.Document.SelectionEnd.Line + 1}:{code.Document.SelectionEnd.Column + 1}"
            : string.Empty);

    private IEnumerable<McpToolResult?> OpenFile(JsonObject arguments)
    {
        string path = McpPath(arguments.String("path") ?? string.Empty);
        if (!File.Exists(path))
        {
            yield return McpToolResult.Error($"There's no file at {path}.");
            yield break;
        }

        int line = Math.Max(1, arguments.Integer("line") ?? 1), column = Math.Max(1, arguments.Integer("column") ?? 1);
        if (arguments.Integer("line") is not null)
            OpenAt(path, line - 1, column - 1);
        else if (!Open(path))
        {
            yield return McpToolResult.Error($"Hex couldn't open {path}, the status bar says \"{Plain(messageItem.Text)}\".");
            yield break;
        }

        yield return null;
        yield return null;
        bool there = document.Path is { } now && PlatformPaths.Same(Path.GetFullPath(now), path);
        yield return there
            ? McpToolResult.Text($"Opened {path}, {code.Document.LineCount} lines, {KindOf(document).Language}. Caret at {McpCaret()}.")
            : McpToolResult.Error($"Asked to open {path}, but the current tab is {McpWhere(document)}.");
    }

    private IEnumerable<McpToolResult?> Documents(JsonObject arguments)
    {
        var said = new StringBuilder();
        foreach (HexDocument open in documents)
        {
            said.Append(open == document ? "* " : "  ").Append(McpWhere(open));
            if (open.Modified)
                said.Append("  (modified)");
            said.Append("  [").Append(KindOf(open).Language).Append(open.IsText ? string.Empty : $", {open.View} view").Append("]\n");
        }

        yield return McpToolResult.Text($"{documents.Count} open, * is the current one.\n{said}");
    }

    private IEnumerable<McpToolResult?> EditorText(JsonObject arguments)
    {
        UITextDocument text = code.Document;
        int first = Math.Clamp(arguments.Integer("start_line") ?? 1, 1, text.LineCount);
        int last = Math.Clamp(arguments.Integer("end_line") ?? text.LineCount, first, text.LineCount);
        bool numbers = arguments.Flag("numbers");

        var said = new StringBuilder();
        said.Append($"{McpWhere(document)}{(document.Modified ? " (modified)" : string.Empty)}, {KindOf(document).Language}, {text.LineCount} lines, caret at {McpCaret()}.\n");
        if (first != 1 || last != text.LineCount)
            said.Append($"Lines {first} to {last}.\n");
        said.Append("----\n");

        int width = last.ToString().Length;
        for (int line = first; line <= last; line++)
        {
            if (said.Length > MCP_MOST_TEXT)
            {
                said.Append($"\n... cut off at line {line - 1}, read the rest with start_line {line}.");
                break;
            }

            if (numbers)
                said.Append(line.ToString().PadLeft(width)).Append("  ");
            said.Append(text.LineAt(line - 1)).Append('\n');
        }

        yield return McpToolResult.Text(said.ToString());
    }

    private IEnumerable<McpToolResult?> SetSelection(JsonObject arguments)
    {
        UITextDocument text = code.Document;
        UITextPosition from = text.Clamp(new UITextPosition((arguments.Integer("line") ?? 1) - 1, (arguments.Integer("column") ?? 1) - 1));
        if (arguments.Integer("end_line") is { } endLine)
        {
            UITextPosition to = text.Clamp(new UITextPosition(endLine - 1, (arguments.Integer("end_column") ?? 1) - 1));
            code.GoTo(to.Line, to.Column);
            text.Select(from, to);
        }
        else
        {
            code.GoTo(from.Line, from.Column);
        }

        code.Focus();
        yield return null;
        yield return McpToolResult.Text($"Caret at {McpCaret()} in {McpWhere(document)}.");
    }

    private IEnumerable<McpToolResult?> Problems(JsonObject arguments)
    {
        // Gathered fresh, what the panel shows is put together only while it's looked at
        GatherProblems();
        string? only = arguments.String("path") is { } asked ? McpPath(asked) : null;
        bool errorsOnly = arguments.Flag("errors_only");

        var shown = problemList.Where(problem => (!errorsOnly || !problem.Warning) && (only is null || PlatformPaths.Same(Path.GetFullPath(problem.Path), only))).ToList();
        var said = new StringBuilder($"{errorCount} errors and {warningCount} warnings in all{(only is not null || errorsOnly ? $", {shown.Count} shown" : string.Empty)}.\n");
        foreach (HexProblem problem in shown.Take(500))
            said.Append($"{problem.Path}:{problem.Line + 1}:{problem.Column + 1} {(problem.Hint ? "hint" : problem.Warning ? "warning" : "error")} {problem.Text}\n");
        if (shown.Count > 500)
            said.Append($"... and {shown.Count - 500} more\n");

        yield return McpToolResult.Text(said.ToString());
    }

    private IEnumerable<McpToolResult?> Outline(JsonObject arguments)
    {
        List<OutlineEntry> entries = OutlineHere();
        var said = new StringBuilder($"The outline of {McpWhere(document)}, {entries.Count} entries.\n");
        foreach (OutlineEntry entry in entries)
            said.Append(' ', Math.Min(entry.Depth, 10) * 2).Append($"{entry.Name}  ({entry.Icon.Replace("symbol-", string.Empty)}, line {entry.Line + 1})\n");

        yield return McpToolResult.Text(said.ToString());
    }

    private IEnumerable<McpToolResult?> FindInFilesTool(JsonObject arguments)
    {
        string query = arguments.String("query") ?? string.Empty;
        List<string> roots = arguments.String("folder") is { } folder ? [McpPath(folder)] : [.. explorer.workspace.Folders];
        if (roots.Count == 0)
        {
            yield return McpToolResult.Error("No folder is open to search, open one or say which with folder.");
            yield break;
        }

        var options = new UIFindOptions(arguments.Flag("match_case"), arguments.Flag("whole_word"), arguments.Flag("regex"));
        Task<List<HexResult>> finding = Task.Run(() => HexSearch.FindIn(roots, query, options));
        while (!finding.IsCompleted)
            yield return null;

        if (finding.IsFaulted)
        {
            yield return McpToolResult.Error($"The search went wrong, {finding.Exception?.InnerException?.Message}");
            yield break;
        }

        List<HexResult> found = finding.Result;
        int most = Math.Clamp(arguments.Integer("most") ?? 200, 1, 5000);
        var said = new StringBuilder($"{found.Count} places in {found.Select(result => result.Path).Distinct(PlatformPaths.Comparer).Count()} files.\n");
        foreach (HexResult result in found.Take(most))
            said.Append(McpResultLine(result));
        if (found.Count > most)
            said.Append($"... and {found.Count - most} more\n");

        yield return McpToolResult.Text(said.ToString());
    }

    /// <summary>The caret put at the line and column asked for, when they were, false when the current tab isn't code.</summary>
    private bool McpCaretAt(JsonObject arguments)
    {
        if (!document.IsText && !document.CodeEdited && document.View == HexView.Design)
            return false;

        if (arguments.Integer("line") is { } line)
            code.GoTo(Math.Max(0, line - 1), Math.Max(0, (arguments.Integer("column") ?? 1) - 1));
        return true;
    }

    private IEnumerable<McpToolResult?> Definition(JsonObject arguments)
    {
        if (!McpCaretAt(arguments))
        {
            yield return McpToolResult.Error("The current tab isn't code. Open a file with hex_open first.");
            yield break;
        }

        HexDocument from = document;
        UITextPosition caret = code.Document.Caret;
        long saidBefore = saidAt;
        codeEditing.GoToDefinition();

        // A language server answers later, HIDL's own at once. Over when the editor has gone somewhere or said why not
        long giveUp = System.Environment.TickCount64 + 15_000;
        while (System.Environment.TickCount64 < giveUp && document == from && code.Document.Caret == caret && saidAt == saidBefore)
            yield return null;

        if (document != from || code.Document.Caret != caret)
        {
            string line = code.Document.LineAt(code.Document.Caret.Line).Trim();
            yield return McpToolResult.Text($"Declared in {McpWhere(document)} at Ln {code.Document.Caret.Line + 1}, Col {code.Document.Caret.Column + 1}, now the current tab.\n{line}");
        }
        else
        {
            yield return McpToolResult.Error($"Hex didn't go anywhere. {(saidAt != saidBefore ? $"It said \"{Plain(messageItem.Text)}\"." : "Nothing answered in time.")}");
        }
    }

    private IEnumerable<McpToolResult?> SymbolInfo(JsonObject arguments)
    {
        if (!McpCaretAt(arguments))
        {
            yield return McpToolResult.Error("The current tab isn't code. Open a file with hex_open first.");
            yield break;
        }

        if (code.HoverSource is not { } source)
        {
            yield return McpToolResult.Error("Nothing says what names are in this file.");
            yield break;
        }

        // A server's hover is asked for the first time and in later, the same question again gets it once it's in
        UITextPosition at = code.Document.Caret;
        long giveUp = System.Environment.TickCount64 + 10_000;
        while (System.Environment.TickCount64 < giveUp)
        {
            if (source(code, at) is { Length: > 0 } said)
            {
                yield return McpToolResult.Text($"At Ln {at.Line + 1}, Col {at.Column + 1}:\n{said}");
                yield break;
            }

            yield return null;
        }

        yield return McpToolResult.Text($"Nothing is said about Ln {at.Line + 1}, Col {at.Column + 1} (\"{code.Document.LineAt(at.Line).Trim()}\").");
    }

    private IEnumerable<McpToolResult?> References(JsonObject arguments)
    {
        if (!McpCaretAt(arguments))
        {
            yield return McpToolResult.Error("The current tab isn't code. Open a file with hex_open first.");
            yield break;
        }

        List<HexResult>? before = results;
        long saidBefore = saidAt;
        codeEditing.FindReferences();

        long giveUp = System.Environment.TickCount64 + 25_000;
        while (System.Environment.TickCount64 < giveUp && ReferenceEquals(results, before) && saidAt == saidBefore)
            yield return null;

        // A language server says how many it found as it lists them, a moment for the list to land too
        for (int settle = 0; settle < 3 && ReferenceEquals(results, before); settle++)
            yield return null;

        if (ReferenceEquals(results, before) || results is null)
        {
            yield return McpToolResult.Error($"No references were listed. {(saidAt != saidBefore ? $"Hex said \"{Plain(messageItem.Text)}\"." : "Nothing answered in time.")}");
            yield break;
        }

        var said = new StringBuilder($"{resultsTitle}\n");
        foreach (HexResult result in results.Take(500))
            said.Append(McpResultLine(result));
        yield return McpToolResult.Text(said.ToString());
    }

    /// <summary>
    /// A place found as a line, where it is and its text. A result of a search starts its text with its file and line for
    /// the side bar's row ("demo.hor:2  let speed"), which the line says already, so that's left off.
    /// </summary>
    private static string McpResultLine(HexResult result)
    {
        string text = result.Text;
        string marker = $":{result.Line + 1}  ";
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at > 0 && text[..at].EndsWith(Path.GetFileName(result.Path), PlatformPaths.Comparison))
            text = text[(at + marker.Length)..];
        return $"{result.Path}:{result.Line + 1}:{result.Column + 1}: {text.Trim()}\n";
    }
}
