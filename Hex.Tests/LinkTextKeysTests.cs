using System.Text.RegularExpressions;

namespace Hex.Tests;

/// <summary>Every link.* text key the Link sources of Dawn build is in en-gb, so no "@link.right_ports" shows raw.</summary>
public sealed partial class LinkTextKeysTests
{
    private static string HexRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [GeneratedRegex(@"^\s*link: \{", RegexOptions.Multiline)]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"^\s*(\w+):\s*""", RegexOptions.Multiline)]
    private static partial Regex KeyRegex();

    [GeneratedRegex(@"""(link\.[a-z0-9_.]*)""")]
    private static partial Regex LiteralRegex();

    [Fact]
    public void Every_link_text_key_the_link_sources_use_is_in_en_gb()
    {
        string text = File.ReadAllText(Path.Combine(HexRoot, "Hex.Dawn", "Assets", "hex", "localisation", "en-gb.hor")).ReplaceLineEndings("\n");
        Match start = SectionRegex().Match(text);
        Assert.True(start.Success);
        int end = text.IndexOf("\n    },", start.Index, StringComparison.Ordinal);
        var keys = new HashSet<string>(KeyRegex().Matches(text[start.Index..end]).Select(match => match.Groups[1].Value));

        string[] files = Directory.GetFiles(Path.Combine(HexRoot, "Hex.Dawn", "Link"), "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var missing = new List<string>();
        foreach (string file in files)
            foreach (Match literal in LiteralRegex().Matches(File.ReadAllText(file)))
            {
                string name = literal.Groups[1].Value[5..];
                string last = name[(name.LastIndexOf('.') + 1)..];
                // A literal ending in _ or . is the start of a key built at run time
                bool found = last.Length == 0 || (last.EndsWith('_') ? keys.Any(each => each.StartsWith(last, StringComparison.Ordinal)) : keys.Contains(last));
                if (!found)
                    missing.Add(Path.GetFileName(file) + ": " + literal.Groups[1].Value);
            }
        Assert.Empty(missing);
    }

    [GeneratedRegex(@"HexText\.Count\(\s*""([^""]*)""")]
    private static partial Regex CountRegex();

    [Fact]
    public void Every_counts_key_the_sources_build_is_in_en_gb_in_its_one_and_many_shapes()
    {
        string text = File.ReadAllText(Path.Combine(HexRoot, "Hex.Dawn", "Assets", "hex", "localisation", "en-gb.hor")).ReplaceLineEndings("\n");
        int start = text.IndexOf("\n    counts: {", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = text.IndexOf("\n    },", start, StringComparison.Ordinal);
        var keys = new HashSet<string>(KeyRegex().Matches(text[start..end]).Select(match => match.Groups[1].Value));

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string root in new[] { "Hex.Dawn", "Hex.Dusk", "Hex.Shell", "Hex.Core" })
            foreach (string file in Directory.GetFiles(Path.Combine(HexRoot, root), "*.cs", SearchOption.AllDirectories))
                foreach (Match call in CountRegex().Matches(File.ReadAllText(file)))
                    foreach (string shape in new[] { "_one", "_many" })
                        if (!keys.Contains(call.Groups[1].Value + shape))
                            missing.Add("counts." + call.Groups[1].Value + shape);
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_right_the_link_names_has_a_text()
    {
        string text = File.ReadAllText(Path.Combine(HexRoot, "Hex.Dawn", "Assets", "hex", "localisation", "en-gb.hor"));
        foreach (string name in Hex.Core.HexLinkTrust.RightsNames((Hex.Core.HexLinkRights)~0))
            Assert.Contains("right_" + name + ":", text);
    }
}
