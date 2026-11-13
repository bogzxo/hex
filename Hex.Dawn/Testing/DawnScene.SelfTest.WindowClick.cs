using System.Numerics;

namespace Hex.Dawn;

// A press on a window of the editor's (the settings) leaves it where it is: it doesn't jump, give or come in again, and
// the pointer isn't moved. Watched every tick from the press to after the release.
internal sealed partial class DawnScene
{
    private void TestWindowClick(HexPointerScript test)
    {
        Vector2 centreWas = default, sizeWas = default;
        float moved = 0.0f, offset = 0.0f, scaled = 0.0f, faded = 0.0f;
        bool watching = false;

        void Watch()
        {
            if (!watching || SettingsWindow is not { } window)
                return;
            moved = MathF.Max(moved, Vector2.Distance(window.Bounds.Center, centreWas) + Vector2.Distance(window.Bounds.Size, sizeWas));
            offset = MathF.Max(offset, window.VisualOffset.Length());
            scaled = MathF.Max(scaled, MathF.Abs(window.VisualScale.X - 1.0f) + MathF.Abs(window.VisualScale.Y - 1.0f));
            faded = MathF.Max(faded, 1.0f - window.Opacity);
        }

        test.Run(() => OpenSettings(null));
        test.Settle(0.8f);
        test.Check("window click: the settings are up and settled", () => SettingsWindow is { Visible: true, Opacity: 1.0f });

        // Twice: on the page (nothing of the window's own under the pointer) and on its title, which is what it's moved by
        foreach (bool onTitle in (bool[])[false, true])
        {
            Vector2 At() => SettingsWindow is { } window
                ? chrome.ToWorld(onTitle ? window.TitleBounds.Center : new Vector2(window.Bounds.Center.X, window.Bounds.Min.Y + 6.0f))
                : Vector2.Zero;

            test.Run(() =>
            {
                centreWas = SettingsWindow!.Bounds.Center;
                sizeWas = SettingsWindow.Bounds.Size;
                moved = offset = scaled = faded = 0.0f;
                watching = true;
            });
            test.Press(At);
            test.WaitFor(() => { Watch(); return false; }, 0.3f);
            test.Release(At);
            test.WaitFor(() => { Watch(); return false; }, 0.4f);
            test.Run(() =>
            {
                watching = false;
                Console.WriteLine($"[Hex self-test] window click {(onTitle ? "title" : "page")}: moved {moved:0.00}, offset {offset:0.00}, scale {scaled:0.000}, faded {faded:0.00}");
            });
            string where = onTitle ? "on its title" : "on its page";
            test.Check($"window click: a press {where} leaves the window where it is", () => moved < 0.5f && offset < 0.5f && scaled < 0.001f && faded < 0.01f);
        }

        test.Run(CloseSettings);
        test.Settle(0.4f);
    }
}
