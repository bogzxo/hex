using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using CSharpMath.SkiaSharp;
using SkiaSharp;
using Svg.Skia;

namespace Hex.Notebooks.Render;

/// <summary>
/// NativeAOT C-ABI exports and managed Debug entry points for Hex's optional Notebooks renderer.
/// Owns all SkiaSharp, Svg.Skia, and CSharpMath dependencies so the main Hex binary never links or loads them unless
/// the Notebooks module is enabled and a notebook graphic is rendered.
/// </summary>
public static unsafe partial class NotebookRenderNative
{
    public const int AbiVersion = 1;
    public const int MaxSource = 4_000_000;
    private const int MaxSide = 4096;
    private const int MaxPixels = 8_000_000;
    private const float ExportText = 15.0f * 0.75f;
    private const float ExportDisplay = 18.0f * 0.75f;
    private const float ExportMargin = 2.0f;

    private static readonly ConcurrentDictionary<(string FontsDir, string Name), SKTypeface> Typefaces = new();

    private readonly record struct RenderedBitmap(int Width, int Height, float NatW, float NatH, byte[] Pixels);
    private readonly record struct MathPiece(string Text, bool Math = false);
    private sealed record InlineRun(string Text, SKFont Font, MathPainter? Math, bool Code,
        float Width, float Ascent, float Descent, float Scale = 1);

