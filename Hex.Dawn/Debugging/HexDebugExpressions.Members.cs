using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

using Horizon.UI.Scripting;

namespace Hex.Dawn;

// What a value has, for a member netcoredbg couldn't find on it. An array and a string have .NET's own members, which
// netcoredbg can't reach at all, so their Length is worked out from what they come to. Anything else has what the
// debugger lists inside it, and a member that isn't among those is missing, said with the one nearest it
internal static partial class HexDebugExpressions
{
    /// <summary>
    /// Where a chain of names starts and ends in an expression, the first names of a chain that starts an operand, null
    /// when it isn't there or what comes after it is a call.
    /// </summary>
    internal static (int Start, int End)? SpanOf(string expression, IReadOnlyList<string> names)
    {
        List<Token> tokens = Tokens(expression);
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Name || tokens[i].Text != names[0] || (i > 0 && tokens[i - 1].Text is "." or "?."))
                continue;

            int at = i, matched = 1;
            while (matched < names.Count && at + 2 < tokens.Count && tokens[at + 1].Text == "." && tokens[at + 2].Text == names[matched])
            {
                at += 2;
                matched++;
            }

            if (matched == names.Count && !(at + 1 < tokens.Count && tokens[at + 1].Text == "("))
                return (tokens[i].Start, tokens[at].Start + tokens[at].Length);
        }

        return null;
    }

    /// <summary>
    /// Where a member of what a call or an indexer comes to is in an expression, s.Trim().Length or a[0].Length, from the
    /// start of what it's a member of to the end of its name, and where the dot before it is. Null when it isn't there.
    /// </summary>
    internal static (int Start, int Dot, int End)? MemberOfCall(string expression, string member)
    {
        List<Token> tokens = Tokens(expression);
        for (int i = 2; i < tokens.Count; i++)
        {
            if (tokens[i].Text != member || tokens[i - 1].Text != "." || tokens[i - 2].Text is not (")" or "]") || (i + 1 < tokens.Count && tokens[i + 1].Text == "("))
                continue;

            // Back over the brackets and the names before them to the start of what it's a member of
            int j = i - 2, start = tokens[j].Start;
            while (j >= 0)
            {
                if (tokens[j].Text is ")" or "]")
                {
                    int depth = 0, k = j;
                    for (; k >= 0; k--)
                    {
                        if (tokens[k].Text is ")" or "]")
                            depth++;
                        else if (tokens[k].Text is "(" or "[" && --depth == 0)
                            break;
                    }

                    if (k < 0)
                        return null;
                    start = tokens[k].Start;
                    j = k - 1;
                    if (j >= 0 && (tokens[j].Kind == TokenKind.Name || tokens[j].Text is ")" or "]"))
                        continue;
                    break;
                }

                if (tokens[j].Kind is not (TokenKind.Name or TokenKind.Number or TokenKind.Text))
                    break;
                start = tokens[j].Start;
                if (j < 2 || tokens[j - 1].Text is not ("." or "?."))
                    break;
                j -= 2;
            }

            return (start, tokens[i - 1].Start, tokens[i].Start + tokens[i].Length);
        }

        return null;
    }

    // An array as netcoredbg says it, {string[3]} or {int[2, 3]}, and the empty brackets after of a jagged one
    private static readonly Regex ArraySaid = new(@"\[(?<sizes>\d+(?:\s*,\s*\d+)*)\](?:\[[,\s]*\])*\}$", RegexOptions.Compiled);

    /// <summary>
    /// How long an array or a string is by what netcoredbg says it comes to, every element of an array of more than one
    /// rank, the characters of a string with its escapes read. Null for anything else, and for a null.
    /// </summary>
    internal static long? LengthIn(DebugVariable value)
    {
        if (value.Type is { } type && type.EndsWith(']') && ArraySaid.Match(value.Value) is { Success: true } array)
        {
            long all = 1;
            foreach (string size in array.Groups["sizes"].Value.Split(','))
                all *= long.Parse(size.Trim(), CultureInfo.InvariantCulture);
            return all;
        }

        if (value.Type == "string" && value.Value.Length >= 2 && value.Value[0] == '"' && value.Value[^1] == '"')
        {
            try
            {
                return Regex.Unescape(value.Value[1..^1]).Length;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The members .NET gives an array, a string or a number of its own, by the type netcoredbg says it is, null for any
    /// other type, whose members are the debugger's to list.
    /// </summary>
    internal static IReadOnlyList<string>? BuiltInMembers(string? type)
    {
        Type? of = BuiltInType(type);
        if (of is null)
            return null;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        return of.GetProperties(flags).Select(member => member.Name)
            .Concat(of.GetFields(flags).Select(member => member.Name))
            .Concat(of.GetMethods(flags).Select(member => member.Name))
            .Distinct()
            .ToArray();
    }

    // A name from the debuggee cannot carry trimming annotations through Type.GetType.
    // Keep only the built-in types whose public member names this evaluator inspects.
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties |
        DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicMethods)]
    private static Type? BuiltInType(string? type) => type switch
    {
        null => null,
        _ when type.EndsWith(']') => typeof(Array),
        "System.Array" => typeof(Array),
        "string" or "System.String" => typeof(string),
        "int" or "System.Int32" => typeof(int),
        "uint" or "System.UInt32" => typeof(uint),
        "long" or "System.Int64" => typeof(long),
        "ulong" or "System.UInt64" => typeof(ulong),
        "short" or "System.Int16" => typeof(short),
        "ushort" or "System.UInt16" => typeof(ushort),
        "byte" or "System.Byte" => typeof(byte),
        "sbyte" or "System.SByte" => typeof(sbyte),
        "float" or "System.Single" => typeof(float),
        "double" or "System.Double" => typeof(double),
        "decimal" or "System.Decimal" => typeof(decimal),
        "bool" or "System.Boolean" => typeof(bool),
        "char" or "System.Char" => typeof(char),
        "object" or "System.Object" => typeof(object),
        _ => null
    };

    // The members asked for when the one that means how many there are is called something else
    private static readonly HashSet<string> Counting = new(StringComparer.OrdinalIgnoreCase) { "Count", "Length", "LongLength", "Size", "Len" };

    /// <summary>
    /// The member a value has that one it hasn't got most likely meant, the same name in another case, Length for a Count
    /// (and Count for a Length), or one a letter or two off. Null when nothing is near.
    /// </summary>
    internal static string? NearestMember(string member, IReadOnlyList<string> has)
    {
        if (has.FirstOrDefault(name => name.Equals(member, StringComparison.OrdinalIgnoreCase)) is { } cased)
            return cased;
        if (Counting.Contains(member) && has.FirstOrDefault(name => name is "Length" or "Count") is { } counted)
            return counted;

        string? nearest = null;
        int best = member.Length <= 4 ? 1 : 2;
        foreach (string name in has)
        {
            if (name.StartsWith('[') || name.StartsWith('<'))
                continue;
            int apart = Apart(member.ToLowerInvariant(), name.ToLowerInvariant(), best);
            if (apart <= best && (nearest is null || apart < best))
            {
                nearest = name;
                best = apart;
            }
        }

        return nearest;
    }

    // How many letters two names are apart (Levenshtein's), given up on past most
    private static int Apart(string a, string b, int most)
    {
        if (Math.Abs(a.Length - b.Length) > most)
            return most + 1;

        var row = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            row[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }

        return row[b.Length];
    }

    private static readonly Regex Qualified = new(@"\b(?:[A-Za-z_]\w*\.)+(?=[A-Za-z_])", RegexOptions.Compiled);

    /// <summary>A type as netcoredbg says it without its namespaces, Horizon.Testing.TestEntry[] as TestEntry[].</summary>
    internal static string ShortType(string type) => Qualified.Replace(type, string.Empty);
}
