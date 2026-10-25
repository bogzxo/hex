using System.Numerics;

using Horizon.Graphics;

namespace Hex.Dawn;

// Pictures, a .png or a .jpg opened from the explorer shown as the picture it is in a tab of its own, where it used to be
// asked about as a file of nothing but bytes and opened as nonsense. Fitted to the view, a small one (pixel art, a sprite)
// made bigger by whole steps so its pixels stay sharp, a big one made smaller and smoothed. Its size, its pixels and how
// big it's drawn in the corner. The tab is the picture's to look at, it's never saved.
internal sealed partial class DawnScene
{
    // The kinds of picture the engine reads (stb_image), by the end of their name
    private static readonly HashSet<string> PictureKinds = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".gif" };

    // Bigger than this either way and a picture is drawn smoothed, it's going to be shrunk to fit and nearest would shimmer
    private const int SMOOTH_FROM = 512;

    // The most a small picture is made bigger
    private const int MOST_ZOOM = 16;

    private Panel imageView = null!;
    private Image imagePicture = null!;
    private Label imageInfo = null!;

    /// <summary>Whether a file is one of the pictures that's shown rather than opened as text.</summary>
    internal static bool IsPicture(string path) => PictureKinds.Contains(Path.GetExtension(path));

    // Pictures asked for before there was a GPU to put them on (Hex started with one to open), opened once there is
    private readonly List<string> picturesWaiting = [];

    // Pictures whose tabs were closed, freed on the thread that draws a few frames later, frames are drawn from the last
    // two snapshots and one of them can still have the picture in it
    private readonly List<(Texture Picture, int Frames)> picturesToFree = [];
    private const int FREE_AFTER_FRAMES = 4;

    private void BuildImageView()
    {
        imageView = layout.Get<Panel>("image_view");
        imagePicture = layout.Get<Image>("image_picture");
        imageInfo = layout.Get<Label>("image_info");
        documentClosed += FreePicture;
        Engine.WindowManager.Exclusive += _ => FreeClosedPictures();
    }

    /// <summary>
    /// A closed picture's texture let go of when no other tab shows it, a big one is hundreds of megabytes and it stayed
    /// for as long as Hex did. Only the thread that draws may free it, so it's asked for there.
    /// </summary>
    private void FreePicture(HexDocument closed)
    {
        if (closed.Picture is not { } picture || documents.Any(open => ReferenceEquals(open.Picture, picture)))
            return;

        if (ReferenceEquals(imagePicture.Texture, picture))
            imagePicture.Texture = null;
        closed.Picture = null;
        lock (picturesToFree)
            picturesToFree.Add((picture, FREE_AFTER_FRAMES));
        Engine.WindowManager.RequestExclusive();
    }

    private void FreeClosedPictures()
    {
        lock (picturesToFree)
        {
            for (int i = picturesToFree.Count - 1; i >= 0; i--)
            {
                var (picture, frames) = picturesToFree[i];
                if (frames > 0)
                {
                    picturesToFree[i] = (picture, frames - 1);
                    continue;
                }

                picture.Dispose();
                picturesToFree.RemoveAt(i);
            }

            if (picturesToFree.Count > 0)
                Engine.WindowManager.RequestExclusive();
        }
    }

    /// <summary>Opens a picture in a tab of its own, or shows the one it has open already. False when it couldn't be read.</summary>
    private bool OpenPicture(string path)
    {
        // Too early, there's nowhere to put a picture before the GPU is made, so it waits for it
        if (Engine.WindowManager.Graphics is null)
        {
            picturesWaiting.Add(path);
            return true;
        }

        Texture picture = Texture.Load(path, smooth: false);
        if (!picture.IsValid)
        {
            Say(Said("images.not_read", ("name", Path.GetFileName(path))), error: true);
            return false;
        }

        if (picture.Width > SMOOTH_FROM || picture.Height > SMOOTH_FROM)
            picture = Texture.Load(path, smooth: true);

        // The empty layout Hex started with goes, the same as for a file of code
        if (document is { Path: null, IsText: false } && !document.Module.Root.Children.Any() && !document.Modified)
        {
            stage.RemoveModule(document.Module);
            documents.Remove(document);
        }

        var opened = new HexDocument(Path.GetFileNameWithoutExtension(path), stage.CreateModule())
        {
            IsText = true,
            Path = path,
            Picture = picture,
            FallbackDirectory = browseDirectory
        };

        opened.Module.Interactive = false;
        PrepareEditor(opened);
        opened.Editor.ReadOnly = true;
        opened.Editor.Highlighter = null;
        opened.SavedCode = opened.Editor.Text;

        documents.Add(opened);
        Show(opened);
        Remember(path);

        long bytes = new FileInfo(path).Length;
        Say(Said("images.opened", ("name", Path.GetFileName(path)), ("width", picture.Width.ToString()), ("height", picture.Height.ToString())));
        Console.WriteLine($"[Hex] Opened '{path}' as a picture, {picture.Width} by {picture.Height}, {bytes / 1024} KB.");
        return true;
    }

    /// <summary>Every tick a picture is on screen, it's fitted to the view and its size said in the corner.</summary>
    private void UpdateImageView()
    {
        if (picturesWaiting.Count > 0 && Engine.WindowManager.Graphics is not null)
        {
            string[] waiting = [.. picturesWaiting];
            picturesWaiting.Clear();
            foreach (string path in waiting)
                OpenPicture(path);
        }

        if (document.Picture is not { } picture || !imageView.Visible)
            return;

        if (!ReferenceEquals(imagePicture.Texture, picture))
            imagePicture.Texture = picture;

        UIRect room = imageView.Bounds.Shrink(new UIEdges(24.0f));
        if (room.IsEmpty)
            return;

        float width = picture.Width, height = picture.Height;
        float fit = MathF.Min(room.Width / width, room.Height / height);

        // Smaller than the room, it's made bigger by a whole number of times so every pixel is a square of the same size
        float zoom = fit >= 1.0f ? MathF.Min(MOST_ZOOM, MathF.Floor(fit)) : fit;
        var size = new Vector2(MathF.Round(width * zoom), MathF.Round(height * zoom));
        if (imagePicture.Size != size)
            imagePicture.Size = size;

        string info = Said("images.info", ("width", picture.Width.ToString()), ("height", picture.Height.ToString()), ("zoom", (zoom * 100.0f).ToString("0")));
        if (imageInfo.Text != info)
            imageInfo.Text = info;
    }
}
