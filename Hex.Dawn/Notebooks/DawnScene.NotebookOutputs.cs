using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

using Hex.Core.Notebooks;

using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

/// <summary>A highlighter that colours nothing, for what a cell printed, its colours are its own (its ANSI escapes).</summary>
internal sealed class PlainHighlighter : ICodeHighlighter
{
    public static readonly PlainHighlighter Instance = new();

    public string Name => "output";

    public List<SyntaxSpan>[] Read(string code, int lineCount, CodeLineStart[]? starts = null, List<HidlSwatch>? swatches = null, List<HidlArt>? art = null)
    {
        var lines = new List<SyntaxSpan>[lineCount];
        for (int i = 0; i < lineCount; i++)
            lines[i] = [];
        return lines;
    }

    public List<SyntaxSpan> ReadLine(string line, in CodeLineStart start) => [];
}

// What running a code cell came to, under it. Text (what it printed, a result's text, an error's traceback) in an editor
// of the code's font that can be selected and copied, in the colours its ANSI escapes say drawn in the terminal's own
// sixteen, as tall as it is up to a screenful and scrolled past that. A picture (PNG, JPEG) drawn as it came, markdown
// written out, a DataFrame's table a table. Of the forms a result comes in the one Hex shows best is shown, other HTML
// and widgets fall back to the rest
internal sealed partial class DawnScene
{
    // How many rows of text an output shows before it scrolls, and the most of its text that's put in an editor at all
    private const int OUTPUT_ROWS = 30;
    private const int OUTPUT_MOST = 200_000;
    private const int OUTPUT_WHOLE_ROWS = 100_000;

    // The pictures of the outputs on screen, by the data they came out of, kept while it's the same data
    private readonly ConditionalWeakTable<JsonNode, Texture> outputPictures = new();
    private readonly Dictionary<NotebookCellView, List<NotebookPicture>> cellPictures = [];
    private readonly Dictionary<NotebookCellView, List<NotebookPicture>> markdownPictures = [];

    // Pictures decoded and waiting for their textures, which are only made while nothing draws (see MakeOutputPictures),
    // by the data they came out of, with the outputs waiting to show them
    private readonly Dictionary<JsonNode, (ImagePixels Pixels, List<NotebookPicture> Waiting)> picturesToMake = new(ReferenceEqualityComparer.Instance);

    /// <summary>A number that's the same while a cell's outputs are, what they say included, without making anything.</summary>
    private static int OutputsSignature(NotebookCell cell, NotebookInput? asking)
    {
        var hash = new HashCode();
        hash.Add(cell.Outputs.Count);
        hash.Add(asking?.Cell == cell ? RuntimeHelpers.GetHashCode(asking) : 0);
        foreach (JsonObject output in cell.Outputs)
        {
            hash.Add(RuntimeHelpers.GetHashCode(output));
            if (output["text"] is { } text)
                hash.Add(RuntimeHelpers.GetHashCode(text));
            if (output["data"] is { } data)
                hash.Add(RuntimeHelpers.GetHashCode(data));
        }

        return hash.ToHashCode();
    }