    [UnmanagedCallersOnly(EntryPoint = "hex_nb_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int AbiVersionExport() => AbiVersion;

    [UnmanagedCallersOnly(EntryPoint = "hex_nb_free", CallConvs = [typeof(CallConvCdecl)])]
    public static void FreeExport(byte* ptr)
    {
        if (ptr != null)
            NativeMemory.Free(ptr);
    }

    [UnmanagedCallersOnly(EntryPoint = "hex_nb_render_svg", CallConvs = [typeof(CallConvCdecl)])]
    public static int RenderSvgExport(
        byte* utf8, int utf8Len, float density,
        int* width, int* height, float* natW, float* natH,
        byte** pixelsOut, int* pixelsLen,
        byte** errOut, int* errLen)
    {
        *width = *height = *pixelsLen = *errLen = 0;
        *natW = *natH = 0;
        *pixelsOut = *errOut = null;
        try
        {
            string source = DecodeUtf8(utf8, utf8Len);
            RenderedBitmap result = RenderSvgCore(source, density);
            *width = result.Width;
            *height = result.Height;
            *natW = result.NatW;
            *natH = result.NatH;
            AllocCopy(result.Pixels, out byte* pix, out int len);
            *pixelsOut = pix;
            *pixelsLen = len;
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or XmlException)
        {
            AllocError(e.Message, out byte* err, out int len);
            *errOut = err;
            *errLen = len;
            return 1;
        }
        catch (Exception e)
        {
            AllocError(e.Message, out byte* err, out int len);
            *errOut = err;
            *errLen = len;
            return 2;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "hex_nb_render_math", CallConvs = [typeof(CallConvCdecl)])]
    public static int RenderMathExport(
        byte* utf8, int utf8Len, float density, int prose, int transparent, float width, float zoom,
        int* outW, int* outH, float* natW, float* natH,
        byte** pixelsOut, int* pixelsLen,
        byte** errOut, int* errLen)
    {
        *outW = *outH = *pixelsLen = *errLen = 0;
        *natW = *natH = 0;
        *pixelsOut = *errOut = null;
        try
        {
            string source = DecodeUtf8(utf8, utf8Len);
            RenderedBitmap result = RenderMathCore(source, density, prose != 0, transparent != 0, width, zoom);
            *outW = result.Width;
            *outH = result.Height;
            *natW = result.NatW;
            *natH = result.NatH;
            AllocCopy(result.Pixels, out byte* pix, out int len);
            *pixelsOut = pix;
            *pixelsLen = len;
            return 0;
        }
        catch (ArgumentException e)
        {
            AllocError(e.Message, out byte* err, out int len);
            *errOut = err;
            *errLen = len;
            return 1;
        }
        catch (Exception e)
        {
            AllocError(e.Message, out byte* err, out int len);
            *errOut = err;
            *errLen = len;
            return 2;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "hex_nb_render_markdown_math", CallConvs = [typeof(CallConvCdecl)])]
    public static int RenderMarkdownMathExport(
        byte* utf8, int utf8Len, byte* fontsDirUtf8, int fontsDirLen,
        float density, float width, float zoom,
        int* outW, int* outH, float* natW, float* natH,
        byte** pixelsOut, int* pixelsLen,
        byte** errOut, int* errLen)
    {
        *outW = *outH = *pixelsLen = *errLen = 0;
        *natW = *natH = 0;
        *pixelsOut = *errOut = null;
        try
        {
            string source = DecodeUtf8(utf8, utf8Len);
            string? fontsDir = fontsDirUtf8 != null && fontsDirLen > 0 ? DecodeUtf8(fontsDirUtf8, fontsDirLen) : null;
            RenderedBitmap result = RenderMarkdownMathCore(source, fontsDir, density, width, zoom);
            *outW = result.Width;
            *outH = result.Height;
            *natW = result.NatW;
            *natH = result.NatH;
            AllocCopy(result.Pixels, out byte* pix, out int len);
            *pixelsOut = pix;
            *pixelsLen = len;
            return 0;
        }
        catch (ArgumentException e)
        {
            AllocError(e.Message, out byte* err, out int len);
            *errOut = err;
            *errLen = len;
            return 1;
        }
        catch (Exception e)
        {
            AllocError(e.Message, out byte* err, out int len);
            *errOut = err;
            *errLen = len;
            return 2;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "hex_nb_render_math_svg", CallConvs = [typeof(CallConvCdecl)])]
    public static int RenderMathSvgExport(
        byte* utf8, int utf8Len, int display,
        byte** svgOut, int* svgLen,
        float* outW, float* outH, float* descent)
    {
        *svgOut = null;
        *svgLen = 0;
        *outW = *outH = *descent = 0;
        try
        {
            string tex = DecodeUtf8(utf8, utf8Len);
            if (RenderMathSvgCore(tex, display != 0) is not { } result)
                return 1;
            *outW = result.Width;
            *outH = result.Height;
            *descent = result.Descent;
            AllocCopy(Encoding.UTF8.GetBytes(result.Svg), out byte* svg, out int len);
            *svgOut = svg;
            *svgLen = len;
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    /* Managed entry points used by Hex.Dawn's Debug JIT fallback when NativeAOT hasn't been published. */

    public static int RenderSvgManaged(
        string source, float density,
        out int width, out int height, out float natW, out float natH,
        out byte[] pixels, out string? error)
    {
        width = height = 0;
        natW = natH = 0;
        pixels = [];
        error = null;
        try
        {
            RenderedBitmap result = RenderSvgCore(source, density);
            width = result.Width;
            height = result.Height;
            natW = result.NatW;
            natH = result.NatH;
            pixels = result.Pixels;
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or XmlException)
        {
            error = e.Message;
            return 1;
        }
        catch (Exception e)
        {
            error = e.Message;
            return 2;
        }
    }

    public static int RenderMathManaged(
        string source, float density, bool prose, bool transparent, float width, float zoom,
        out int outW, out int outH, out float natW, out float natH,
        out byte[] pixels, out string? error)
    {
        outW = outH = 0;
        natW = natH = 0;
        pixels = [];
        error = null;
        try
        {
            RenderedBitmap result = RenderMathCore(source, density, prose, transparent, width, zoom);
            outW = result.Width;
            outH = result.Height;
            natW = result.NatW;
            natH = result.NatH;
            pixels = result.Pixels;
            return 0;
        }
        catch (ArgumentException e)
        {
            error = e.Message;
            return 1;
        }
        catch (Exception e)
        {
            error = e.Message;
            return 2;
        }
    }

    public static int RenderMarkdownMathManaged(
        string source, string? fontsDir, float density, float width, float zoom,
        out int outW, out int outH, out float natW, out float natH,
        out byte[] pixels, out string? error)
    {
        outW = outH = 0;
        natW = natH = 0;
        pixels = [];
        error = null;
        try
        {
            RenderedBitmap result = RenderMarkdownMathCore(source, fontsDir, density, width, zoom);
            outW = result.Width;
            outH = result.Height;
            natW = result.NatW;
            natH = result.NatH;
            pixels = result.Pixels;
            return 0;
        }
        catch (ArgumentException e)
        {
            error = e.Message;
            return 1;
        }
        catch (Exception e)
        {
            error = e.Message;
            return 2;
        }
    }

    public static bool RenderMathSvgManaged(
        string tex, bool display,
        out string? svg, out float width, out float height, out float descent)
    {
        svg = null;
        width = height = descent = 0;
        try
        {
            if (RenderMathSvgCore(tex, display) is not { } result)
                return false;
            svg = result.Svg;
            width = result.Width;
            height = result.Height;
            descent = result.Descent;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /* Core Skia / Svg.Skia / CSharpMath rendering implementations */

    private static RenderedBitmap RenderSvgCore(string source, float density)
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
        var (w, h, pixels) = Paint(bounds.Width * scale, bounds.Height * scale, canvas =>
        {
            canvas.Scale(scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        });

        return new RenderedBitmap(w, h, w / scale, h / scale, pixels);
    }

    private static RenderedBitmap RenderMathCore(string source, float density, bool prose, bool transparent, float width, float zoom)
    {
        if (source.Length > 32_000)
            throw new ArgumentException("Math output exceeds the rendering limit.");
        source = CommandBoundary().Replace(source, "$0 ");
        source = FractionAliases().Replace(source, @"\frac");
        float size = (prose ? 10.5f : 18) * zoom * density;
        (int Width, int Height, byte[] Pixels) bitmap;
        if (prose)
        {
            var painter = new TextPainter { LaTeX = source, FontSize = size, TextColor = transparent ? SKColors.White : SKColors.Black };
            float pixelsWide = Math.Clamp((width - 8) * density, 40 * density, 2000 * density);
            var bounds = painter.Measure(pixelsWide);
            if (painter.ErrorMessage is { } error)
                throw new ArgumentException(error);
            bitmap = Paint(MathF.Min(pixelsWide, bounds.Width) + 8 * density, bounds.Height + 12 * density,
                canvas => painter.Draw(canvas, offsetX: 4 * density, offsetY: 6 * density), white: !transparent);
        }
        else
        {
            var painter = new MathPainter { LaTeX = Undelimit(source), FontSize = size, TextColor = transparent ? SKColors.White : SKColors.Black };
            var bounds = painter.Measure();
            if (painter.ErrorMessage is { } error)
                throw new ArgumentException(error);
            bitmap = Paint(bounds.Width + 12 * density, bounds.Height + 12 * density,
                canvas => painter.Draw(canvas), white: !transparent);
        }

        Ink(bitmap.Pixels, transparent);
        return new RenderedBitmap(bitmap.Width, bitmap.Height, bitmap.Width / density, bitmap.Height / density, bitmap.Pixels);
    }

    private static RenderedBitmap RenderMarkdownMathCore(string source, string? fontsDir, float density, float width, float zoom)
    {
        if (source.Length > 32_000)
            throw new ArgumentException("Math output exceeds the rendering limit.");
        float size = 13 * zoom * density;
        float room = Math.Clamp((width - 8) * density, 40 * density, 2000 * density);
        SKTypeface proseFace = LoadProseFace(fontsDir, "inter");
        SKTypeface codeFace = LoadProseFace(fontsDir, "hack");
        using var regular = new SKFont(proseFace, size);
        using var bold = new SKFont(proseFace, size) { Embolden = true };
        using var italic = new SKFont(proseFace, size) { SkewX = -0.2f };
        using var mono = new SKFont(codeFace, size * 0.95f);
        using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var chip = new SKPaint { Color = SKColors.White.WithAlpha(22), IsAntialias = true };
        var runs = new List<InlineRun>();

        void Text(string value, SKFont font, bool code = false)
        {
            foreach (Match token in WordTokens().Matches(value))
            {
                string word = string.IsNullOrWhiteSpace(token.Value) ? " " : token.Value;
                float padding = code ? 3 * density : 0;
                float measured = font.MeasureText(word) + padding * 2;
                if (measured <= room)
                    runs.Add(new(word, font, null, code, measured, -font.Metrics.Ascent, font.Metrics.Descent));
                else
                {
                    var letters = StringInfo.GetTextElementEnumerator(word);
                    while (letters.MoveNext())
                    {
                        string letter = letters.GetTextElement();
                        runs.Add(new(letter, font, null, code, font.MeasureText(letter) + padding * 2,
                            -font.Metrics.Ascent, font.Metrics.Descent));
                    }
                }
            }
        }

        foreach (var piece in SplitMath(source))
        {
            if (piece.Math)
            {
                string latex = FractionAliases().Replace(CommandBoundary().Replace(piece.Text, "$0 "), @"\frac");
                var math = new MathPainter
                {
                    LaTeX = latex,
                    FontSize = size * 0.75f,
                    TextColor = SKColors.White,
                    LineStyle = CSharpMath.Atom.LineStyle.Text
                };
                var bounds = math.Measure();
                if (math.ErrorMessage is { } error)
                    throw new ArgumentException(error);
                float scale = MathF.Min(1, room / MathF.Max(1, bounds.Width));
                runs.Add(new("", regular, math, false, bounds.Width * scale,
                    -bounds.Top * scale, bounds.Bottom * scale, scale));
                continue;
            }

            int at = 0;
            foreach (Match styled in InlineStyleTokens().Matches(piece.Text))
            {
                Text(piece.Text[at..styled.Index], regular);
                if (styled.Groups[1].Success) Text(styled.Groups[1].Value, bold);
                else if (styled.Groups[2].Success) Text(styled.Groups[2].Value, mono, code: true);
                else Text(styled.Groups[3].Value, italic);
                at = styled.Index + styled.Length;
            }
            Text(piece.Text[at..], regular);
        }

        var placed = new List<(InlineRun Run, float X, float Baseline)>();
        var line = new List<(InlineRun Run, float X)>();
        float x = 0, y = 4 * density, widest = 0;
        void Flush()
        {
            if (line.Count == 0) return;
            float ascent = MathF.Max(-regular.Metrics.Ascent, line.Max(item => item.Run.Ascent));
            float descent = MathF.Max(regular.Metrics.Descent, line.Max(item => item.Run.Descent));
            foreach (var item in line) placed.Add((item.Run, item.X, y + ascent));
            widest = MathF.Max(widest, x);
            y += MathF.Max(size * 1.55f, ascent + descent + 4 * density);
            line.Clear();
            x = 0;
        }
        foreach (var run in runs)
        {
            if (x > 0 && x + run.Width > room) Flush();
            if (x == 0 && run.Math is null && string.IsNullOrWhiteSpace(run.Text)) continue;
            line.Add((run, x));
            x += run.Width;
        }
        Flush();

        var bitmap = Paint(MathF.Min(room, MathF.Max(1, widest)) + 8 * density, y + 2 * density, canvas =>
        {
            foreach (var (run, left, baseline) in placed)
            {
                float px = left + 4 * density;
                if (run.Math is { } math)
                {
                    canvas.Save();
                    canvas.Translate(px, baseline);
                    canvas.Scale(run.Scale);
                    math.Draw(canvas, 0, 0);
                    canvas.Restore();
                }
                else
                {
                    if (run.Code)
                        canvas.DrawRoundRect(new SKRect(px, baseline - run.Ascent - density,
                            px + run.Width, baseline + run.Descent + density), 3 * density, 3 * density, chip);
                    canvas.DrawText(run.Text, px + (run.Code ? 3 * density : 0), baseline,
                        SKTextAlign.Left, run.Font, ink);
                }
            }
        });
        Ink(bitmap.Pixels, true);
        return new RenderedBitmap(bitmap.Width, bitmap.Height, bitmap.Width / density, bitmap.Height / density, bitmap.Pixels);
    }

    private static (string Svg, float Width, float Height, float Descent)? RenderMathSvgCore(string tex, bool display)
    {
        if (tex.Length > 32_000)
            return null;
        string source = FractionAliases().Replace(CommandBoundary().Replace(tex, "$0 "), @"\frac");
        var painter = new MathPainter { LaTeX = source, FontSize = display ? ExportDisplay : ExportText, TextColor = SKColors.Black };
        if (!display)
            painter.LineStyle = CSharpMath.Atom.LineStyle.Text;
        var bounds = painter.Measure();
        if (painter.ErrorMessage is not null || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        float width = bounds.Width + ExportMargin * 2, height = bounds.Height + ExportMargin * 2;
        using var stream = new MemoryStream();
        using (var canvas = SKSvgCanvas.Create(SKRect.Create(width, height), stream))
        {
            canvas.Translate(ExportMargin - bounds.Left, ExportMargin - bounds.Top);
            painter.Draw(canvas, 0, 0);
        }

        string svg = Encoding.UTF8.GetString(stream.ToArray());
        float descent = MathF.Max(0.0f, bounds.Bottom) + ExportMargin;
        return (svg, width, height, descent);
    }

    private static SKTypeface LoadProseFace(string? fontsDir, string name)
    {
        string keyDir = fontsDir ?? string.Empty;
        return Typefaces.GetOrAdd((keyDir, name), static key =>
        {
            if (key.FontsDir.Length > 0)
            {
                string path = Path.Combine(key.FontsDir, key.Name, key.Name + ".ttf");
                if (File.Exists(path) && SKTypeface.FromFile(path) is { } loaded)
                    return loaded;
            }
            return SKTypeface.FromFamilyName(key.Name == "hack" ? "Consolas" : "Segoe UI") ?? SKTypeface.Default;
        });
    }

    private static bool EmbeddedRaster(string value) =>
        value.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("data:image/gif;base64,", StringComparison.OrdinalIgnoreCase);

    private static bool UnsafeCss(string value)
    {
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

    private static void Ink(byte[] pixels, bool ink)
    {
        if (!ink) return;
        Span<uint> texels = MemoryMarshal.Cast<byte, uint>(pixels);
        for (int i = 0; i < texels.Length; i++)
            texels[i] |= 0x00FFFFFFu;
    }

    private static string Undelimit(string source)
    {
        string text = source.Trim();
        foreach (var (open, close) in new[] { ("$$", "$$"), ("\\[", "\\]"), ("\\(", "\\)"), ("$", "$") })
        {
            if (text.Length >= open.Length + close.Length && text.StartsWith(open, StringComparison.Ordinal) && text.EndsWith(close, StringComparison.Ordinal))
                return text[open.Length..^close.Length].Trim();
        }
        return text;
    }

    private static List<MathPiece> SplitMath(string text)
    {
        var result = new List<MathPiece>();
        int start = 0, i = 0;
        while (i < text.Length)
        {
            if (text[i] is '`' or '~')
            {
                char marker = text[i];
                int length = 1;
                while (i + length < text.Length && text[i + length] == marker) length++;
                if (marker == '`' || length >= 3)
                {
                    string fence = new(marker, length);
                    int end = text.IndexOf(fence, i + length, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + length;
                    continue;
                }
            }
            bool escaped = i > 0 && text[i - 1] == '\\';
            string? open = !escaped && text.AsSpan(i).StartsWith("$$") ? "$$"
                : !escaped && text.AsSpan(i).StartsWith("\\[") ? "\\["
                : !escaped && text.AsSpan(i).StartsWith("\\(") ? "\\("
                : !escaped && text[i] == '$' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) ? "$" : null;
            if (open is null) { i++; continue; }
            string close = open switch { "\\[" => "\\]", "\\(" => "\\)", _ => open };
            int finish = i + open.Length;
            while ((finish = text.IndexOf(close, finish, StringComparison.Ordinal)) >= 0)
            {
                if (finish > 0 && text[finish - 1] != '\\' && (open != "$" || !char.IsWhiteSpace(text[finish - 1]) && (finish + 1 == text.Length || !char.IsDigit(text[finish + 1])))) break;
                finish += close.Length;
            }
            if (finish < 0) { i += open.Length; continue; }
            if (i > start) result.Add(new MathPiece(text[start..i]));
            result.Add(new MathPiece(text[(i + open.Length)..finish], true));
            i = start = finish + close.Length;
        }
        if (start < text.Length) result.Add(new MathPiece(text[start..]));
        return result;
    }

    private static (int Width, int Height, byte[] Pixels) Paint(float width, float height, Action<SKCanvas> draw, bool white = false)
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
        return (w, h, pixels);
    }

    private static string DecodeUtf8(byte* ptr, int len)
    {
        if (ptr == null || len <= 0)
            return string.Empty;
        return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(ptr, len));
    }

    private static void AllocCopy(ReadOnlySpan<byte> data, out byte* ptr, out int len)
    {
        len = data.Length;
        ptr = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, len));
        if (len > 0)
            data.CopyTo(new Span<byte>(ptr, len));
    }

    private static void AllocError(string message, out byte* errOut, out int errLen)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message ?? string.Empty);
        AllocCopy(bytes, out errOut, out errLen);
    }

    [GeneratedRegex(@"\\[a-zA-Z]+(?=[^a-zA-Z\s{])")]
    private static partial Regex CommandBoundary();

    [GeneratedRegex(@"\\[dt]frac\b")]
    private static partial Regex FractionAliases();

    [GeneratedRegex(@"\s+|\S+")]
    private static partial Regex WordTokens();

    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`]+)`|\*([^*]+)\*")]
    private static partial Regex InlineStyleTokens();
}
