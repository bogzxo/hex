using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of the wheel and the bars over the settings window, up over a long file of code. The wheel over the
// window is the window's whatever part of it the pointer is on, and never goes through to the code behind. The code
// above and below the window gets its own, and both bars are dragged and clicked
internal sealed partial class DawnScene
{
    private void TestSettingsWheel(HexPointerScript test)
    {
        string longFile = Path.Combine(testFolder, "selftest_long.cs");
        HexDocument? before = null;
        Dictionary<string, bool> folded = [];

        SettingsPage Page() => SettingsPage!;
        ScrollPanel Rows() => Page().RowsScroll;
        ScrollPanel Nav() => Page().NavScroll;
        Func<Vector2> In(Func<UIRect> rect, float x, float y) => () => rect() is { IsEmpty: false } at
            ? chrome.ToWorld(new Vector2(at.Min.X + at.Width * x, at.Min.Y + at.Height * y))
            : default;

        test.Run(() =>
        {
            before = document;
            File.WriteAllLines(longFile, Enumerable.Range(0, 400).Select(line => $"// line {line} of a file long enough to scroll"));
            Open(longFile);
            SwitchView(HexView.Code);
            CloseSettings();
            OpenSettings(null);
            Page().Find(string.Empty);
        });
        test.WaitFor(() => SettingsPage is { } page && page.SectionsShown.Count > 0 && page.RowsScroll.MaxOffset > 0.0f, 3.0f);
        test.Settle(0.4f);

        // Turns the wheel down a couple of notches over a place, and keeps who took it
        UIComponent? took = null;
        void Wheel(Func<Vector2> where)
        {
            test.Run(() => Rows().Offset = 0.0f);
            test.Hover(where);
            test.Run(() => compositor.Scroll(-2));
            test.Wait();
            test.Run(() => took = compositor.WheelTakenBy);
            test.Settle(0.3f);
        }
        bool RowsWentDown() => Rows().Offset > 1.0f;
        bool NotTheCode() => took is not null && took is not CodeEditor;

        /* The wheel */

        Wheel(In(() => Rows().Bounds, 0.4f, 0.5f));
        test.Run(() => Shot("settings-wheel-rows"));
        test.Check("settings wheel: over the rows it scrolls them and the code behind the window doesn't get it", () =>
            took == Rows() && RowsWentDown());
        Wheel(() => chrome.ToWorld(Page().Search.Bounds.Center));
        test.Check("settings wheel: over the search, which can't scroll, it still scrolls the rows", () =>
            NotTheCode() && RowsWentDown());
        Wheel(In(() => SettingsWindow!.TitleBounds, 0.3f, 0.5f));
        test.Check("settings wheel: over the window's title bar it scrolls the rows too", () =>
            NotTheCode() && RowsWentDown());
        Wheel(() => Descendants(Rows()).OfType<ToggleButton>().FirstOrDefault(toggle => toggle.Visible && Rows().Bounds.Contains(toggle.Bounds.Center)) is { } toggle
            ? chrome.ToWorld(toggle.Bounds.Center) : default);
        test.Check("settings wheel: over a toggle of a row it scrolls the rows and leaves the toggle be", () =>
            took == Rows() && RowsWentDown());

        // The code under the window and past its bottom gets its own wheel, the rows ran on down there out of sight and took it
        Func<Vector2> belowWindow = () => SettingsWindow is { } window && window.Bounds.Min.Y - 40.0f > code.Bounds.Min.Y
            ? chrome.ToWorld(new Vector2(Rows().Bounds.Center.X, window.Bounds.Min.Y - 40.0f))
            : default;
        Wheel(belowWindow);
        test.Check("settings wheel: the code below the window gets the wheel there, not the rows cut off above it", () =>
            belowWindow() == default || (took is CodeEditor && Rows().Offset == 0.0f));

        /* The rows' bar */

        float grabbedAt = 0.0f;
        test.Run(() => Rows().Offset = 0.0f);
        test.Wait();
        test.Run(() => grabbedAt = Rows().ThumbBounds.Max.Y);
        test.Drag(In(() => Rows().ThumbBounds, 0.5f, 0.8f), () => chrome.ToWorld(new Vector2(Rows().TrackBounds.Center.X, grabbedAt - Rows().ThumbBounds.Height * 0.2f - 150.0f)));
        test.Wait();
        test.Run(() => Shot("settings-wheel-thumb"));
        test.Check("settings bar: the thumb dragged scrolls the rows, held where it was taken", () =>
            Rows().Offset > 0.0f && Near(Rows().ThumbBounds.Max.Y, grabbedAt - 150.0f, 2.0f));

        test.Run(() => Rows().Offset = 0.0f);
        test.Wait();
        test.Click(In(() => Rows().TrackBounds, 0.5f, 0.25f));
        test.Wait();
        test.Check("settings bar: a click on the track below the thumb jumps it there", () =>
            Rows().Offset > Rows().MaxOffset * 0.5f && Rows().ThumbBounds is var thumb
            && Near(thumb.Center.Y, Rows().TrackBounds.Min.Y + Rows().TrackBounds.Height * 0.25f, 2.0f));

        // In the gap just before the bar counts as the bar, a few pixels square on was a needle to thread
        test.Run(() => Rows().Offset = 0.0f);
        test.Wait();
        test.Click(() => Rows().TrackBounds is { IsEmpty: false } track
            ? chrome.ToWorld(new Vector2(track.Min.X - Rows().BarGap * 0.5f, track.Min.Y + track.Height * 0.25f))
            : default);
        test.Wait();
        test.Check("settings bar: a press just before the bar takes hold of it as well", () => Rows().Offset > Rows().MaxOffset * 0.5f);

        /* The sections' bar, every part open so there's more of them than fit */

        test.Run(() =>
        {
            Rows().Offset = 0.0f;
            foreach (string part in Hex.Shell.EditorSettingsCatalog.Parts)
            {
                folded[part] = Horizon.UI.Components.SettingsPage.IsPartCollapsed(part);
                Page().SetPartCollapsed(part, false);
            }
        });
        test.WaitFor(() => Nav().MaxOffset > 0.0f, 2.0f);
        test.Wait();
        Wheel(In(() => Nav().Bounds, 0.5f, 0.5f));
        test.Check("settings wheel: over the sections, when there's more of them than fit, it scrolls them and not the rows", () =>
            took == Nav() && Nav().Offset > 0.0f && Rows().Offset == 0.0f);

        test.Run(() => Nav().Offset = 0.0f);
        test.Wait();
        test.Run(() => grabbedAt = Nav().ThumbBounds.Max.Y);
        test.Drag(In(() => Nav().ThumbBounds, 0.5f, 0.5f), () => chrome.ToWorld(new Vector2(Nav().TrackBounds.Center.X, grabbedAt - Nav().ThumbBounds.Height * 0.5f - 60.0f)));
        test.Wait();
        test.Run(() => Shot("settings-wheel-nav-thumb"));
        test.Check("settings bar: the sections' thumb dragged scrolls them", () =>
            Nav().Offset > 0.0f && Near(Nav().ThumbBounds.Max.Y, grabbedAt - 60.0f, 2.0f));

        // As it was, the parts folded the way they were and the file of code closed
        test.Run(() =>
        {
            foreach (var (part, wasFolded) in folded)
                Page().SetPartCollapsed(part, wasFolded);
            CloseSettings();
            if (document.Path is { } path && PlatformPaths.Same(path, longFile))
                Close();
            if (before is not null && documents.Contains(before))
                Show(before);
        });
        test.Wait();
    }
}
