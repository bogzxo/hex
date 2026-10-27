using Horizon.HIDL.Lexing;
using Horizon.UI.Scripting;

namespace Hex.Core;

/// <summary>A name a file of HIDL declares at its top and every place it's used, lines and columns from 0.</summary>
/// <param name="Name">What it's called.</param>
/// <param name="Line">The line it's declared on.</param>
/// <param name="Column">Where on the line its name is.</param>
/// <param name="Uses">Every place it's used, by line and column, the declaration not among them.</param>
public sealed record HidlDeclared(string Name, int Line, int Column, IReadOnlyList<(int Line, int Column)> Uses);

// The references of what a file of HIDL declares, for the counts over each declaration (the editor's lenses, Visual
// Studio's code lens). Only what's declared at the top of the file is counted, the way Visual Studio counts a class's
// members and not the locals of a method. A use is whatever the semantics work out a name means, so a parameter or a
// local of the same name somewhere else isn't counted as a use of the top one. HIDL has no imports, a file is all
// there is that can use what it declares.
public static partial class HidlSemantics
{
    /// <summary>Every name the top of some HIDL declares with the places it's used, in the order they're declared.</summary>
    public static List<HidlDeclared> References(string text)
    {
        Token[] all = Lexer.Tokenize(text, lenient: true);
        var kept = new List<Token>(all.Length);
        foreach (Token token in all)
        {
            if (token.Type != TokenType.Comment)
                kept.Add(token);
        }

        Token[] tokens = [.. kept];
        var brackets = new Brackets(tokens);
        var made = new List<Made>();
        var madeAt = new Dictionary<int, int>();
        Declarations(tokens, brackets, made, madeAt);
        int[] means = Resolve(tokens, made, madeAt, out _);

        // The declarations counted, by the index of what's made, and their uses as they're found along the tokens
        var counted = new Dictionary<int, List<(int Line, int Column)>>();
        for (int m = 0; m < made.Count; m++)
        {
            Made one = made[m];
            if (one.Kind is SyntaxKind.Method or SyntaxKind.Field or SyntaxKind.NamedConstant && AtTop(tokens, brackets, one.Name))
                counted[m] = [];
        }

        for (int i = 0; i < tokens.Length; i++)
        {
            if (means[i] >= 0 && made[means[i]].Name != i && counted.TryGetValue(means[i], out var uses))
                uses.Add((tokens[i].Line - 1, tokens[i].Col - 1));
        }

        var declared = new List<HidlDeclared>(counted.Count);
        for (int m = 0; m < made.Count; m++)
        {
            if (!counted.TryGetValue(m, out var uses))
                continue;

            Token name = tokens[made[m].Name];
            declared.Add(new HidlDeclared(name.Value, name.Line - 1, name.Col - 1, uses));
        }

        declared.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
        return declared;
    }

    // Whether a name is made at the top of the file, a let { a, b } there making its names inside the brackets after it
    private static bool AtTop(Token[] tokens, Brackets brackets, int name)
    {
        int inside = brackets.Enclosing(name);
        return inside < 0 || (inside > 0 && tokens[inside - 1].Type is TokenType.Let or TokenType.Const && brackets.Enclosing(inside - 1) < 0);
    }
}
