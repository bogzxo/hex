using System.Text.RegularExpressions;

namespace Hex.Dawn;

/// <summary>
/// What the names in a file of C# could mean, read off its text, for a debugger that only knows a type by its whole name.
/// The namespaces it's in and the ones its usings bring in (the project's global usings too), the types of its using
/// statics, its aliases and the constants it declares. netcoredbg honours none of a file's usings, Ease.Apply said "does
/// not exist" in a file with using Horizon.Core.Tweening at the top, and it can't see a const local at all.
/// </summary>
internal sealed record HexDebugScope(IReadOnlyList<string> Namespaces, IReadOnlyList<string> StaticTypes,
    IReadOnlyDictionary<string, string> Aliases, IReadOnlyDictionary<string, HexDebugConstant> Constants)
{
    /// <summary>Nothing known, every name as it was written.</summary>
    public static readonly HexDebugScope Empty = new([], [], new Dictionary<string, string>(), new Dictionary<string, HexDebugConstant>());

    /// <summary>What an SDK project's implicit usings are when its generated file of them can't be found.</summary>
    public static readonly string[] IMPLICIT = ["System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks"];

    private static readonly Regex UsingSaid = new(@"^\s*(?:global\s+)?using\s+(?<static>static\s+)?(?:global::)?(?<name>[\w.]+)\s*;", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex AliasSaid = new(@"^\s*(?:global\s+)?using\s+(?<alias>\w+)\s*=\s*(?:global::)?(?<name>[\w.]+)\s*;", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex NamespaceSaid = new(@"^\s*namespace\s+(?<name>[\w.]+)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ConstantSaid = new(@"\bconst\s+(?<type>[\w.]+\??)\s+(?<declared>\w+\s*=[^;{}]+);", RegexOptions.Compiled);
    private static readonly Regex DeclaredSaid = new(@"^\s*(?<name>\w+)\s*=\s*(?<value>.+?)\s*$", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// A file's scope from its text, with the usings the project has for every file (its global usings) after its own.
    /// The namespaces go innermost first, the one a type is looked for in first the way C# does.
    /// </summary>
    public static HexDebugScope Of(string source, IEnumerable<string> globalUsings)
    {
        var namespaces = new List<string>();
        foreach (Match declared in NamespaceSaid.Matches(source))
        {
            // Horizon.Testing.Examples is in Horizon.Testing and Horizon as well
            string name = declared.Groups["name"].Value;
            for (; name.Length > 0; name = name.Contains('.') ? name[..name.LastIndexOf('.')] : string.Empty)
                Add(namespaces, name);
        }
        namespaces.Sort((a, b) => b.Count(c => c == '.').CompareTo(a.Count(c => c == '.')));

        var statics = new List<string>();
        foreach (Match used in UsingSaid.Matches(source))
            Add(used.Groups["static"].Success ? statics : namespaces, used.Groups["name"].Value);
        foreach (string used in globalUsings)
            Add(namespaces, used);

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match alias in AliasSaid.Matches(source))
            aliases.TryAdd(alias.Groups["alias"].Value, alias.Groups["name"].Value);

        var constants = new Dictionary<string, HexDebugConstant>(StringComparer.Ordinal);
        // const float back = 1.70158f, backInOut = back * 1.525f declares two, cut at the commas outside brackets and strings
        foreach (Match constant in ConstantSaid.Matches(source))
        {
            foreach (string declared in Declarators(constant.Groups["declared"].Value))
            {
                if (DeclaredSaid.Match(declared) is { Success: true } one)
                    constants.TryAdd(one.Groups["name"].Value, new HexDebugConstant(constant.Groups["type"].Value, one.Groups["value"].Value.Trim()));
            }
        }

        return new HexDebugScope(namespaces, statics, aliases, constants);
    }

    // The declarators of a declaration, cut at its commas that aren't in brackets or a string
    private static List<string> Declarators(string declared)
    {
        var found = new List<string>();
        int depth = 0, from = 0;
        foreach (HexDebugExpressions.Token token in HexDebugExpressions.Tokens(declared))
        {
            if (token.Text is "(" or "[" or "{")
                depth++;
            else if (token.Text is ")" or "]" or "}")
                depth--;
            else if (token.Text == "," && depth == 0)
            {
                found.Add(declared[from..token.Start]);
                from = token.Start + 1;
            }
        }

        found.Add(declared[from..]);
        return found;
    }

    private static void Add(List<string> to, string name)
    {
        if (name.Length > 0 && !to.Contains(name))
            to.Add(name);
    }

    private static readonly Regex GlobalUsingSaid = new(@"^\s*global\s+using\s+(?!static\b)(?:global::)?(?<name>[\w.]+)\s*;", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The namespaces a project's generated file of global usings (obj/.../X.GlobalUsings.g.cs) brings in.</summary>
    public static IReadOnlyList<string> GlobalUsingsIn(string generated) =>
        [.. GlobalUsingSaid.Matches(generated).Select(used => used.Groups["name"].Value).Distinct()];
}

/// <summary>A const a file declares, its type as written and what it's set to.</summary>
internal readonly record struct HexDebugConstant(string Type, string Value)
{
    /// <summary>
    /// What it's set to as an expression of its own type, a number given the suffix of a float, a double, a long or a
    /// decimal, which a debugger reading 2 for a const float 2 would take for an int. Anything else goes in brackets as it
    /// was written, the names in it worked out in turn when the debugger can't find them. Null for nothing at all.
    /// </summary>
    public string? Literal
    {
        get
        {
            if (Value.Length == 0)
                return null;
            if (Value[0] is '"' or '\'' or '@' or '$' || Value is "true" or "false" or "null")
                return Value;
            if (!Regex.IsMatch(Value, @"^-?\s*(?:\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?|0[xXbB][\da-fA-F_]+)[fFdDmMuUlL]*$"))
                return $"({Value})";

            bool suffixed = char.IsLetter(Value[^1]) && !Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (suffixed)
                return Value;
            return Type switch
            {
                "float" or "System.Single" or "Single" => Value + "f",
                "double" or "System.Double" or "Double" => Value + "d",
                "decimal" or "System.Decimal" or "Decimal" => Value + "m",
                "long" or "System.Int64" or "Int64" => Value + "L",
                "uint" or "System.UInt32" or "UInt32" => Value + "u",
                "ulong" or "System.UInt64" or "UInt64" => Value + "ul",
                _ => Value
            };
        }
    }
}
