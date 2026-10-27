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

    // The pictures of the outputs on screen, by the data they came out of, kept while it's the same data
    private readonly ConditionalWeakTable<JsonNode, Texture> outputPictures = new();
    private readonly Dictionary<NotebookCellView, List<NotebookPicture>> cellPictures = [];

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
            if (output["text"] is JsonValue text && text.TryGetValue(out string? said))
                hash.Add(said.Length);
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
            if (DrawOutput(notebook, output, now) is { } drawn)
                view.Outputs.Add(drawn);
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
        foreach (Texture gone in was.Select(picture => picture.Texture).OfType<Texture>().Distinct())
            FreeOutputPicture(gone);
        view.Outputs.Visible = view.Outputs.Children.Count > 0;
    }

    // Helper method for one output, null for one with nothing Hex can show
    private UIComponent? DrawOutput(HexNotebook notebook, JsonObject output, List<NotebookPicture> pictures)
    {
        string type = output["output_type"]?.GetValue<string>() ?? string.Empty;
        switch (type)
        {
            case "stream":
                return OutputText(NotebookFile.Text(output["text"]), output["name"]?.GetValue<string>() == "stderr");

            case "error":
            {
                string traceback = output["traceback"] is JsonArray lines ? string.Join('\n', lines.Select(line => line?.GetValue<string>() ?? string.Empty)) : string.Empty;
                if (traceback.Length == 0)
                    traceback = $"{output["ename"]?.GetValue<string>()}: {output["evalue"]?.GetValue<string>()}";
                return OutputText(traceback, error: true);
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
        foreach (string mime in (string[])["image/png", "image/jpeg", "image/gif", "image/bmp"])
        {
            if (data[mime] is JsonValue value && value.TryGetValue(out string? encoded) && OutputPicture(data[mime]!, encoded, metadata?[mime] as JsonObject, pictures) is { } picture)
                return picture;
        }

        // A DataFrame's HTML is drawn as a table rather than its text/plain's columns of spaces
        if (data["text/html"] is { } html && OutputHtmlTable(NotebookFile.Text(html)) is { } table)
            return table;

        if (data["text/markdown"] is { } markdown)
            return OutputMarkdown(notebook, NotebookFile.Text(markdown));

        // Other HTML and JSON only when the text is no more than the object's name, IPython's HTML and JSON and a
        // class with only a _repr_html_ said "<IPython.core.display.HTML object>" and nothing else
        string? plain = data["text/plain"] is { } said ? NotebookFile.Text(said) : null;
        if (plain is null || NotebookHtml.IsBareRepr(plain))
        {
            if (data["text/html"] is { } other)
                return OutputMarkdown(notebook, NotebookHtml.ToMarkdown(NotebookFile.Text(other)));
            if (data["application/json"] is { } json)
                return OutputText(json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), error: false);
        }

        if (plain is not null)
            return OutputText(plain, error: false);

        if (data["text/latex"] is { } latex)
            return OutputText(NotebookFile.Text(latex), error: false);

        return null;
    }

    // Helper method for markdown written out, its tables drawn as tables
    private StackPanel OutputMarkdown(HexNotebook notebook, string markdown)
    {
        var written = new StackPanel { Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = 4.0f };
        WriteMarkdown(written, markdown, notebook.Model.Language);
        return written;
    }

    // Helper method for text in the code's font, its ANSI colours drawn in the terminal's
    private CodeEditor OutputText(string text, bool error)
    {
        if (text.Length > OUTPUT_MOST)
            text = text[..OUTPUT_MOST];
        if (text.EndsWith('\n'))
            text = text[..^1];

        var (plain, colours) = AnsiText.Read(text);
        var editor = new CodeEditor(plain)
        {
            ReadOnly = true,
            LineNumbers = false,
            Minimap = false,
            StickyScroll = false,
            CheckSyntax = false,
            FitRows = OUTPUT_ROWS,
            IndentGuides = false,
            WordWrap = true,
            LightOccurrences = false,
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft,
            TextScale = CODE_TEXT * settings.CodeZoom,
            Font = codeEditing.codeFont,
            Padding = new UIEdges(6.0f, 2.0f, 6.0f, 2.0f),
            Highlighter = PlainHighlighter.Instance,
            Color = error && compositor.Skin is { } skin ? skin.ErrorColor with { W = 0.08f } : new Vector4(0.0f, 0.0f, 0.0f, 0.0f)
        };
        if (compositor.Skin is { } palette)
            editor.AnsiColours = TerminalPalette.FromPalette(palette.Palette).Ansi;
        editor.SemanticSpans = colours;
        return editor;
    }

    // Helper method for a picture out of its base64, decoded once for as long as it's the same data. Its texture is made
    // later while nothing draws, the picture shows it from then on
    private NotebookPicture? OutputPicture(JsonNode data, string encoded, JsonObject? size, List<NotebookPicture> pictures)
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
                        byte[] bytes = Convert.FromBase64String(encoded.Replace("\n", string.Empty, StringComparison.Ordinal));
                        waiting = (ImageFile.Load(new MemoryStream(bytes)), []);
                    }
                    catch (Exception e) when (e is FormatException or InvalidOperationException or IOException or ArgumentException)
                    {
                        return null;
                    }

                    picturesToMake[data] = waiting;
                    Engine.WindowManager.RequestExclusive();
                }
            }

            (natural, tallness) = (waiting.Pixels.Width, waiting.Pixels.Height);
        }

        float width = size?["width"] is JsonValue w && w.TryGetValue(out float wanted) ? wanted : natural;
        float height = size?["height"] is JsonValue h && h.TryGetValue(out float tall) ? tall : tallness * (width / MathF.Max(1.0f, natural));
        var picture = new NotebookPicture(new Vector2(width, height)) { Texture = made, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(width, height) };
        if (made is null)
        {
            lock (picturesToMake)
                waiting.Waiting.Add(picture);
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
            Texture texture = Texture.FromPixels((uint)pixels.Width, (uint)pixels.Height, pixels.Pixels);
            outputPictures.AddOrUpdate(data, texture);
            foreach (NotebookPicture picture in waiting)
                picture.Texture = texture;

            // Its cell ran again before it was made, nothing shows it
            FreeOutputPicture(texture);
        }
    }

    // Helper method for the outputs as wide as the cell, a picture no wider than it came or than there's room for
    private static void FitOutputs(NotebookCellView view, float width)
    {
        foreach (UIComponent output in view.Outputs.Children)
        {
            switch (output)
            {
                case NotebookPicture picture:
                    float scale = MathF.Min(1.0f, width / MathF.Max(1.0f, picture.Natural.X));
                    picture.Size = picture.Natural * scale;
                    break;
                case Label label:
                    label.Size = label.Size with { X = width - 8.0f };
                    break;
                case StackPanel written:
                    FitWritten(written, width);
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
        if (!cellPictures.Remove(view, out var pictures))
            return;
        foreach (Texture picture in pictures.Select(shown => shown.Texture).OfType<Texture>().Distinct())
            FreeOutputPicture(picture);
    }

    // Helper method to free a picture an output had, on the thread that draws and a few frames later, the way a closed
    // picture's tab is (see FreePicture), when no other output on screen shows it
    private void FreeOutputPicture(Texture picture)
    {
        if (cellPictures.Values.Any(shown => shown.Exists(output => ReferenceEquals(output.Texture, picture))))
            return;

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

/// <summary>A picture an output drew, and the size it came at, the most it's drawn.</summary>
internal sealed class NotebookPicture(Vector2 natural) : Image
{
    public Vector2 Natural { get; } = natural;
}
