using System.Numerics;
using System.Text.RegularExpressions;

using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Pictures in the agents' chat, seen where they are and full screen. A picture pasted into what was asked shows under
// the bubble, and one an agent links in what it says (Markdown's ![..](file) of a picture on the disk) under its words,
// each a small one at most THUMB_WIDE by THUMB_TALL, a click opening it over the whole window to zoom and drag
// (HexPictureView). The small ones' textures are kept for the last few pictures seen and the rest let go of, a long
// chat of screenshots never holds them all.
internal sealed partial class DawnScene
{
    // The most a picture in the chat is drawn at, and how many pictures' textures are kept at once
    private const float THUMB_WIDE = 240.0f, THUMB_TALL = 150.0f;
    private const int PICTURES_KEPT = 24;

    // The pictures of the chat loaded, by their path, the newest seen last
    private readonly Dictionary<string, Texture> chatPictures = new(PlatformPaths.Comparer);
    private readonly List<string> chatPicturesOrder = [];

    [GeneratedRegex(@"\[Pasted image: (?<path>[^\]\r\n]+)\]")]
    private static partial Regex PastedPicture();

    [GeneratedRegex(@"!\[[^\]]*\]\((?<path>[^)\s]+)\)")]
    private static partial Regex LinkedPicture();

    /// <summary>The pictures a row says, pasted ones by their path at the end of what was asked or linked ones in what was said.</summary>
    private static List<string> PicturesIn(string text, bool asked)
    {
        var found = new List<string>();
        if (text.Length == 0 || (!text.Contains("[Pasted image: ", StringComparison.Ordinal) && !text.Contains("![", StringComparison.Ordinal)))
            return found;
        foreach (Match match in (asked ? PastedPicture() : LinkedPicture()).Matches(text))
        {
            string path = match.Groups["path"].Value.Trim();
            if (path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                path = Uri.UnescapeDataString(path[8..]);
            if (IsPicture(path) && Path.IsPathRooted(path) && !found.Contains(path))
                found.Add(path);
        }

        return found;
    }

    // A picture's texture for the chat, loaded the first time and kept among the last few, null when it can't be read
    private Texture? ChatPicture(string path)
    {
        if (chatPictures.TryGetValue(path, out Texture? kept))
        {
            chatPicturesOrder.Remove(path);
            chatPicturesOrder.Add(path);
            return kept;
        }

        if (Engine.WindowManager.Graphics is null || !File.Exists(path))
            return null;
        Texture picture = Texture.Load(path, smooth: true);
        if (!picture.IsValid)
            return null;

        chatPictures[path] = picture;
        chatPicturesOrder.Add(path);
        // Past the most kept, the oldest let go of on the thread that draws, a few frames on as a closed tab's is
        while (chatPicturesOrder.Count > PICTURES_KEPT)
        {
            string oldest = chatPicturesOrder[0];
            chatPicturesOrder.RemoveAt(0);
            if (chatPictures.Remove(oldest, out Texture? gone))
            {
                lock (picturesToFree)
                    picturesToFree.Add((gone, FREE_AFTER_FRAMES));
                Engine.WindowManager.RequestExclusive();
            }
        }

        return picture;
    }

    /// <summary>
    /// The pictures a row has, a small one each in a row of them, a click on one opening it full screen. Null for a row
    /// with none, or none that can be read.
    /// </summary>
    private UIComponent? PictureStrip(IReadOnlyList<string> paths, Origin side)
    {
        if (paths.Count == 0)
            return null;

        var strip = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Anchor = side, Pivot = side, Padding = new UIEdges(0, 4, 0, 0) };
        foreach (string path in paths)
        {
            if (ChatPicture(path) is not { } picture)
                continue;
            float fit = MathF.Min(1.0f, MathF.Min(THUMB_WIDE / picture.Width, THUMB_TALL / picture.Height));
            var size = new Vector2(MathF.Round(picture.Width * fit), MathF.Round(picture.Height * fit));
            var frame = strip.Add(new Panel { Size = size, Radius = 6, Color = Vector4.Zero, Outline = Skin?.BorderColor ?? Vector4.Zero, Tooltip = path });
            frame.Add(new Image { Texture = picture, Size = size, Anchor = Origin.Center, Pivot = Origin.Center });
            // Over the picture, the whole of it a button that opens it full screen
            string shown = path;
            frame.Add(new Button { Style = "button_flat", Fill = UIFill.Both, Label = string.Empty, Animated = false, Tooltip = Said("agents_panel.picture_open"), OnPressed = () => ShowPictureFull(shown) });
        }

        return strip.Children.Count == 0 ? null : strip;
    }

