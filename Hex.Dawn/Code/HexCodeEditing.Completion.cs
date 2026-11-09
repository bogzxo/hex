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

namespace Hex.Dawn;

// Completing what is typed by what it is being typed into, and going from a name to where it is declared (F12) and to
// everywhere it is used (Shift+F12).
internal sealed partial class HexCodeEditing
{
    /* Completing what is typed, by what it is being typed into */

    // Every name and key of every file of the workspace, gathered off the editor's thread the first time anything is
    // completed and again once something changes the files (null until then). It read up to two thousand files on the
    // editor's thread, the first completion of a big workspace froze Hex
    internal HashSet<string>? vocabulary;
    private bool gathering;

    // What the files of the workspace document, by name, gathered with the names (see DawnScene.Docs)
    internal Dictionary<string, WorkspaceDoc>? docIndex;

    // Which gathering is the one that counts, a change of the workspace while one is going makes it out of date
    private int vocabularyGeneration;

    /// <summary>Lets go of the names, the files they came from changed.</summary>
    internal void ForgetVocabulary()
    {
        vocabulary = null;
        docIndex = null;
        gathering = false;
        vocabularyGeneration++;
    }

    private static readonly HashSet<string> NoVocabulary = [];

    /// <summary>The names of the workspace, nothing while they are still being gathered.</summary>
    private HashSet<string> Vocabulary()
    {
        if (vocabulary is not null)
            return vocabulary;

        if (!gathering)
        {
            gathering = true;
            List<string> roots = [.. context.Workspace.Roots];
            int generation = vocabularyGeneration;
            context.Work.InBackground(context.Strings.Said("code_completion.gathering_names"),() => Gather(roots), gathered =>
            {
                if (generation != vocabularyGeneration)
                    return;

                (vocabulary, docIndex) = gathered;
                gathering = false;

                // Whatever was being typed was completed without them, or found nothing and closed
                if (code.IsFocused || code.CompletionOpen)
                    code.Complete(typed: true);
            }, quiet: true);
        }

        return NoVocabulary;
    }

    /// <summary>
    /// Every name in the files under some folders, the first two thousand of them, and what the documented ones say. For
    /// the background.
    /// </summary>
    private static (HashSet<string> Names, Dictionary<string, WorkspaceDoc> Docs) Gather(IReadOnlyList<string> roots)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var docs = new Dictionary<string, WorkspaceDoc>(StringComparer.Ordinal);
        foreach (string file in HexSearch.FilesUnder(roots).Take(2000))
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

            Token[] tokens = Lexer.Tokenize(source, lenient: true);
            foreach (Token token in tokens)
            {
                if (token.Type == TokenType.Identifier && token.Value.Length > 1)
                    names.Add(token.Value);
            }

