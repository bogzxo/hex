using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

using Horizon.UI.Scripting;

namespace Hex.Dawn;

// What came back from netcoredbg read, a cast it can't do worked out from what was cast, an enum told apart from
// anything else, and its errors sorted into what they mean
internal static partial class HexDebugExpressions
{
    // The numeric types a cast in front is worked out for, by the keyword and by the name netcoredbg says a value's type with
    private static readonly HashSet<string> Integral = ["sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "char"];
    private static readonly HashSet<string> Floating = ["float", "double", "decimal"];

    /// <summary>
    /// A cast to a number in front of an expression, (int)x or (float)(a + b), the type and what's cast. Null for one that
    /// isn't the whole expression, (int)x + 1 among them, and for a cast to anything that isn't a number.
    /// </summary>
    internal static (string Type, string Inner)? TopCast(string expression)
    {
        List<Token> tokens = Tokens(expression);
        if (tokens.Count < 4 || tokens[0].Text != "(" || tokens[2].Text != ")" || !(Integral.Contains(tokens[1].Text) || Floating.Contains(tokens[1].Text)))
            return null;

        // What's cast has no operator at its own level, it's one operand, a sign in front allowed
        int depth = 0;
        for (int i = 3; i < tokens.Count; i++)
        {
            string text = tokens[i].Text;
            if (text is "(" or "[")
                depth++;
            else if (text is ")" or "]")
                depth--;
            else if (depth == 0 && tokens[i].Kind == TokenKind.Punctuation && text is not "." and not "?." && !(i == 3 && text is "-" or "+"))
                return null;
        }

        return (tokens[1].Text, expression[tokens[3].Start..].Trim());
    }

    /// <summary>
    /// What a value comes to cast to a numeric type the way C# casts it, a whole number wrapped round the way an unchecked
    /// cast does and a fraction cut off towards 0. Null when it can't be said for sure, with why, a value that isn't a
    /// number (an enum netcoredbg says by its name), or a fraction too big for the type, where C# leaves the result undefined.
    /// </summary>
    internal static string? Cast(DebugVariable value, string type, out string? why)
    {
        why = null;
        string from = value.Type ?? string.Empty;
        string said = value.Value.Trim();

        if (Integral.Contains(from))
        {
            // A char says its number first, 97 'a'
            string number = from == "char" ? said.Split(' ')[0] : said;
            if (!BigInteger.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger whole))
                return Refused(out why, "number");
            return CastWhole(whole, type);
        }

        if (Floating.Contains(from))
        {
            if (from == "decimal")
            {
                if (!decimal.TryParse(said, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal exact))
                    return Refused(out why, "number");
                if (type is "float" or "double" or "decimal")
                    return CastFloating((double)exact, type, exact);
                decimal cut = decimal.Truncate(exact);
                return Fits(new BigInteger(cut), type) ? CastWhole(new BigInteger(cut), type) : Refused(out why, "range");
            }

            if (!double.TryParse(said, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
                return Refused(out why, said is "NaN" or "Infinity" or "-Infinity" or "∞" or "-∞" ? "range" : "number");
            if (type is "float" or "double" or "decimal")
                return CastFloating(real, type, null);
            if (double.IsNaN(real) || double.IsInfinity(real))
                return Refused(out why, "range");
            var truncated = new BigInteger(Math.Truncate(real));
            return Fits(truncated, type) ? CastWhole(truncated, type) : Refused(out why, "range");
        }

        return Refused(out why, LooksLikeEnum(value) ? "enum" : "number");
    }

    private static string? Refused(out string? why, string reason)
    {
        why = reason;
        return null;
    }

    private static int Bits(string type) => type switch
    {
        "sbyte" or "byte" => 8,
        "short" or "ushort" or "char" => 16,
        "int" or "uint" => 32,
        _ => 64
    };

    private static bool Signed(string type) => type is "sbyte" or "short" or "int" or "long";

    private static bool Fits(BigInteger whole, string type)
    {
        int bits = Bits(type);
        return Signed(type)
            ? whole >= -(BigInteger.One << (bits - 1)) && whole < BigInteger.One << (bits - 1)
            : whole >= 0 && whole < BigInteger.One << bits;
    }

    // A whole number cast to a type, an integral one wrapped round the way an unchecked cast does
    private static string CastWhole(BigInteger whole, string type)
    {
        switch (type)
        {
            case "float":
                return ((float)whole).ToString(CultureInfo.InvariantCulture);
            case "double":
                return ((double)whole).ToString(CultureInfo.InvariantCulture);
            case "decimal":
                return ((decimal)whole).ToString(CultureInfo.InvariantCulture);
        }

        int bits = Bits(type);
        BigInteger room = BigInteger.One << bits;
        BigInteger wrapped = ((whole % room) + room) % room;
        if (Signed(type) && wrapped >= room >> 1)
            wrapped -= room;
        if (type == "char")
            return $"{wrapped} '{(char)(int)wrapped}'";
        return wrapped.ToString(CultureInfo.InvariantCulture);
    }

    private static string? CastFloating(double real, string type, decimal? exact) => type switch
    {
        "float" => ((float)real).ToString(CultureInfo.InvariantCulture),
        "double" => real.ToString(CultureInfo.InvariantCulture),
        _ => exact?.ToString(CultureInfo.InvariantCulture) ?? (Math.Abs(real) < 7.9e28 ? ((decimal)real).ToString(CultureInfo.InvariantCulture) : null)
    };

    // The types netcoredbg says that are never an enum
    private static readonly HashSet<string> NotEnums =
    [
        "string", "bool", "object", "char", "sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "float",
        "double", "decimal", "nint", "nuint"
    ];

    private static readonly Regex EnumSaid = new(@"^(?:-?\d+|[A-Za-z_]\w*(?:\s*[|,]\s*[A-Za-z_]\w*)*)$", RegexOptions.Compiled);

    /// <summary>
    /// Whether what netcoredbg says a value is looks like an enum, a name (or names put together) or a bare number with a
    /// type that isn't one of the built in ones. netcoredbg gives an enum's value its static members to open, and says a
    /// class or a struct in braces, so that's not what tells them apart.
    /// </summary>
    internal static bool LooksLikeEnum(DebugVariable value) =>
        value.Type is { Length: > 0 } type && !NotEnums.Contains(type) && !type.EndsWith(']')
        && value.Value is not ("null" or "true" or "false") && EnumSaid.IsMatch(value.Value.Trim());

    /// <summary>
    /// Whether an argument names a member of an enum itself, Easing.OutQuad, rather than a variable that holds one.
    /// netcoredbg passes an enum's member to a call as the enum's first value whatever it is, Ease.Apply(Easing.OutQuad,
    /// 0.5f) came to 0.5 where it's 0.75, while a variable holding OutQuad goes through right.
    /// </summary>
    internal static bool IsEnumMember(string argument, DebugVariable value)
    {
        if (!LooksLikeEnum(value))
            return false;

        List<Chain> chains = Chains(argument);
        if (chains.Count != 1 || chains[0].Called || chains[0].Names.Length < 2 || chains[0].Start != 0 || chains[0].End != argument.Trim().Length)
            return false;

        string[] names = chains[0].Names;
        string type = value.Type!;
        return names[^1] == value.Value.Trim() && (type == names[^2] || type.EndsWith("." + names[^2], StringComparison.Ordinal) || type.EndsWith("+" + names[^2], StringComparison.Ordinal));
    }

    /// <summary>What netcoredbg's error for an expression says went wrong.</summary>
    internal enum Failure
    {
        /// <summary>Something else, or no error at all.</summary>
        Other,

        /// <summary>"The name 'x.y' does not exist in the current context", the name in <see cref="Failed.Name"/>.</summary>
        NoName,

        /// <summary>0x80004005, a call to a method it couldn't find (a type's short name) or couldn't make.</summary>
        NoMethod,

        /// <summary>0x80070057, a method whose parameters aren't exactly the types of the arguments.</summary>
        Arguments,

        /// <summary>A kind of C# it doesn't evaluate at all (a cast, default, new), its kind in <see cref="Failed.Name"/>.</summary>
        NotDone
    }

    internal readonly record struct Failed(Failure Kind, string Name);

    private static readonly Regex NoNameSaid = new(@"The name '(?<name>[^']+)' does not exist", RegexOptions.Compiled);
    private static readonly Regex NotDoneSaid = new(@"SyntaxKindNotImplementedException: (?<kind>\w+) not implemented", RegexOptions.Compiled);

    /// <summary>What an error netcoredbg gave means.</summary>
    internal static Failed FailureOf(string? error)
    {
        if (error is null)
            return new Failed(Failure.Other, string.Empty);
        if (NoNameSaid.Match(error) is { Success: true } name)
            return new Failed(Failure.NoName, name.Groups["name"].Value);
        if (NotDoneSaid.Match(error) is { Success: true } kind)
            return new Failed(Failure.NotDone, kind.Groups["kind"].Value);
        if (error.Contains("0x80004005", StringComparison.Ordinal))
            return new Failed(Failure.NoMethod, string.Empty);
        if (error.Contains("0x80070057", StringComparison.Ordinal))
            return new Failed(Failure.Arguments, string.Empty);
        return new Failed(Failure.Other, string.Empty);
    }

    /// <summary>What a kind of C# netcoredbg doesn't do is called in Hex's words, by the key of them.</summary>
    internal static string NotDoneKey(string kind) => kind switch
    {
        "CastExpression" => "debugging.eval_kind_cast",
        "DefaultExpression" => "debugging.eval_kind_default",
        "ArrayRankSpecifier" or "ArrayCreationExpression" or "ObjectCreationExpression" or "ImplicitArrayCreationExpression" or "ImplicitObjectCreationExpression" => "debugging.eval_kind_new",
        "SimpleLambdaExpression" or "ParenthesizedLambdaExpression" => "debugging.eval_kind_lambda",
        "TypeOfExpression" => "debugging.eval_kind_typeof",
        _ => string.Empty
    };
}
