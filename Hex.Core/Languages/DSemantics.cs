using Horizon.UI.Scripting;

namespace Hex.Core;

/// <summary>
/// D's names told apart by Hex itself, for when no server says what they are (serve-d doesn't do semantic tokens), so D
/// gets ReSharper's kind of colouring all the same. Read from the tokens alone, no parsing, so it's a guess at the edges,
/// but a good one. What a struct, a class, an interface, an enum, a template or an alias declares is that kind wherever
/// the name turns up in the file, a function declared or called is a method, a member reached with a dot is a field, a
/// method (a call, a UFCS one too) or a property, the names a function declares are its parameters and locals, the ones
/// an aggregate declares its fields, the ones at the top of a module are module variables (static fields as far as the
/// colours go) or manifest constants. A local or a parameter written to again is mutable, and whatever is declared
/// deprecated is struck through everywhere.
/// </summary>
public static partial class DSemantics
{
    private enum Scope : byte
    {
        Module,
        Aggregate,
        Function,
        Enum
    }

    // What the tokens inside a pair of brackets are, from a brace of a declaration to its closing brace
    private sealed class Frame(Scope kind, int body, int end, Dictionary<string, (SyntaxKind Kind, SyntaxModifiers Modifiers)>? names)
    {
        public Scope Kind { get; } = kind;
        public int Body { get; } = body;
        public int End { get; } = end;
        public Dictionary<string, (SyntaxKind Kind, SyntaxModifiers Modifiers)>? Names { get; } = names;
    }

    // What the file declares that goes for the whole of it, by name
    private sealed class Declared
    {
        public readonly Dictionary<string, SyntaxKind> Types = new(StringComparer.Ordinal);
        public readonly HashSet<string> Functions = new(StringComparer.Ordinal);
        public readonly Dictionary<string, SyntaxModifiers> Fields = new(StringComparer.Ordinal);
        public readonly HashSet<string> Constants = new(StringComparer.Ordinal);
        public readonly HashSet<string> EnumMembers = new(StringComparer.Ordinal);
        public readonly HashSet<string> TypeParameters = new(StringComparer.Ordinal);
        public readonly HashSet<string> Deprecated = new(StringComparer.Ordinal);
    }

