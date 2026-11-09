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
            while (running[i].TryTakeEdits(out var request))
                ApplyRequested(running[i], request);
        }
    }

    /// <summary>The server of a document when it does something, with the document's path, its latest text sent to it first.</summary>
    private bool Serves(HexDocument of, string provider, out LanguageServer server, out string path)
    {
        server = null!;
        path = string.Empty;
        if (!CanRequest(of, provider) || of.Path is not { } at || !linters.TryGetValue(of, out LanguageServer? found) || found.Failure is not null || !found.Offers(provider))
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
        CanRequest(of, provider) && of.Path is not null && linters.TryGetValue(of, out LanguageServer? server) && server.Failure is null && server.Offers(provider);

    /* What the pointer rests on, and what a call takes */

    private sealed record HoverRequest(HexDocument Of, LanguageServer Server, string? Path, int Version,
        UITextPosition Position, int Workspace);
    private (HoverRequest? Request, string? Said) hovered;
    private HoverRequest? hovering;
    private long hoverRequestNumber;

    private string? ServerHover(HexDocument of, CodeEditor editor, UITextPosition at)
    {
        if (!Serves(of, "hoverProvider", out LanguageServer server, out string path))
            return linters.ContainsKey(of) ? string.Empty : null;

        var request = new HoverRequest(of, server, of.Path, editor.Document.Version, at, editWorkspaceGeneration);
        return RequestHover(request, () => server.Hover(path, at.Line, at.Column));
    }

    private string RequestHover(HoverRequest request, Func<Task<string?>> ask)
    {
        if (hovered.Request == request)
            return hovered.Said ?? string.Empty;
        if (hovering != request)
        {
            hovering = request;
            long number = ++hoverRequestNumber;
            Task<string?> answer = ask().ContinueWith(task => task.IsCompletedSuccessfully ? task.Result : null, TaskScheduler.Default);
            When(answer, said =>
            {
                if (number != hoverRequestNumber) return;
                hovering = null;
                if (request.Workspace != editWorkspaceGeneration || request.Of.Path != request.Path
                    || request.Of.Editor.Document.Version != request.Version || context.Documents.Current != request.Of
                    || linters.GetValueOrDefault(request.Of) != request.Server || request.Server.Failure is not null) return;
                hovered = (request, said is null ? null : Printable(said));
                request.Of.Editor.AskAgain();
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
        WhenNavigating(CaptureNavigation(of, server), server.Hover(path, caret.Line, caret.Column), said =>
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

    private sealed record SignatureRequest(HexDocument Of, LanguageServer Server, int Version, UITextPosition Caret, string Name, int Argument);
    private (SignatureRequest? Request, UISignature? Said) signed;
    private SignatureRequest? signing;
    private long signatureRequestNumber;

    /// <summary>
    /// Signature help belongs to the exact document, server and caret that requested it.
    /// A slow answer must not block a newer request or replace its parameter highlight.
    /// </summary>
    private UISignature? ServerSignature(HexDocument of, string name, int argument)
    {
        if (!Serves(of, "signatureHelpProvider", out LanguageServer server, out string path))
            return null;

        UITextPosition caret = of.Editor.Document.Caret;
        var request = new SignatureRequest(of, server, of.Editor.Document.Version, caret, name, argument);
        return RequestSignature(request, () => server.Signature(path, caret.Line, caret.Column, argument));
    }

    private UISignature? RequestSignature(SignatureRequest request, Func<Task<UISignature?>> ask)
    {
        if (signed.Request == request)
            return signed.Said;

        if (signing != request)
        {
            signing = request;
            long number = ++signatureRequestNumber;
            Task<UISignature?> asked = ask()
                .ContinueWith(task => task.IsCompletedSuccessfully ? task.Result : null, TaskScheduler.Default);
            When(asked, said =>
            {
                if (number != signatureRequestNumber) return;
                signing = null;
                if (request.Of.Editor.Document.Version != request.Version || request.Of.Editor.Document.Caret != request.Caret
                    || context.Documents.Current != request.Of || linters.GetValueOrDefault(request.Of) != request.Server) return;
                signed = (request, said);
                request.Of.Editor.AskAgain();
            });
        }
        return null;
    }

    /* Going to where something is */

    /// <summary>F12 in a file of a server's, where the name at the caret is declared. False when its server can't say, for Hex's own way.</summary>
    internal bool GoToDefinition(HexDocument of)
    {
        if (!Serves(of, "definitionProvider", out LanguageServer server, out string path))
            return false;

        UITextPosition caret = of.Editor.Document.Caret;
        string word = WordAtCaret(of);
        var request = CaptureNavigation(of, server);
        WhenNavigating(request, server.Definition(path, caret.Line, caret.Column), places =>
        {
            if (places.Count == 0)
            {
                context.Notices.Say(context.Strings.Said("code_server.no_definition", ("word", word), ("server", ShownName(server))));
                return;
            }

            if (places.Count > 1)
            {
                ShowDefinitionPlaces(context.Strings.Said("code_server.definitions_title", ("word", word), ("count", places.Count.ToString())), server, places, word, request.Workspace);
                return;
            }

            LanguageServerPlace place = places[0];
            Console.WriteLine($"[Hex] {server.Name} says {word} is declared in {place.Path} on line {place.Line + 1}.");
            if (!place.OnDisk)
            {
                OpenSource(server, place, word, request);
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
    private void OpenSource(LanguageServer server, LanguageServerPlace place, string word, NavigationRequest request, bool alignName = true)
    {
        WhenNavigating(request, server.Source(place.Path), source =>
        {
            if (source is null)
            {
                context.Notices.Say(context.Strings.Said("code_server.no_definition", ("word", word), ("server", ShownName(server))));
                return;
            }

            string file = SourceCachePath(server.RootDirectory, place.Path);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, source);
                if (context.Documents.Find(file) is {} existing)
                {
                    existing.Editor.Text = source;
                    existing.SavedCode = existing.Editor.Text;
                    existing.DiskText = source;
                    existing.Modified = false;
                }
            }
            catch (Exception e)
            {
                context.Notices.Say(context.Strings.Said("code_server.source_not_written", ("why", e.Message)), error: true);
                return;
            }

            var onDisk = place with { Path = file };
            context.Documents.OpenAt(file, place.Line, alignName ? NameColumn(onDisk, word) : place.Character);
            if (context.Documents.Find(file) is { } opened)
                AttachSource(opened, server, place.Path);
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

        var request = CaptureNavigation(of, server);
        WhenNavigating(request, server.References(path, caret.Line, caret.Column), all =>
        {
            List<LanguageServerPlace> places = ProjectPlaces(server, all, word);
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

    /// <summary>
    /// The places a server said a name is used that are the project's own. serve-d says every name like it in the
    /// standard library as well, and nothing there can use a name of the project's. For a name of the library's
    /// (writeln) it said the library's 309 places and none of the project's, so the project's uses are looked for by the
    /// word instead. Never a text or a comment that says the name, which serve-d counted as using it.
    /// </summary>
    private List<LanguageServerPlace> ProjectPlaces(LanguageServer server, IReadOnlyList<LanguageServerPlace> all, string word)
    {
        var lines = new Dictionary<string, string[]>(PlatformPaths.Comparer);
        var places = all.Where(place => PlatformPaths.IsUnder(place.Path, server.RootDirectory) || context.Workspace.Roots.Any(root => PlatformPaths.IsUnder(place.Path, root))).ToList();
        if (places.Count == 0)
            places = WordPlaces(word);

        return [.. places.Where(place => !HexSearch.InTextOrComment(LineOf(place, lines), place.Character))];
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

    // The line of a place, read off the editor that has its file open or the disk, each file once for all its places
    private string LineOf(LanguageServerPlace place, Dictionary<string, string[]> lines)
    {
        if (!lines.TryGetValue(place.Path, out string[]? text))
            lines[place.Path] = text = context.Documents.Find(place.Path) is { } open ? open.Editor.Text.Split('\n') : HexSearch.ReadLines(place.Path);
        return place.Line < text.Length ? text[place.Line] : string.Empty;
    }

    // Every place in the workspace's files a word is said as a word of its own, as places a server would have said
    private List<LanguageServerPlace> WordPlaces(string word) =>
        [.. HexSearch.FindIn(context.Workspace.Roots.ToList(), word, new UIFindOptions(MatchCase: true, WholeWord: true))
            .Select(found => new LanguageServerPlace(found.Path, found.Line, found.Column, found.Line, found.Column + word.Length))];

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
    /// F2 renames only references resolved by the language server. An empty response is a rejected or unavailable
    /// rename, never permission to replace unrelated words. False when no server offers rename.
    /// </summary>
    internal bool Rename(HexDocument of)
    {
        if (of.Editor.ReadOnly) return true;
        if (!Serves(of, "renameProvider", out LanguageServer server, out string path))
            return false;

        string word = WordAtCaret(of);
        UITextPosition caret = of.Editor.Document.Caret;
        EditSnapshot promptSnapshot = CaptureEdits();
        NavigationRequest request = CaptureNavigation(of, server);
        if (server.PreparesRename)
        {
            FlushEditsToServer(server);
            WhenNavigating(request, server.PrepareRename(path, caret.Line, caret.Column).ContinueWith(task =>
                (Prepared: task.IsCompletedSuccessfully ? task.Result : null, Error: task.Exception?.GetBaseException().Message), TaskScheduler.Default), result =>
            {
                if (context.Documents.Current != of || of.Editor.Document.Caret != caret || !EditsCurrent(promptSnapshot, [path])) return;
                if (result.Prepared is null)
                {
                    string message = context.Strings.Said("code_server.not_renamed", ("server", ShownName(server)), ("name", word));
                    context.Notices.Say(result.Error is null ? message : message + "\n" + result.Error, error: true);
                    return;
                }
                string? suggested = result.Prepared.Placeholder;
                if (suggested is null && result.Prepared.Range is { } range)
                {
                    var text = of.Editor.Document;
                    if (range.Line >= text.LineCount || range.EndLine >= text.LineCount
                        || range.Character > text.LineAt(range.Line).Length || range.EndCharacter > text.LineAt(range.EndLine).Length) return;
                    suggested = text.TextBetween(new(range.Line, range.Character), new(range.EndLine, range.EndCharacter));
                }
                PromptRename(of, suggested ?? word, caret, promptSnapshot, request);
            });
        }
        else PromptRename(of, word, caret, promptSnapshot, request);
        return true;
    }

    private void PromptRename(HexDocument of, string word, UITextPosition caret, EditSnapshot promptSnapshot, NavigationRequest request)
    {
        if (word.Length == 0)
        {
            context.Notices.Say(context.Strings.Said("code_hints.caret_not_on_name"), error: true);
            return;
        }

        string originalPath = of.Path!;
        context.Windows.Prompt(context.Strings.Said("code_hints.rename"), context.Strings.Said("code_server.rename_prompt", ("name", word)), word, context.Strings.Said("code_hints.rename"), string.Empty, renamed =>
        {
            if (!NavigationCurrent(request) || !EditsCurrent(promptSnapshot, [originalPath]) || renamed.Length == 0 || renamed == word || !Serves(of, "renameProvider", out LanguageServer server, out string path))
                return;

            FlushEditsToServer(server);
            EditSnapshot snapshot = CaptureEdits();
            When(server.Rename(path, caret.Line, caret.Column, renamed), edits =>
            {
                if (!EditsCurrent(snapshot, edits is null ? [path] : edits.Keys.Append(path)))
                    return;
                if (edits is null || edits.Count == 0)
                {
                    context.Notices.Say(context.Strings.Said("code_server.not_renamed", ("server", ShownName(server)), ("name", word)), error: true);
                    return;
                }

                var (places, files) = ApplyEdits(edits);
                if (files == 0)
                    return;
                ShowRenamed(word, renamed, edits);
                context.Notices.Say(context.Strings.Said("code_server.renamed", ("name", word), ("renamed", renamed), ("places", HexText.Count("place", places)), ("files", HexText.Count("file", files))));
            });
        });
    }

    /// <summary>
    /// Every place a rename changed, listed on the left to look over, VS's rename preview after the fact. Nothing is
    /// saved yet, so a place that's wrong is a click away and Ctrl+Z in it takes it back. The places are where the edits
    /// were, moved along a line by the names before them on it that grew or shrank.
    /// </summary>
    private void ShowRenamed(string word, string renamed, IReadOnlyDictionary<string, IReadOnlyList<LanguageServerEdit>> edits)
    {
        var places = new List<LanguageServerPlace>();
        foreach (var (file, changes) in edits)
        {
            foreach (var onLine in changes.GroupBy(edit => edit.Line))
            {
                int shift = 0;
                foreach (LanguageServerEdit edit in onLine.OrderBy(edit => edit.Character))
                {
                    int at = edit.Character + shift;
                    places.Add(new LanguageServerPlace(file, edit.Line, at, edit.Line, at + edit.Text.Length));
                    shift += edit.Text.Length - (edit.EndLine == edit.Line ? edit.EndCharacter - edit.Character : 0);
                }
            }
        }

        if (places.Count > 0)
            ShowPlaces(context.Strings.Said("code_server.renamed_title", ("name", word), ("renamed", renamed), ("count", places.Count.ToString())), places);
    }

    /// <summary>Shift+Alt+F, the file on screen formatted by its server.</summary>
    private void FormatDocument() => FormatCode(selection: false);

    internal void FormatSelection() => FormatCode(selection: true);

    private void FormatCode(bool selection)
    {
        HexDocument of = context.Documents.Current;
        UITextDocument text = of.Editor.Document;
        if (selection && !text.HasSelection) return;
        if (!Serves(of, selection ? "documentRangeFormattingProvider" : "documentFormattingProvider", out LanguageServer server, out string path))
        {
            context.Notices.Say(context.Strings.Said("code_server.cannot_format"), error: true);
            return;
        }

        EditSnapshot snapshot = CaptureEdits();
        // The file's own indentation, its tab size and spaces or tabs, as the settings and its .editorconfig made it
        var request = selection
            ? server.FormatRange(path, text.SelectionStart.Line, text.SelectionStart.Column, text.SelectionEnd.Line, text.SelectionEnd.Column,
                of.Editor.TabSize, insertSpaces: !text.Indent.StartsWith('\t'))
            : server.Format(path, of.Editor.TabSize, insertSpaces: !text.Indent.StartsWith('\t'));
        When(request, edits =>
        {
            if (!EditsCurrent(snapshot, [path]))
                return;

            if (edits is null || edits.Count == 0)
            {
                context.Notices.Say(context.Strings.Said("code_server.formatted_already"));
                return;
            }

            if (!Rewrite(of, edits)) return;
            context.Notices.Say(context.Strings.Said("code_server.formatted", ("server", ShownName(server))));
        });
    }

    /// <summary>
    /// Ctrl+., the fixes the compiler's own words make plain and the fixes and refactorings the server has for the caret's
    /// line (or what's selected), picked from a list.
    /// </summary>
    internal void QuickFix()
    {
        HexDocument of = context.Documents.Current;
        if (of.Editor.ReadOnly) return;
        UITextDocument text = of.Editor.Document;
        UITextPosition from = text.HasSelection ? text.SelectionStart : new UITextPosition(text.Caret.Line, 0);
        UITextPosition to = text.HasSelection ? text.SelectionEnd : new UITextPosition(text.Caret.Line, text.LineAt(text.Caret.Line).Length);
        List<LanguageServerAction> own = CompilerFixes(of, from.Line, to.Line);
        EditSnapshot snapshot = CaptureEdits();

        if (!Serves(of, "codeActionProvider", out LanguageServer server, out string path))
        {
            OfferFixes(of, own, null, snapshot);
            return;
        }

        // A server that failed to answer still has the compiler's fixes offered, or it's said there are none. Nothing
        // came up at all and Ctrl+. looked like it had been swallowed
        FlushEditsToServer(server);
        Task<IReadOnlyList<LanguageServerAction>> asked = server.Actions(path, from.Line, from.Column, to.Line, to.Column)
            .ContinueWith(task => task.IsCompletedSuccessfully ? task.Result : (IReadOnlyList<LanguageServerAction>)[], TaskScheduler.Default);
        When(asked, actions => OfferFixes(of, [.. own, .. actions], server, snapshot));
    }

    // The fixes there are, picked from a list, or said there are none
    private void OfferFixes(HexDocument of, IReadOnlyList<LanguageServerAction> actions, LanguageServer? server, EditSnapshot snapshot)
    {
        if (of.Path is not { } path || !EditsCurrent(snapshot, [path]))
            return;
        if (actions.Count == 0)
        {
            context.Notices.Say(context.Strings.Said("code_server.no_fixes_here"));
            return;
        }

        // What fixes the line first, the refactors after. csharp-ls put Extract local function over the fix for a
        // misspelt name, and Enter took it. By the caret with what each would change under the list, Visual Studio's
        // lightbulb, rather than up at the top of the window
        of.Editor.HideSignature();
        string text = of.Editor.Text;
        List<UIChoice> choices = [.. actions.OrderBy(action => action.Fixes ? 0 : 1).Select(action =>
            new UIChoice(action.Title, () => TakeAction(server, action, snapshot, path)) { Preview = FixPreview(of.Path, text, action) })];
        context.Windows.ChooseAtCaret(context.Strings.Said("code_server.fixes_title"),
            context.Strings.Said("code_server.fixes_prompt", ("server", server is null ? "the compiler" : ShownName(server))), choices, of.Editor);
    }

    // A fix picked, its edits made and its command run (which may ask for more edits, taken as they come)
    private void TakeAction(LanguageServer? server, LanguageServerAction action, EditSnapshot snapshot, string origin)
    {
        if (!EditsCurrent(snapshot, action.Edits.Keys.Append(origin))) return;
        if (action.Resolve is { } resolve)
        {
            When(resolve().ContinueWith(task => task.IsCompletedSuccessfully ? task.Result : null, TaskScheduler.Default), resolved =>
            {
                if (resolved is null) context.Notices.Say(context.Strings.Said("code_server.resolve_failed"), error: true);
                else TakeAction(server, resolved, snapshot, origin);
            });
            return;
        }
        if (server is not null && action.Command is not null && commandsPending.ContainsKey(server))
        {
            context.Notices.Say(context.Strings.Said("code_server.command_pending"), error: true);
            return;
        }
        if (action.Edits.Count > 0 && ApplyEdits(action.Edits).Files == 0)
            return;
        if (server is not null && action.Command is { } command)
        {
            var pending = new PendingEditCommand(CaptureEdits());
            commandsPending[server] = pending;
            When(server.Execute(command).ContinueWith(task => task.IsCompletedSuccessfully, TaskScheduler.Default), succeeded =>
            {
                if (commandsPending.GetValueOrDefault(server) == pending)
                    commandsPending.Remove(server);
                if (succeeded && !pending.Failed)
                    context.Notices.Say(context.Strings.Said("code_server.applied", ("title", action.Title)));
                else if (!pending.Failed)
                    context.Notices.Say(context.Strings.Said("code_server.edit_conflict"), error: true);
            });
            return;
        }
        context.Notices.Say(context.Strings.Said("code_server.applied", ("title", action.Title)));
    }

    /// <summary>
    /// Edits a server wants made, by file, each file's as one step of its undo. A file that isn't open is opened behind
    /// the one on screen with them made and nothing saved, the way renaming in other files has always gone. How many
    /// places and files were changed.
    /// </summary>
    private (int Places, int Files) ApplyEdits(IReadOnlyDictionary<string, IReadOnlyList<LanguageServerEdit>> edits)
    {
        var prepared = new List<(HexDocument Document, IReadOnlyList<LanguageServerEdit> Changes)>();
        foreach (var (file, changes) in edits)
        {
            if (changes.Count == 0)
                continue;
            HexDocument? doc = context.Documents.Find(file) ?? (File.Exists(file) ? context.Documents.OpenBehind(file) : null);
            if (doc is not { IsText: true })
            {
                context.Notices.Say(context.Strings.Said("code_server.edit_conflict"), error: true);
                return (0, 0);
            }
            // Validate every target before changing any buffer.
            var sorted = SortUsings(doc.Editor.Text, changes);
            if (!doc.Editor.Document.CanApplyEdits(EditorEdits(sorted)))
            {
                context.Notices.Say(context.Strings.Said("code_server.edit_conflict"), error: true);
                return (0, 0);
            }
            prepared.Add((doc, sorted));
        }
        int places = 0, files = 0;
        foreach (var (open, changes) in prepared)
        {
            if (changes.Count == 0)
                continue;

            // A using a fix puts in goes where it goes in order among the others (HexLanguages.Usings)
            if (!Rewrite(open, changes)) return (0, 0);
            places += changes.Count;
            files++;
        }

        return (places, files);
    }

    /// <summary>
    /// A document's text with a server's edits made to it, one step of its undo, the caret moved down by the lines put in
    /// over it. A fix that put a using at the top left the caret where it was, on the line above the one it had been on.
    /// </summary>
    private bool Rewrite(HexDocument of, IReadOnlyList<LanguageServerEdit> edits)
    {
        bool applied = context.Documents.Rewrite(of, EditorEdits(edits));
        if (!applied) context.Notices.Say(context.Strings.Said("code_server.edit_conflict"), error: true);
        return applied;
    }

    private static UITextEdit[] EditorEdits(IReadOnlyList<LanguageServerEdit> edits) => edits.Select(edit =>
        new UITextEdit(new(edit.Line, edit.Character), new(edit.EndLine, edit.EndCharacter), edit.Text)).ToArray();

    /// <summary>The commands that go through a file's server, there when the file on screen has a server that does them.</summary>
    private void AddServerCommands()
    {
        UICommands commands = context.Commands;
        commands.Add("Go/symbols", context.Strings.Said("code_server.related_diagnostic"), GoToRelatedDiagnostic, icon: "go-to-file")
            .IsVisible = () => RelatedDiagnosticsAtCaret().Count > 0;
        commands.Add("Go/symbols", context.Strings.Said("code_server.diagnostic_documentation"), OpenDiagnosticDocumentation, icon: "info")
            .IsVisible = () => DiagnosticDocumentationAtCaret() is not null;
        commands.Add("Edit/comment", context.Strings.Said("code_server.organize_imports"), OrganizeImports, icon: "list-flat")
            .IsVisible = CanOrganizeImports;
        commands.Add("Edit/comment", context.Strings.Said("code_server.format_document"), FormatDocument, "Shift+Alt+F", "list-flat", UIKeyUse.BoundWhileTyping)
            .IsVisible = () => CanServe(context.Documents.Current, "documentFormattingProvider");
        var formatSelection = commands.Add("Edit/comment", context.Strings.Said("code_server.format_selection"), FormatSelection, "Ctrl+K Ctrl+F", "list-flat", UIKeyUse.BoundWhileTyping);
        formatSelection.IsVisible = () => CanServe(context.Documents.Current, "documentRangeFormattingProvider");
        formatSelection.IsEnabled = () => context.Documents.Current.Editor.Document.HasSelection;
        commands.Add("Edit/comment", context.Strings.Said("code_server.quick_fix"), QuickFix, "Ctrl+.", "lightbulb", UIKeyUse.BoundWhileTyping)
            .IsVisible = () => context.Documents.Current is { IsText: true, Path: not null } current && (CanServe(current, "codeActionProvider") || Checked(current));
        commands.Add("Go/symbols", context.Strings.Said("code_server.show_hover"), ShowHover, "Ctrl+Shift+I", "info", UIKeyUse.BoundWhileTyping)
            .IsVisible = () => CanServe(context.Documents.Current, "hoverProvider");
        commands.Add("Run/check", context.Strings.Said("languages.restart_server"), RestartCurrentServer, icon: "refresh")
            .IsVisible = () => context.Documents.Current is { IsText: true, Path: { } path } && LanguageServers.RegistrationFor(CodeLanguages.WithoutBackupSuffix(path)) is not null;
    }
}