    // Helper method for a cell's outputs drawn again, as they are now
    private void DrawOutputs(HexNotebook notebook, NotebookCellView view)
    {
        view.OutputsDrawn = OutputsSignature(view.Cell, notebook.Run.Input);
        List<NotebookPicture> was = cellPictures.TryGetValue(view, out var kept) ? kept : [];
        var now = new List<NotebookPicture>();
        foreach (UIComponent child in view.Outputs.Children.ToList())
            view.Outputs.Remove(child);

        foreach (JsonObject output in view.Cell.Outputs)
        {
            // An error says what it was first, in a line of its own with the way to the line it failed on
            if (output["output_type"]?.GetValue<string>() == "error")
                view.Outputs.Add(ErrorHeader(notebook, view, output));

            if (DrawOutput(notebook, output, now) is not { } drawn)
                continue;
            view.Outputs.Add(drawn);

            // Text longer than an output shows says so under it, with all of it a press away
            if (drawn is CodeEditor text && OutputFooter(view, output, text) is { } footer)
                view.Outputs.Add(footer);
        }

        // The code asking for something typed (input()), a box under what it printed with its prompt, Enter sends it
        if (notebook.Run.Input is { } asked && asked.Cell == view.Cell)
        {
            var box = view.Outputs.Add(new TextBox
            {
                Placeholder = asked.Prompt.Length > 0 ? asked.Prompt.Trim() : Said("notebook.input_placeholder"),
                Tooltip = Said("notebook.input_placeholder"),
                MaxLength = 4096,
                Secret = asked.Password,
                Anchor = Origin.TopLeft,
                Pivot = Origin.TopLeft,
                Size = new Vector2(360.0f, 28.0f)
            });
            box.OnSubmitted = typed => AnswerInput(notebook, typed);
            box.Focus();
        }

        // What it showed before let go of once it's the new list that's kept. Looked for in the old one, nothing was ever freed
        cellPictures[view] = now;
        ReleaseNotebookPictures(was);
        view.Outputs.Visible = view.Outputs.Children.Count > 0;
    }

    // Helper method for one output, null for one with nothing Hex can show
    private UIComponent? DrawOutput(HexNotebook notebook, JsonObject output, List<NotebookPicture> pictures)
    {
        string type = output["output_type"]?.GetValue<string>() ?? string.Empty;
        switch (type)
        {
            case "stream":
                return OutputText(NotebookFile.Text(output["text"]), output["name"]?.GetValue<string>() == "stderr", output, tail: true);

            case "error":
            {
                string traceback = output["traceback"] is JsonArray lines ? string.Join('\n', lines.Select(line => line?.GetValue<string>() ?? string.Empty)) : string.Empty;
                if (traceback.Length == 0)
                    traceback = $"{output["ename"]?.GetValue<string>()}: {output["evalue"]?.GetValue<string>()}";
                return OutputText(traceback, error: true, output);
            }

            case "execute_result":
            case "display_data":
                if (output["data"] is not JsonObject data)
                    return null;
                return OutputData(notebook, data, output["metadata"] as JsonObject, pictures);
        }

        return null;
    }

    // Helper method for the best of the forms a result came in, a picture, a table, markdown, then text
    private UIComponent? OutputData(HexNotebook notebook, JsonObject data, JsonObject? metadata, List<NotebookPicture> pictures)
    {
        if (data["image/svg+xml"] is { } svg && RenderedPicture(svg, density => NotebookRender.Svg(NotebookFile.Text(svg), density), pictures, zoomed: true, NotebookFile.Text(data["text/plain"])) is { } vector)
        {
            OfferSaveImage(vector, "image/svg+xml", svg);
            return vector;
        }

        foreach (string mime in (string[])["image/png", "image/jpeg", "image/gif", "image/bmp"])
        {
            if (data[mime] is JsonValue value && value.TryGetValue(out string? encoded) && OutputPicture(data[mime]!, encoded, metadata?[mime] as JsonObject, pictures) is { } picture)
            {
                OfferSaveImage(picture, mime, data[mime]!);
                return picture;
            }
        }

        // A DataFrame's HTML is drawn as a table rather than its text/plain's columns of spaces
        if (data["text/html"] is { } html && OutputHtmlTable(NotebookFile.Text(html)) is { } table)
            return table;

        if (data["text/latex"] is { } formula && RenderedPicture(formula, density => NotebookRender.Math(NotebookFile.Text(formula), density, transparent: true), pictures, zoomed: true, NotebookFile.Text(data["text/plain"]), mathInk: true) is { } math)
            return math;

        if (data["text/markdown"] is { } markdown)
            return OutputMarkdown(notebook, NotebookFile.Text(markdown), pictures);

        // Other HTML and JSON only when the text is no more than the object's name, IPython's HTML and JSON and a
        // class with only a _repr_html_ said "<IPython.core.display.HTML object>" and nothing else
        string? plain = data["text/plain"] is { } said ? NotebookFile.Text(said) : null;
        if (plain is null || NotebookHtml.IsBareRepr(plain))
        {
            if (data["text/html"] is { } other)
                return OutputMarkdown(notebook, NotebookHtml.ToMarkdown(NotebookFile.Text(other)), pictures);
            if (data["application/json"] is { } json)
                return OutputText(json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), error: false);
        }

