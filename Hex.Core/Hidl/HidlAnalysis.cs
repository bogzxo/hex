using System.Text;

using Horizon.Content;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;

namespace Hex.Core;

/// <summary>What a name is documented as and where, "greetings.hor line 9", the where null for the file itself.</summary>
internal readonly record struct HidlDocumented(string Doc, string? Where);

/// <summary>
/// What Hex knows about a file of HIDL from its text alone, worked out the same way for the editor and for the language
/// server (HidlServer): what can be written at a place, what a word is when the pointer rests on it, where a name is
/// declared and what the parser didn't like. Places are lines and columns from zero. Whatever needs more than the text
/// (the names of the rest of the workspace, what a run documented) is handed in.
/// </summary>
internal static class HidlAnalysis
{
    /// <summary>The words the language keeps to itself, offered first when nothing more particular is.</summary>
    public static readonly string[] Keywords = ["let", "const", "func", "if", "else", "while", "do", "for", "in", "return", "break", "continue", "delete", "true", "false", "null", "vec", "include", "compositor"];

    /// <summary>
    /// The kinds of components made known to scripts, which a module does as it's made. A program with a window has made
    /// one already, the language server hasn't, so it makes one for nothing else (no window and no device are needed).
    /// </summary>
    public static void KnowKinds()
    {
        // The engine's own, another assembly's (the terminal's) may be known already without them
        if (UIModule.Kinds.Contains("label"))
            return;

        _ = new UIModule(new UICompositor(new Horizon.Engine.Camera2D(new System.Numerics.Vector2(1600, 900)), string.Empty, string.Empty));
    }

    /// <summary>A line of a text, empty past its end.</summary>
    public static string LineOf(string text, int line)
    {
        int start = 0;
        for (int at = 0; at < line; at++)
        {
            int next = text.IndexOf('\n', start);
            if (next < 0)
                return string.Empty;
            start = next + 1;
        }

        int end = text.IndexOf('\n', start);
        return (end < 0 ? text[start..] : text[start..end]).TrimEnd('\r');
    }

    /// <summary>Where a line and a column are in a text, as an offset into it.</summary>
    public static int OffsetOf(string text, int line, int column)
    {
        int start = 0;
        for (int at = 0; at < line; at++)
        {
            int next = text.IndexOf('\n', start);
            if (next < 0)
                return text.Length;
            start = next + 1;
        }

        return Math.Min(text.Length, start + column);
    }

    /// <summary>The word a column of a line is in or just after, as where it starts and ends.</summary>
    public static (int Start, int End) WordAt(string line, int column)
    {
        column = Math.Clamp(column, 0, line.Length);
        int start = column, end = column;
        while (start > 0 && UITextDocument.IsWordChar(line[start - 1])) start--;
        while (end < line.Length && UITextDocument.IsWordChar(line[end])) end++;
        return (start, end);
    }

    /* Completion */

