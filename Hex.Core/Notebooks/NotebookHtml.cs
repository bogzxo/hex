using System.Net;
using System.Text.RegularExpressions;

namespace Hex.Core.Notebooks;

/// <summary>
/// HTML an output came in (IPython's HTML, a class's _repr_html_) written as markdown, which Hex draws. Headings, bold,
/// italics, code, links, lists and paragraphs come through, styles and scripts are dropped, the rest is its text. It's
/// what the HTML says rather than how it looks, but it beat showing "&lt;IPython.core.display.HTML object&gt;".
/// </summary>
internal static partial class NotebookHtml
{
    public static string ToMarkdown(string html)
    {
        string text = DroppedPattern().Replace(html, string.Empty);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        text = FencePattern().Replace(text, match => $"\n```\n{WebUtility.HtmlDecode(TagPattern().Replace(match.Groups[1].Value, string.Empty)).Trim('\n')}\n```\n");
        text = HeadingPattern().Replace(text, match => $"\n\n{new string('#', match.Groups[1].Value[0] - '0')} ");
        text = LinkPattern().Replace(text, "[$2]($1)");
        text = BoldPattern().Replace(text, "**");
        text = ItalicPattern().Replace(text, "*");
        text = CodePattern().Replace(text, "`");
        text = ItemPattern().Replace(text, "\n- ");
        text = BreakPattern().Replace(text, "\n");
        text = BlockPattern().Replace(text, "\n\n");
        text = WebUtility.HtmlDecode(TagPattern().Replace(text, string.Empty)).Replace(' ', ' ');
        return GapPattern().Replace(text, "\n\n").Trim();
    }

    /// <summary>Whether a result's text is only Python's name for the object (&lt;IPython.core.display.HTML object&gt;), which its other forms say better.</summary>
    public static bool IsBareRepr(string plain)
    {
        string said = plain.Trim();
        return said.StartsWith('<') && said.EndsWith('>') && said.Contains(" object", StringComparison.Ordinal) && !said.Contains('\n');
    }

    [GeneratedRegex(@"<(style|script|head)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DroppedPattern();

    [GeneratedRegex(@"<pre[^>]*>(.*?)</pre>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"<h([1-6])[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"<a\s[^>]*href\s*=\s*[""']([^""']*)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"</?(b|strong)(\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex BoldPattern();

    [GeneratedRegex(@"</?(i|em)(\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex ItalicPattern();

    [GeneratedRegex(@"</?(code|tt|kbd)(\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"<li(\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex ItemPattern();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakPattern();

    [GeneratedRegex(@"</?(p|div|h[1-6]|ul|ol|table|tr|blockquote|section)(\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockPattern();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\n[ \t]*\n(\s*\n)+")]
    private static partial Regex GapPattern();
}
