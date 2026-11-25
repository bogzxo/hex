using System.Globalization;
using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>The size label a model shows: "12B-A2.5B" (total and active parameters) for a mixture of experts, else what the file says.</summary>
internal static partial class LocalModelSize
{
    [GeneratedRegex(@"(?<![\d.])(\d+(?:\.\d+)?)B-A(\d+(?:\.\d+)?)B", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TotalActive();

    // general.size_label of a mixture of experts: experts x size each, "64x1.2B"
    [GeneratedRegex(@"^\s*(\d+)\s*[xX×]\s*(\d+(?:\.\d+)?)([BM])\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ExpertsTimesSize();

    /// <summary>
    /// "12B-A2.5B" when a name, file name or the label has total and active parameters; "64×1.2B MoE" for a raw "64x1.2B" label;
    /// else the label as it is (null when there is none).
    /// </summary>
    public static string? Label(string? sizeLabel, params string?[] names)
    {
        foreach (string? name in names)
        {
            if (!string.IsNullOrEmpty(name) && TotalActive().Match(name) is { Success: true } match)
                return Format(match.Groups[1].Value, match.Groups[2].Value);
        }
        if (string.IsNullOrWhiteSpace(sizeLabel))
            return null;
        if (TotalActive().Match(sizeLabel) is { Success: true } labelled)
            return Format(labelled.Groups[1].Value, labelled.Groups[2].Value);
        if (ExpertsTimesSize().Match(sizeLabel) is { Success: true } experts)
            return $"{experts.Groups[1].Value}×{experts.Groups[2].Value}{experts.Groups[3].Value.ToUpperInvariant()} MoE";
        return sizeLabel.Trim();
    }

    private static string Format(string total, string active) =>
        string.Create(CultureInfo.InvariantCulture, $"{total}B-A{active}B");
}
