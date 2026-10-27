namespace Hex.Dawn;

// Reference counts over declarations, Visual Studio's code lens, "3 references" over a method and "2 references | 2
// references" over a line that declares two things. HIDL's are worked out here from the file itself (HidlSemantics's
// References), another language's come from its server, its code lenses when it says them (csharp-ls) or what the file
// declares and the references of each otherwise. A server is only asked about the declarations in sight, a few at a time,
// and what it said is kept with the file for as long as the text is what it was said of. Typing holds everything back
// until it stops for half a second, and the counts there were stay up meanwhile (the editor moves them along with their
// lines), so nothing jumps about under the caret. A press on a count is DawnScene.CodeLensMenu's.
internal sealed partial class DawnScene
{
    // How long the typing has to stop, how many questions a server has out at once, how many lines either side of what's
    // in sight are counted too (a line scrolled to has its count already), and how long before a file with no server yet
    // is looked at again
    private const long LENSES_SETTLE_MS = 500;
    private const int LENS_ASKS = 4;
    private const int LENS_AROUND = 8;
    private const long LENS_RETRY_MS = 2000;

    // A count of something said that isn't one (a lens of a server's that says Run Test), never shown
    private const int NOT_A_COUNT = -2;

    /// <summary>Something a file declares that has a count over it, where, and what's known of its count.</summary>
    private sealed class LensDeclaration
    {
        public required string Name;
        public string Kind = string.Empty;
        public int LensLine, Line, Column;

        // The count of this text, -1 until it's known, and the one there was before the text changed, shown meanwhile
        public int Count = -1, Shown = -1;
        public string Text = string.Empty;
        public bool Asking;

        // Every place that uses it, once they're known (HIDL's always, a server's once it's been asked)
        public IReadOnlyList<LanguageServerPlace>? Places;

        // The server's lens it came from, to resolve
        public LanguageServerLens? Lens;
    }

    /// <summary>The counts of one file, what text they're of, and what's still out being asked.</summary>
    private sealed class LensFile
    {
        public int Version = -1, Generation, InFlight, Missing, Refreshes;
        public long RetryAt;
        public bool Asking, Dirty, Server;
        public List<LensDeclaration> Declarations = [];
    }

    private readonly Dictionary<HexDocument, LensFile> lensFiles = [];
    private (HexDocument? Of, int Version) lensSeen = (null, -1);
    private long lensChangedAt;

    /// <summary>
    /// Every tick, the counts of the file on screen. Asked again once the typing stops (or the server says its project
    /// changed), the server's for whatever's in sight that isn't counted yet, and whatever came back given to the editor.
    /// </summary>
    private void UpdateReferenceLenses()
    {
        if (!settings.CodeLensReferences || !document.IsText || document.DiffOf is not null || document.Picture is not null || code.Highlighter is null)
            return;

        if (!lensFiles.TryGetValue(document, out LensFile? file))
            file = NewLensFile(document);

        int version = code.Document.Version;
        if (file.Dirty && file.Version == version)
            PushLenses(code, file);

        bool refreshed = file.Server && file.Refreshes != languages.SemanticRefreshesOf(document);
        if (file.Version != version || refreshed)
        {
            long now = System.Environment.TickCount64;
            if (file.Asking || now < file.RetryAt)
                return;

            // Something was counted before, so it waits for the typing to stop, a file just shown is counted at once
            if (file.Version >= 0 && !refreshed)
            {
                if (lensSeen != (document, version))
                {
                    lensSeen = (document, version);
                    lensChangedAt = now;
                    return;
                }

                if (now - lensChangedAt < LENSES_SETTLE_MS)
                    return;
            }

            AskLensDeclarations(document, code, file, version);
            return;
        }

        if (file.Server && file.Missing > 0 && file.InFlight < LENS_ASKS)
            AskCountsInSight(document, code, file);
    }

    // Helper method for the counts of a file met for the first time, and the files closed since let go of
    private LensFile NewLensFile(HexDocument of)
    {
        foreach (HexDocument gone in lensFiles.Keys.Where(open => !documents.Contains(open)).ToList())
            lensFiles.Remove(gone);

        var file = new LensFile();
        lensFiles[of] = file;
        return file;
    }

