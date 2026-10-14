using System.Numerics;
using System.Text;

using Horizon.Content;
using Horizon.HIDL;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// Hex as the editor of every .hor there is and not only of layouts. A file that doesn't build a layout (a data file, a
// language, a skin, a move set) opens as code, the code of a layout can be edited by hand and put back into it, and
// there is finding, going to a line, opening any file of the workspace by a few letters of its name, finding across
// all of them, checking all of them for what doesn't parse, and running a file to see what it comes to.
internal sealed partial class HexScene
{
    // The editor of the document on screen, where its code is edited and the code of a layout is shown
    private CodeEditor code => document.Editor;

    // Set while the program puts code into an editor, so that isn't taken for somebody typing
    private bool settingCode;

    // What the list on the left shows instead of the layout or the files, the places something was found (null for nothing)
    private List<HexResult>? results;
    private string resultsTitle = string.Empty;

    // What was last looked for, to look for again
    private string lastFind = string.Empty;

    // What running a file of code last came to, shown in the properties
    private readonly List<string> runOutput = [];

    // The folders never looked in for files of a workspace, what gets built, git's own and old builds kept on purpose
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules", "Build History" };

    private readonly record struct HexResult(string Path, int Line, int Column, string Text);

    // The parts of the editor's own layout that only mean something for a layout, hidden while a file of code is open
    private static readonly string[] LayoutOnlyParts = ["add_title", "palette", "tree_tools", "layers_title", "layers_scroll", "play_button"];

    /// <summary>Whether a file's code builds a layout, which it does by asking the compositor for its components.</summary>
    private static bool IsLayoutCode(string source) => source.Contains("compositor.", StringComparison.Ordinal);

    /// <summary>Helper method to set an editor up the way the editor's code is shown, once, when its document is made.</summary>
    private void PrepareEditor(HexDocument prepared)
    {
        CodeEditor editor = prepared.Editor;
        editor.TextScale = EDITOR_TEXT;
        editor.Padding = new UIEdges(4);
        editor.Color = new Vector4(0.055f, 0.06f, 0.08f, 1.0f);
        editor.OnChanged = () => CodeChanged(prepared);
        editor.CompletionSource = Completions;
        editor.OnChecked = _ => CodeChecked(prepared);
    }

    private void CodeChanged(HexDocument changed)
    {
        if (settingCode)
            return;

        if (changed.IsText)
        {
            codeDirty = true;
            return;
        }

        // The code of a layout typed into, it goes back into the layout when it is applied
        if (!changed.CodeEdited)
        {
            changed.CodeEdited = true;
            tabsDirty = true;
            Say("the code was changed by hand, Ctrl+Enter puts it into the layout");
        }
    }

    /// <summary>
    /// Helper method to put code into an editor that the editor's user didn't type, the code a layout is written as, or
    /// a file that was opened. As a step that can be undone unless it is the first thing the editor ever had.
    /// </summary>
    private void SetCode(HexDocument into, string text, bool fresh)
    {
        settingCode = true;
        try
        {
            if (fresh)
                into.Editor.Text = text;
            else if (into.Editor.Text != text)
                into.Editor.Document.Replace(text);
        }
        finally
        {
            settingCode = false;
        }
    }

    /// <summary>After the code of a document was parsed, what it says about the texts of the game it names by key.</summary>
    private void CodeChecked(HexDocument checkedDocument)
    {
        checkedDocument.Editor.Diagnostics = LocalisationDiagnostics(checkedDocument.Editor.Text);

        if (checkedDocument == document)
        {
            inspectorDirty |= checkedDocument.IsText;
            if (checkedDocument.Editor.SyntaxError is { } error)
                Say($"line {error.Line + 1}: {error.Message}", error: true);
        }
    }

    /// <summary>
    /// Helper method to find the texts a piece of code names by key ("@main_menu.quit") that the language of its game
    /// hasn't got, with no languages loaded there is nothing to say.
    /// </summary>
    private static IReadOnlyList<UICodeDiagnostic> LocalisationDiagnostics(string text)
    {
        if (Localisation.Count == 0 || !text.Contains("\"@", StringComparison.Ordinal))
            return [];

        var found = new List<UICodeDiagnostic>();
        foreach (Token token in Lexer.Tokenize(text, lenient: true))
        {
            if (token.Type != TokenType.TextLiteral || !Localisation.IsKey(token.Value))
                continue;

            if (!Localisation.Has(token.Value[1..]))
                found.Add(new UICodeDiagnostic(token.Line - 1, token.Col - 1, token.Length, $"{Localisation.Language} has no text for {token.Value[1..]}", Warning: true));
        }

        return found;
    }

