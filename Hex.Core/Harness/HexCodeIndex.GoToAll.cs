namespace Hex.Core;

// Go to All, Visual Studio's Ctrl+T, every type and member of the workspace by a few letters of its name. The same
// ranking hex_symbols has (the exact name, its start, its capitals, anywhere in it) and then Visual Studio's looser
// ways, the letters each starting a word of the name (gtd for GoToDefinition) and the letters in a few pieces with
// every piece after the first starting a word (simloop for SimulationLoop). Only code is in it, a heading of a markdown
// file or a key of a JSON one isn't something anybody goes to by Ctrl+T.
internal sealed partial class HexCodeIndex
{
    // What a name that only has the letters starting its words scores, and one that has them in a few pieces
    private const int INITIALS_SCORE = 30, PIECES_SCORE = 20;

    // How many pieces the letters can be in, past that it's letters scattered about
    private const int MOST_PIECES = 3;

    // The longest name the pieces are looked for in, a name longer than that is somebody's generated nonsense
    private const int LONGEST_NAME = 128;

    /// <summary>Whether Go to All lists a kind of symbol, the types and members of code.</summary>
    public static bool GoesToAll(string kind) => kind is "class" or "interface" or "struct" or "enum" or "method" or "property" or "field" or "event" or "variable";

    /// <summary>How well a name fits what was typed for Go to All, 0 for not at all. hex_symbols' ways first, then the looser ones.</summary>
    public static int LooseScore(string name, string asked)
    {
        int score = NameScore(name, asked);
        if (score > 0 || asked.Length < 2)
            return score;
        if (Initials(name, asked))
            return INITIALS_SCORE;
        return Pieces(name, asked) ? PIECES_SCORE : 0;
    }

    /// <summary>
    /// The declarations Go to All lists for what was typed, best first, at most so many. Container.Name narrows to what's
    /// inside something whose name ends that way, the way hex_symbols does. Stops early once it's cancelled.
    /// </summary>
    public static List<(HexIndexedFile File, HexSymbol Symbol, int Score)> GoToAll(HexIndexSnapshot index, string query, int most, CancellationToken cancel = default)
    {
        string asked = query.Trim();
        string? container = null;
        int dot = asked.LastIndexOf('.');
        if (dot > 0 && dot < asked.Length - 1)
            (container, asked) = (asked[..dot], asked[(dot + 1)..]);

        var found = new List<(HexIndexedFile File, HexSymbol Symbol, int Score)>();
        if (asked.Length == 0)
            return found;

        for (int i = 0; i < index.Files.Length; i++)
        {
            // A look at the token every so often, not every symbol
            if ((i & 63) == 0 && cancel.IsCancellationRequested)
                return [];

            HexIndexedFile file = index.Files[i];
            foreach (HexSymbol symbol in file.Symbols)
            {
                if (!GoesToAll(symbol.Kind))
                    continue;
                if (container is not null && !symbol.Container.EndsWith(container, StringComparison.OrdinalIgnoreCase))
                    continue;

                int score = LooseScore(symbol.Name, asked);
                if (score > 0)
                    found.Add((file, symbol, score + KindScore(symbol.Kind)));
            }
        }

        found.Sort((a, b) =>
        {
            int by = b.Score.CompareTo(a.Score);
            if (by != 0) return by;
            by = a.Symbol.Name.Length.CompareTo(b.Symbol.Name.Length);
            if (by != 0) return by;
            by = string.Compare(a.File.Relative, b.File.Relative, StringComparison.OrdinalIgnoreCase);
            return by != 0 ? by : a.Symbol.Line.CompareTo(b.Symbol.Line);
        });

        if (found.Count > most)
            found.RemoveRange(most, found.Count - most);
        return found;
    }

    // Whether a word of a name starts at that letter, the first, one after an underscore or a digit, a capital after a
    // small letter, or the last capital of a run before a small letter (the H of XMLHttp)
    private static bool StartsWord(string name, int at)
    {
        if (at == 0)
            return true;

        char before = name[at - 1], here = name[at];
        if (before is '_' or '-' or '.' || (char.IsDigit(before) != char.IsDigit(here)))
            return true;
        if (char.IsUpper(here) && char.IsLower(before))
            return true;
        return char.IsUpper(here) && at + 1 < name.Length && char.IsLower(name[at + 1]) && char.IsUpper(before);
    }

    private static bool Same(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    // Whether every letter typed lands on the start of a word of the name, in order
    private static bool Initials(string name, string asked)
    {
        int at = 0;
        foreach (char letter in asked)
        {
            while (at < name.Length && !(Same(name[at], letter) && StartsWord(name, at)))
                at++;
            if (at == name.Length)
                return false;
            at++;
        }

        return true;
    }

    /// <summary>
    /// Whether the letters typed are in the name in order in no more than MOST_PIECES pieces, the first starting a word
    /// and the rest each starting one too ("simloop" in SimulationLoop is Sim and Loop). pieces[t] is the fewest pieces
    /// it takes with the letter so far landing at t, on the stack so a keystroke over the workspace makes no garbage.
    /// </summary>
    private static bool Pieces(string name, string asked)
    {
        if (asked.Length > name.Length || name.Length > LONGEST_NAME)
            return false;

        const int NONE = int.MaxValue / 2;
        Span<int> pieces = stackalloc int[name.Length];
        Span<int> next = stackalloc int[name.Length];
        for (int t = 0; t < name.Length; t++)
            pieces[t] = Same(name[t], asked[0]) && StartsWord(name, t) ? 1 : NONE;

        for (int i = 1; i < asked.Length; i++)
        {
            // The fewest pieces for the last letter anywhere before t, for a new piece starting at t
            int fewest = NONE;
            for (int t = 0; t < name.Length; t++)
            {
                int best = NONE;
                if (Same(name[t], asked[i]))
                {
                    if (t > 0)
                        best = pieces[t - 1];
                    if (StartsWord(name, t))
                        best = Math.Min(best, fewest + 1);
                }

                next[t] = best;
                fewest = Math.Min(fewest, pieces[t]);
            }

            next.CopyTo(pieces);
        }

        int least = NONE;
        foreach (int count in pieces)
            least = Math.Min(least, count);
        return least <= MOST_PIECES;
    }
}
