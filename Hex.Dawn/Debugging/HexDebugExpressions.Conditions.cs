namespace Hex.Dawn;

// A breakpoint's condition looked over when it's set, for what makes it no C# at all before it's ever evaluated. Brackets
// that don't pair up, a string left open, = where == was meant, an operator with nothing on one side and two values with
// nothing between them. Visual Studio says that when the condition is typed rather than at the first hit. And a condition
// (or anything) cut at its && and || to be evaluated an operand at a time, the way C# stops at the first that decides it
internal static partial class HexDebugExpressions
{
    // The operators that need something on both sides of them
    private static readonly HashSet<string> Binary = ["*", "/", "%", "&", "|", "^", "<", ">", "=", ".", ",", "?", ":"];

    // Words that go between two values, x is null, o as T, a and b in a pattern
    private static readonly HashSet<string> Between = ["is", "as", "not", "and", "or", "with", "switch", "when", "new", "typeof", "sizeof", "default", "nameof", "checked", "unchecked", "await", "out", "ref", "in", "var"];

    /// <summary>
    /// What makes a condition no C# at all, the key of Hex's words for it, null for one that looks like it'd evaluate.
    /// Only what's wrong whatever the names in it mean is said here, a name that isn't there is the first hit's to find.
    /// </summary>
    internal static string? ConditionProblem(string condition)
    {
        List<Token> tokens = Tokens(condition);
        if (tokens.Count == 0)
            return null;

        var open = new Stack<char>();
        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];
            string text = token.Text;
            if (token.Kind == TokenKind.Text && (text.Length < 2 || text[^1] != text.TrimStart('@', '$')[0]))
                return "debugging.condition_quote";

            switch (text)
            {
                case "(" or "[" or "{":
                    open.Push(text[0]);
                    break;
                case ")" or "]" or "}":
                    if (open.Count == 0 || open.Pop() != (text == ")" ? '(' : text == "]" ? '[' : '{'))
                        return "debugging.condition_brackets";
                    break;
            }

            // A lone = sets, it isn't ==, !=, <=, >= or =>
            if (text == "=" && !(i > 0 && tokens[i - 1].Start + 1 == token.Start && tokens[i - 1].Text is "=" or "!" or "<" or ">")
                && !(i + 1 < tokens.Count && tokens[i + 1].Start == token.Start + 1 && tokens[i + 1].Text is "=" or ">"))
                return "debugging.condition_assignment";

            if (i > 0 && Operand(tokens[i - 1]) && Operand(token))
                return "debugging.condition_operator_missing";
        }

        if (open.Count > 0)
            return "debugging.condition_brackets";
        if (Binary.Contains(tokens[0].Text) || Binary.Contains(tokens[^1].Text) || tokens[^1].Text is "+" or "-" or "!" or "?.")
            return "debugging.condition_incomplete";
        return null;
    }

    /// <summary>
    /// An expression cut at its &amp;&amp; and || outside any brackets, the operands of each || made of the operands of its &amp;&amp;s,
    /// for them to be evaluated the way C# would and no further. Null for one with neither, and for one with a ternary, a ??
    /// or a lambda at the top, which binds looser and isn't cut here.
    /// </summary>
    internal static List<List<string>>? LogicalParts(string expression)
    {
        List<Token> tokens = Tokens(expression);
        var ors = new List<List<string>>();
        var ands = new List<string>();
        int depth = 0, from = 0;
        bool cut = false;
        for (int i = 0; i < tokens.Count; i++)
        {
            string text = tokens[i].Text;
            bool doubled = i + 1 < tokens.Count && tokens[i + 1].Text == text && tokens[i + 1].Start == tokens[i].Start + 1;
            if (text is "(" or "[" or "{")
                depth++;
            else if (text is ")" or "]" or "}")
                depth--;
            else if (depth == 0 && (text == "?" || (text == "=" && i + 1 < tokens.Count && tokens[i + 1].Text == ">")))
                return null;
            else if (depth == 0 && text is "&" or "|" && doubled)
            {
                ands.Add(expression[from..tokens[i].Start].Trim());
                if (text == "|")
                {
                    ors.Add(ands);
                    ands = [];
                }

                from = tokens[i + 1].Start + 1;
                cut = true;
                i++;
            }
        }

        if (!cut)
            return null;
        ands.Add(expression[from..].Trim());
        ors.Add(ands);
        return ors.Any(operands => operands.Any(operand => operand.Length == 0)) ? null : ors;
    }

    // A value on its own, a name that isn't a word that goes between two, a number or a string
    private static bool Operand(Token token) =>
        token.Kind is TokenKind.Number or TokenKind.Text || (token.Kind == TokenKind.Name && !Between.Contains(token.Text));
}