    // Helper method to have what a file declares worked out or asked for, out of UpdateReferenceLenses so the closures of
    // the answer are only made when there's something to ask
    private void AskLensDeclarations(HexDocument of, CodeEditor editor, LensFile file, int version)
    {
        int generation = ++file.Generation;
        int refreshes = languages.SemanticRefreshesOf(of);
        void Take(List<LensDeclaration> made, bool server)
        {
            file.Asking = false;
            if (file.Generation == generation)
                TakeLensDeclarations(editor, file, version, refreshes, made, server);
        }

        if (ReferenceEquals(editor.Highlighter, CodeLanguages.Hidl))
        {
            file.Asking = true;
            string text = editor.Text, path = of.Path ?? string.Empty;
            InBackground("lenses", () => HidlLensesOf(text, path), made =>
            {
                if (made is not null)
                {
                    Take(made, false);
                    return;
                }

                // Text that doesn't parse keeps the counts it had, moved along with their lines, until it does again
                file.Asking = false;
                if (file.Generation == generation)
                    file.Version = version;
            }, quiet: true);
        }
        else if (languages.DoesLenses(of))
        {
            file.Asking = languages.AskServerLenses(of, lenses => Take(ServerLenses(editor, version, lenses), true));
        }
        else
        {
            file.Asking = languages.AskDeclarations(of, symbols => Take(SymbolLenses(editor, version, symbols), true));
        }

        // No server to ask yet, csharp-ls is still starting, say. Looked at again in a moment
        if (!file.Asking)
            file.RetryAt = System.Environment.TickCount64 + LENS_RETRY_MS;
    }

