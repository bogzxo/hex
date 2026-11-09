using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

using Horizon.UI.Scripting;

namespace Hex.Dawn;

// C# expressions taken apart as far as getting them past netcoredbg needs, their names, the chains of them (a.b.c), the
// calls and their arguments, a cast in front, and what netcoredbg's errors mean. Never a parser of the whole language,
// only enough to say what a debugger can't, a string's contents and a char's are skipped over whole.
internal static partial class HexDebugExpressions
{
    internal enum TokenKind
    {
        Name,
        Number,
        Text,
        Punctuation
    }

    internal readonly record struct Token(TokenKind Kind, int Start, int Length, string Text);

    /// <summary>The tokens of an expression, a string or a char one token whatever is in it, ?. one token.</summary>
    internal static List<Token> Tokens(string expression)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < expression.Length)
        {
            char c = expression[i];
            int start = i;
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsLetter(c) || c == '_' || (c == '@' && i + 1 < expression.Length && (char.IsLetter(expression[i + 1]) || expression[i + 1] == '_')))
            {
                i++;
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_'))
                    i++;
                tokens.Add(new Token(TokenKind.Name, start, i - start, expression[start..i]));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < expression.Length && char.IsDigit(expression[i + 1])))
            {
                i++;
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_'
                    || (expression[i] == '.' && i + 1 < expression.Length && char.IsDigit(expression[i + 1]))
                    || ((expression[i] == '+' || expression[i] == '-') && (expression[i - 1] == 'e' || expression[i - 1] == 'E') && !expression[start..i].StartsWith("0x", StringComparison.OrdinalIgnoreCase))))
                    i++;
                tokens.Add(new Token(TokenKind.Number, start, i - start, expression[start..i]));
                continue;
            }

            if (c == '"' || c == '\'' || ((c == '@' || c == '$') && i + 1 < expression.Length && (expression[i + 1] == '"' || expression[i + 1] == '@' || expression[i + 1] == '$')))
            {
                bool verbatim = false;
                while (i < expression.Length && expression[i] is '@' or '$')
                {
                    verbatim |= expression[i] == '@';
                    i++;
                }

                char quote = i < expression.Length ? expression[i] : '"';
                i++;
                while (i < expression.Length)
                {
                    if (!verbatim && expression[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (expression[i] == quote)
                    {
                        if (verbatim && i + 1 < expression.Length && expression[i + 1] == quote)
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                i = Math.Min(i, expression.Length);
                tokens.Add(new Token(TokenKind.Text, start, i - start, expression[start..i]));
                continue;
            }

            int length = c == '?' && i + 1 < expression.Length && expression[i + 1] == '.' ? 2 : 1;
            tokens.Add(new Token(TokenKind.Punctuation, start, length, expression.Substring(start, length)));
            i += length;
        }

        return tokens;
    }

    // Words that are never the start of a name to look for
    private static readonly HashSet<string> Keywords =
    [
        "true", "false", "null", "this", "base", "new", "typeof", "sizeof", "default", "is", "as", "nameof", "checked",
        "unchecked", "await", "global", "string", "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte",
        "float", "double", "decimal", "bool", "char", "object"
    ];

    /// <summary>A chain of names in an expression, a.b.c, where it is, its names, and whether it's called.</summary>
    internal readonly record struct Chain(int Start, int End, string[] Names, bool Called)
    {
        public string Head => Names[0];
    }

    /// <summary>Every chain of names that starts an operand, not one that's a member of something before it.</summary>
    internal static List<Chain> Chains(string expression) => Chains(expression, Tokens(expression));

    private static List<Chain> Chains(string expression, List<Token> tokens)
    {
        var chains = new List<Chain>();
        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];
            bool member = i > 0 && tokens[i - 1].Text is "." or "?.";
            if (token.Kind != TokenKind.Name || member || Keywords.Contains(token.Text))
                continue;

            var names = new List<string> { token.Text };
            int last = i;
            while (last + 2 < tokens.Count && tokens[last + 1].Text == "." && tokens[last + 2].Kind == TokenKind.Name)
            {
                names.Add(tokens[last + 2].Text);
                last += 2;
            }

            bool called = last + 1 < tokens.Count && tokens[last + 1].Text == "(";
            chains.Add(new Chain(token.Start, tokens[last].Start + tokens[last].Length, [.. names], called));
            i = last;
        }

        return chains;
    }

    /// <summary>A call in an expression, what's called and the text of each of its arguments.</summary>
    internal readonly record struct Call(Chain Callee, IReadOnlyList<string> Arguments);

    /// <summary>Every call in an expression, the ones inside the arguments of another too.</summary>
    internal static List<Call> Calls(string expression)
    {
        List<Token> tokens = Tokens(expression);
        var calls = new List<Call>();
        foreach (Chain chain in Chains(expression, tokens))
        {
            if (!chain.Called)
                continue;

            int open = tokens.FindIndex(token => token.Start >= chain.End && token.Text == "(");
            if (open < 0)
                continue;

            var arguments = new List<string>();
            int depth = 0, from = tokens[open].Start + 1;
            for (int i = open; i < tokens.Count; i++)
            {
                string text = tokens[i].Text;
                if (text is "(" or "[" or "{")
                {
                    depth++;
                }
                else if (text is ")" or "]" or "}")
                {
                    depth--;
                    if (depth == 0)
                    {
                        string argument = expression[from..tokens[i].Start].Trim();
                        if (argument.Length > 0 || arguments.Count > 0)
                            arguments.Add(argument);
                        break;
                    }
                }
                else if (text == "," && depth == 1)
                {
                    arguments.Add(expression[from..tokens[i].Start].Trim());
                    from = tokens[i].Start + 1;
                }
            }

            calls.Add(new Call(chain, arguments));
        }

        return calls;
    }

    /// <summary>An expression with every chain that starts with a name started with something else instead.</summary>
    internal static string ReplaceHead(string expression, string head, string with)
    {
        var replaced = new StringBuilder(expression);
        List<Chain> chains = Chains(expression);
        for (int i = chains.Count - 1; i >= 0; i--)
        {
            if (chains[i].Head == head)
                replaced.Remove(chains[i].Start, head.Length).Insert(chains[i].Start, with);
        }

        return replaced.ToString();
    }

    /// <summary>An expression with one chain of names, by where it is, written another way.</summary>
    internal static string ReplaceChain(string expression, Chain chain, string with) =>
        expression[..chain.Start] + with + expression[chain.End..];

    // The keyword types netcoredbg doesn't know as types, string.Empty said "The name 'Empty' does not exist"
    private static readonly Dictionary<string, string> KeywordTypeNames = new(StringComparer.Ordinal)
    {
        ["string"] = "System.String", ["int"] = "System.Int32", ["uint"] = "System.UInt32", ["long"] = "System.Int64",
        ["ulong"] = "System.UInt64", ["short"] = "System.Int16", ["ushort"] = "System.UInt16", ["byte"] = "System.Byte",
        ["sbyte"] = "System.SByte", ["float"] = "System.Single", ["double"] = "System.Double", ["decimal"] = "System.Decimal",
        ["bool"] = "System.Boolean", ["char"] = "System.Char", ["object"] = "System.Object"
    };

    /// <summary>A keyword type with a member after it (int.MaxValue, string.Empty) written as its type's whole name.</summary>
    internal static string KeywordTypes(string expression)
    {
        List<Token> tokens = Tokens(expression);
        var written = new StringBuilder(expression);
        for (int i = tokens.Count - 2; i >= 0; i--)
        {
            bool member = i > 0 && tokens[i - 1].Text is "." or "?.";
            if (!member && tokens[i + 1].Text == "." && KeywordTypeNames.TryGetValue(tokens[i].Text, out string? type))
                written.Remove(tokens[i].Start, tokens[i].Length).Insert(tokens[i].Start, type);
        }

        return written.ToString();
    }

    /// <summary>
    /// The expression again with every whole number passed straight to a call written as a long, then a float, then a
    /// double, the order C# would convert an int to find a method. netcoredbg only takes a call whose arguments are
    /// exactly the types of its parameters, MathF.Pow(2, 3) said 0x80070057.
    /// </summary>
    internal static IEnumerable<string> WiderNumbers(string expression)
    {
        List<Token> tokens = Tokens(expression);
        var numbers = new List<Token>();
        foreach (Call call in Calls(expression))
        {
            foreach (Token token in tokens)
            {
                if (token.Kind == TokenKind.Number && token.Text.All(char.IsDigit) && call.Arguments.Any(argument => argument == token.Text || argument == "-" + token.Text))
                {
                    // Only a number that's an argument of its own, not one inside a sum
                    int before = tokens.IndexOf(token) - 1;
                    while (before >= 0 && tokens[before].Text == "-")
                        before--;
                    int after = tokens.IndexOf(token) + 1;
                    if (before >= 0 && tokens[before].Text is "(" or "," && after < tokens.Count && tokens[after].Text is ")" or "," && !numbers.Contains(token))
                        numbers.Add(token);
                }
            }
        }

        if (numbers.Count == 0)
            yield break;
        numbers.Sort((a, b) => a.Start.CompareTo(b.Start));

        foreach (string suffix in new[] { "L", "f", "d" })
        {
            var widened = new StringBuilder(expression);
            for (int i = numbers.Count - 1; i >= 0; i--)
                widened.Insert(numbers[i].Start + numbers[i].Length, suffix);
            yield return widened.ToString();
        }
    }
}
