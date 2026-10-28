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
internal static partial class NotebookRender
{
    public const float Density = 2;
    public const int MaxSource = 4_000_000;
    private const int MaxSide = 4096;
    private const int MaxPixels = 8_000_000;

    public static ImagePixels Svg(string source)
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
        float scale = MathF.Min(Density, MathF.Min(MaxSide / bounds.Width, MaxSide / bounds.Height));
        scale = MathF.Min(scale, MathF.Sqrt(MaxPixels / (bounds.Width * bounds.Height)));
        return Paint(bounds.Width * scale, bounds.Height * scale, canvas =>
        {
            canvas.Scale(scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        });
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

    public static ImagePixels Math(string source, bool prose = false, bool transparent = false, float width = 600, float zoom = 1)
    {
        if (source.Length > 32_000)
            throw new ArgumentException("Math output exceeds the rendering limit.");
        // CSharpMath's command lookup can include adjacent punctuation in the control word. TeX ignores this
        // separating space, so make the control-word boundary explicit without changing the stored notebook source.
        source = CommandBoundary().Replace(source, "$0 ");
        source = FractionAliases().Replace(source, @"\frac");
        float size = (prose ? 28 : 36) * zoom;
        if (prose)
        {
            var painter = new TextPainter { LaTeX = source, FontSize = size, TextColor = transparent ? SKColors.White : SKColors.Black };
            float pixelsWide = System.Math.Clamp(width * Density - 16, 80, 4000);
            var bounds = painter.Measure(pixelsWide);
            if (painter.ErrorMessage is { } error)
                throw new ArgumentException(error);
            return Paint(MathF.Min(pixelsWide, bounds.Width) + 16, bounds.Height + 24, canvas => painter.Draw(canvas, offsetX: 8, offsetY: 12), white: !transparent);
        }
        else
        {
            var painter = new MathPainter { LaTeX = Undelimit(source), FontSize = size, TextColor = transparent ? SKColors.White : SKColors.Black };
            var bounds = painter.Measure();
            if (painter.ErrorMessage is { } error)
                throw new ArgumentException(error);
            return Paint(bounds.Width + 24, bounds.Height + 24, canvas => painter.Draw(canvas), white: !transparent);
        }
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