    /// <summary>What every name of some D is, by the line and column it starts at.</summary>
    public static List<SemanticSpan> Of(string text)
    {
        List<Piece> p = Lex(text);
        int n = p.Count;
        var (close, enclosing) = Brackets(p);
        var kinds = new SyntaxKind?[n];
        var modifiers = new SyntaxModifiers[n];
        var scopes = new Dictionary<string, (SyntaxKind Kind, SyntaxModifiers Modifiers)>?[n];
        var file = new Declared();

        void Make(int at, SyntaxKind kind, SyntaxModifiers with = SyntaxModifiers.None)
        {
            if (at < 0 || at >= n || p[at].Kind != DToken.Name || kinds[at] is not null)
                return;
            kinds[at] = kind;
            modifiers[at] = with | SyntaxModifiers.Declaration;
        }

        var stack = new Stack<Frame>();
        stack.Push(new Frame(Scope.Module, -1, n, null));
        var bodies = new HashSet<int>();
        bool deprecated = false;

        for (int i = 0; i < n; i++)
        {
            while (stack.Count > 1 && stack.Peek().End < i)
                stack.Pop();

            Frame frame = stack.Peek();
            Frame? function = stack.FirstOrDefault(each => each.Kind == Scope.Function);
            scopes[i] = function?.Names;
            Piece piece = p[i];

            // A plain block inside a function keeps the function's names, one at the top or in an aggregate (a version, a
            // static if) is no scope of its own
            if (piece.Is('{') && !bodies.Contains(i))
                continue;
            if (piece.Is(';') || piece.Is('{') || piece.Is('}'))
            {
                deprecated = false;
                continue;
            }

            if (piece.Kind == DToken.Keyword)
            {
                switch (piece.Text)
                {
                    case "deprecated":
                        deprecated = true;
                        break;

                    case "module" or "import":
                        for (int at = i + 1; at < n && !p[at].Is(';') && !p[at].Is(':'); at++)
                        {
                            if (p[at].Kind == DToken.Name)
                                kinds[at] = SyntaxKind.Namespace;
                        }
                        break;

                    case "struct" or "union" or "class" or "interface" or "template" when i + 1 < n && p[i + 1].Kind == DToken.Name:
                    {
                        SyntaxKind kind = piece.Text switch
                        {
                            "struct" or "union" => SyntaxKind.Struct,
                            "interface" => SyntaxKind.Interface,
                            _ => SyntaxKind.Class
                        };
                        Make(i + 1, kind, deprecated ? SyntaxModifiers.Deprecated : SyntaxModifiers.None);
                        file.Types[p[i + 1].Text] = kind;
                        if (deprecated)
                            file.Deprecated.Add(p[i + 1].Text);
                        if (i + 2 < n && p[i + 2].Is('('))
                            TemplateParameters(p, close, i + 2, file, Make);
                        if (BodyAfter(p, close, i + 2) is var body and >= 0)
                        {
                            bodies.Add(body);
                            stack.Push(new Frame(Scope.Aggregate, body, close[body], null));
                        }
                        break;
                    }

                    case "enum":
                    {
                        // enum Name { or enum Name : base {, a type of its own. enum Name = is a constant, and enum { a, b } a few
                        int at = i + 1;
                        if (at < n && p[at].Kind == DToken.Name && at + 1 < n && (p[at + 1].Is('{') || p[at + 1].Is(':')))
                        {
                            Make(at, SyntaxKind.Enum);
                            file.Types[p[at].Text] = SyntaxKind.Enum;
                        }
                        else if (at < n && p[at].Kind == DToken.Name && at + 1 < n && p[at + 1].Is('='))
                        {
                            Make(at, SyntaxKind.NamedConstant, SyntaxModifiers.Readonly | SyntaxModifiers.Static);
                            file.Constants.Add(p[at].Text);
                            break;
                        }
                        else if (!(at < n && p[at].Is('{')))
                            break;

                        if (BodyAfter(p, close, at) is var body and >= 0)
                        {
                            bodies.Add(body);
                            stack.Push(new Frame(Scope.Enum, body, close[body], null));
                        }
                        break;
                    }

                    case "alias" when i + 1 < n:
                    {
                        // alias Name = ..., or the old alias int Name;
                        int named = p[i + 1].Kind == DToken.Name && i + 2 < n && p[i + 2].Is('=') ? i + 1 : -1;
                        if (named < 0)
                        {
                            for (int at = i + 1; at < n && !p[at].Is(';'); at++)
                            {
                                if (p[at].Kind == DToken.Name && at + 1 < n && p[at + 1].Is(';'))
                                    named = at;
                            }
                        }

                        if (named >= 0 && p[named].Text != "this")
                        {
                            Make(named, SyntaxKind.Class);
                            file.Types[p[named].Text] = SyntaxKind.Class;
                        }
                        break;
                    }

                    // A constructor, and a unittest, which declare names as a function does
                    case "this" when i + 1 < n && p[i + 1].Is('(') && frame.Kind != Scope.Function && Body(p, close, close[i + 1] + 1) is var (start, end) && start >= 0:
                    {
                        var names = new Dictionary<string, (SyntaxKind, SyntaxModifiers)>(StringComparer.Ordinal);
                        Parameters(p, close, i + 1, names, Make);
                        if (p[start].Is('{'))
                            bodies.Add(start);
                        stack.Push(new Frame(Scope.Function, start, end, names));
                        break;
                    }

                    case "unittest" when i + 1 < n && p[i + 1].Is('{'):
                        bodies.Add(i + 1);
                        stack.Push(new Frame(Scope.Function, i + 1, close[i + 1], new(StringComparer.Ordinal)));
                        break;

                    // foreach (i, ref item; list), and catch (Exception e)
                    case "foreach" or "foreach_reverse" or "catch" when i + 1 < n && p[i + 1].Is('(') && function?.Names is { } locals:
                    {
                        int last = -1;
                        for (int at = i + 2; at < close[i + 1]; at++)
                        {
                            if (p[at].Is(';'))
                                break;
                            if (p[at].Kind == DToken.Name && enclosing[at] == i + 1)
                                last = at;
                            if ((p[at].Is(',') || at + 1 == close[i + 1] || (at + 1 < n && p[at + 1].Is(';'))) && last >= 0)
                            {
                                Make(last, SyntaxKind.Local);
                                locals[p[last].Text] = (SyntaxKind.Local, SyntaxModifiers.None);
                                last = -1;
                            }
                        }
                        break;
                    }
                }

                continue;
            }

            if (piece.Kind != DToken.Name || kinds[i] is not null || (i > 0 && (p[i - 1].Is('.') || p[i - 1].Is('@'))))
                continue;

            // A member of an enum, a name straight after its brace or a comma
            if (frame.Kind == Scope.Enum && i > 0 && (i - 1 == frame.Body || (p[i - 1].Is(',') && enclosing[i - 1] == frame.Body)))
            {
                Make(i, SyntaxKind.EnumMember, SyntaxModifiers.Static | SyntaxModifiers.Readonly);
                file.EnumMembers.Add(piece.Text);
                continue;
            }

            // A function, a type and a name before a bracket, its parameters (and maybe its template's before them), whatever
            // goes after them (const, pure, @safe, an if) and then its body, a ; or a =>
            if (i + 1 < n && p[i + 1].Is('(') && i > 0 && EndsType(p, i - 1) && StartsStatement(p, close, enclosing, i - 1))
            {
                int runtime = i + 1;
                if (close[runtime] + 1 < n && p[close[runtime] + 1].Is('('))
                {
                    TemplateParameters(p, close, runtime, file, Make);
                    runtime = close[runtime] + 1;
                }

                if (Body(p, close, close[runtime] + 1) is var (start, end) && start >= 0)
                {
                    SyntaxModifiers with = deprecated ? SyntaxModifiers.Deprecated : SyntaxModifiers.None;
                    Make(i, SyntaxKind.Method, with);
                    file.Functions.Add(piece.Text);
                    if (deprecated)
                        file.Deprecated.Add(piece.Text);

                    var names = new Dictionary<string, (SyntaxKind, SyntaxModifiers)>(StringComparer.Ordinal);
                    Parameters(p, close, runtime, names, Make);
                    if (end > start)
                    {
                        if (p[start].Is('{'))
                            bodies.Add(start);
                        stack.Push(new Frame(Scope.Function, start, end, names));
                    }
                    continue;
                }
            }

            // A variable, a type (or auto, immutable, enum) and a name before an =, a ; or a comma, at the start of a statement
            if (i + 1 < n && (p[i + 1].Is(';') || (p[i + 1].Is('=') && !(i + 2 < n && (p[i + 2].Is('=') || p[i + 2].Is('>')))))
                && i > 0 && EndsType(p, i - 1) && StartsStatement(p, close, enclosing, i - 1)
                && (enclosing[i] < 0 || p[enclosing[i]].Is('{')))
            {
                SyntaxModifiers storage = Storage(p, close, i - 1);
                bool constant = (storage & SyntaxModifiers.Static) != 0 && (storage & SyntaxModifiers.Readonly) != 0;
                if (deprecated)
                    file.Deprecated.Add(piece.Text);
                SyntaxModifiers dep = deprecated ? SyntaxModifiers.Deprecated : SyntaxModifiers.None;

                if (function?.Names is { } locals)
                {
                    SyntaxKind kind = constant ? SyntaxKind.NamedConstant : SyntaxKind.Local;
                    Make(i, kind, (storage & SyntaxModifiers.Readonly) | dep);
                    locals[piece.Text] = (kind, (storage & SyntaxModifiers.Readonly) | dep);
                }
                else if (constant || (frame.Kind == Scope.Module && (storage & SyntaxModifiers.Readonly) != 0))
                {
                    Make(i, SyntaxKind.NamedConstant, SyntaxModifiers.Static | SyntaxModifiers.Readonly | dep);
                    file.Constants.Add(piece.Text);
                }
                else
                {
                    SyntaxModifiers with = storage | dep | (frame.Kind == Scope.Module ? SyntaxModifiers.Static : SyntaxModifiers.None);
                    Make(i, SyntaxKind.Field, with);
                    file.Fields[piece.Text] = with & ~SyntaxModifiers.Declaration;
                }
            }
        }

        Resolve(p, kinds, modifiers, scopes, file);

        var spans = new List<SemanticSpan>();
        var lines = text.Split('\n');
        for (int i = 0; i < n; i++)
        {
            if (kinds[i] is { } kind)
                spans.Add(new SemanticSpan(p[i].Line, p[i].Column, p[i].Text.Length, kind, modifiers[i]));
        }

        return SemanticWrites.Mark(spans, lines);
    }