    // The picture full screen, while it's up, and its line of how far it's zoomed
    private Panel? pictureFull;

    /// <summary>A picture over the whole window to zoom and drag, Escape or its cross putting it away.</summary>
    internal void ShowPictureFull(string path)
    {
        pictureFull?.Parent?.Remove(pictureFull);
        if (Engine.WindowManager.Graphics is null)
            return;
        // The chat's own texture of it, kept and let go of by the chat. Loaded again here it was the same texture the small
        // one draws, and freeing it as this closed left the small one white
        if (ChatPicture(path) is not { } picture)
        {
            Say(Said("images.not_read", ("name", Path.GetFileName(path))), error: true);
            return;
        }

        var overlay = chrome.Root.Add(new Panel { Fill = UIFill.Both, Color = new Vector4(0.02f, 0.02f, 0.03f, 0.92f) });
        pictureFull = overlay;
        var view = overlay.Add(new HexPictureView { Fill = UIFill.Both, Picture = picture, Padding = new UIEdges(24, 56, 24, 24) });
        var bar = overlay.Add(new StackPanel
        {
            Direction = UIDirection.Horizontal, Spacing = 6, Anchor = Origin.Top, Pivot = Origin.Top, Position = new Vector2(0, -12), Size = new Vector2(0, 30),
            Padding = new UIEdges(10, 3, 10, 3), Radius = 8, Color = Skin?.Palette.Raised ?? new Vector4(0.15f, 0.15f, 0.18f, 1.0f)
        });
        var name = bar.Add(new Label { Text = UIRichText.Literal($"{Path.GetFileName(path)}  ·  {picture.Width} × {picture.Height}"), TextScale = 0.13f, Anchor = Origin.Left, Pivot = Origin.Left });
        var zoom = bar.Add(new Label { TextScale = 0.13f, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(58, 0), Align = Origin.Center });
        void SayZoom() => zoom.Text = $"{view.Shown * 100:0}%";
        view.OnZoomed = SayZoom;

        void Close()
        {
            overlay.Parent?.Remove(overlay);
            if (ReferenceEquals(pictureFull, overlay))
                pictureFull = null;
        }

        Button Tool(string icon, string tip, Action pressed) =>
            bar.Add(new Button { Icon = icon, Style = "button_flat", Size = new Vector2(28, 24), Animated = false, Tooltip = tip, Anchor = Origin.Left, Pivot = Origin.Left, OnPressed = pressed });
        Tool("zoom-out", Said("agents_panel.picture_zoom_out"), () => view.ZoomBy(0.8f));
        Tool("zoom-in", Said("agents_panel.picture_zoom_in"), () => view.ZoomBy(1.25f));
        Tool("screen-normal", Said("agents_panel.picture_fit"), view.Fit);
        Tool("screen-full", Said("agents_panel.picture_actual"), view.Actual);
        Tool("go-to-file", Said("agents_panel.picture_tab"), () =>
        {
            Close();
            ShowFace(Hex.Shell.HexFace.Dawn);
            Open(path);
        });
        Tool("close", Said("agents_panel.picture_close"), Close);
        view.OnClose = Close;
        name.Size = new Vector2(MathF.Min(420, (Skin?.Font.Measure(name.Text, name.TextScale).X ?? 300) + 8), 0);
        bar.Size = new Vector2(name.Size.X + 58 + 6 * 34 + 40, 30);
        view.Focus();
        // The zoom said once it's laid out and its fitted size is known
        view.OnZoomed?.Invoke();
        Engine.WindowManager.RequestFrames();
    }
}
