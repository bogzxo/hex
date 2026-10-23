using Horizon.UI.Scripting;

namespace Hex.Dawn;

// Going about the code of another language through its server, VS Code's way. What a name is as the pointer rests on it,
// what a call takes as it's typed, F12 to where a name is declared, Shift+F12 everywhere it's used, F2 to rename it
// everywhere, Shift+Alt+F to format the file and Ctrl+. for the fixes the server has. Each asks the server off the
// editor's thread and does what it says when the answer is in (a hover and a signature are asked for again by the
// editor once they're there). A file whose server can't do one of these is left to what Hex does by itself.
internal sealed partial class HexLanguages
{
    // Answers on their way, each with what to do once it's in, done on the editor's thread
    private readonly List<(Task Task, Action Then)> answers = [];

    /// <summary>Something to do with a server's answer once it's in, on the editor's thread. Nothing for one that failed.</summary>
    private void When<T>(Task<T> asked, Action<T> then)
    {
        asked.ContinueWith(_ => context.Wake(), TaskScheduler.Default);
        answers.Add((asked, () =>
        {
            if (asked.IsCompletedSuccessfully)
                then(asked.Result);
        }));
    }

    /// <summary>Every tick, the answers that are in done, and the edits a server asked for made.</summary>
    private void UpdateAnswers()
    {
        for (int i = 0; i < answers.Count; i++)
        {
            if (!answers[i].Task.IsCompleted)
                continue;

            Action then = answers[i].Then;
            answers.RemoveAt(i--);
            try
            {
                then();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Hex] A language server's answer couldn't be used, {e.Message}");
            }
        }