        if (plain is not null)
            return OutputText(plain, error: false);

        if (data["text/latex"] is { } latex)
            return OutputText(NotebookFile.Text(latex), error: false);

        if (data["image/svg+xml"] is { } unsupportedSvg)
            return OutputText("SVG could not be rendered.\n" + NotebookFile.Text(unsupportedSvg), error: true);

        if (data.Count > 0)
            return OutputText("No static renderer for: " + string.Join(", ", data.Select(entry => entry.Key)), error: false);

        return null;
    }

    // Helper method for markdown written out, its tables drawn as tables
    private StackPanel OutputMarkdown(HexNotebook notebook, string markdown, List<NotebookPicture> pictures)
    {
        var written = new StackPanel { Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = 4.0f };
        WriteMarkdown(written, markdown, notebook.Model.Language, pictures);
        return written;
    }

    // Helper method for text in the code's font, its ANSI colours drawn in the terminal's
    private CodeEditor OutputText(string text, bool error, JsonObject? output = null, bool tail = false)
    {
        if (text.Length > OUTPUT_MOST)
            text = text[..OUTPUT_MOST];
        if (text.EndsWith('\n'))
            text = text[..^1];

        var (plain, colours) = AnsiText.Read(text);
        bool whole = output is not null && wholeOutputs.TryGetValue(output, out _);
        var editor = new NotebookTextOutput(plain)
        {
            Error = error,
            ReadOnly = true,
            LineNumbers = false,
            Minimap = false,
            StickyScroll = false,
            CheckSyntax = false,
            FitRows = whole ? OUTPUT_WHOLE_ROWS : OUTPUT_ROWS,
            IndentGuides = false,
            WordWrap = true,
            LightOccurrences = false,
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft,
            TextScale = CODE_TEXT * settings.CodeZoom,
            Font = codeEditing.codeFont,
            Padding = new UIEdges(6.0f, 2.0f, 6.0f, 2.0f),
            Highlighter = PlainHighlighter.Instance,
            Color = error && compositor.Skin is { } skin ? skin.ErrorColor with { W = 0.08f } : Vector4.Zero
        };
        if (compositor.Skin is { } palette)
            editor.AnsiColours = TerminalPalette.FromPalette(palette.Palette).Ansi;
        editor.SemanticSpans = colours;

        // A log longer than the output shows is shown at its end, the newest of it, the way a terminal is
        if (tail && !whole && editor.Document.LineCount > OUTPUT_ROWS)
            editor.GoTo(editor.Document.LineCount - 1);
        return editor;
    }

    // The outputs shown whole rather than OUTPUT_ROWS of them, by the output, for as long as it's the same output
    private readonly ConditionalWeakTable<JsonObject, object> wholeOutputs = new();

    // Helper method for the line under text longer than an output shows, how much of it there is, all of it or less of it,
    // and a copy of all of it. Null for text that fits
    private NotebookOutputStrip? OutputFooter(NotebookCellView view, JsonObject output, CodeEditor text)
    {
        int lines = text.Document.LineCount;
        if (lines <= OUTPUT_ROWS)
            return null;

        bool whole = wholeOutputs.TryGetValue(output, out _);
        string count = lines.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var strip = new NotebookOutputStrip
        {
            Said = new Label
            {
                Text = whole ? Said("notebook.lines_all", ("count", count))
                    : Said(output["output_type"]?.GetValue<string>() == "stream" ? "notebook.lines_some" : "notebook.lines_first",("shown", OUTPUT_ROWS.ToString(System.Globalization.CultureInfo.CurrentCulture)), ("count", count)),
                Dim = true, TextScale = 0.12f, Align = Origin.Left, Anchor = Origin.Left
            }
        };
        strip.Add(strip.Said);
        var more = strip.Add(new Button
        {
            Icon = whole ? "fold" : "unfold", Label = Said(whole ? "notebook.show_less" : "notebook.show_all"), LabelScale = 0.12f,
            Size = new Vector2(0.0f, 22.0f)
        });
        more.OnPressed = () =>
        {
            if (!wholeOutputs.Remove(output))
                wholeOutputs.Add(output, output);
            view.OutputsDrawn = -1;
        };
        var copy = strip.Add(new Button { Icon = "copy", Label = Said("notebook.copy_text"), LabelScale = 0.12f, Size = new Vector2(0.0f, 22.0f) });
        copy.OnPressed = () =>
        {
            UIClipboard.Copy(text.Text);
            Say(Said("notebook.output_copied"));
        };
        return strip;
    }

    // Helper method for the line over an error's traceback, the kind of error and what it said in the error colour, and the
    // line of the cell it failed on a press away when the traceback says which
    private NotebookOutputStrip ErrorHeader(HexNotebook notebook, NotebookCellView view, JsonObject output)
    {
        string name = output["ename"]?.GetValue<string>() ?? string.Empty;
        string value = output["evalue"]?.GetValue<string>() ?? string.Empty;
        var strip = new NotebookOutputStrip
        {
            Error = true,
            Said = new Label
            {
                Text = UIRichText.Literal(value.Length > 0 ? $"{name}: {value}" : name),
                Bold = true, Wrap = true, TextScale = 0.15f, Align = Origin.TopLeft, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft,
                Color = compositor.Skin?.ErrorColor
            }
        };
        strip.Add(strip.Said);

        string traceback = output["traceback"] is JsonArray lines ? string.Join('\n', lines.Select(line => line?.GetValue<string>() ?? string.Empty)) : string.Empty;
        if (FailingLine(AnsiText.Read(traceback).Item1, view.Cell.ExecutionCount) is int line)
        {
            var go = strip.Add(new Button
            {
                Icon = "go-to-file", Label = Said("notebook.go_to_line", ("line", line.ToString(System.Globalization.CultureInfo.CurrentCulture))),
                LabelScale = 0.12f, Size = new Vector2(0.0f, 22.0f), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft
            });
            go.OnPressed = () =>
            {
                int index = cellViews.IndexOf(view);
                if (index < 0)
                    return;
                HideSource(notebook, view.Cell, hide: false);
                EditCell(notebook, index);
                view.Editor.GoTo(Math.Max(0, line - 1));
            };
        }

        return strip;
    }

    /// <summary>
    /// The line of the cell an error happened on, out of IPython's traceback: the last "Cell In[n], line k" of this cell's
    /// run (the deepest frame that's in the cell), else the first "----> k" of an older IPython. Null when it doesn't say.
    /// </summary>
    internal static int? FailingLine(string traceback, int? count)
    {
        int? found = null;
        foreach (System.Text.RegularExpressions.Match match in CellLine().Matches(traceback))
        {
            if (count is null || match.Groups[1].Value == count.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                found = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (found is null && ArrowLine().Match(traceback) is { Success: true } arrow)
            found = int.Parse(arrow.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return found is > 0 ? found : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"Cell In\s*\[(\d+)\],\s*line (\d+)")]
    private static partial System.Text.RegularExpressions.Regex CellLine();

    [System.Text.RegularExpressions.GeneratedRegex(@"^-+>\s*(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex ArrowLine();

    // Helper method for a picture out of its base64, decoded once for as long as it's the same data. Its texture is made
    // later while nothing draws, the picture shows it from then on
    private NotebookPicture? OutputPicture(JsonNode data, string encoded, JsonObject? size, List<NotebookPicture> pictures)
        => OutputPicture(data, () =>
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            return ImageFile.Load(new MemoryStream(bytes));
        }, size, pictures);

    private NotebookPicture? OutputPicture(JsonNode data, Func<ImagePixels> decode, JsonObject? size, List<NotebookPicture> pictures, float density = 1)
    {
        if (Engine.WindowManager.Graphics is null)
            return null;

        Texture? made = outputPictures.TryGetValue(data, out Texture? kept) ? kept : null;
        (ImagePixels Pixels, List<NotebookPicture> Waiting) waiting = default;
        float natural, tallness;
        if (made is not null)
        {
            (natural, tallness) = (made.Width, made.Height);
        }
        else
        {
            lock (picturesToMake)
            {
                if (!picturesToMake.TryGetValue(data, out waiting))
                {
                    try
                    {
                        waiting = (decode(), []);
                    }
                    catch (Exception e) when (e is FormatException or InvalidOperationException or IOException or ArgumentException or System.Xml.XmlException)
                    {
                        return null;
                    }

                    picturesToMake[data] = waiting;
                    Engine.WindowManager.RequestExclusive();
                }
            }

            (natural, tallness) = (waiting.Pixels.Width, waiting.Pixels.Height);
        }

        float width = size?["width"] is JsonValue w && w.TryGetValue(out float wanted) && wanted > 0 && float.IsFinite(wanted) ? wanted : natural / density;
        float height = size?["height"] is JsonValue h && h.TryGetValue(out float tall) && tall > 0 && float.IsFinite(tall) ? tall : tallness * (width / MathF.Max(1.0f, natural));
        var picture = new NotebookPicture(new Vector2(width, height)) { Texture = made, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(width, height) };
        if (made is null)
        {
            lock (picturesToMake)
                waiting.Waiting!.Add(picture);
        }

        pictures.Add(picture);
        return picture;
    }

    /// <summary>
    /// The textures of the pictures outputs decoded, made while nothing draws, each handed to the outputs waiting for it. A
    /// texture made on the thread that runs the notebook went into the frame being drawn half way through, and a Run All
    /// of a few plots came out as pieces of each other.
    /// </summary>
    private void MakeOutputPictures()
    {
        MakeRenderedNotebookPictures();
        List<(JsonNode Data, ImagePixels Pixels, List<NotebookPicture> Waiting)> making;
        lock (picturesToMake)
        {
            if (picturesToMake.Count == 0)
                return;
            making = [.. picturesToMake.Select(entry => (entry.Key, entry.Value.Pixels, entry.Value.Waiting))];
            picturesToMake.Clear();
        }

        foreach (var (data, pixels, waiting) in making)
        {
            Texture texture = Texture.FromPixels((uint)pixels.Width, (uint)pixels.Height, pixels.Pixels, TextureDefinition.RgbaUnsignedBytePicture);
            outputPictures.AddOrUpdate(data, texture);
            foreach (NotebookPicture picture in waiting)
                picture.Texture = texture;

            // Its cell ran again before it was made, nothing shows it
            FreeOutputPicture(texture);
        }
    }

    // Helper method for the outputs as wide as the cell, a picture no wider than it came or than there's room for
    private static void FitOutputs(NotebookCellView view, float width, float zoom = 1)
    {
        for (int index = 0; index < view.Outputs.Children.Count; index++)
        {
            UIComponent output = view.Outputs.Children[index];
            switch (output)
            {
                case NotebookPicture picture:
                    picture.Reflow?.Invoke(width, zoom);
                    picture.KeepDense?.Invoke();
                    float scale = MathF.Min(picture.Reflow is null ? zoom : 1, width / MathF.Max(1.0f, picture.Natural.X));
                    picture.Size = picture.Natural * scale;
                    break;
                case Label label:
                    label.Size = label.Size with { X = width - 8.0f };
                    break;
                case NotebookOutputStrip strip:
                    strip.Fit(width, zoom);
                    break;
                case StackPanel written:
                    FitWritten(written, width, zoom);
                    break;
                case CodeEditor editor:
                    editor.TextScale = CODE_TEXT * zoom;
                    editor.Size = editor.Size with { X = width };
                    break;
                default:
                    output.Size = output.Size with { X = width };
                    break;
            }
        }
    }

    /// <summary>A cell's outputs let go of, its pictures freed once nothing on screen has them.</summary>
    private void ForgetOutputs(NotebookCellView view)
    {
        if (markdownPictures.Remove(view, out var written))
            ReleaseNotebookPictures(written);
        if (!cellPictures.Remove(view, out var pictures))
            return;
        ReleaseNotebookPictures(pictures);
    }

    // Helper method to free a picture an output had, on the thread that draws and a few frames later, the way a closed
    // picture's tab is (see FreePicture), when no other output on screen shows it
    private void FreeOutputPicture(Texture picture)
    {
        if (cellPictures.Values.Any(shown => shown.Exists(output => ReferenceEquals(output.Texture, picture))))
            return;
        if (markdownPictures.Values.Any(shown => shown.Exists(output => ReferenceEquals(output.Texture, picture))))
            return;
        if (previewPictures.Any(output => ReferenceEquals(output.Texture, picture))) return;

        foreach (var (data, kept) in outputPictures.ToList())
        {
            if (ReferenceEquals(kept, picture))
                outputPictures.Remove(data);
        }

        lock (picturesToFree)
            picturesToFree.Add((picture, FREE_AFTER_FRAMES));
        Engine.WindowManager.RequestExclusive();
    }
}

/// <summary>
/// A line with an output, the error over a traceback or the count of lines under a long one: what it says, then its
/// buttons, the words as wide as the buttons leave.
/// </summary>
internal sealed class NotebookOutputStrip : StackPanel
{
    public required Label Said { get; init; }

    /// <summary>Whether it says an error, its words in the skin's error colour.</summary>
    public bool Error { get; init; }

    public NotebookOutputStrip()
    {
        Direction = UIDirection.Horizontal;
        Anchor = Origin.TopLeft;
        Pivot = Origin.TopLeft;
        Spacing = 6.0f;
    }

    public void Fit(float width, float zoom)
    {
        float buttons = 0.0f;
        foreach (UIComponent child in Children)
        {
            if (child == Said || !child.Visible)
                continue;

            // A button sized to its words and icon, it says how wide it is only once it has been
            if (child is Button { Size.X: <= 0.0f } button)
                button.Size = button.Size with { X = 34.0f + button.Label.Length * button.LabelScale * 62.0f };
            buttons += child.Size.X + Spacing;
        }

        Said.Size = Said.Size with { X = MathF.Max(80.0f, width - buttons - 4.0f) };
        Size = Size with { X = width };
    }
}

/// <summary>Text an output put out, in an editor that can be selected and copied, an error's traceback tinted the error colour.</summary>
internal sealed class NotebookTextOutput(string text) : CodeEditor(text)
{
    public bool Error { get; init; }
}

/// <summary>A picture an output drew, and the size it came at, the most it's drawn.</summary>
internal sealed class NotebookPicture(Vector2 natural) : Image
{
    protected override bool ClipsChildren => true;
    public Vector2 Natural { get; set; } = natural;
    public Label? RenderStatus { get; set; }
    public string? RenderFallback { get; set; }
    public bool MathInk { get; set; }
    public Action<float, float>? Reflow { get; set; }
    public JsonNode? RenderKey { get; set; }

    /// <summary>The pixels a unit of the layout it's rendered at, or on its way to being rendered at.</summary>
    public float Density { get; set; }

    /// <summary>Renders it again when the screen it's on wants it at another density (a zoom, another monitor), see DawnScene.PictureDensity.</summary>
    public Action? KeepDense { get; set; }

    protected override void Paint(Horizon.UI.Drawing.UIDrawList list)
    {
        if (MathInk) Tint = list.Skin.TextColor;
        base.Paint(list);
    }
}
