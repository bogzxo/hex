using System.Globalization;
using System.Numerics;

using Hex.Shell;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The self test of Hex without a mouse and for eyes that need more. The colours of every look measured against what
// they're on (text 4.5:1, the ring round what has the keyboard 3:1, the web's guidelines), Tab walked round the main
// window from the code with every stop shown having the keyboard, named and big enough to hit, Shift+Tab going back,
// and the UI still with its motion at 0, no spinner turning and no caret blinking. Every ratio and stop is printed.
internal sealed partial class DawnScene
{
    // The most Tab is pressed going round the window, more than it has stops
    private const int TAB_WALK_MOST = 90;

    // The least a target is across at 100%, WCAG 2.2's 2.5.8
    private const float LEAST_TARGET = 24.0f;

    private void TestAccessibility(HexPointerScript test)
    {
        OnCode(test, dataFile);
        TestLooksContrast(test);
        TestTabWalk(test);
        TestStillWithoutMotion(test);
    }

    /// <summary>A colour as the eye gets it, laid over what's under it by its alpha.</summary>
    private static Vector4 Over(Vector4 color, Vector4 under) => Vector4.Lerp(under, color with { W = 1.0f }, color.W) with { W = 1.0f };

    private void TestLooksContrast(HexPointerScript test)
    {
        string themeBefore = settings.EditorTheme, skinBefore = settings.EditorSkin;
        bool keysBefore = settings.VisualStudioKeys;
        // Each look in Dawn and in Agentic, which has a theme of its own in the looks that have one
        foreach (HexLook look in HexLooks.BuiltIn)
        foreach (HexFace face in (HexFace[])[HexFace.Dawn, HexFace.Noon])
        {
            var failed = new List<string>();
            string named = face == HexFace.Dawn ? look.Name : $"{look.Name} {face.Name()}";
            test.Run(() =>
            {
                ShowFace(HexFace.Dawn);
                PickLook(look);
                ShowFace(face);
                noonWait = 0;
            });
            test.Settle(0.3f);
            test.Run(() =>
            {
                UIPalette palette = compositor.Skin!.Palette;

                // The grounds: the panels over black where they're see-through (glass is over whatever's behind, black is
                // the darkest it gets), the field code is on, and what floats over the window
                Vector4 panel = Over(palette.Panel, new Vector4(0, 0, 0, 1));
                Vector4 field = Over(palette.Field, panel);
                Vector4 raised = palette.Raised;
                Vector4 control = Over(palette.Control, panel);
                Vector4 accent = palette.Accent with { W = 1.0f };
                Console.WriteLine($"[Hex self-test] contrast: {named} is {settings.EditorSkin}/{compositor.Skin.Theme}, panel {palette.Panel} field {palette.Field} raised {raised} text {palette.Text} dim {palette.DimText} accent {palette.Accent} link {palette.Link} ring {palette.Highlight}");
                (string Name, Vector4 Fore, Vector4 Ground, float Least)[] pairs =
                [
                    ("text on panel", palette.Text, panel, 4.5f),
                    ("text on field", palette.Text, field, 4.5f),
                    ("text on raised", palette.Text, raised, 4.5f),
                    ("dim text on panel", palette.DimText, panel, 4.5f),
                    ("dim text on field", palette.DimText, field, 4.5f),
                    ("dim text on raised", palette.DimText, raised, 4.5f),
                    ("control text on control", palette.ControlText, control, 4.5f),
                    ("link on panel", palette.Link, panel, 4.5f),
                    ("error on panel", palette.Error, panel, 4.5f),
                    ("warning on panel", palette.Warning, panel, 4.5f),
                    ("success on panel", palette.Success, panel, 4.5f),
                    ("text on accent", palette.OnAccent, accent, 4.5f),
                    ("focus ring on panel", palette.Highlight, panel, 3.0f),
                    ("focus ring on field", palette.Highlight, field, 3.0f),
                    ("focus ring on raised", palette.Highlight, raised, 3.0f)
                ];

                foreach (var (name, fore, ground, least) in pairs)
                {
                    float ratio = UIPalette.Contrast(fore, ground);
                    bool passes = ratio >= least;
                    Console.WriteLine($"[Hex self-test] contrast: {named}, {name} {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1 (at least {least.ToString("0.0", CultureInfo.InvariantCulture)}){(passes ? "" : " TOO LOW")}");
                    if (!passes)
                        failed.Add($"{name} {ratio.ToString("0.00", CultureInfo.InvariantCulture)}");
                }
            });
            // A look changed takes a few frames to be drawn in, the picture straight after was of an empty canvas
            test.Run(() => Engine.WindowManager.RequestFrames());
            test.Settle(0.5f);
            test.Run(() => Shot($"a11y-look-{named.Replace(' ', '-').ToLowerInvariant()}"));
            test.Check($"accessibility: {named}, text reads at 4.5:1 and the focus ring shows at 3:1 on every ground", () =>
            {
                if (failed.Count > 0)
                    Console.WriteLine($"[Hex self-test] contrast: {named} too low: {string.Join(", ", failed)}");
                return failed.Count == 0;
            });
        }

        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            PickLook(HexLooks.Solstice);
            UseEditorSkin(skinBefore, themeBefore, save: false);

            // The Visual Studio look puts Visual Studio's keys on, which has no Ctrl+M
            settings.VisualStudioKeys = keysBefore;
            ApplyKeyLayer();
        });
        test.Settle(0.3f);
    }

    /// <summary>What a stop is, for the printout, its kind and what it says or shows.</summary>
    private static string StopName(UIComponent stop) => stop switch
    {
        Button button => $"Button '{(button.Label.Length > 0 ? button.Label : button.Tooltip.Length > 0 ? button.Tooltip : "$(" + button.Icon + ")")}'",
        ToggleButton toggle => $"Toggle '{toggle.Label}'",
        _ => stop.GetType().Name + (stop.Tooltip.Length > 0 ? $" '{stop.Tooltip}'" : "")
    };

    // Whether a stop says what it is to somebody who can't see its icon, its words or its tooltip
    private static bool Named(UIComponent stop) => stop switch
    {
        Button button => button.Label.Length > 0 || button.Tooltip.Length > 0,
        ToggleButton toggle => toggle.Label.Length > 0 || toggle.Tooltip.Length > 0,
        _ => true
    };

    private void TestTabWalk(HexPointerScript test)
    {
        float scaleBefore = 1.0f;
        var stops = new List<(UIComponent Stop, bool Shown, UIRect Bounds)>();
        bool closed = false;
        test.Run(() =>
        {
            scaleBefore = compositor.Scale;
            SetInterfaceSize(1.0f, remember: false);
            Open(dataFile);
            code.Focus();
        });
        test.Settle(0.3f);

        // The code keeps Tab for indenting, Ctrl+M lets it go on
        PressKeys(test, "Ctrl+M");
        test.Check("accessibility: Ctrl+M turns Tab Moves Focus on in the code", () => tabMovesFocus && code.TabMovesFocus);
        test.Run(() => compositor.ForgetFocusShown());
        PressKeys(test, "Tab");
        test.Settle(0.1f);
        test.Check("accessibility: Tab then goes on out of the code", () => compositor.Focus != code && compositor.Focus is not null);

        // Round the window, a stop a Tab, until it's back at the first one
        for (int i = 0; i < TAB_WALK_MOST; i++)
        {
            test.Run(() =>
            {
                if (closed || compositor.Focus is not { } stop)
                    return;
                if (stops.Count > 0 && stops[0].Stop == stop)
                {
                    closed = true;
                    return;
                }

                bool shown = compositor.FocusShownOn == stop;
                stops.Add((stop, shown, stop.Bounds));
                Console.WriteLine($"[Hex self-test] tab walk: {stops.Count,2} {StopName(stop)} {stop.Bounds.Width:0}x{stop.Bounds.Height:0}{(shown ? "" : " NO RING")}");
                Shot($"a11y-tab-{stops.Count:00}");
                compositor.ForgetFocusShown();
            });
            // Only until it's round, a Tab more and the keyboard isn't where the walk ended
            test.Run(() =>
            {
                if (!closed)
                    Engine.Input.Keyboard.Tap((HexKeyStroke.Parse("Tab") ?? throw new InvalidOperationException("Tab stroke missing")).Keys);
                Engine.WindowManager.RequestFrames();
            });
            test.Settle(0.05f);
        }

        test.Check("accessibility: Tab goes round the main window and comes back to where it started", () => closed && stops.Count >= 5);
        test.Check("accessibility: every stop Tab makes is shown with a ring or a sign of its own of having the keyboard", () =>
        {
            var missing = stops.Where(stop => !stop.Shown).Select(stop => StopName(stop.Stop)).ToList();
            if (missing.Count > 0)
                Console.WriteLine($"[Hex self-test] tab walk: no ring on {string.Join(", ", missing)}");
            return missing.Count == 0;
        });
        test.Check("accessibility: every stop Tab makes is named, by its words or its tooltip", () =>
        {
            var unnamed = stops.Where(stop => !Named(stop.Stop)).Select(stop => StopName(stop.Stop)).ToList();
            if (unnamed.Count > 0)
                Console.WriteLine($"[Hex self-test] tab walk: nothing names {string.Join(", ", unnamed)}");
            return unnamed.Count == 0;
        });
        test.Check($"accessibility: every button Tab stops on is at least {LEAST_TARGET:0} px across at 100%", () =>
        {
            var small = stops.Where(stop => stop.Stop is Button or ToggleButton && MathF.Min(stop.Bounds.Width, stop.Bounds.Height) < LEAST_TARGET - 0.5f)
                .Select(stop => $"{StopName(stop.Stop)} {stop.Bounds.Width:0}x{stop.Bounds.Height:0}").ToList();
            if (small.Count > 0)
                Console.WriteLine($"[Hex self-test] tab walk: under {LEAST_TARGET:0} px: {string.Join(", ", small)}");
            return small.Count == 0;
        });

        // Shift+Tab goes back the way Tab came
        UIComponent? before = null;
        test.Run(() => before = stops.Count > 1 ? stops[^1].Stop : null);
        PressKeys(test, "Shift+Tab");
        test.Check("accessibility: Shift+Tab goes back to the stop before", () => before is not null && compositor.Focus == before);

        test.Run(() =>
        {
            ToggleTabMovesFocus();
            code.Focus();
            SetInterfaceSize(scaleBefore, remember: false);
        });
        test.Check("accessibility: Ctrl+M again gives Tab back to the code", () => !tabMovesFocus && !code.TabMovesFocus);
        test.Settle(0.2f);
    }

    private void TestStillWithoutMotion(HexPointerScript test)
    {
        float animation = 1.0f;
        Button spinner = null!;
        bool movedAtOne = false;
        test.Run(() =>
        {
            animation = settings.UiAnimation;
            settings.UiAnimation = 1.0f;
            ApplyEffects();
            spinner = chrome.AddComponent(new Button(string.Empty) {
                Anchor = Origin.BottomLeft, Pivot = Origin.BottomLeft, Position = new Vector2(70, 50),
                Size = new Vector2(32, 32), Icon = "loading~spin", Tooltip = "Working" });
        });
        test.Settle(0.2f);
        test.Run(() => Engine.WindowManager.RequestFrames());
        test.Settle(0.1f);
        test.Run(() => movedAtOne = compositor.Moved);

        // The motion at 0, the spinner is drawn as its icon, still
        test.Run(() =>
        {
            settings.UiAnimation = 0.0f;
            ApplyEffects();
            Engine.WindowManager.RequestFrames();
        });
        test.Settle(0.2f);
        test.Check("accessibility: a spinner turns with the UI's motion on", () => movedAtOne);
        test.Check("accessibility: and with the UI's motion at 0 nothing on the window moves", () => compositor.AnimationSpeed == 0.0f && !compositor.Moved);
        test.Run(() =>
        {
            spinner.Parent?.Remove(spinner);
            settings.UiAnimation = animation;
            ApplyEffects();
        });
        test.Settle(0.2f);
    }
}
