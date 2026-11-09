using CSharpMath.SkiaSharp;
using Hex.Core.Notebooks;
using SkiaSharp;

namespace Hex.Dawn;

internal static partial class NotebookRender
{
    // Points to a formula's size in CSS pixels, the 15px of an exported page's text
    private const float EXPORT_TEXT = 15.0f * 0.75f, EXPORT_DISPLAY = 18.0f * 0.75f, EXPORT_MARGIN = 2.0f;

    /// <summary>
    /// A formula drawn as SVG for an HTML export, its glyphs as paths so it needs no font and stays sharp at any zoom or
    /// print size. Null when CSharpMath can't read it, the export shows its TeX then. Nothing in it depends on the
    /// screen's density or the engine's render scale.
    /// </summary>
    public static NotebookHtmlMath? MathSvg(string tex, bool display)
    {
        if (tex.Length > 32_000)
            return null;
        string source = FractionAliases().Replace(CommandBoundary().Replace(tex, "$0 "), @"\frac");
        var painter = new MathPainter { LaTeX = source, FontSize = display ? EXPORT_DISPLAY : EXPORT_TEXT, TextColor = SKColors.Black };
        if (!display)
            painter.LineStyle = CSharpMath.Atom.LineStyle.Text;
        var bounds = painter.Measure();
        if (painter.ErrorMessage is not null || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        float width = bounds.Width + EXPORT_MARGIN * 2, height = bounds.Height + EXPORT_MARGIN * 2;
        using var stream = new MemoryStream();
        using (var canvas = SKSvgCanvas.Create(SKRect.Create(width, height), stream))
        {
            canvas.Translate(EXPORT_MARGIN - bounds.Left, EXPORT_MARGIN - bounds.Top);
            painter.Draw(canvas, 0, 0);
        }

        string svg = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        // The baseline is at nought, what's under it is how far the formula drops below the text round it
        float descent = MathF.Max(0.0f, bounds.Bottom) + EXPORT_MARGIN;
        return new NotebookHtmlMath(svg, width, height, descent);
    }
}
