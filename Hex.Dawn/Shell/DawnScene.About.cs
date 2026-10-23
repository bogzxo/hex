using System.Numerics;
using System.Reflection;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// About Hex, the way a Mac's About window is. The icon, the name big, the version under it and what it runs on, then the
// licences in small print. It was a page of markdown in the help window, with a third of it empty under the text.
internal sealed partial class DawnScene
{
    private const float ABOUT_WIDTH = 440.0f, ABOUT_HEIGHT = 296.0f, ABOUT_ICON = 72.0f, ABOUT_INSET = 28.0f;

    // How big the name and the small print are, as a share of the skin's text
    private const float ABOUT_NAME = 2.0f, ABOUT_SMALL = 0.85f;

    private void ShowAbout()
    {
        ReadStrings();
        UIComponent root = chrome.Root;
        float text = compositor.Skin?.TextScale ?? 0.13f;
        Vector4 quiet = compositor.Skin?.DimTextColor ?? new Vector4(1.0f, 1.0f, 1.0f, 0.6f);
        float inner = ABOUT_WIDTH - ABOUT_INSET * 2.0f;

        var window = new WindowPanel(Said("about.title"))
        {
            Icon = "info",
            Size = new Vector2(ABOUT_WIDTH, ABOUT_HEIGHT),
            MinSize = new Vector2(ABOUT_WIDTH, ABOUT_HEIGHT),
            ResizeGrip = false
        };
        DressWindow(window);
        window.OnClosed = () => window.Parent?.Remove(window);

        var column = window.Add(new StackPanel { Spacing = 6.0f, Padding = new UIEdges(ABOUT_INSET, 18.0f), Anchor = Origin.Top, Pivot = Origin.Top });
        column.Add(new Image { File = Hex.Shell.HexIdentity.ICON, Size = new Vector2(ABOUT_ICON, ABOUT_ICON), Anchor = Origin.Top, Pivot = Origin.Top });
        column.Add(new Label { Text = "**Hex**", Markdown = true, TextScale = text * ABOUT_NAME, Size = new Vector2(inner, 0.0f) });
        column.Add(new Label
        {
            Text = Said("about.version", ("version", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?")),
            Size = new Vector2(inner, 0.0f)
        });
        column.Add(new Label
        {
            Text = Said("about.runs_on", ("engine", typeof(UICompositor).Assembly.GetName().Version?.ToString(3) ?? "?"), ("dotnet", Environment.Version.ToString())),
            Color = quiet,
            Size = new Vector2(inner, 0.0f)
        });
        column.Add(new Label { Text = Said("about.tagline"), Wrap = true, Color = quiet, Size = new Vector2(inner, 0.0f), Padding = new UIEdges(0.0f, 6.0f, 0.0f, 6.0f) });
        column.Add(new Label { Text = Said("about.licences"), Wrap = true, Color = quiet, TextScale = text * ABOUT_SMALL, Size = new Vector2(inner, 0.0f) });

        root.Add(window);
        window.TakeKeyboard();
    }
}
