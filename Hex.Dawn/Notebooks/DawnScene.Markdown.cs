using System.Numerics;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private Panel markdownPreview = null!;
    private NotebookScrollPanel markdownScroll = null!;
    private MarkdownPagePanel markdownWritten = null!;
    private const float PAGE_WIDTH = 800, PAGE_MARGIN = 24, PAGE_INNER = 48;
    private List<NotebookPicture> previewPictures = [];
    private HexDocument? markdownDocument;
    private int markdownVersion = -1;
    private float markdownDelay;

    internal static bool IsMarkdownFile(string? path) => path is not null && Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown";
    private static bool IsMarkdown(HexDocument file) => file.IsText && file.Picture is null && file.DiffOf is null && IsMarkdownFile(file.Path);
    private bool MarkdownShown => markdownPreview is { Visible: true };

    private void BuildMarkdownPreview()
    {
        markdownPreview = layout.Get<Panel>("markdown_preview");
        markdownPreview.Visible = false;
        markdownScroll = markdownPreview.Add(new NotebookScrollPanel
        {
            Anchor = Origin.TopLeft, Pivot = Origin.TopLeft,
            Zoom = delta => ZoomCode(delta > 0 ? CODE_ZOOM_STEP : 1 / CODE_ZOOM_STEP)
        });
        markdownWritten = markdownScroll.Add(new MarkdownPagePanel
        {
            Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft,
            Padding = new UIEdges(PAGE_INNER, 16 + 36, PAGE_INNER, 16 + 48), Spacing = 6
        });
        layout.Get<Button>("markdown_button").OnPressed = () => SwitchView(HexView.Markdown);
    }

    private void ApplyMarkdownPreview()
    {
        bool show = document.View == HexView.Markdown && IsMarkdown(document);
        markdownPreview.Visible = show;
        layout.Get<Button>("markdown_button").Visible = IsMarkdown(document);
        layout.Get<Button>("markdown_button").Selected = show;
        if (!show || markdownDocument != document)
        {
            var old = previewPictures;
            previewPictures = [];
            ReleaseNotebookPictures(old);
            foreach (var child in markdownWritten.Children.ToList()) markdownWritten.Remove(child);
            markdownDocument = null;
            markdownVersion = -1;
        }
        if (show && markdownDocument is null)
        {
            markdownDocument = document;
            markdownScroll.Offset = 0;
            markdownDelay = 0;
        }
    }

    private void UpdateMarkdownPreview(float dt)
    {
        if (!MarkdownShown || markdownDocument != document) return;
        int version = document.Editor.Document.Version;
        if (markdownVersion != version)
        {
            if ((markdownDelay -= dt) > 0) return;
            var old = previewPictures;
            previewPictures = [];
            WriteMarkdown(markdownWritten, document.Editor.Text, "markdown", previewPictures);

            // A preview reads like the page it will be, no buttons over its tables, a right click has them
            foreach (var tools in markdownWritten.Children.OfType<NotebookTablePanel>())
                tools.Strip.Visible = false;
            ReleaseNotebookPictures(old);
            markdownVersion = version;
            markdownDelay = 0.15f;
        }
        // The view is the desk, the page the field colour on it; the scroll panel spans the whole view so the wheel works anywhere
        markdownPreview.Color = compositor.Skin?.PanelColor;
        markdownWritten.Color = compositor.Skin?.FieldColor;
        markdownScroll.Size = markdownPreview.Size;
        float room = MathF.Max(120, markdownPreview.Size.X - markdownScroll.BarWidth - markdownScroll.BarGap);
        float page = MathF.Min(PAGE_WIDTH * settings.CodeZoom, MathF.Max(120, room - PAGE_MARGIN * 2));
        float margin = MathF.Max(0, (room - page) / 2);
        float inner = MathF.Min(PAGE_INNER, page * 0.08f);
        markdownWritten.Margin = margin;
        markdownWritten.Padding = new UIEdges(margin + inner, markdownWritten.Padding.Top, margin + inner, markdownWritten.Padding.Bottom);
        FitWritten(markdownWritten, MathF.Max(80, page - inner * 2 - 14), settings.CodeZoom, keepWidth: true);
    }
}