    /// <summary>
    /// What there is to write at a place. A key of the game's language inside a text that starts with an @, one of the
    /// words a property takes inside its text (an anchor, a fill), a kind of component after compositor., the properties
    /// of the component being made inside its braces, and otherwise the language's words, the names of the file and the
    /// names of the workspace (<paramref name="workspace"/>, nothing while they aren't known). Null for nothing to offer.
    /// </summary>
    public static UICompletionRequest? Complete(string text, int line, int column, IEnumerable<string>? workspace = null)
    {
        string current = LineOf(text, line);
        string before = current[..Math.Clamp(column, 0, current.Length)];

        // Inside a text, the quotes before the place on its line odd in number
        int quote = before.LastIndexOf('"');
        if (quote >= 0 && before.Count(character => character == '"') % 2 == 1)
        {
            string inside = before[(quote + 1)..];

            if (inside.StartsWith('@'))
            {
                var keys = Localisation.Keys.Order(StringComparer.Ordinal)
                    .Select(key => new UICompletion(key, HexEvaluation.Shorten(Localisation.Get(key), 40), Kind: "string"))
                    .ToList();
                return new UICompletionRequest(quote + 2, keys);
            }

            // The words of a property that is one of a few, after its name and a colon
            string head = before[..quote].TrimEnd();
            if (head.EndsWith(':'))
            {
                string property = TrailingWord(head[..^1].TrimEnd());
                if (HidlComponents.Choices.TryGetValue(property, out string[]? words))
                    return new UICompletionRequest(quote + 1, [.. words.Select(word => new UICompletion(word, property, Kind: "value"))]);
            }

            return null;
        }

        string word = TrailingWord(before);
        int start = before.Length - word.Length;
        string ahead = before[..start];

        // A kind of component, after compositor.
        if (ahead.EndsWith("compositor.", StringComparison.Ordinal))
            return new UICompletionRequest(start, [.. UIModule.Kinds.Order(StringComparer.Ordinal).Select(kind => new UICompletion(kind, HexEvaluation.Shorten(HidlComponents.Describe(kind), 40), kind + "({ ", Kind: "class"))]);

        // A property of the component being made, where a key goes inside its braces
        if (ComponentBeingMade(text, OffsetOf(text, line, start)) is { } kind && UIModule.CreateDetached(kind) is { } made)
        {
            string trimmed = ahead.TrimEnd();
            if (trimmed.Length == 0 || trimmed.EndsWith('{') || trimmed.EndsWith(','))
            {
                var properties = made.Object.Properties.Keys
                    .Where(name => name != "parent")
                    .Prepend("parent")
                    .Select(name => new UICompletion(name, HexEvaluation.Shorten(UIPropertyHelp.Describe(kind, name), 48), name + ": ", Kind: "property"))
                    .ToList();
                return new UICompletionRequest(start, properties);
            }
        }

        var items = new List<UICompletion>();
        var seen = new HashSet<string>();
        foreach (string keyword in Keywords)
        {
            if (seen.Add(keyword)) items.Add(new UICompletion(keyword, "keyword", Kind: "keyword"));
        }

        foreach (Token token in Lexer.Tokenize(text, lenient: true))
        {
            if (token.Type == TokenType.Identifier && token.Value.Length > 1 && seen.Add(token.Value))
                items.Add(new UICompletion(token.Value, "in this file", Kind: "variable"));
        }

        if (workspace is not null)
        {
            foreach (string known in workspace.Order(StringComparer.Ordinal))
            {
                if (seen.Add(known))
                    items.Add(new UICompletion(known, "in the workspace", Kind: "variable"));
            }
        }

        return new UICompletionRequest(start, items);
    }

    private static string TrailingWord(string text)
    {
        int start = text.Length;
        while (start > 0 && UITextDocument.IsWordChar(text[start - 1])) start--;
        return text[start..];
    }

    /// <summary>
    /// Which kind of component the code is in the middle of making at an offset, the innermost compositor.kind({ that
    /// isn't closed yet, null for none.
    /// </summary>
    public static string? ComponentBeingMade(string text, int offset)
    {
        Token[] tokens = Lexer.Tokenize(text[..Math.Clamp(offset, 0, text.Length)], lenient: true);
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

    /* Declarations */

    /// <summary>
    /// Where a name is declared in some code, by its tokens, null for nowhere. A let or a const (a name taken out of
    /// something, <c>let { a, b } = thing</c>, counts) or a func name. Given where the name is used (zero based), a
    /// parameter of the function round it or a name of the for round it comes first, the nearest one, since that's the
    /// one it means.
    /// </summary>
    public static (int Line, int Column)? FindDeclaration(string source, string name, int line = -1, int column = -1)
    {
        Token[] tokens = Lexer.Tokenize(source, lenient: true);

        if (line >= 0 && HidlLocals.Nearest(tokens, name, line + 1, column + 1) is { } local)
            return (local.Line - 1, local.Col - 1);

        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Type == TokenType.Function && tokens[i + 1] is { Type: TokenType.Identifier } function && function.Value == name)
                return (function.Line - 1, function.Col - 1);

            if (tokens[i].Type is not (TokenType.Let or TokenType.Const))
                continue;

            foreach (Token declared in HIDLDeclarations.Declared(tokens, i))
            {
                if (declared.Value == name)
                    return (declared.Line - 1, declared.Col - 1);
            }
        }