    // Every name declared nowhere in sight told by what the file declares and by where it is
    private static void Resolve(List<Piece> p, SyntaxKind?[] kinds, SyntaxModifiers[] modifiers, Dictionary<string, (SyntaxKind Kind, SyntaxModifiers Modifiers)>?[] scopes, Declared file)
    {
        int n = p.Count;
        for (int i = 0; i < n; i++)
        {
            if (p[i].Kind != DToken.Name)
                continue;

            string name = p[i].Text;
            if (kinds[i] is not null)
            {
                if (file.Deprecated.Contains(name))
                    modifiers[i] |= SyntaxModifiers.Deprecated;
                continue;
            }

            if (i > 0 && p[i - 1].Is('@'))
                continue;

            bool called = i + 1 < n && (p[i + 1].Is('(') || (p[i + 1].Is('!') && i + 2 < n && !p[i + 2].Is('=')));
            SyntaxKind? kind = null;
            SyntaxModifiers with = SyntaxModifiers.None;

            if (i > 0 && p[i - 1].Is('.'))
            {
                // A member, Enum.member, a call (a UFCS one as well), a field, a constant, or a property (.length)
                if (i > 1 && p[i - 2].Kind == DToken.Name && file.Types.GetValueOrDefault(p[i - 2].Text) == SyntaxKind.Enum)
                    (kind, with) = (SyntaxKind.EnumMember, SyntaxModifiers.Static | SyntaxModifiers.Readonly);
                else if (called || file.Functions.Contains(name) && !file.Fields.ContainsKey(name))
                    kind = SyntaxKind.Method;
                else if (file.Fields.TryGetValue(name, out SyntaxModifiers field))
                    (kind, with) = (SyntaxKind.Field, field);
                else if (file.Constants.Contains(name))
                    (kind, with) = (SyntaxKind.NamedConstant, SyntaxModifiers.Static | SyntaxModifiers.Readonly);
                else if (file.Types.TryGetValue(name, out SyntaxKind type))
                    kind = type;
                else
                    kind = SyntaxKind.Property;
            }
            else if (scopes[i] is { } locals && locals.TryGetValue(name, out var local))
                (kind, with) = local;
            else if (file.Types.TryGetValue(name, out SyntaxKind type))
                kind = type;
            else if (file.TypeParameters.Contains(name))
                kind = SyntaxKind.TypeParameter;
            else if (file.Constants.Contains(name))
                (kind, with) = (SyntaxKind.NamedConstant, SyntaxModifiers.Static | SyntaxModifiers.Readonly);
            else if (file.Fields.TryGetValue(name, out SyntaxModifiers field))
                (kind, with) = (SyntaxKind.Field, field);
            else if (file.EnumMembers.Contains(name))
                (kind, with) = (SyntaxKind.EnumMember, SyntaxModifiers.Static | SyntaxModifiers.Readonly);
            else if (called || file.Functions.Contains(name))
                kind = SyntaxKind.Method;
            else if (char.IsUpper(name[0]) && (i + 1 < n && p[i + 1].Kind == DToken.Name || i > 0 && p[i - 1].IsWord("new")))
                kind = SyntaxKind.Class;

            if (kind is not { } found)
                continue;

            kinds[i] = found;
            modifiers[i] = with & ~SyntaxModifiers.Declaration;
            if (file.Deprecated.Contains(name))
                modifiers[i] |= SyntaxModifiers.Deprecated;
        }
    }

