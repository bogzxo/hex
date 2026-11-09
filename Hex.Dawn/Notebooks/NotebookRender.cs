using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using CSharpMath.SkiaSharp;
using Horizon.Graphics;
using SkiaSharp;
using Svg.Skia;

namespace Hex.Dawn;

/// <summary>CPU rendering of static notebook graphics. GPU uploads remain owned by Dawn's output lifecycle.</summary>
/// <summary>A picture rendered, and how big it is in the layout's units, which is its pixels over the density it was rendered at.</summary>
internal readonly record struct NotebookPixels(ImagePixels Pixels, Vector2 Natural);

internal static partial class NotebookRender
{
    /// <summary>
    /// The fewest and the most pixels a unit of the layout is rendered at. A picture is rendered at the density it's
    /// shown at, a pixel of it to a pixel of the screen. Rendered at twice that and halved by the GPU, a stroke a pixel
    /// wide came out two pixels at half strength and the lines and the formulas went grey.
    /// </summary>
    public const float LeastDensity = 1, MostDensity = 4;

    public const int MaxSource = 4_000_000;
    private const int MaxSide = 4096;
    private const int MaxPixels = 8_000_000;

    /// <param name="density">How many pixels a unit of the layout is, see <see cref="LeastDensity"/>.</param>
    public static NotebookPixels Svg(string source, float density)
    {
        if (source.Length > MaxSource)
            throw new ArgumentException("SVG output exceeds the rendering limit.");
        using var reader = XmlReader.Create(new StringReader(source), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaxSource
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "svg")
            throw new ArgumentException("Output is not an SVG document.");

        // Only embedded resources and document fragments may resolve. Never fetch a notebook's file or URL references.
        foreach (XElement element in document.Descendants().ToList())
        {
            if (element.Name.LocalName is "script" or "foreignObject" || element.Name.LocalName == "style" && UnsafeCss(element.Value))
            {
                element.Remove();
                continue;
            }
            foreach (XAttribute attribute in element.Attributes().ToList())
            {
                string name = attribute.Name.LocalName;
                string value = attribute.Value.Trim();
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || name is "href" or "src" && !value.StartsWith('#') && !EmbeddedRaster(value)
                    || UnsafeCss(value))
                    attribute.Remove();
            }
        }
        // Processing instructions can reference remote stylesheets independently of element attributes.
        foreach (var instruction in document.DescendantNodes().OfType<XProcessingInstruction>().ToList())
            instruction.Remove();
        using var svg = new SKSvg();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)));
        SKPicture picture = svg.Load(stream) ?? throw new ArgumentException("SVG could not be rendered.");
        SKRect bounds = picture.CullRect;
        if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentException("SVG has no finite drawing area.");
        float scale = MathF.Min(density, MathF.Min(MaxSide / bounds.Width, MaxSide / bounds.Height));
        scale = MathF.Min(scale, MathF.Sqrt(MaxPixels / (bounds.Width * bounds.Height)));
        ImagePixels pixels = Paint(bounds.Width * scale, bounds.Height * scale, canvas =>
        {
            canvas.Scale(scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        });

        // As big as the SVG says, a picture too big for the most pixels allowed is rendered at less and blown up
        return new NotebookPixels(pixels, new Vector2(pixels.Width, pixels.Height) / scale);
    }

    private static bool EmbeddedRaster(string value) => new[] { "data:image/png;base64,", "data:image/jpeg;base64,", "data:image/gif;base64," }
        .Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool UnsafeCss(string value)
    {
        // CSS escapes and imports can disguise external URLs. Ordinary inline styles and url(#id) stay supported.
        if (value.Contains("@import", StringComparison.OrdinalIgnoreCase) || value.Contains('\\'))
            return true;
        int start = 0;
        while ((start = value.IndexOf("url(", start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = value.IndexOf(')', start + 4);
            if (end < 0 || !value[(start + 4)..end].Trim().Trim('\'', '"').StartsWith('#'))
                return true;
            start = end + 1;
        }
        return false;
    }

    /// <param name="density">How many pixels a unit of the layout is, see <see cref="LeastDensity"/>.</param>
    public static NotebookPixels Math(string source, float density, bool prose = false, bool transparent = false, float width = 600, float zoom = 1)
    {
        if (source.Length > 32_000)
            throw new ArgumentException("Math output exceeds the rendering limit.");
        // CSharpMath's command lookup can include adjacent punctuation in the control word. TeX ignores this
        // separating space, so make the control-word boundary explicit without changing the stored notebook source.
        source = CommandBoundary().Replace(source, "$0 ");
        source = FractionAliases().Replace(source, @"\frac");
        // Sizes and margins in units of the layout, so the formula is the same size whatever it's rendered at
        // TextPainter uses points (4/3 screen pixels); match the surrounding 14px document prose.
        float size = (prose ? 10.5f : 18) * zoom * density;
        ImagePixels pixels;
        if (prose)
        {
            var painter = new TextPainter { LaTeX = source, FontSize = size, TextColor = transparent ? SKColors.White : SKColors.Black };
            float pixelsWide = System.Math.Clamp((width - 8) * density, 40 * density, 2000 * density);
            var bounds = painter.Measure(pixelsWide);
            if (painter.ErrorMessage is { } error)
                throw new ArgumentException(error);
            pixels = Paint(MathF.Min(pixelsWide, bounds.Width) + 8 * density, bounds.Height + 12 * density, canvas => painter.Draw(canvas, offsetX: 4 * density, offsetY: 6 * density), white: !transparent);
        }
        else
        {
            var painter = new MathPainter { LaTeX = Undelimit(source), FontSize = size, TextColor = transparent ? SKColors.White : SKColors.Black };
            var bounds = painter.Measure();
            if (painter.ErrorMessage is { } error)
                throw new ArgumentException(error);
            pixels = Paint(bounds.Width + 12 * density, bounds.Height + 12 * density, canvas => painter.Draw(canvas), white: !transparent);
        }

        pixels = Ink(pixels, transparent);
        return new NotebookPixels(pixels, new Vector2(pixels.Width, pixels.Height) / density);
    }

    // Helper method for math drawn as ink to be tinted, white all over with only its alpha saying where the ink is. The
    // see-through texels were black, and a mip level halving the picture blended that black into the edges of every
    // stroke, so tinted the light text of a dark theme every glyph had a dark rim
    private static ImagePixels Ink(ImagePixels pixels, bool ink)
    {
        if (!ink)
            return pixels;

        Span<uint> texels = MemoryMarshal.Cast<byte, uint>(pixels.Pixels);
        for (int i = 0; i < texels.Length; i++)
            texels[i] |= 0x00FFFFFFu;
        return pixels;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\\[a-zA-Z]+(?=[^a-zA-Z\s{])")]
    private static partial System.Text.RegularExpressions.Regex CommandBoundary();

    [System.Text.RegularExpressions.GeneratedRegex(@"\\[dt]frac\b")]
    private static partial System.Text.RegularExpressions.Regex FractionAliases();

    internal static string Undelimit(string source)
    {
        string text = source.Trim();
        foreach (var (open, close) in new[] { ("$$", "$$"), ("\\[", "\\]"), ("\\(", "\\)"), ("$", "$") })
            if (text.Length >= open.Length + close.Length && text.StartsWith(open, StringComparison.Ordinal) && text.EndsWith(close, StringComparison.Ordinal))
                return text[open.Length..^close.Length].Trim();
        return text;
    }

    private static ImagePixels Paint(float width, float height, Action<SKCanvas> draw, bool white = false)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || width * height > MaxPixels)
            throw new ArgumentException("Output dimensions exceed the rendering limit.");
        int w = (int)MathF.Ceiling(width), h = (int)MathF.Ceiling(height);
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(white ? SKColors.White : SKColors.Transparent);
        draw(canvas);
        canvas.Flush();
        byte[] pixels = new byte[checked(w * h * 4)];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return new ImagePixels(w, h, pixels);
    }
}