        return null;
    }

    /* Hovers */

    /// <summary>
    /// What the word at a place is, as markdown with the icons Hex draws ($(name)), null for nothing worth saying. What
    /// it is is worked out from what is round it the way the highlighter does it. <paramref name="docOf"/> is what knows
    /// the documentation of names the text itself doesn't declare (a run's, the workspace's).
    /// </summary>
    public static string? Hover(string text, int line, int column, Func<string, HidlDocumented?>? docOf = null)
    {
        string current = LineOf(text, line);
        var (start, end) = WordAt(current, column);
        if (end <= start)
            return null;

        string word = current[start..end];
        string before = current[..start].TrimEnd();
        string after = current[end..].TrimStart();

        // A key of the game's language, the whole of the text "@main_menu.quit"
        int quote = current.LastIndexOf('"', Math.Max(0, start - 1));
        if (quote >= 0 && quote + 1 < current.Length && current[quote + 1] == '@')
        {
            int close = current.IndexOf('"', quote + 1);
            string key = current[(quote + 2)..(close > quote ? close : current.Length)];
            return KeyHint(key);
        }

        // The kind of a component, compositor.button
        if (before.EndsWith("compositor.", StringComparison.Ordinal))
        {
            string described = HidlComponents.Describe(word);
            return described.Length > 0 ? $"$({HidlComponents.KindIcon(word)}) **{word}**, a kind of component\n\n{described}" : $"**{word}**, no kind of component Hex knows";
        }

        // A property of the component being made, padding: in compositor.label({ ... })
        if (after.StartsWith(':') && ComponentBeingMade(text, OffsetOf(text, line, column)) is { } kind)
        {
            string help = UIPropertyHelp.Describe(kind, word);
            return help.Length > 0 ? $"$(symbol-property) **{word}** of a {kind}\n\n{help}" : null;
        }

        // The language's own
        if (word == "vec")
            return "$(symbol-method) **vec**(x, y, z, w)\n\nA vector of two to four numbers. A position, a size, or a colour as red, green, blue and how solid it is, each 0 to 1.";
        if (HidlHighlighter.IsBuiltin(word))
            return LibraryHint(word);
        if (word is "let" or "const" or "func" or "if" or "else" or "while" or "for" or "in" or "return" or "break" or "continue" or "do" or "delete")
            return KeywordHint(word);

        // A directive and what it defines
        if (current.TrimStart().StartsWith('#'))
            return word == "layout"
                ? "$(layout) **#define layout**\n\nThe file is a layout, Hex opens it with its designer, which writes the whole file anew every time it is saved from there."
                : $"$(symbol-constant) **{word}**, a directive. Read before the rest of the file, `defined(\"name\")` asks after a name.";

        // A name made in this file, with what it is documented as wherever that is
        HidlDocumented? doc = docOf?.Invoke(word) ?? TextDoc(text, word);
        if (FindDeclaration(text, word, line, column) is { } declared)
        {
            string declaration = LineOf(text, declared.Line).Trim();
            return $"$(symbol-variable) **{word}**, made on line {declared.Line + 1}\n\n```\n{HexEvaluation.Shorten(declaration, 120)}\n```{DocHint(doc)}";
        }

        // Made somewhere else, an included file or anywhere in the workspace, or a key of an object, and documented there
        if (doc is { } found)
        {
            string icon = after.StartsWith(':') || before.EndsWith('.') ? "symbol-field" : "symbol-variable";
            return $"$({icon}) **{word}**\n\n{UIRichText.Literal(found.Doc)}{(found.Where is { } where ? $"\n\n*{where}*" : string.Empty)}";
        }

        return null;
    }

    /// <summary>What a text documents a name as with a // @doc comment, null for nothing.</summary>
    public static HidlDocumented? TextDoc(string text, string name)
    {
        foreach (HidlDoc doc in HIDLDocs.Read(Lexer.Tokenize(text, lenient: true)))
        {
            if (doc.Name == name)
                return new HidlDocumented(doc.Doc, null);
        }

        return null;
    }

    /// <summary>A name's documentation as the end of a hint, nothing if it has none.</summary>
    private static string DocHint(HidlDocumented? doc) => doc is { } documented
        ? $"\n\n{UIRichText.Literal(documented.Doc)}{(documented.Where is { } where ? $"\n\n*{where}*" : string.Empty)}"
        : string.Empty;

    /// <summary>What a key of the game's language says in every language there is.</summary>
    private static string KeyHint(string key)
    {
        if (Localisation.Languages.Count == 0)
            return $"$(globe) **@{key}**\n\nNo languages are loaded, a layout near a localisation folder has its game's.";

        var text = new StringBuilder($"$(globe) **@{key}**\n");
        foreach (string language in Localisation.Languages)
        {
            string? said = Localisation.In(language, key);
            text.Append("\n- **").Append(language).Append("**  ").Append(said is null ? "*hasn't got it*" : UIRichText.Plain(said));
        }

        return text.ToString();
    }

    private static string LibraryHint(string word) => word switch
    {
        "print" => "$(symbol-method) **print**(value, ...)\n\nWrites what it is given to the output, F5 shows it.",
        "include" => "$(symbol-method) **include**(\"file.hor\")\n\nRuns another file, looked for next to this one, and comes to what that declares.",
        "keys" => "$(symbol-method) **keys**(object)\n\nThe keys of an object, as a list.",
        "values" => "$(symbol-method) **values**(object)\n\nThe values of an object, as a list.",
        "entries" => "$(symbol-method) **entries**(object)\n\nThe keys of an object and what each holds, as a list of `[key, value]`. `for (key, value in object)` walks the same.",
        "has" => "$(symbol-method) **has**(object or list, key or value)\n\nWhether it has it, the same as `key in object`.",
        "range" => "$(symbol-method) **range**(to) or **range**(from, to)\n\nThe numbers from, up to but not with to, as a list.",
        "len" => "$(symbol-method) **len**(text or list or object)\n\nHow long it is.",
        "text" => "$(symbol-method) **text**(value)\n\nThe value written as a text.",
        "number" => "$(symbol-method) **number**(text)\n\nThe number a text is.",
        "format" => "$(symbol-method) **format**(\"{0} of {1}\", a, b)\n\nA text with the values put in where it says, `{name}` a key of an object when that's the one value. `format(number, digits)` is the number with so many digits after the point.",
        "typeof" => "$(symbol-method) **typeof**(value)\n\nWhat kind of value it is, number, text, list, object.",
        "error" => "$(symbol-method) **error**(\"why\")\n\nStops the script and says why.",
        "defined" => "$(symbol-method) **defined**(\"name\")\n\nWhether a directive (`#define name`) or the program defined it.",
        "definition" => "$(symbol-method) **definition**(\"name\")\n\nWhat a name was defined as, null if it wasn't.",
        "math" => "$(symbol-namespace) **math**\n\nThe library's maths, `math.sin`, `math.floor`, `math.pi` and the rest.",
        "compositor" => "$(symbol-namespace) **compositor**\n\nWhat a layout makes its components with, `compositor.label({ ... })`.",
        _ => $"$(symbol-method) **{word}**, part of the language"
    };

    private static string KeywordHint(string word) => word switch
    {
        "let" => "$(symbol-keyword) **let** name = value;\n\nMakes a name that can be set again. `let { a, b } = thing;` makes a name for each key, `let [a, b] = list;` for each item.",
        "const" => "$(symbol-keyword) **const** name = value;\n\nMakes a name that can't be set again.",
        "func" => "$(symbol-keyword) **func** name(a, b = 2) { ... }\n\nMakes a function, `b` is 2 when it isn't handed over. `(a, b) => a + b` is one without a name.",
        "for" => "$(symbol-keyword) **for** (item in list) { ... }\n\nGoes through every item of a list. `for (key, value in object)` and `for (index, item in list)` have the place too.",
        "in" => "$(symbol-keyword) **in**\n\n`key in object`, `item in list`, `part in text`, whether it has it. And the in of a for.",
        "delete" => "$(symbol-keyword) **delete** name;\n\nTakes a name away, or a key out of an object (`delete thing.key`) or an item out of a list (`delete list[2]`).",
        "if" => "$(symbol-keyword) **if** (test) { ... } else { ... }\n\nOne statement needs no braces, `if (done) return;`.",
        _ => $"$(symbol-keyword) **{word}**, a word the language keeps to itself"
    };

    /* What's wrong with it */

    /// <summary>What the parser didn't like in a text, every mistake and not only the first, as errors.</summary>
    public static List<UICodeDiagnostic> SyntaxErrors(string text)
    {
        var found = new List<UICodeDiagnostic>();
        var errors = new List<ParseException>();
        try
        {
            new Parser().ProduceSyntaxTree(Lexer.Tokenize(text), errors);
        }
        catch (ParseException e)
        {
            errors.Add(e);
        }
        catch (Exception e)
        {
            found.Add(new UICodeDiagnostic(0, 0, 1, e.Message));
        }

        foreach (ParseException e in errors)
            found.Add(new UICodeDiagnostic(Math.Max(0, e.Line - 1), Math.Max(0, e.Col - 1), Math.Max(0, e.Length), e.Message));

        return found;
    }

    /// <summary>Whether a file is a layout (#define layout), which only a program with a compositor can run.</summary>
    public static bool IsLayout(string text) => Horizon.HIDL.Preprocessing.HIDLPreprocessor.Read(text).ContainsKey("layout");

    /// <summary>
    /// Everything wrong with a file, what the parser didn't like, and when it parses (and isn't a layout) where running it
    /// went wrong or the lints of what it came to, as the editor underlines them. Runs the file, so off the editor's
    /// thread. <paramref name="open"/> is the text of files that are open and maybe not saved, by their full path, which
    /// an include reads before the disk.
    /// </summary>
    public static List<UICodeDiagnostic> Check(string text, string? path, IReadOnlyDictionary<string, string> open, HexSaid said)
    {
        List<UICodeDiagnostic> found = SyntaxErrors(text);
        if (found.Count > 0 || IsLayout(text))
            return found;

        string? file = path is null ? null : Path.GetFullPath(path);
        HexValues ran = HexEvaluation.RunNow(text, file, file is null ? null : Path.GetDirectoryName(file), open);
        if (ran.Error is null)
            return HexLints.Lint(ran, file, said);

        if (ran.ErrorAt.Span.IsKnown && (ran.ErrorAt.File is null || (file is not null && string.Equals(Path.GetFullPath(ran.ErrorAt.File), file, PlatformPaths.Comparison))))
            found.Add(new UICodeDiagnostic(ran.ErrorAt.Span.Line - 1, ran.ErrorAt.Span.Column - 1, Math.Max(0, ran.ErrorAt.Span.Length), ran.Error + HexFixes.LikelyFix(ran.Error, text)));
        else
            found.Add(new UICodeDiagnostic(0, 0, 1, ran.Error + HexFixes.LikelyFix(ran.Error, text)));

        return found;
    }
}
