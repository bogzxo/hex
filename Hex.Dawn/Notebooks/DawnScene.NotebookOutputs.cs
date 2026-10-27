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
// written out. Of the forms a result comes in the one Hex shows best is shown, HTML and widgets fall back to the rest
internal sealed partial class DawnScene
{
    // How many rows of text an output shows before it scrolls, and the most of its text that's put in an editor at all
    private const int OUTPUT_ROWS = 30;
    private const int OUTPUT_MOST = 200_000;

    // The pictures of the outputs on screen, by the data they came out of, kept while it's the same data
    private readonly ConditionalWeakTable<JsonNode, Texture> outputPictures = new();
    private readonly Dictionary<NotebookCellView, List<Texture>> cellPictures = [];

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
        List<Texture> was = cellPictures.TryGetValue(view, out var kept) ? kept : [];
        var now = new List<Texture>();
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
                Anchor = Origin.TopLeft,
                Pivot = Origin.TopLeft,
                Size = new Vector2(360.0f, 28.0f)
            });
            box.OnSubmitted = typed => AnswerInput(notebook, typed);
            box.Focus();
        }

        foreach (Texture gone in was.Except(now))
            FreeOutputPicture(gone);
        cellPictures[view] = now;
        view.Outputs.Visible = view.Outputs.Children.Count > 0;
    }

    // Helper method for one output, null for one with nothing Hex can show
    private UIComponent? DrawOutput(HexNotebook notebook, JsonObject output, List<Texture> pictures)
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

    // Helper method for the best of the forms a result came in, a picture, markdown, then text
    private UIComponent? OutputData(HexNotebook notebook, JsonObject data, JsonObject? metadata, List<Texture> pictures)
    {
        foreach (string mime in (string[])["image/png", "image/jpeg", "image/gif", "image/bmp"])
        {
            if (data[mime] is JsonValue value && value.TryGetValue(out string? encoded) && OutputPicture(data[mime]!, encoded, metadata?[mime] as JsonObject, pictures) is { } picture)
                return picture;
        }

        if (data["text/markdown"] is { } markdown)
            return OutputMarkdown(NotebookFile.Text(markdown), notebook.Model.Language);

        if (data["text/plain"] is { } plain)
            return OutputText(NotebookFile.Text(plain), error: false);

        if (data["text/latex"] is { } latex)
            return OutputText(NotebookFile.Text(latex), error: false);

        return null;
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

    private Label OutputMarkdown(string markdown, string language) => new()
    {
        Text = markdown,
        Markdown = true,
        Wrap = true,
        Align = Origin.TopLeft,
        Anchor = Origin.TopLeft,
        Pivot = Origin.TopLeft,
        TextScale = 0.16f,
        CodeLanguage = language
    };

    // Helper method for a picture out of its base64, decoded once for as long as it's the same data
    private Image? OutputPicture(JsonNode data, string encoded, JsonObject? size, List<Texture> pictures)
    {
        if (Engine.WindowManager.Graphics is null)
            return null;

        if (!outputPictures.TryGetValue(data, out Texture? picture))
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(encoded.Replace("\n", string.Empty, StringComparison.Ordinal));
                ImagePixels pixels = ImageFile.Load(new MemoryStream(bytes));
                picture = Texture.FromPixels((uint)pixels.Width, (uint)pixels.Height, pixels.Pixels);
                outputPictures.AddOrUpdate(data, picture);
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or IOException or ArgumentException)
            {
                return null;
            }
        }

        pictures.Add(picture);
        float width = size?["width"] is JsonValue w && w.TryGetValue(out float wanted) ? wanted : picture.Width;
        float height = size?["height"] is JsonValue h && h.TryGetValue(out float tall) ? tall : picture.Height * (width / MathF.Max(1.0f, picture.Width));
        return new NotebookPicture(new Vector2(width, height)) { Texture = picture, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(width, height) };
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
        foreach (Texture picture in pictures)
            FreeOutputPicture(picture);
    }

    // Helper method to free a picture an output had, on the thread that draws and a few frames later, the way a closed
    // picture's tab is (see FreePicture), when no other output on screen shows it
    private void FreeOutputPicture(Texture picture)
    {
        if (cellPictures.Values.Any(shown => shown.Contains(picture)))
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
