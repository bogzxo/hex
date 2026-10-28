using System.Text.RegularExpressions;

namespace Hex.Core.Notebooks;

internal static partial class NotebookMarkdown
{
    /// <summary>Notebook Markdown permits inline HTML subscripts and superscripts; retain code spans verbatim.</summary>
    public static string Normalize(string source) => InlineHtml().Replace(source, match =>
        match.Groups["index"].Success
            ? "\\(" + match.Groups["base"].Value + (match.Groups["kind"].Value.Equals("sub", StringComparison.OrdinalIgnoreCase) ? "_" : "^") + "{" + match.Groups["index"].Value + "}\\)"
            : match.Value);

    [GeneratedRegex(@"```[\s\S]*?```|~~~[\s\S]*?~~~|`[^`\n]*`|(?<em>\*?)(?<base>[A-Za-z0-9])<(?<kind>sub|sup)>(?<index>[^<>]+)</\k<kind>>\k<em>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineHtml();
}
