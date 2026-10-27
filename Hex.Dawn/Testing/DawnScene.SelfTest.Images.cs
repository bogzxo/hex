namespace Hex.Dawn;

// The self test, a picture opened as the picture it is. In a tab of its own in place of the code, fitted to the view,
// its size in the corner, and a save that leaves the file as it was.
internal sealed partial class DawnScene
{
    private void TestPictures(HexPointerScript test)
    {
        string picture = Path.Combine(testFolder, "picture.png");
        byte[] before = [];

        test.Run(() =>
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "hex", "images", "background_icon.png"), picture, overwrite: true);
            before = File.ReadAllBytes(picture);
            Open(picture);
        });
        test.Wait();
        test.Check("pictures: a .png opens as the picture in a tab of its own, not as bytes", () =>
            document.Picture is { IsValid: true } && imageView.Visible && !codeScroll.Visible && chrome.Dialog is not { IsOpen: true });
        test.Settle(0.3f);
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "picture.png"));
        });
        test.Settle(0.3f);
        test.Check("pictures: fitted to the view with its size in the corner", () =>
            imagePicture.Texture == document.Picture && imagePicture.Size.X > 0 && imagePicture.Size.X <= imageView.Bounds.Width
            && imageInfo.Text.Contains($"{document.Picture!.Width} × {document.Picture.Height}", StringComparison.Ordinal));

        test.Run(Save);
        test.Wait();
        test.Check("pictures: a save leaves the file as it was", () => File.ReadAllBytes(picture).AsSpan().SequenceEqual(before));

        Horizon.Graphics.Texture? shown = null;
        test.Run(() =>
        {
            shown = document.Picture;
            if (document.Picture is not null)
                CloseNow();
        });
        test.WaitFor(() => shown is { IsValid: false }, 3.0f);
        test.Check("pictures: and closing it goes back to code and lets go of the picture", () => document.Picture is null && shown is { IsValid: false });
        // Quick open of the folder the picture is in, what the workspace is by now is up to the parts before this one
        test.Check("pictures: quick open lists pictures as well as text", () => HexSearch.OpenableFilesUnder([testFolder], IsPicture).Any(file => PlatformPaths.Same(file, picture)));
    }
}
