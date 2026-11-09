using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using CSharpMath.SkiaSharp;
using Hex.Core.Notebooks;
using SkiaSharp;

namespace Hex.Dawn;

internal static partial class NotebookRender
{
    private static readonly Lazy<SKTypeface> ProseFace = new(() => LoadProseFace("inter"));
    private static readonly Lazy<SKTypeface> CodeFace = new(() => LoadProseFace("hack"));

    private static SKTypeface LoadProseFace(string name) =>
        SKTypeface.FromFile(Path.Combine(Hex.Core.HexHost.ProgramFolder, "Assets", "fonts", name, name + ".ttf"))
        ?? SKTypeface.FromFamilyName(name == "hack" ? "Consolas" : "Segoe UI");

    private sealed record InlineRun(string Text, SKFont Font, MathPainter? Math, bool Code,
        float Width, float Ascent, float Descent, float Scale = 1);

    /// <summary>Document prose uses the UI fonts; only formula runs go through mathematical typesetting.</summary>
    internal static NotebookPixels MarkdownMath(string source, float density, float width = 600, float zoom = 1)
    {
        if (source.Length > 32_000) throw new ArgumentException("Math output exceeds the rendering limit.");
        float size = 13 * zoom * density;
        float room = System.Math.Clamp((width - 8) * density, 40 * density, 2000 * density);
        using var regular = new SKFont(ProseFace.Value, size);
        using var bold = new SKFont(ProseFace.Value, size) { Embolden = true };
        using var italic = new SKFont(ProseFace.Value, size) { SkewX = -0.2f };
        using var mono = new SKFont(CodeFace.Value, size * 0.95f);
        using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var chip = new SKPaint { Color = SKColors.White.WithAlpha(22), IsAntialias = true };
        var runs = new List<InlineRun>();

        void Text(string value, SKFont font, bool code = false)
        {
            foreach (Match token in Regex.Matches(value, @"\s+|\S+"))
            {
                string word = string.IsNullOrWhiteSpace(token.Value) ? " " : token.Value;
                float padding = code ? 3 * density : 0;
                float measured = font.MeasureText(word) + padding * 2;
                if (measured <= room)
                    runs.Add(new(word, font, null, code, measured, -font.Metrics.Ascent, font.Metrics.Descent));
                else
                {
                    // Long identifiers wrap without splitting a surrogate pair or combining sequence.
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

        foreach (var piece in NotebookMath.Split(source))
        {
            if (piece.Math)
            {
                string latex = FractionAliases().Replace(CommandBoundary().Replace(piece.Text, "$0 "), @"\frac");
                var math = new MathPainter { LaTeX = latex, FontSize = size * 0.75f,
                    TextColor = SKColors.White, LineStyle = CSharpMath.Atom.LineStyle.Text };
                var bounds = math.Measure();
                if (math.ErrorMessage is { } error) throw new ArgumentException(error);
                float scale = MathF.Min(1, room / MathF.Max(1, bounds.Width));
                runs.Add(new("", regular, math, false, bounds.Width * scale,
                    -bounds.Top * scale, bounds.Bottom * scale, scale));
                continue;
            }

            // Match the inline emphasis and code supported by the previous prose-math path.
            int at = 0;
            foreach (Match styled in Regex.Matches(piece.Text, @"\*\*(.+?)\*\*|`([^`]+)`|\*([^*]+)\*"))
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
        var pixels = Paint(MathF.Min(room, MathF.Max(1, widest)) + 8 * density, y + 2 * density, canvas =>
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
        return new NotebookPixels(Ink(pixels, true), new Vector2(pixels.Width, pixels.Height) / density);
    }
}