        // By index, a foreach over the list as an IReadOnlyList made an enumerator every tick
        IReadOnlyList<LanguageServer> running = LanguageServers.Running;
        for (int i = 0; i < running.Count; i++)
        {
            while (running[i].TryTakeEdits(out var edits))
                ApplyEdits(edits);
        }
    }

    /// <summary>The server of a document when it does something, with the document's path, its latest text sent to it first.</summary>
    private bool Serves(HexDocument of, string provider, out LanguageServer server, out string path)
    {
        server = null!;
        path = string.Empty;
        if (of.Path is not { } at || !linters.TryGetValue(of, out LanguageServer? found) || found.Failure is not null || !found.Offers(provider))
            return false;

        if (lintIn.Remove(of))
            found.Change(at, of.Editor.Text);
        (server, path) = (found, at);
        return true;
    }

    // What a server is called where it's said, the program (serve-d, csharp-ls) rather than its entry's name (d, csharp)
    private string ShownName(LanguageServer server) =>
        ServerEntry(server.Name) is { } entry ? Path.GetFileNameWithoutExtension(CommandOf(entry)) : server.Name;

    /// <summary>Whether a document's server does something, for a command that's only there when it does.</summary>
    internal bool CanServe(HexDocument of, string provider) =>
        of.Path is not null && linters.TryGetValue(of, out LanguageServer? server) && server.Failure is null && server.Offers(provider);

    /* What the pointer rests on, and what a call takes */

    // What the server last said about a name and where, and the place being asked about now
    private (HexDocument? Of, int Version, int Line, int Column, string? Said) hovered;
    private (HexDocument? Of, int Version, int Line, int Column) hovering;

    /// <summary>
    /// What a document's server says the name at a place is, markdown. Nothing the first time, the editor is asked to ask
    /// again once it's in. Empty rather than null for a file of a server, HIDL's own hints read a D word as HIDL.
    /// </summary>
    private string? ServerHover(HexDocument of, CodeEditor editor, UITextPosition at)
    {
        if (!Serves(of, "hoverProvider", out LanguageServer server, out string path))
            return linters.ContainsKey(of) ? string.Empty : null;

        int version = editor.Document.Version;
        if (hovered.Of == of && (hovered.Version, hovered.Line, hovered.Column) == (version, at.Line, at.Column))
            return hovered.Said ?? string.Empty;

        if (hovering != (of, version, at.Line, at.Column))
        {
            hovering = (of, version, at.Line, at.Column);
            When(server.Hover(path, at.Line, at.Column), said =>
            {
                hovered = (of, version, at.Line, at.Column, said is null ? null : Printable(said));
                if (said is not null)
                    editor.AskAgain();
            });
        }

        return string.Empty;
    }

    /// <summary>
    /// Ctrl+Shift+I, what the name at the caret is, the hover the pointer would get, in a little window of its own. The
    /// hover was the mouse's alone, and nobody at the keyboard could read it.
    /// </summary>
    private void ShowHover()
    {
        HexDocument of = context.Documents.Current;
        string word = WordAtCaret(of);
        if (word.Length == 0)
        {
            context.Notices.Say(context.Strings.Said("code_hints.caret_not_on_name"), error: true);
            return;
        }

        if (!Serves(of, "hoverProvider", out LanguageServer server, out string path))
        {
            context.Notices.Say(context.Strings.Said("code_server.no_hover", ("word", word)));
            return;
        }

        UITextPosition caret = of.Editor.Document.Caret;
        When(server.Hover(path, caret.Line, caret.Column), said =>
        {
            if (said is null)
                context.Notices.Say(context.Strings.Said("code_server.no_hover", ("word", word)));
            else if (context.Documents.Current == of && of.Editor.Document.Caret == caret)
            {
                // Blank lines run together, serve-d's hover of a struct came with a dozen and the window was mostly empty.
                // An answer for a caret that's moved on since isn't shown, it came up over the file F12 had opened
                // In the hover's own box under the caret, the one the pointer gets, not a window over the line it's about
                of.Editor.ShowHoverAtCaret(Printable(BlankLines.Replace(said.Trim(), "\n\n")));
            }
        });
    }

    /// <summary>
    /// A server's markdown with the characters the UI font doesn't have put as ones it does, dashes and arrows and the
    /// like. phobos's docs said "Throws ? In case of" for an em dash.
    /// </summary>
    internal static string Printable(string markdown)
    {
        if (!markdown.Any(c => c > '~'))
            return markdown;

        var printable = new System.Text.StringBuilder(markdown.Length);
        foreach (char c in markdown)
        {
            printable.Append(c switch
            {
                '–' or '—' or '−' => "-",
                '‘' or '’' => "'",
                '“' or '”' => "\"",
                '…' => "...",
                '→' => "->",
                '←' => "<-",
                ' ' => " ",
                _ => c.ToString()
            });
        }

        return printable.ToString();
    }

    // Two blank lines or more, with whatever spaces are on them
    private static readonly System.Text.RegularExpressions.Regex BlankLines = new(@"\n(?:[ \t]*\n){2,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    // What the server last said a call takes and where the caret was, and whether it's being asked
    private (HexDocument? Of, int Version, UITextPosition Caret, UISignature? Said) signed;
    private bool signing;

    /// <summary>
    /// What a document's server says the call at the caret takes. The last one it said while it's asked again, so the box
    /// over the call doesn't blink as each letter is typed.
    /// </summary>
    private UISignature? ServerSignature(HexDocument of, string name, int argument)
    {
        if (!Serves(of, "signatureHelpProvider", out LanguageServer server, out string path))
            return null;

        UITextPosition caret = of.Editor.Document.Caret;
        int version = of.Editor.Document.Version;
        if (signed.Of == of && signed.Version == version && signed.Caret == caret)
            return signed.Said;

        if (!signing)
        {
            signing = true;
            When(server.Signature(path, caret.Line, caret.Column), said =>
            {
                signing = false;
                signed = (of, version, caret, said);
                of.Editor.AskAgain();
            });
        }

        return signed.Of == of ? signed.Said : null;
    }

    /* Going to where something is */

    /// <summary>F12 in a file of a server's, where the name at the caret is declared. False when its server can't say, for Hex's own way.</summary>
    internal bool GoToDefinition(HexDocument of)
    {
        if (!Serves(of, "definitionProvider", out LanguageServer server, out string path))
            return false;

        UITextPosition caret = of.Editor.Document.Caret;
        string word = WordAtCaret(of);
        When(server.Definition(path, caret.Line, caret.Column), places =>
        {
            if (places.Count == 0)
            {
                context.Notices.Say(context.Strings.Said("code_server.no_definition", ("word", word), ("server", ShownName(server))));
                return;
            }

            if (places.Count > 1)
            {
                ShowPlaces(context.Strings.Said("code_server.definitions_title", ("word", word), ("count", places.Count.ToString())), places);
                return;
            }

            LanguageServerPlace place = places[0];
            Console.WriteLine($"[Hex] {server.Name} says {word} is declared in {place.Path} on line {place.Line + 1}.");
            if (!place.OnDisk)
            {
                OpenSource(server, place, word);
                return;
            }

            context.Documents.OpenAt(place.Path, place.Line, NameColumn(place, word));
            context.Notices.Say(context.Strings.Said("code_server.declared_at", ("word", word), ("file", Path.GetFileName(place.Path)), ("line", (place.Line + 1).ToString())));
        });
        return true;
    }

    /// <summary>The folder a name of the framework's decompiled source is put in to be read, never linted.</summary>
    internal static string DecompiledFolder => Path.Combine(Path.GetTempPath(), "hex-decompiled");

    /// <summary>
    /// F12 on a name of the framework (Console), its source as the server decompiles it, opened to be read with the caret
    /// on the name. It said csharp didn't know where Console was declared.
    /// </summary>
    private void OpenSource(LanguageServer server, LanguageServerPlace place, string word)
    {
        When(server.Source(place.Path), source =>
        {
            if (source is null)
            {
                context.Notices.Say(context.Strings.Said("code_server.no_definition", ("word", word), ("server", ShownName(server))));
                return;
            }

            string file = Path.Combine(DecompiledFolder, place.Path[(place.Path.LastIndexOf('/') + 1)..]);
            try
            {
                Directory.CreateDirectory(DecompiledFolder);
                File.WriteAllText(file, source);
            }
            catch (Exception e)
            {
                context.Notices.Say(context.Strings.Said("code_server.source_not_written", ("why", e.Message)), error: true);
                return;
            }

            var onDisk = place with { Path = file };
            context.Documents.OpenAt(file, place.Line, NameColumn(onDisk, word));
            if (context.Documents.Find(file) is { } opened)
                opened.Editor.ReadOnly = true;
            context.Notices.Say(context.Strings.Said("code_server.decompiled", ("word", word), ("file", Path.GetFileName(file))));
        });
    }

    /// <summary>Shift+F12 in a file of a server's, everywhere the name at the caret is used, listed on the left. False when its server can't say.</summary>
    internal bool FindReferences(HexDocument of)
    {
        if (!Serves(of, "referencesProvider", out LanguageServer server, out string path))
            return false;

        UITextPosition caret = of.Editor.Document.Caret;
        string word = WordAtCaret(of);
        if (word.Length == 0)
        {
            context.Notices.Say(context.Strings.Said("code_hints.caret_not_on_name"), error: true);
            return true;
        }

        When(server.References(path, caret.Line, caret.Column), all =>
        {
            // The project's own when some are in it. serve-d says every name like it in the standard library as well, and
            // nothing there can use a name of the project's
            var places = all.Where(place => PlatformPaths.IsUnder(place.Path, server.RootDirectory) || context.Workspace.Roots.Any(root => PlatformPaths.IsUnder(place.Path, root))).ToList();
            if (places.Count == 0)
                places = [.. all];

            // Never a text or a comment that says the name, which serve-d counted as using it
            places = [.. places.Where(place => !HexSearch.InTextOrComment(LineOf(place), place.Character))];

            if (places.Count == 0)
            {
                context.Notices.Say(context.Strings.Said("code_server.no_references", ("word", word)));
                return;
            }

            ShowPlaces(context.Strings.Said("code_server.references_title", ("word", word), ("count", places.Count.ToString())), places);
            context.Notices.Say(context.Strings.Said("code_server.references_found", ("word", word), ("places", HexText.Count("place", places.Count)),
                ("files", HexText.Count("file", places.Select(place => place.Path).Distinct(PlatformPaths.Comparer).Count()))));
        });
        return true;
    }

    // Places listed in the side bar's results, each with its line's text, read off the file or the editor that has it open
    private void ShowPlaces(string title, IReadOnlyList<LanguageServerPlace> places)
    {
        var lines = new Dictionary<string, string[]>(PlatformPaths.Comparer);
        var found = new List<HexResult>();
        foreach (LanguageServerPlace place in places.OrderBy(place => place.Path, PlatformPaths.Comparer).ThenBy(place => place.Line))
        {
            if (!lines.TryGetValue(place.Path, out string[]? text))
                lines[place.Path] = text = context.Documents.Find(place.Path) is { } open ? open.Editor.Text.Split('\n') : HexSearch.ReadLines(place.Path);

            string said = place.Line < text.Length ? text[place.Line].TrimEnd('\r') : string.Empty;
            int lead = said.Length - said.TrimStart().Length;
            int length = place.EndLine == place.Line ? Math.Max(0, place.EndCharacter - place.Character) : 0;
            found.Add(new HexResult(place.Path, place.Line, place.Character, said.Trim(), length, Math.Max(-1, place.Character - lead)));
        }

        context.Window.ShowResults(title, found);
    }

    // The line of a place, read off the editor that has its file open or the disk
    private string LineOf(LanguageServerPlace place)
    {
        string[] text = context.Documents.Find(place.Path) is { } open ? open.Editor.Text.Split('\n') : HexSearch.ReadLines(place.Path);
        return place.Line < text.Length ? text[place.Line] : string.Empty;
    }

    /// <summary>
    /// Where on a place's line its name is, from where the place starts. serve-d says a declaration from its start, int
    /// in int area(), and the caret went there and a rename after it renamed int.
    /// </summary>
    private int NameColumn(LanguageServerPlace place, string word)
    {
        if (word.Length == 0)
            return place.Character;

        string[] text = context.Documents.Find(place.Path) is { } open ? open.Editor.Text.Split('\n') : HexSearch.ReadLines(place.Path);
        if (place.Line >= text.Length)
            return place.Character;

        string line = text[place.Line];
        for (int at = line.IndexOf(word, Math.Min(place.Character, line.Length), StringComparison.Ordinal); at >= 0; at = line.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            bool before = at > 0 && (char.IsLetterOrDigit(line[at - 1]) || line[at - 1] == '_');
            bool after = at + word.Length < line.Length && (char.IsLetterOrDigit(line[at + word.Length]) || line[at + word.Length] == '_');
            if (!before && !after)
                return at;
        }

        return place.Character;
    }

    // The word the caret is on in a document, for what's said about it
    private static string WordAtCaret(HexDocument of)
    {
        UITextDocument text = of.Editor.Document;
        var (start, end) = text.WordAt(text.Caret);
        return end > start ? text.LineAt(text.Caret.Line)[start..end] : string.Empty;
    }

    /* Changing it */

    /// <summary>
    /// F2 in a file of a server's, the name at the caret renamed everywhere the server knows of it. A server that says it
    /// renames and then can't there (serve-d) has Hex's own way do it, the name as a word of its own. False when its server
    /// doesn't rename at all.
    /// </summary>
    internal bool Rename(HexDocument of, Action<string, string> byWords)
    {
        if (!CanServe(of, "renameProvider"))
            return false;

        string word = WordAtCaret(of);
        if (word.Length == 0)
        {
            context.Notices.Say(context.Strings.Said("code_hints.caret_not_on_name"), error: true);
            return true;
        }

        UITextPosition caret = of.Editor.Document.Caret;
        context.Windows.Prompt(context.Strings.Said("code_hints.rename"), context.Strings.Said("code_server.rename_prompt", ("name", word)), word, context.Strings.Said("code_hints.rename"), string.Empty, renamed =>
        {
            if (renamed.Length == 0 || renamed == word || !Serves(of, "renameProvider", out LanguageServer server, out string path))
                return;

            When(server.Rename(path, caret.Line, caret.Column, renamed), edits =>
            {
                if (edits is null || edits.Count == 0)
                {
                    byWords(word, renamed);
                    return;
                }

                var (places, files) = ApplyEdits(edits);
                context.Notices.Say(context.Strings.Said("code_server.renamed", ("name", word), ("renamed", renamed), ("places", HexText.Count("place", places)), ("files", HexText.Count("file", files))));
            });
        });
        return true;
    }

    /// <summary>Shift+Alt+F, the file on screen formatted by its server.</summary>
    private void FormatDocument()
    {
        HexDocument of = context.Documents.Current;
        if (!Serves(of, "documentFormattingProvider", out LanguageServer server, out string path))
        {
            context.Notices.Say(context.Strings.Said("code_server.cannot_format"), error: true);
            return;
        }

        int version = of.Editor.Document.Version;
        When(server.Format(path, 4, insertSpaces: true), edits =>
        {
            if (of.Editor.Document.Version != version)
                return;

            if (edits is null || edits.Count == 0)
            {
                context.Notices.Say(context.Strings.Said("code_server.formatted_already"));
                return;
            }

            Rewrite(of, edits);
            context.Notices.Say(context.Strings.Said("code_server.formatted", ("server", ShownName(server))));
        });
    }

    /// <summary>
    /// Ctrl+., the fixes the compiler's own words make plain and the fixes and refactorings the server has for the caret's
    /// line (or what's selected), picked from a list.
    /// </summary>
    private void QuickFix()
    {
        HexDocument of = context.Documents.Current;
        UITextDocument text = of.Editor.Document;
        UITextPosition from = text.HasSelection ? text.SelectionStart : new UITextPosition(text.Caret.Line, 0);
        UITextPosition to = text.HasSelection ? text.SelectionEnd : new UITextPosition(text.Caret.Line, text.LineAt(text.Caret.Line).Length);
        List<LanguageServerAction> own = CompilerFixes(of, from.Line, to.Line);

        if (!Serves(of, "codeActionProvider", out LanguageServer server, out string path))
        {
            OfferFixes(own, null);
            return;
        }

        When(server.Actions(path, from.Line, from.Column, to.Line, to.Column), actions => OfferFixes([.. own, .. actions], server));
    }

    // The fixes there are, picked from a list, or said there are none
    private void OfferFixes(IReadOnlyList<LanguageServerAction> actions, LanguageServer? server)
    {
        if (actions.Count == 0)
        {
            context.Notices.Say(context.Strings.Said("code_server.no_fixes_here"));
            return;
        }

        context.Windows.Choose(context.Strings.Said("code_server.fixes_title"), context.Strings.Said("code_server.fixes_prompt", ("server", server is null ? "the compiler" : ShownName(server))),
            [.. actions.Select(action => (action.Title, (Action)(() => TakeAction(server, action))))]);
    }

    // A fix picked, its edits made and its command run (which may ask for more edits, taken as they come)
    private void TakeAction(LanguageServer? server, LanguageServerAction action)
    {
        if (action.Edits.Count > 0)
            ApplyEdits(action.Edits);
        if (server is not null && action.Command is { } command)
            When(server.Execute(command).ContinueWith(_ => true, TaskScheduler.Default), _ => { });
        context.Notices.Say(context.Strings.Said("code_server.applied", ("title", action.Title)));
    }

    /// <summary>
    /// Edits a server wants made, by file, each file's as one step of its undo. A file that isn't open is opened behind
    /// the one on screen with them made and nothing saved, the way renaming in other files has always gone. How many
    /// places and files were changed.
    /// </summary>
    private (int Places, int Files) ApplyEdits(IReadOnlyDictionary<string, IReadOnlyList<LanguageServerEdit>> edits)
    {
        int places = 0, files = 0;
        foreach (var (file, changes) in edits)
        {
            if (changes.Count == 0)
                continue;

            HexDocument? open = context.Documents.Find(file) ?? (File.Exists(file) ? context.Documents.OpenBehind(file) : null);
            if (open is not { IsText: true })
                continue;

            Rewrite(open, changes);
            places += changes.Count;
            files++;
        }

        return (places, files);
    }

    /// <summary>
    /// A document's text with a server's edits made to it, one step of its undo, the caret moved down by the lines put in
    /// over it. A fix that put a using at the top left the caret where it was, on the line above the one it had been on.
    /// </summary>
    private void Rewrite(HexDocument of, IReadOnlyList<LanguageServerEdit> edits)
    {
        UITextPosition caret = of.Editor.Document.Caret;
        int moved = 0;
        foreach (LanguageServerEdit edit in edits)
        {
            if (edit.EndLine < caret.Line)
                moved += edit.Text.Count(character => character == '\n') - (edit.EndLine - edit.Line);
        }

        context.Documents.Rewrite(of, LanguageServer.Apply(of.Editor.Text, edits));
        if (moved != 0)
        {
            int line = Math.Clamp(caret.Line + moved, 0, of.Editor.Document.LineCount - 1);
            of.Editor.Document.MoveTo(new UITextPosition(line, Math.Min(caret.Column, of.Editor.Document.LineAt(line).Length)));
        }
    }

    /// <summary>The commands that go through a file's server, there when the file on screen has a server that does them.</summary>
    private void AddServerCommands()
    {
        UICommands commands = context.Commands;
        commands.Add("Edit/comment", context.Strings.Said("code_server.format_document"), FormatDocument, "Shift+Alt+F", "list-flat", UIKeyUse.BoundWhileTyping)
            .IsVisible = () => CanServe(context.Documents.Current, "documentFormattingProvider");
        commands.Add("Edit/comment", context.Strings.Said("code_server.quick_fix"), QuickFix, "Ctrl+.", "lightbulb", UIKeyUse.BoundWhileTyping)
            .IsVisible = () => context.Documents.Current is { IsText: true, Path: not null } current && (CanServe(current, "codeActionProvider") || Checked(current));
        commands.Add("Go/symbols", context.Strings.Said("code_server.show_hover"), ShowHover, "Ctrl+Shift+I", "info", UIKeyUse.BoundWhileTyping)
            .IsVisible = () => CanServe(context.Documents.Current, "hoverProvider");
    }
}