    // HIDL's counts worked out off the editor's thread, null for text that doesn't parse, so the answer always comes back
    // and the file isn't left waiting on it for good. Half typed, a declaration isn't one (sdflet speed), its count went
    // and its row with it, and every line under it jumped up a line and back down again once it was put right
    private static List<LensDeclaration>? HidlLensesOf(string text, string path)
    {
        try
        {
            return HidlAnalysis.SyntaxErrors(text).Count > 0 ? null : HidlLenses(HidlSemantics.References(text), path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // HIDL's declarations, counted already, every use a place in the file (an untitled one's path is empty)
    private static List<LensDeclaration> HidlLenses(List<HidlDeclared> found, string path)
    {
        var made = new List<LensDeclaration>(found.Count);
        foreach (HidlDeclared declared in found)
        {
            var places = new LanguageServerPlace[declared.Uses.Count];
            for (int i = 0; i < places.Length; i++)
            {
                var (line, column) = declared.Uses[i];
                places[i] = new LanguageServerPlace(path, line, column, line, column + declared.Name.Length);
            }

            made.Add(new LensDeclaration { Name = declared.Name, LensLine = declared.Line, Line = declared.Line, Column = declared.Column, Count = places.Length, Places = places });
        }

        return made;
    }

    // A server's code lenses as declarations, each over its attributes, counted already when the server said a title
    private static List<LensDeclaration> ServerLenses(CodeEditor editor, int version, IReadOnlyList<LanguageServerLens> lenses)
    {
        var made = new List<LensDeclaration>(lenses.Count);
        if (editor.Document.Version != version)
            return made;

        UITextDocument text = editor.Document;
        foreach (LanguageServerLens lens in lenses)
        {
            int count = lens.Title.Length > 0 ? LanguageServer.ReferenceCountOf(lens.Title) ?? NOT_A_COUNT : -1;
            if (count == NOT_A_COUNT || lens.Line >= text.LineCount)
                continue;

            int start = Math.Min(text.LineAt(lens.Line).Length, lens.Character);
            made.Add(new LensDeclaration
            {
                Name = WordAt(text.LineAt(lens.Line), start), LensLine = HexCodeLens.LensLine(text.LineAt, text.LineCount, lens.Line, lens.Line),
                Line = lens.Line, Column = lens.Character, Count = count, Lens = lens
            });
        }

        return made;
    }

    // What a file's server says it declares as declarations, the kinds that get a count, inside each other and all
    private static List<LensDeclaration> SymbolLenses(CodeEditor editor, int version, IReadOnlyList<LanguageServerSymbol> symbols)
    {
        var made = new List<LensDeclaration>();
        if (editor.Document.Version != version)
            return made;

        UITextDocument text = editor.Document;
        void Add(IReadOnlyList<LanguageServerSymbol> list)
        {
            foreach (LanguageServerSymbol symbol in list)
            {
                if (HexCodeLens.Counts(symbol.Kind) && symbol.Line < text.LineCount)
                {
                    made.Add(new LensDeclaration
                    {
                        Name = symbol.Name, Kind = symbol.Kind, LensLine = HexCodeLens.LensLine(text.LineAt, text.LineCount, symbol.Line, symbol.StartLine),
                        Line = symbol.Line, Column = symbol.Character
                    });
                }

                Add(symbol.Children);
            }
        }

        Add(symbols);
        return made;
    }

    // The name a place starts at on its line, for what a count's list says it's of
    private static string WordAt(string line, int start)
    {
        int end = start;
        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
            end++;
        return line[start..end];
    }

    /// <summary>
    /// What a file declares, in, the counts there were carried over to the same names meanwhile (by name and which one of
    /// that name it is), so they stay up while the new ones are asked for.
    /// </summary>
    private void TakeLensDeclarations(CodeEditor editor, LensFile file, int version, int refreshes, List<LensDeclaration> made, bool server)
    {
        // Typed into since, it's asked again once the typing stops
        if (editor.Document.Version != version)
            return;

        var before = new Dictionary<(string, int), int>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (LensDeclaration was in file.Declarations)
        {
            int nth = seen[was.Name] = seen.GetValueOrDefault(was.Name) + 1;
            before[(was.Name, nth)] = was.Count >= 0 ? was.Count : was.Shown;
        }

        seen.Clear();
        made.Sort((a, b) => a.LensLine != b.LensLine ? a.LensLine.CompareTo(b.LensLine) : a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
        int missing = 0;
        foreach (LensDeclaration now in made)
        {
            int nth = seen[now.Name] = seen.GetValueOrDefault(now.Name) + 1;
            if (now.Count < 0)
            {
                now.Shown = before.GetValueOrDefault((now.Name, nth), -1);
                missing++;
            }

            now.Text = LensText(now.Count >= 0 ? now.Count : now.Shown);
        }

        file.Declarations = made;
        file.Version = version;
        file.Refreshes = refreshes;
        file.Server = server;
        file.Missing = missing;
        PushLenses(editor, file);
    }

    // Helper method to count the declarations in sight that aren't counted yet, as many as can be out at once
    private void AskCountsInSight(HexDocument of, CodeEditor editor, LensFile file)
    {
        var (first, last) = editor.LinesInSight;
        if (last < 0)
            return;

        // The first declaration at or after the top of what's in sight, by halves, a file can declare thousands of things
        List<LensDeclaration> declared = file.Declarations;
        int low = 0, high = declared.Count;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if (declared[middle].LensLine < first - LENS_AROUND)
                low = middle + 1;
            else
                high = middle;
        }

        for (int i = low; i < declared.Count && declared[i].LensLine <= last + LENS_AROUND && file.InFlight < LENS_ASKS; i++)
        {
            if (declared[i].Count == -1 && !declared[i].Asking)
                AskLensCount(of, file, declared[i]);
        }
    }

    // Helper method to have one declaration counted, by resolving its lens or asking for its references
    private void AskLensCount(HexDocument of, LensFile file, LensDeclaration declared)
    {
        int generation = file.Generation;
        declared.Asking = true;
        file.InFlight++;

        void Done(int count, IReadOnlyList<LanguageServerPlace>? places)
        {
            file.InFlight--;
            declared.Asking = false;
            if (file.Generation != generation || declared.Count != -1)
                return;

            declared.Count = count;
            declared.Places = places ?? declared.Places;
            declared.Text = count >= 0 ? LensText(count) : string.Empty;
            file.Missing--;
            file.Dirty = true;
        }

        bool asked = declared.Lens is { } lens
            ? languages.AskLensTitle(of, lens, title => Done(title is null ? NOT_A_COUNT : LanguageServer.ReferenceCountOf(title) ?? NOT_A_COUNT, null))
            : languages.AskReferences(of, declared.Line, declared.Column, places =>
            {
                List<LanguageServerPlace> uses = UsesOf(of, declared, places);
                Done(uses.Count, uses);
            });
        if (!asked)
            Done(NOT_A_COUNT, null);
    }

    // The places a server said use a declaration that really do, the declaration itself and a property's own get and set left out
    private static List<LanguageServerPlace> UsesOf(HexDocument of, LensDeclaration declared, IReadOnlyList<LanguageServerPlace> places, int line = -1)
    {
        string path = of.Path ?? string.Empty;
        string kind = declared.Lens is null ? declared.Kind : "property";
        line = line < 0 ? declared.Line : line;
        var uses = new List<LanguageServerPlace>(places.Count);
        foreach (LanguageServerPlace place in places)
        {
            if (HexCodeLens.IsUse(place, path, kind, line, declared.Column))
                uses.Add(place);
        }

        return uses;
    }

    /// <summary>What a count says, "1 reference", "12 references", "99+ references", nothing for one not known yet.</summary>
    private string LensText(int count) => count switch
    {
        < 0 => string.Empty,
        1 => Said("code_lens.reference"),
        > HexCodeLens.MOST_SHOWN => Said("code_lens.references_many", ("count", HexCodeLens.MOST_SHOWN.ToString())),
        _ => Said("code_lens.references", ("count", count.ToString()))
    };

    // The counts handed to the editor, every declaration that's a count by its index, for a press to find it again
    private static void PushLenses(CodeEditor editor, LensFile file)
    {
        file.Dirty = false;
        var lenses = new List<UICodeLensAbove>(file.Declarations.Count);
        for (int i = 0; i < file.Declarations.Count; i++)
        {
            LensDeclaration declared = file.Declarations[i];
            if (declared.Count != NOT_A_COUNT)
                lenses.Add(new UICodeLensAbove(declared.LensLine, declared.Column, declared.Text, i));
        }

        editor.LensesAbove = lenses;
    }

    /// <summary>The counts let go of in every file, the setting turned off, or on again to be counted afresh.</summary>
    private void ForgetReferenceLenses()
    {
        foreach (HexDocument open in documents)
            open.Editor.LensesAbove = [];
        lensFiles.Clear();
        lensSeen = (null, -1);
    }
}
