using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.UI.Scripting;

namespace Hex.Core;

/// <summary>
/// HIDL's semantic highlighting, every name coloured by what it is the way ReSharper colours C#. A parameter, a local
/// (a let inside a function, a name a for goes through, one taken out of something with let { }), a top level let (a
/// field of the file, more or less), a const, a function (func name, and a let holding a func or an arrow), what the
/// language comes with, a key of an object and a member reached with a dot. A use of a name is whatever declared it
/// nearest round it, worked out in one go along the tokens with what's in scope kept by name, so a big file is as quick
/// as a small one. A local or a parameter given a new value somewhere is mutable, underlined. Worked out from the tokens,
/// so a file halfway through being typed still gets what can be made out of it.
/// </summary>
public static partial class HidlSemantics
{
    // A name made somewhere, what it is, and the tokens it's in scope over
    private readonly record struct Made(int Name, SyntaxKind Kind, SyntaxModifiers Modifiers, int From, int To);

    /// <summary>What every name of some HIDL is, by the line and column it starts at.</summary>
    public static List<SemanticSpan> Of(string text)
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

        int[] means = Resolve(tokens, made, madeAt, out bool[] reassigned);
        return Spans(tokens, made, means, reassigned);
    }

    // Every name the tokens make, with the tokens it's in scope over. A name is made once, the first thing to make it wins
    // (a for's let is the loop's name, not a let of the bracket it's in)
    private static void Declarations(Token[] tokens, Brackets brackets, List<Made> made, Dictionary<int, int> madeAt)
    {
        void Add(int name, SyntaxKind kind, SyntaxModifiers modifiers, int from, int to)
        {
            if (madeAt.ContainsKey(name))
                return;
            madeAt[name] = made.Count;
            made.Add(new Made(name, kind, modifiers, from, to));
        }

        int last = tokens.Length - 1;
        for (int i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i].Type)
            {
                // A function's parameters, in scope over its body, an arrow's expression too
                case TokenType.OpenParenthesis when HIDLDeclarations.OpensParameters(tokens, i):
                {
                    int body = brackets.Close(i) + 1;
                    if (body < tokens.Length && tokens[body].Type == TokenType.Arrow)
                        body++;
                    int to = brackets.BodyEnd(body);
                    foreach (Token parameter in HIDLDeclarations.Parameters(tokens, i))
                    {
                        int at = IndexOf(tokens, parameter, i);
                        Add(at, SyntaxKind.Parameter, SyntaxModifiers.None, at, to);
                    }
                    break;
                }

                // x => without brackets
                case TokenType.Identifier when HIDLDeclarations.IsArrowParameter(tokens, i):
                    Add(i, SyntaxKind.Parameter, SyntaxModifiers.None, i, brackets.BodyEnd(i + 2));
                    break;

                case TokenType.For:
                {
                    if (i + 1 >= tokens.Length)
                        break;
                    int to = brackets.BodyEnd(brackets.Close(i + 1) + 1);
                    foreach (Token name in HIDLDeclarations.LoopNames(tokens, i))
                    {
                        int at = IndexOf(tokens, name, i);
                        Add(at, SyntaxKind.Local, SyntaxModifiers.None, at, to);
                    }
                    break;
                }

                // func name, in scope over the whole of the block it's in, so it can be called above where it's written
                case TokenType.Function when i + 1 < tokens.Length && tokens[i + 1].Type == TokenType.Identifier:
                    Add(i + 1, SyntaxKind.Method, SyntaxModifiers.None, brackets.Enclosing(i) + 1, brackets.BlockEnd(i));
                    break;

                case TokenType.Let or TokenType.Const:
                {
                    bool top = brackets.Enclosing(i) < 0, constant = tokens[i].Type == TokenType.Const;
                    List<Token> names = HIDLDeclarations.Declared(tokens, i);
                    int to = top ? last : brackets.BlockEnd(i);
                    foreach (Token name in names)
                    {
                        int at = IndexOf(tokens, name, i);
                        SyntaxKind kind = names.Count == 1 && HoldsFunction(tokens, at) ? SyntaxKind.Method
                            : constant ? SyntaxKind.NamedConstant
                            : top ? SyntaxKind.Field : SyntaxKind.Local;
                        Add(at, kind, constant ? SyntaxModifiers.Readonly : SyntaxModifiers.None, at, to);
                    }
                    break;
                }
            }
        }
    }

    // Which declaration every name means, -1 for none. The names in scope are kept by name as a stack, a name made
    // inside a block over one of the same name outside it, and one whose scope is over is taken off when it's looked at
    private static int[] Resolve(Token[] tokens, List<Made> made, Dictionary<int, int> madeAt, out bool[] reassigned)
    {
        var means = new int[tokens.Length];
        Array.Fill(means, -1);
        reassigned = new bool[made.Count];

        var starting = new List<int>?[tokens.Length];
        for (int m = 0; m < made.Count; m++)
        {
            int from = Math.Clamp(made[m].From, 0, Math.Max(0, tokens.Length - 1));
            (starting[from] ??= []).Add(m);
        }

        var inScope = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (starting[i] is { } now)
            {
                foreach (int m in now)
                {
                    string name = tokens[made[m].Name].Value;
                    if (!inScope.TryGetValue(name, out List<int>? stack))
                        inScope[name] = stack = [];
                    stack.Add(m);
                }
            }

            if (tokens[i].Type != TokenType.Identifier)
                continue;

            if (madeAt.TryGetValue(i, out int itself))
            {
                means[i] = itself;
                continue;
            }

            if (IsMember(tokens, i) || IsKey(tokens, i) || !inScope.TryGetValue(tokens[i].Value, out List<int>? named))
                continue;

            while (named.Count > 0 && made[named[^1]].To < i)
                named.RemoveAt(named.Count - 1);
            if (named.Count == 0)
                continue;

            int meant = named[^1];
            means[i] = meant;
            if (i + 1 < tokens.Length && tokens[i + 1].Type is TokenType.Equals or TokenType.CompoundEquals)
                reassigned[meant] = true;
        }

        return means;
    }

    // The spans, each name by what it means, and what means nothing that can still be told by where it is
    private static List<SemanticSpan> Spans(Token[] tokens, List<Made> made, int[] means, bool[] reassigned)
    {
        var spans = new List<SemanticSpan>();
        for (int i = 0; i < tokens.Length; i++)
        {
            Token token = tokens[i];
            if (token.Type != TokenType.Identifier || token.Length <= 0)
                continue;

            SyntaxKind kind;
            SyntaxModifiers modifiers = SyntaxModifiers.None;
            bool called = i + 1 < tokens.Length && tokens[i + 1].Type == TokenType.OpenParenthesis;
            if (means[i] >= 0)
            {
                Made meant = made[means[i]];
                kind = meant.Kind;
                modifiers = meant.Modifiers;
                if (meant.Name == i)
                    modifiers |= SyntaxModifiers.Declaration;
                if (reassigned[means[i]] && kind is SyntaxKind.Local or SyntaxKind.Parameter)
                    modifiers |= SyntaxModifiers.Mutable;
            }
            else if (IsMember(tokens, i))
            {
                // compositor.button is a kind of component, the grammar's overlay has it as a type already
                if (i >= 2 && tokens[i - 2].Type == TokenType.Identifier && tokens[i - 2].Value == "compositor")
                    continue;
                kind = called ? SyntaxKind.Method : SyntaxKind.Property;
            }
            else if (IsKey(tokens, i))
                kind = SyntaxKind.Property;
            else if (HidlHighlighter.IsBuiltin(token.Value))
                kind = SyntaxKind.Builtin;
            else if (called)
                kind = SyntaxKind.Method;
            else
                continue;

            spans.Add(new SemanticSpan(token.Line - 1, token.Col - 1, token.Length, kind, modifiers));
        }

        return spans;
    }

    // Whether a name is reached with a dot, thing.name or thing?.name
    private static bool IsMember(Token[] tokens, int i) => i > 0 && tokens[i - 1].Type is TokenType.Dot or TokenType.QuestionDot;

    // Whether a name is the key of an object, name: value right after its brace or a comma (and not the middle of a ?:)
    private static bool IsKey(Token[] tokens, int i) =>
        i + 1 < tokens.Length && tokens[i + 1].Type == TokenType.Colon && (i == 0 || tokens[i - 1].Type is TokenType.OpenBracket or TokenType.Comma);

    // Whether the let or const whose name is at a place holds a function, = func, = (a, b) => or = x =>
    private static bool HoldsFunction(Token[] tokens, int name)
    {
        int value = name + 2;
        if (name + 1 >= tokens.Length || tokens[name + 1].Type != TokenType.Equals || value >= tokens.Length)
            return false;

        return tokens[value].Type == TokenType.Function || HIDLDeclarations.IsArrowParameter(tokens, value) || HIDLDeclarations.OpensArrowParameters(tokens, value);
    }

    // Where a token the declarations handed back is, looked for from a place on (they're the same tokens, by where they start)
    private static int IndexOf(Token[] tokens, Token token, int from)
    {
        for (int i = Math.Max(0, from); i < tokens.Length; i++)
        {
            if (tokens[i].Index == token.Index)
                return i;
        }

        return from;
    }

    /// <summary>
    /// Where every bracket closes and which bracket every token is inside, worked out once along the tokens rather than
    /// counted again for every name.
    /// </summary>
    private sealed class Brackets
    {
        private readonly Token[] tokens;
        private readonly int[] close, enclosing;

        public Brackets(Token[] tokens)
        {
            this.tokens = tokens;
            close = new int[tokens.Length];
            enclosing = new int[tokens.Length];
            var open = new Stack<int>();
            for (int i = 0; i < tokens.Length; i++)
            {
                close[i] = tokens.Length - 1;
                enclosing[i] = open.Count > 0 ? open.Peek() : -1;
                if (tokens[i].Type is TokenType.OpenParenthesis or TokenType.OpenBracket or TokenType.OpenBrace)
                    open.Push(i);
                else if (tokens[i].Type is TokenType.CloseParenthesis or TokenType.CloseBracket or TokenType.CloseBrace && open.Count > 0)
                    close[open.Pop()] = i;
            }
        }

        /// <summary>The bracket that closes the one at a place, the last token when nothing does.</summary>
        public int Close(int open) => open >= 0 && open < close.Length ? close[open] : tokens.Length - 1;

        /// <summary>The bracket a token is inside, -1 at the top.</summary>
        public int Enclosing(int at) => at >= 0 && at < enclosing.Length ? enclosing[at] : -1;

        /// <summary>The end of the block a token is in, the bracket that closes it or the end of the file at the top.</summary>
        public int BlockEnd(int at) => Enclosing(at) is var inside and >= 0 ? close[inside] : tokens.Length - 1;

        /// <summary>
        /// Where a body starting at a place ends, a block at its closing brace, one statement or an arrow's expression at
        /// the semicolon or comma after it or the bracket round it.
        /// </summary>
        public int BodyEnd(int at)
        {
            if (at >= tokens.Length)
                return tokens.Length - 1;
            if (tokens[at].Type == TokenType.OpenBracket)
                return close[at];

            int depth = 0;
            for (int i = at; i < tokens.Length; i++)
            {
                switch (tokens[i].Type)
                {
                    case TokenType.OpenParenthesis or TokenType.OpenBracket or TokenType.OpenBrace:
                        depth++;
                        break;
                    case TokenType.CloseParenthesis or TokenType.CloseBracket or TokenType.CloseBrace when --depth < 0:
                        return i;
                    case TokenType.Semicolon or TokenType.Comma when depth == 0:
                        return i;
                }
            }

            return tokens.Length - 1;
        }
    }
}