    /* Text documents */

    /// <summary>Opens a file of code that isn't a layout, as the code it is.</summary>
    private bool OpenText(string path, string source)
    {
        // The empty layout the editor starts with is put away, nobody wants it next to a file
        if (document is { Path: null, IsText: false } && !document.Module.Root.Children.Any() && !document.Modified)
        {
            stage.RemoveModule(document.Module);
            documents.Remove(document);
        }

        var opened = new HexDocument(Path.GetFileNameWithoutExtension(path), stage.CreateModule())
        {
            IsText = true,
            Path = path,
            FallbackDirectory = browseDirectory,
            CrLf = source.Contains("\r\n", StringComparison.Ordinal)
        };

        opened.Module.Interactive = false;
        PrepareEditor(opened);

        // Only HIDL is coloured and parsed as HIDL, anything else is text
        bool hidl = Path.GetExtension(path).Equals(".hor", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(HexWorkspace.EXTENSION, StringComparison.OrdinalIgnoreCase);
        opened.Editor.Highlight = hidl;
        opened.Editor.CheckSyntax = hidl;
        SetCode(opened, source, fresh: true);
        opened.SavedCode = opened.Editor.Text;

        documents.Add(opened);
        UseLanguagesNear(path);
        Show(opened);
        Remember(path);

        Console.WriteLine($"[Hex] Opened '{path}' as code: {opened.Editor.Document.LineCount} lines.");
        Say($"opened {opened.Name}, {opened.Editor.Document.LineCount} lines of code");
        return true;
    }

    /// <summary>Opens a file and puts the caret somewhere in it, for whatever lists places in files.</summary>
    private void OpenAt(string path, int line, int column)
    {
        string full = Path.GetFullPath(path);
        HexDocument? open = documents.Find(other => other.Path is not null && Path.GetFullPath(other.Path) == full);

        if (open is null)
        {
            if (!Open(path) && !documents.Any(other => other.Path is not null && Path.GetFullPath(other.Path) == full))
                return;
        }
        else
        {
            Show(open);
        }

        code.GoTo(line, column);
        code.Focus();
    }

    /* The code of a layout, put back into it */

    /// <summary>Builds the layout again out of its code as it was edited by hand. What doesn't build is said and left as it is.</summary>
    private void ApplyCode()
    {
        if (document.IsText || !document.CodeEdited)
            return;

        var (success, message) = document.Load(code.Text, document.Path, keepHistory: true);
        if (!success)
        {
            Say($"the code didn't build, {message}", error: true);
            return;
        }

        document.CodeEdited = false;
        document.Record();
        MarkSelection();
        tabsDirty = treeDirty = inspectorDirty = codeDirty = true;
        Say(message.Length > 0 ? message : $"the code is in the layout, {document.Walk().Count()} components");
    }

    /// <summary>Throws away what was typed into the code of a layout, for the code the layout is written as.</summary>
    private void RevertCode()
    {
        if (!document.CodeEdited)
            return;

        document.CodeEdited = false;
        SetCode(document, document.GenerateCode(), fresh: false);
        tabsDirty = codeDirty = true;
        Say("the code is what the layout says again");
    }

    /* Finding and going places */

    private UIDialog? textDialog;

    /// <summary>Helper method to ask for a line of text in a dialog and do something with it, enter or the first button.</summary>
    private void AskText(string title, string message, string start, string go, Action<string> answered, Action<TextBox>? extra = null)
    {
        if (textDialog is { IsOpen: true })
            textDialog.Close();

        TextBox box = new(start) { Size = new Vector2(420, 32), TextScale = EDITOR_TEXT, Padding = new UIEdges(8, 6), MaxLength = 400 };
        UIDialog shown = UIDialog.Show(chrome, title, message,
            new DialogChoice(go, () => answered(box.Text)),
            new DialogChoice("Cancel"));

        shown.Body.Add(box);
        box.OnSubmitted = text =>
        {
            shown.Close();
            answered(text);
        };

        extra?.Invoke(box);
        box.Focus();
        box.SelectAll();
        textDialog = shown;
    }

    private void FindInFile()
    {
        string start = code.Document.HasSelection && code.Document.SelectionStart.Line == code.Document.SelectionEnd.Line ? code.Document.SelectedText : lastFind;
        AskText("Find", "Look for in this file, F3 finds the next, Shift+F3 the one before", start, "Find", query =>
        {
            lastFind = query;
            code.FindQuery = query;
            bool any = code.FindNext();
            code.Focus();
            Say(any ? $"'{query}', {CountIn(code.Text, query)} of it" : $"no '{query}' in {document.Name}", error: !any);
        });
    }

    private void FindAgain(bool forward)
    {
        if (lastFind.Length == 0)
        {
            FindInFile();
            return;
        }

        code.FindQuery = lastFind;
        if (!code.FindNext(forward))
            Say($"no '{lastFind}' in {document.Name}", error: true);
    }

    private void ReplaceInFile()
    {
        AskText("Replace", "Look for what, then what goes in its place", lastFind, "Next", query =>
        {
            lastFind = query;
            AskText("Replace with", $"Every '{query}' in {document.Name} becomes", string.Empty, "Replace all", with =>
            {
                code.FindQuery = query;
                int count = code.ReplaceAll(with);
                code.Focus();
                Say(count > 0 ? $"replaced {count} of '{query}', Ctrl+Z puts them back" : $"no '{query}' in {document.Name}", error: count == 0);
            });
        });
    }

    private static int CountIn(string text, string query)
    {
        int count = 0;
        for (int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase))
            count++;
        return count;
    }