    // Where every bracket closes, and which bracket every token is inside (-1 at the top)
    private static (int[] Close, int[] Enclosing) Brackets(List<Piece> p)
    {
        int n = p.Count;
        var close = new int[n];
        var enclosing = new int[n];
        var open = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            close[i] = n - 1;
            enclosing[i] = open.Count > 0 ? open.Peek() : -1;
            if (p[i].Kind != DToken.Punctuation)
                continue;
            if (p[i].Text[0] is '(' or '[' or '{')
                open.Push(i);
            else if (p[i].Text[0] is ')' or ']' or '}' && open.Count > 0)
                close[open.Pop()] = i;
        }

        return (close, enclosing);
    }

    // Whether the token at a place can be the last of a type, a name, a basic type, a ] of an array, a * of a pointer, a
    // ) of a const(...) or a template's !(...), or a storage word that stands for one (auto, immutable, enum)
    private static bool EndsType(List<Piece> p, int at)
    {
        Piece piece = p[at];
        if (piece.Kind == DToken.Name)
            return true;
        if (piece.Kind == DToken.Keyword)
            return TypeWords.Contains(piece.Text) || piece.Text is "immutable" or "const" or "shared" or "static" or "enum" or "__gshared" or "scope" or "inout" or "ref";
        return piece.Is(']') || piece.Is('*') || piece.Is(')');
    }

    // Whether the type that ends at a place starts a statement, going back over it (names, dots, a template's !(...), the
    // brackets of an array or a const(...), storage words and attributes) to a ; a brace, a colon or the start
    private static bool StartsStatement(List<Piece> p, int[] close, int[] enclosing, int at)
    {
        int words = 0;
        for (int i = at; i >= 0; i--)
        {
            Piece piece = p[i];
            if (piece.Is(')') || piece.Is(']'))
            {
                int open = OpenOf(close, i);
                if (open < 0)
                    return false;

                // A ) is only ever the end of a type after !, const, immutable, shared, inout, typeof or a function's attribute
                if (piece.Is(')') && !(open > 0 && (p[open - 1].Is('!') || p[open - 1].Kind == DToken.Keyword && p[open - 1].Text is "const" or "immutable" or "shared" or "inout" or "typeof" or "extern" or "align" or "function" or "delegate" or "deprecated")))
                    return false;
                i = open;
                continue;
            }

            if (piece.Kind == DToken.Name || piece.Is('.') || piece.Is('!') || piece.Is('*'))
            {
                if (piece.Kind == DToken.Name && ++words > 2 && !(i > 0 && (p[i - 1].Is('.') || p[i - 1].Is('@'))))
                    return false;
                continue;
            }

            if (piece.Kind == DToken.Keyword)
            {
                if (TypeWords.Contains(piece.Text) || piece.Text is "immutable" or "const" or "shared" or "static" or "enum" or "__gshared" or "scope"
                    or "inout" or "ref" or "auto" or "private" or "public" or "protected" or "package" or "export" or "override" or "final"
                    or "abstract" or "extern" or "align" or "pure" or "nothrow" or "deprecated" or "synchronized" or "function" or "delegate")
                    continue;
                return false;
            }

            return piece.Is(';') || piece.Is('{') || piece.Is('}') || piece.Is(':') || piece.Is('@');
        }

        return true;
    }

    // The bracket a closing one closes, -1 for none
    private static int OpenOf(int[] close, int at)
    {
        for (int i = at - 1; i >= 0; i--)
        {
            if (close[i] == at)
                return i;
        }

        return -1;
    }

    // What the storage words of a declaration ending at a place say, immutable and const readonly, enum a manifest
    // constant (static and readonly), static static
    private static SyntaxModifiers Storage(List<Piece> p, int[] close, int at)
    {
        SyntaxModifiers with = SyntaxModifiers.None;
        for (int i = at; i >= 0; i--)
        {
            Piece piece = p[i];
            if (piece.Is(';') || piece.Is('{') || piece.Is('}') || piece.Is(':'))
                break;
            if (piece.Kind != DToken.Keyword)
                continue;
            with |= piece.Text switch
            {
                "immutable" or "const" => SyntaxModifiers.Readonly,
                "enum" => SyntaxModifiers.Readonly | SyntaxModifiers.Static,
                "static" or "__gshared" => SyntaxModifiers.Static,
                _ => SyntaxModifiers.None
            };
        }

        return with;
    }

    // Where the body after a declaration starts and ends, after its attributes (const, pure, @safe, an if constraint). It's
    // a brace and its closing brace, a => and the ; after it, or nothing for a ; (a prototype) with the start at the ;.
    // A start of -1 for something that isn't a declaration after all
    private static (int Start, int End) Body(List<Piece> p, int[] close, int from)
    {
        int n = p.Count;
        for (int i = from; i < n; i++)
        {
            Piece piece = p[i];
            if (piece.Is('{'))
                return (i, close[i]);
            if (piece.Is(';'))
                return (i, i);
            if (piece.Is('=') && i + 1 < n && p[i + 1].Is('>'))
            {
                for (int at = i + 2; at < n; at++)
                {
                    if (p[at].Is(';'))
                        return (i, at);
                }
                return (i, n - 1);
            }

            if (piece.Is('@'))
                continue;
            if (piece.Kind == DToken.Name && i > 0 && p[i - 1].Is('@'))
            {
                if (i + 1 < n && p[i + 1].Is('('))
                    i = close[i + 1];
                continue;
            }

            if (piece.Kind == DToken.Keyword)
            {
                if (piece.Text is "if" && i + 1 < n && p[i + 1].Is('('))
                {
                    i = close[i + 1];
                    continue;
                }
                if (piece.Text is "const" or "immutable" or "inout" or "shared" or "pure" or "nothrow" or "scope" or "return" or "ref" or "override" or "final" or "in" or "out" or "do" or "body")
                    continue;
            }

            return (-1, -1);
        }

        return (-1, -1);
    }

    // The brace an aggregate's declaration opens, past its template's parameters, its bases and its constraint, -1 for a ;
    private static int BodyAfter(List<Piece> p, int[] close, int from)
    {
        for (int i = from; i < p.Count; i++)
        {
            if (p[i].Is('{'))
                return i;
            if (p[i].Is(';'))
                return -1;
            if (p[i].Is('('))
                i = close[i];
        }

        return -1;
    }

    // The names of a template's parameters, the last name of every one of them between its brackets
    private static void TemplateParameters(List<Piece> p, int[] close, int open, Declared file, Action<int, SyntaxKind, SyntaxModifiers> make)
    {
        foreach (int name in EntryNames(p, close, open))
        {
            make(name, SyntaxKind.TypeParameter, SyntaxModifiers.None);
            file.TypeParameters.Add(p[name].Text);
        }
    }

    // The names of a function's parameters, the last name before the default of every one of them that has a type, into the
    // names the function declares
    private static void Parameters(List<Piece> p, int[] close, int open, Dictionary<string, (SyntaxKind, SyntaxModifiers)> names, Action<int, SyntaxKind, SyntaxModifiers> make)
    {
        foreach (int name in EntryNames(p, close, open))
        {
            make(name, SyntaxKind.Parameter, SyntaxModifiers.None);
            names[p[name].Text] = (SyntaxKind.Parameter, SyntaxModifiers.None);
        }
    }

    // The last name of every comma's worth between a pair of brackets, before an = when there's one. A worth of only one
    // name is a type alone (a prototype's int) unless it's all there is in a template's list
    private static List<int> EntryNames(List<Piece> p, int[] close, int open)
    {
        var found = new List<int>();
        int end = close[open], last = -1, count = 0;
        bool defaulted = false;
        for (int i = open + 1; i <= end; i++)
        {
            Piece piece = p[i];
            if (i == end || piece.Is(','))
            {
                if (last >= 0 && (count > 1 || p[last].Kind == DToken.Name && char.IsUpper(p[last].Text[0]) || TemplateLike(p, open)))
                    found.Add(last);
                (last, count, defaulted) = (-1, 0, false);
                continue;
            }

            if (piece.Is('(') || piece.Is('['))
            {
                i = close[i];
                count++;
                continue;
            }

            if (piece.Is('='))
                defaulted = true;
            if (defaulted)
                continue;
            if (piece.Kind == DToken.Name)
                last = i;
            if (piece.Kind is DToken.Name or DToken.Keyword)
                count++;
        }

        return found;
    }

    // Whether a bracket opens a template's parameters, the first of two brackets in a row or after struct Name
    private static bool TemplateLike(List<Piece> p, int open) => open > 1 && p[open - 1].Kind == DToken.Name && p[open - 2].Kind == DToken.Keyword
        && p[open - 2].Text is "struct" or "class" or "union" or "interface" or "template";
}