            foreach (HidlDoc doc in HIDLDocs.Read(tokens))
                docs.TryAdd(doc.Name, new WorkspaceDoc(doc.Doc, file, doc.Line - 1, doc.Column - 1));
        }

        return (names, docs);
    }

    /// <summary>
    /// What there is to write at the caret of a document. A key of the game's language inside a text that starts with an
    /// @, one of the words a property takes inside its text (an anchor, a fill), a kind of component after compositor.,
    /// the properties of the component being made inside its braces, and otherwise the language's words with every name
    /// of the workspace.
    /// </summary>
    internal UICompletionRequest? Completions(CodeEditor editor)
    {
        HexDocument? of = documents.FirstOrDefault(open => open.Editor == editor);

        // What the parts complete in some files, the terminal's own files (HexTerminalFiles) and a file of another
        // language by its language server (HexLanguages)
        if (of is not null)
        {
            foreach (HexCompletionSource source in completionSources)
            {
                if (source(of, editor, out bool served) is var offered && served)
                    return offered;
            }
        }

        // A file that isn't HIDL (a .txt, a log, a language with no server) has nothing to complete. HIDL's completion read
        // the whole of a 50,000 line text file for every key typed, a third of a second each
        if (editor.Highlighter is not HidlHighlighter)
            return null;

        // The rest is HIDL's, worked out as the language server works it out, with the names of the workspace
        UITextPosition caret = editor.Document.Caret;
        return HidlAnalysis.Complete(editor.Text, caret.Line, caret.Column, Vocabulary());
    }

    /* Going to where something is */

    /// <summary>
    /// F12. Where what is at the caret is declared. A name goes to its let or const, in this file first and then in the
    /// workspace, a text naming a key of the game's language to the key in the language's file, a text naming a file
    /// (an include, a template) to the file.
    /// </summary>
    internal void GoToDefinition()
    {
        // An #include goes to the file it brings in, Slang's and C's alike. F12 on #include <common/camera.slang> said
        // camera wasn't declared with a let anywhere
        if (IncludedFile() is { } included)
        {
            context.Documents.OpenAt(included, 0, 0);
            return;
        }

        // A file of a language server's goes where the server says
        if (languages?.GoToDefinition(document) == true)
            return;

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

            string folder = document.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path))! : context.Windows.BrowseFolder;
            string named = Path.Combine(folder, token.Value);
            if (token.Value.Length > 0 && File.Exists(named))
            {
                context.Documents.OpenAt(named, 0, 0);
                return;
            }
        }

        var (start, end) = doc.WordAt(caret);
        if (end <= start)
        {
            context.Notices.Say(context.Strings.Said("code_completion.nothing_to_go_to"));
            return;
        }

        string word = line[start..end];

        // A file of a language with no server of its own (Slang, HLSL) goes to where the workspace's index has the name
        // declared. F12 on toClip in a shader said it wasn't declared with a let anywhere, which is HIDL's word for it
        if (document.Path is { } at && !IsHidlFile(at) && Declarations?.Invoke(word) is { Count: > 0 } declared)
        {
            // The same file's own first, then the rest
            var places = declared.OrderBy(place => PlatformPaths.Comparer.Equals(place.Path, Path.GetFullPath(at)) ? 0 : 1).ToList();
            if (places.Count == 1)
            {
                context.Documents.OpenAt(places[0].Path, places[0].Line, places[0].Column);
                context.Notices.Say(context.Strings.Said("code_completion.declared_in_file", ("word", word), ("file", context.Workspace.Relative(places[0].Path)), ("line", (places[0].Line + 1).ToString())));
            }
            else
            {
                window.ShowResults(context.Strings.Said("code_completion.declared_places", ("word", word), ("count", places.Count.ToString())),
                    [.. places.Select(place => new HexResult(place.Path, place.Line, place.Column, place.Says, word.Length, place.Column))]);
            }
            return;
        }

        // In this file
        if (FindDeclaration(code.Text, word, caret) is { } here)
        {
            code.GoTo(here.Line, here.Column);
            context.Notices.Say(context.Strings.Said("code_completion.declared_on_line", ("word", word), ("line", (here.Line + 1).ToString())));
            return;
        }

        // In the workspace
        foreach (string file in HexSearch.FilesUnder(context.Workspace.Roots))
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

            context.Documents.OpenAt(file, there.Line, there.Column);
            context.Notices.Say(context.Strings.Said("code_completion.declared_in_file", ("word", word), ("file", context.Workspace.Relative(file)), ("line", (there.Line + 1).ToString())));
            return;
        }

        IReadOnlyList<string> roots = context.Workspace.Roots;
        context.Notices.Say(roots.Count == 1
            ? context.Strings.Said("code_completion.not_declared_in_folder", ("word", word), ("folder", roots[0]))
            : context.Strings.Said("code_completion.not_declared_in_folders", ("word", word), ("count", roots.Count.ToString())), error: true);
    }

    /// <summary>
    /// The file the #include on the caret's line names, found next to this file, then in each folder above it and a
    /// shaders folder in each, the way the engine's shader compiler looks. Null when the line isn't an include or nothing
    /// is there.
    /// </summary>
    private string? IncludedFile()
    {
        if (document.Path is not { } path)
            return null;

        string line = code.Document.LineAt(code.Document.Caret.Line);
        if (IncludeLine.Match(line) is not { Success: true } match)
            return null;

        string named = match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
        for (string? folder = Path.GetDirectoryName(Path.GetFullPath(path)); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            foreach (string candidate in (string[])[Path.Combine(folder, named), Path.Combine(folder, "shaders", named)])
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            if (context.Workspace.Roots.Any(root => PlatformPaths.Comparer.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), folder)))
                break;
        }

        return null;
    }

    /// <summary>
    /// Where the workspace's index has a name declared, each place with its line's text, for F12 in a language Hex has
    /// no server for. Set by the scene, which has the index.
    /// </summary>
    internal Func<string, IReadOnlyList<(string Path, int Line, int Column, string Says)>>? Declarations { get; set; }

    // Whether a file is HIDL, which finds its declarations its own way
    private static bool IsHidlFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".hor" or ".jab" or ".hex-workspace";

    private static readonly System.Text.RegularExpressions.Regex IncludeLine = new(@"^\s*#\s*include\s*[<""]([^>""]+)[>""]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Where a name is declared in some code, by its tokens, null for nowhere. Given where it's used, the parameter or
    /// loop name or let round that place it means comes first.
    /// </summary>
    internal static UITextPosition? FindDeclaration(string source, string name, UITextPosition? usedAt = null) =>
        HidlAnalysis.FindDeclaration(source, name, usedAt?.Line ?? -1, usedAt?.Column ?? -1) is { } at ? new UITextPosition(at.Line, at.Column) : null;

    /// <summary>
    /// Opens the language file of the game at a key. Found where the file says it is when it runs (the text in Hex when
    /// it's open there), and by the line of its last part under the ones before it when it doesn't.
    /// </summary>
    internal void GoToKey(string key)
    {
        string file = Path.Combine(Localisation.Folder, Localisation.Language + ".hor");
        if (Localisation.Folder.Length == 0 || !File.Exists(file))
        {
            context.Notices.Say(context.Strings.Said("code_completion.no_language_loaded"), error: true);
            return;
        }

        string full = Path.GetFullPath(file);
        string source = context.Documents.Find(full) is { IsText: true } open ? open.Editor.Text : File.ReadAllText(full);
        if (HexLanguageEdits.Find(source, full, key) is var (keyLine, keyColumn))
        {
            context.Documents.OpenAt(full, keyLine, keyColumn);
            context.Notices.Say(context.Strings.Said("code_completion.key_in_language", ("key", key), ("language", Localisation.Language)));
            return;
        }

        string[] lines = HexSearch.ReadLines(file);
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

        context.Documents.OpenAt(file, Math.Min(line, lines.Length - 1), Math.Max(0, lines[Math.Min(line, lines.Length - 1)].Length - lines[Math.Min(line, lines.Length - 1)].TrimStart().Length));
        context.Notices.Say(part == parts.Length
            ? context.Strings.Said("code_completion.key_in_language", ("key", key), ("language", Localisation.Language))
            : context.Strings.Said("code_completion.language_has_no_key", ("key", key), ("language", Localisation.Language)), error: part != parts.Length);
    }

    /// <summary>Shift+F12, every line of the workspace the word at the caret is on as a word of its own, listed on the left.</summary>
    internal void FindReferences()
    {
        if (languages?.FindReferences(document) == true)
            return;

        var (start, end) = code.Document.WordAt(code.Document.Caret);
        if (end <= start)
        {
            context.Notices.Say(context.Strings.Said("code_completion.no_word_to_look_for"));
            return;
        }

        string word = code.Document.LineAt(code.Document.Caret.Line)[start..end];
        List<HexResult> found = HexSearch.FindIn(context.Workspace.Roots, word, new UIFindOptions(WholeWord: true));
        window.ShowResults(context.Strings.Said("code_completion.references_title", ("word", word), ("count", found.Count.ToString())), found);
        context.Notices.Say(context.Strings.Said("code_completion.references_found", ("word", word), ("lines", HexText.Count("line", found.Count)),
            ("files", HexText.Count("file", found.Select(result => result.Path).Distinct().Count()))));
    }
}