    private void GoToLine()
    {
        AskText("Go to line", $"Which line, 1 to {code.Document.LineCount}", string.Empty, "Go", answer =>
        {
            if (int.TryParse(answer.Trim(), out int line))
            {
                code.GoTo(Math.Clamp(line - 1, 0, code.Document.LineCount - 1));
                code.Focus();
            }
        });
    }

    /* The workspace, every .hor of its folders, or of the repository the open file is in when it has none */

    /// <summary>The repository the document on screen is in (the nearest folder up from it with git in it), failing that the folder files are opened from.</summary>
    private string RepositoryRoot()
    {
        string? from = document.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path)) : browseDirectory;
        for (string? at = from; at is not null; at = Path.GetDirectoryName(at))
        {
            if (Directory.Exists(Path.Combine(at, ".git")) || File.Exists(Path.Combine(at, ".git")))
                return at;
        }

        return browseDirectory;
    }

    /// <summary>The folders whose files are looked through, the workspace's, or the repository of the open file without one.</summary>
    private List<string> WorkspaceRoots() => workspace.IsEmpty ? [RepositoryRoot()] : [.. workspace.Folders];

    /// <summary>What is looked through, said the way a person would.</summary>
    private string DescribeRoots()
    {
        var roots = WorkspaceRoots();
        return roots.Count == 1 ? roots[0] : $"the {roots.Count} folders of the workspace";
    }

    /// <summary>Every .hor of every folder looked through, once each.</summary>
    private List<string> AllWorkspaceFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (string root in WorkspaceRoots())
        {
            foreach (string file in WorkspaceFiles(root))
            {
                if (seen.Add(file))
                    files.Add(file);
            }
        }

        return files;
    }

    /// <summary>
    /// Every .hor under a folder, leaving out what gets built, git's own, old builds and the checkouts of submodules
    /// (which are the files of another repository, and there are copies of the engine in more than one).
    /// </summary>
    private static List<string> WorkspaceFiles(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string folder = pending.Pop();
            try
            {
                files.AddRange(Directory.GetFiles(folder, "*.hor").Where(file => !Path.GetFileName(file).StartsWith("~syncthing~")));

                foreach (string inside in Directory.GetDirectories(folder))
                {
                    string name = Path.GetFileName(inside);
                    if (SkippedFolders.Contains(name) || name.StartsWith('.') || File.Exists(Path.Combine(inside, ".git")))
                        continue;
                    pending.Push(inside);
                }
            }
            catch (Exception)
            {
                // A folder that can't be read has no files as far as this goes
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>A file by where it is in the workspace, with the name of its folder in front when there is more than one.</summary>
    private string Relative(string path)
    {
        var roots = WorkspaceRoots();
        string full = Path.GetFullPath(path);
        string? root = roots.Where(folder => full.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).OrderByDescending(folder => folder.Length).FirstOrDefault();
        if (root is null)
            return full.Replace('\\', '/');

        string inside = Path.GetRelativePath(root, full).Replace('\\', '/');
        return roots.Count > 1 ? $"{Path.GetFileName(root.TrimEnd('\\', '/'))}/{inside}" : inside;
    }

    /// <summary>Ctrl+P, opens a file of the workspace by a few letters of its name.</summary>
    private void QuickOpen()
    {
        List<string> files = AllWorkspaceFiles();
        var byName = new Dictionary<string, string>();
        foreach (string file in files)
            byName[Relative(file)] = file;
        string[] names = [.. byName.Keys];

        var list = new ListBox(names) { Size = new Vector2(560, 0), Rows = 12, TextScale = EDITOR_TEXT };
        if (names.Length > 0) list.Choose(0);

        void OpenChosen()
        {
            if (list.Index >= 0 && list.Index < list.Items.Length && byName.TryGetValue(list.Items[list.Index], out string? path))
                OpenAt(path, 0, 0);
        }

        AskText("Open a file", $"{names.Length} files in {DescribeRoots()}, a few letters of the name narrow them down", string.Empty, "Open", _ => OpenChosen(), box =>
        {
            box.Size = new Vector2(560, 32);
            box.OnChanged = typed =>
            {
                list.Items = [.. names.Where(name => Matches(name, typed)).OrderBy(name => Path.GetFileName(name).Length).Take(200)];
                if (list.Items.Length > 0) list.Choose(0);
            };
            textDialog!.Body.Add(list);
            list.OnActivated = _ =>
            {
                textDialog?.Close();
                OpenChosen();
            };
        });
    }

    // Whether a name has the letters typed in it in order, the way every editor's quick open goes
    private static bool Matches(string name, string typed)
    {
        int at = 0;
        foreach (char wanted in typed)
        {
            if (char.IsWhiteSpace(wanted)) continue;
            at = name.IndexOf(wanted.ToString(), at, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            at++;
        }

        return true;
    }

    /// <summary>Ctrl+Shift+F, every line of every file of the workspace something is in, listed on the left.</summary>
    private void FindInFiles()
    {
        AskText("Find in files", $"Look for in every .hor in {DescribeRoots()}", lastFind, "Find", query =>
        {
            if (query.Length == 0)
                return;

            lastFind = query;
            List<HexResult> found = FindEverywhere(query, wholeWord: false);

            ShowResults($"FOUND '{query}' ({found.Count})", found);
            Say(found.Count > 0 ? $"'{query}' is in {found.Select(result => result.Path).Distinct().Count()} files, {found.Count} lines" : $"no '{query}' in {DescribeRoots()}", error: found.Count == 0);
        });
    }

    /// <summary>Every line of every file of the workspace something is on, as a word of its own if it says so.</summary>
    private List<HexResult> FindEverywhere(string query, bool wholeWord)
    {
        var found = new List<HexResult>();

        foreach (string file in AllWorkspaceFiles())
        {
            string[] lines = ReadLines(file);
            for (int line = 0; line < lines.Length && found.Count < 2000; line++)
            {
                for (int at = lines[line].IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0; at = lines[line].IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase))
                {
                    if (wholeWord && !IsWholeWord(lines[line], at, query.Length))
                        continue;

                    found.Add(new HexResult(file, line, at, $"{Relative(file)}:{line + 1}  {lines[line].Trim()}"));
                    break;
                }
            }
        }

        return found;
    }

    private static bool IsWholeWord(string line, int at, int length) =>
        (at == 0 || !UITextDocument.IsWordChar(line[at - 1])) && (at + length >= line.Length || !UITextDocument.IsWordChar(line[at + length]));

    /// <summary>Every .hor of the workspace parsed, what doesn't parse listed on the left, and what names a text the game hasn't got.</summary>
    private void CheckAllFiles()
    {
        List<string> files = AllWorkspaceFiles();
        var problems = new List<HexResult>();

        foreach (string file in files)
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (Exception e)
            {
                problems.Add(new HexResult(file, 0, 0, $"{Relative(file)}  couldn't be read, {e.Message}"));
                continue;
            }

            try
            {
                new Parser().ProduceSyntaxTree(Lexer.Tokenize(source));
            }
            catch (ParseException e)
            {
                problems.Add(new HexResult(file, Math.Max(0, e.Line - 1), Math.Max(0, e.Col - 1), $"{Relative(file)}:{e.Line}  {e.Message}"));
            }
            catch (Exception e)
            {
                problems.Add(new HexResult(file, 0, 0, $"{Relative(file)}  {e.Message}"));
            }

            foreach (UICodeDiagnostic missing in LocalisationDiagnostics(source))
                problems.Add(new HexResult(file, missing.Line, missing.Column, $"{Relative(file)}:{missing.Line + 1}  {missing.Message}"));
        }

        ShowResults($"PROBLEMS ({problems.Count} in {files.Count} files)", problems);
        Console.WriteLine($"[Hex] Checked {files.Count} files in {DescribeRoots()}, {problems.Count} problems.");
        foreach (var problem in problems)
            Console.WriteLine($"[Hex]   {problem.Text}");

        Say(problems.Count == 0 ? $"all {files.Count} files in {DescribeRoots()} parse" : $"{problems.Count} problems in {files.Count} files, listed on the left", error: problems.Count > 0);
    }

    private static string[] ReadLines(string file)
    {
        try
        {
            return File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void ShowResults(string title, List<HexResult> found)
    {
        results = found;
        resultsTitle = title;
        browsing = false;
        treeScroll.Offset = 0;
        treeDirty = true;
    }

    private void CloseResults()
    {
        results = null;
        treeDirty = true;
    }

    /// <summary>Helper method to list the places something was found, a row each, a click goes there.</summary>
    private void RebuildResults()
    {
        treeTitle.Text = resultsTitle;
        var items = layout.Populate(tree, results!.Count);

        for (int i = 0; i < items.Count; i++)
        {
            HexResult result = results[i];
            var row = items[i].Get<Button>("row");
            var caption = items[i].Get<Label>("caption");

            caption.Text = result.Text.Replace('[', '(').Replace(']', ')');
            caption.Position = new Vector2(10, 0);
            caption.Color = null;
            row.Tooltip = result.Path;
            row.OnPressed = () => OpenAt(result.Path, result.Line, result.Column);
        }

        if (results.Count == 0)
            tree.Add(new Label("nothing") { Anchor = Origin.Left, Align = Origin.TopLeft, TextScale = EDITOR_TEXT, Color = DimColor });
    }

    /* The outline of a file of code, what it declares and the keys of its objects, on the left */

    private readonly record struct OutlineEntry(string Name, int Line, int Column, int Depth);

    /// <summary>
    /// What a file declares (its lets and consts) and the keys of the objects in those, a few levels down, by its tokens
    /// so a file that doesn't parse right now still has one.
    /// </summary>
    private static List<OutlineEntry> Outline(string text)
    {
        var entries = new List<OutlineEntry>();
        Token[] tokens = Lexer.Tokenize(text, lenient: true);
        int depth = 0;

        for (int i = 0; i < tokens.Length && entries.Count < 3000; i++)
        {
            Token token = tokens[i];
            switch (token.Type)
            {
                case TokenType.OpenBrace or TokenType.OpenBracket or TokenType.OpenParenthesis:
                    depth++;
                    break;
                case TokenType.CloseBrace or TokenType.CloseBracket or TokenType.CloseParenthesis:
                    depth = Math.Max(0, depth - 1);
                    break;

                case TokenType.Let or TokenType.Const when i + 1 < tokens.Length && tokens[i + 1].Type == TokenType.Identifier:
                    entries.Add(new OutlineEntry(tokens[i + 1].Value, tokens[i + 1].Line - 1, tokens[i + 1].Col - 1, depth));
                    break;

                case TokenType.Identifier when depth is > 0 and <= 4 && i + 1 < tokens.Length && tokens[i + 1].Type == TokenType.Colon:
                    entries.Add(new OutlineEntry(token.Value, token.Line - 1, token.Col - 1, depth));
                    break;
            }
        }

        return entries;
    }

    private void RebuildOutline()
    {
        treeTitle.Text = $"OUTLINE OF {document.Name.ToUpperInvariant()}";

        List<OutlineEntry> entries = Outline(code.Text);
        var items = layout.Populate(tree, entries.Count);

        for (int i = 0; i < items.Count; i++)
        {
            OutlineEntry entry = entries[i];
            var row = items[i].Get<Button>("row");
            var caption = items[i].Get<Label>("caption");

            caption.Text = entry.Depth == 0 ? entry.Name : $"{entry.Name}:";
            caption.Position = new Vector2(10 + entry.Depth * 14, 0);
            caption.Color = entry.Depth == 0 ? null : DimColor;
            row.OnPressed = () =>
            {
                code.GoTo(entry.Line, entry.Column);
                code.Focus();
            };
        }

        if (entries.Count == 0)
            tree.Add(new Label("nothing declared in here") { Anchor = Origin.Left, Align = Origin.TopLeft, TextScale = EDITOR_TEXT, Color = DimColor });

        RebuildLayers();
    }

    /* What the properties show for a file of code, what is wrong with it and what running it came to */

    private void RebuildCodeInspector()
    {
        foreach (var child in inspector.Children.ToArray())
            inspector.Remove(child);

        Label Line(string text, Vector4? color = null) => inspector.Add(new Label(text.Replace('[', '(').Replace(']', ')'))
        {
            Anchor = Origin.Left,
            Align = Origin.TopLeft,
            TextScale = EDITOR_TEXT,
            Color = color ?? DimColor,
            Wrap = true,
            Size = new Vector2(MathF.Max(120, inspectorScroll.Size.X - 30), 0)
        });

        Line(document.Path ?? document.Name, null);
        Line($"{code.Document.LineCount} lines, {(document.CrLf ? "CRLF" : "LF")}");

        if (code.SyntaxError is { } error)
        {
            var go = inspector.Add(new Button($"line {error.Line + 1}: {error.Message}") { Anchor = Origin.Left, LabelScale = EDITOR_TEXT, Style = "button_flat" });
            go.OnPressed = () =>
            {
                code.GoTo(error.Line, error.Column);
                code.Focus();
            };
        }
        else
        {
            Line("parses", new Vector4(0.45f, 0.85f, 0.5f, 1.0f));
        }

        foreach (UICodeDiagnostic warning in code.Diagnostics)
        {
            var go = inspector.Add(new Button($"line {warning.Line + 1}: {warning.Message}") { Anchor = Origin.Left, LabelScale = EDITOR_TEXT, Style = "button_flat" });
            go.OnPressed = () =>
            {
                code.GoTo(warning.Line, warning.Column);
                code.Focus();
            };
        }

        if (runOutput.Count > 0)
        {
            Line(string.Empty);
            Line("WHAT RUNNING IT CAME TO (F5)", new Vector4(0.46f, 0.52f, 0.64f, 1.0f));
            foreach (string said in runOutput)
                Line(said, null);
        }
        else
        {
            Line(string.Empty);
            Line("F5 runs it and lists what it declares, Ctrl+F finds, Ctrl+G goes to a line, Ctrl+P opens any file, Ctrl+Shift+F finds in all of them");
        }
    }

    /// <summary>
    /// F5 on a file of code. It is run the way a game loads it, includes looked for next to it, and what it declares is
    /// listed with what each comes to, or what went wrong. Nothing a game would hand it (a compositor) is there.
    /// </summary>
    private void RunCode()
    {
        runOutput.Clear();

        var runtime = new HIDLRuntime
        {
            BaseDirectory = document.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path)) : browseDirectory,
            Output = line => runOutput.Add($"print: {line}")
        };

        var (success, result) = runtime.Evaluate(code.Text);
        if (!success)
        {
            runOutput.Add($"didn't run, {result}");
            Say($"{document.Name} didn't run, {result}", error: true);
        }
        else
        {
            foreach (var (name, value) in runtime.UserScope.Variables)
                runOutput.Add($"{name} = {Shorten(Describe(value), 240)}");

            Say($"{document.Name} runs, {runtime.UserScope.Variables.Count} things declared, listed on the right");
        }

        inspectorDirty = true;
    }

    private static string Describe(IRuntimeValue value)
    {
        try
        {
            return HIDLWriter.CanWrite(value) ? HIDLWriter.Write(value) : value.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static string Shorten(string text, int most)
    {
        string flat = text.Replace("\r", string.Empty).Replace('\n', ' ');
        while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ");
        return flat.Length <= most ? flat : flat[..most] + "...";
    }

    /* Completing what is typed, by what it is being typed into */

    // Every name and key of every file of the workspace, made the first time anything is completed and again once
    // something changes the files (null until then)
    private HashSet<string>? vocabulary;

    private HashSet<string> Vocabulary()
    {
        if (vocabulary is not null)
            return vocabulary;

        vocabulary = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in AllWorkspaceFiles().Take(2000))
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (Token token in Lexer.Tokenize(source, lenient: true))
            {
                if (token.Type == TokenType.Identifier && token.Value.Length > 1)
                    vocabulary.Add(token.Value);
            }
        }

        return vocabulary;
    }

    /// <summary>
    /// What there is to write at the caret of a document. A key of the game's language inside a text that starts with an
    /// @, one of the words a property takes inside its text (an anchor, a fill), a kind of component after compositor.,
    /// the properties of the component being made inside its braces, and otherwise the language's words with every name
    /// of the workspace.
    /// </summary>
    private UICompletionRequest? Completions(CodeEditor editor)
    {
        UITextDocument doc = editor.Document;
        UITextPosition caret = doc.Caret;
        string line = doc.LineAt(caret.Line);
        string before = line[..caret.Column];

        // Inside a text, the quotes before the caret on its line odd in number
        int quote = before.LastIndexOf('"');
        if (quote >= 0 && before.Count(character => character == '"') % 2 == 1)
        {
            string inside = before[(quote + 1)..];

            if (inside.StartsWith('@'))
            {
                var keys = Localisation.Keys.Order(StringComparer.Ordinal)
                    .Select(key => new UICompletion(key, Shorten(Localisation.Get(key), 40)))
                    .ToList();
                return new UICompletionRequest(quote + 2, keys);
            }

            // The words of a property that is one of a few, after its name and a colon
            string head = before[..quote].TrimEnd();
            if (head.EndsWith(':'))
            {
                string property = TrailingWord(head[..^1].TrimEnd());
                if (Choices.TryGetValue(property, out string[]? words))
                    return new UICompletionRequest(quote + 1, [.. words.Select(word => new UICompletion(word, property))]);
            }

            return null;
        }

        string word = doc.WordBeforeCaret();
        int start = caret.Column - word.Length;
        string ahead = before[..start];

        // A kind of component, after compositor.
        if (ahead.EndsWith("compositor.", StringComparison.Ordinal))
            return new UICompletionRequest(start, [.. UIModule.Kinds.Order(StringComparer.Ordinal).Select(kind => new UICompletion(kind, Shorten(Describe(kind), 40), kind + "({ "))]);

        // A property of the component being made, where a key goes inside its braces
        if (ComponentBeingMade(editor.Text, doc, start) is { } kind && UIModule.CreateDetached(kind) is { } made)
        {
            string trimmed = ahead.TrimEnd();
            if (trimmed.Length == 0 || trimmed.EndsWith('{') || trimmed.EndsWith(','))
            {
                var properties = made.Object.Properties.Keys
                    .Where(name => name != "parent")
                    .Prepend("parent")
                    .Select(name => new UICompletion(name, Shorten(Horizon.UI.Scripting.UIPropertyHelp.Describe(kind, name), 48), name + ": "))
                    .ToList();
                return new UICompletionRequest(start, properties);
            }
        }

        var items = new List<UICompletion>();
        var seen = new HashSet<string>();
        foreach (string keyword in HidlKeywords)
        {
            if (seen.Add(keyword)) items.Add(new UICompletion(keyword, "keyword"));
        }

        foreach (Token token in Lexer.Tokenize(editor.Text, lenient: true))
        {
            if (token.Type == TokenType.Identifier && token.Value.Length > 1 && seen.Add(token.Value))
                items.Add(new UICompletion(token.Value, "in this file"));
        }

        foreach (string known in Vocabulary().Order(StringComparer.Ordinal))
        {
            if (seen.Add(known))
                items.Add(new UICompletion(known, "in the workspace"));
        }

        return new UICompletionRequest(start, items);
    }

    private static readonly string[] HidlKeywords = ["let", "const", "func", "if", "else", "while", "for", "in", "return", "break", "continue", "true", "false", "null", "vec", "include", "compositor"];

    private static string TrailingWord(string text)
    {
        int start = text.Length;
        while (start > 0 && UITextDocument.IsWordChar(text[start - 1])) start--;
        return text[start..];
    }

    /// <summary>
    /// Helper method to find which kind of component the code is in the middle of making at a place, the innermost
    /// compositor.kind({ that isn't closed yet, null for none.
    /// </summary>
    private static string? ComponentBeingMade(string text, UITextDocument doc, int column)
    {
        // Everything up to the place, by its offset in the text
        int offset = 0;
        for (int line = 0; line < doc.Caret.Line; line++)
            offset += doc.LineAt(line).Length + 1;
        offset += column;

        Token[] tokens = Lexer.Tokenize(text[..Math.Min(offset, text.Length)], lenient: true);
        var open = new Stack<string?>();

        // By the character and not the kind of token, the lexer's names for the brackets are the other way round
        for (int i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i].Value)
            {
                case "{":
                    // compositor . kind ( {
                    bool made = i >= 4 && tokens[i - 1].Type == TokenType.OpenParenthesis && tokens[i - 2].Type == TokenType.Identifier
                        && tokens[i - 3].Type == TokenType.Dot && tokens[i - 4] is { Type: TokenType.Identifier, Value: "compositor" };
                    open.Push(made ? tokens[i - 2].Value : null);
                    break;

                case "}" when open.Count > 0:
                    open.Pop();
                    break;
            }
        }

        return open.Count > 0 ? open.Peek() : null;
    }

    /* Going to where something is */

    /// <summary>
    /// F12. Where what is at the caret is declared. A name goes to its let or const, in this file first and then in the
    /// workspace, a text naming a key of the game's language to the key in the language's file, a text naming a file
    /// (an include, a template) to the file.
    /// </summary>
    private void GoToDefinition()
    {
        UITextDocument doc = code.Document;
        UITextPosition caret = doc.Caret;
        string line = doc.LineAt(caret.Line);

        // A text the caret is in
        foreach (Token token in Lexer.Tokenize(line, lenient: true))
        {
            if (token.Type != TokenType.TextLiteral || caret.Column < token.Col - 1 || caret.Column > token.Col - 1 + token.Length)
                continue;

            if (Localisation.IsKey(token.Value))
            {
                GoToKey(token.Value[1..]);
                return;
            }

            string folder = document.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path))! : browseDirectory;
            string named = Path.Combine(folder, token.Value);
            if (token.Value.Length > 0 && File.Exists(named))
            {
                OpenAt(named, 0, 0);
                return;
            }
        }

        var (start, end) = doc.WordAt(caret);
        if (end <= start)
        {
            Say("there is nothing at the caret to go to");
            return;
        }

        string word = line[start..end];

        // In this file
        if (FindDeclaration(code.Text, word) is { } here)
        {
            code.GoTo(here.Line, here.Column);
            Say($"{word} is declared on line {here.Line + 1}");
            return;
        }

        // In the workspace
        foreach (string file in AllWorkspaceFiles())
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (Exception)
            {
                continue;
            }

            if (!source.Contains(word, StringComparison.Ordinal) || FindDeclaration(source, word) is not { } there)
                continue;

            OpenAt(file, there.Line, there.Column);
            Say($"{word} is declared in {Relative(file)}, line {there.Line + 1}");
            return;
        }

        Say($"{word} isn't declared with a let anywhere in {DescribeRoots()}", error: true);
    }

    /// <summary>Where a name is declared with let or const in some code, by its tokens, null for nowhere.</summary>
    private static UITextPosition? FindDeclaration(string source, string name)
    {
        Token[] tokens = Lexer.Tokenize(source, lenient: true);
        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Type is TokenType.Let or TokenType.Const && tokens[i + 1] is { Type: TokenType.Identifier } declared && declared.Value == name)
                return new UITextPosition(declared.Line - 1, declared.Col - 1);
        }

        return null;
    }

    /// <summary>Opens the language file of the game at a key, the line of its last part under the ones before it.</summary>
    private void GoToKey(string key)
    {
        string file = Path.Combine(Localisation.Folder, Localisation.Language + ".hor");
        if (Localisation.Folder.Length == 0 || !File.Exists(file))
        {
            Say("no language is loaded to look the key up in", error: true);
            return;
        }

        string[] lines = ReadLines(file);
        string[] parts = key.Split('.');
        int line = 0, part = 0;

        for (; line < lines.Length && part < parts.Length; line++)
        {
            if (lines[line].TrimStart().StartsWith(parts[part] + ":", StringComparison.Ordinal))
            {
                part++;
                if (part == parts.Length)
                    break;
            }
        }

        OpenAt(file, Math.Min(line, lines.Length - 1), Math.Max(0, lines[Math.Min(line, lines.Length - 1)].Length - lines[Math.Min(line, lines.Length - 1)].TrimStart().Length));
        Say(part == parts.Length ? $"{key} in {Localisation.Language}" : $"{Localisation.Language} has no {key}, this is as far as it goes", error: part != parts.Length);
    }

    /// <summary>Shift+F12, every line of the workspace the word at the caret is on as a word of its own, listed on the left.</summary>
    private void FindReferences()
    {
        var (start, end) = code.Document.WordAt(code.Document.Caret);
        if (end <= start)
        {
            Say("there is no word at the caret to look for");
            return;
        }

        string word = code.Document.LineAt(code.Document.Caret.Line)[start..end];
        List<HexResult> found = FindEverywhere(word, wholeWord: true);
        ShowResults($"WHERE {word} IS ({found.Count})", found);
        Say($"{word} is on {found.Count} lines of {found.Select(result => result.Path).Distinct().Count()} files");
    }
}
