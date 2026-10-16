using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Horizon.Hex;

// The self test, the settings window and the skins.
internal sealed partial class HexScene
{
    private void TestSettings(HexPointerScript test)
    {
        /* The settings, and the skins */

        UIComponent Setting(string key) => settingControls[key];
        void SettingsPage(int page) => test.Run(() => settingsPages!.Select(page));

        Pick("File", () => "Settings...");
        test.Settle(0.4f);
        test.Check("settings: the file menu opens them, a window of their own with a page for each part and a tab for every extension with settings", () =>
            settingsWindow is { Visible: true } window && window.Parent == chrome.Root && settingsPages is { Tabs: [_, _, _, _, .. var more] } && more.Contains("Move editor")
            && Setting("theme") is Dropdown { Value: "Flat, dark" } && Setting("skin") is Selector);
        test.Check("settings: the window has a title bar, an x and a grip in its corner", () =>
            settingsWindow is { Closable: true, ResizeGrip: true } window && compositor.Resolve(window.Title) == "Settings" && !window.CloseBounds.IsEmpty && !window.GripBounds.IsEmpty);

        // Dragged by its title bar, it goes where it is dragged
        Vector2 settingsAt = Vector2.Zero;
        test.Run(() => settingsAt = settingsWindow!.Bounds.Min);
        Vector2 grabbed = Vector2.Zero;
        test.Run(() => grabbed = In(() => settingsWindow!.TitleBounds, 0.4f, 0.5f)());
        test.Drag(() => grabbed, () => grabbed + new Vector2(-60, -30));
        test.Wait();
        test.Check("settings: its title bar drags it about", () => Near(settingsWindow!.Bounds.Min.X, settingsAt.X - 60, 3) && Near(settingsWindow!.Bounds.Min.Y, settingsAt.Y - 30, 3));

        // And its corner bigger
        Vector2 settingsSize = Vector2.Zero;
        test.Run(() => settingsSize = settingsWindow!.Bounds.Size);
        test.Run(() => grabbed = In(() => settingsWindow!.GripBounds, 0.5f, 0.5f)());
        test.Drag(() => grabbed, () => grabbed + new Vector2(40, -20));
        test.Wait();
        test.Check("settings: its corner drags it bigger, its top left staying where it was", () =>
            Near(settingsWindow!.Bounds.Width, settingsSize.X + 40, 3) && Near(settingsWindow!.Bounds.Height, settingsSize.Y + 20, 3)
            && Near(settingsWindow!.Bounds.Min.X, settingsAt.X - 60, 3));

        // A click on the window where it is over the canvas is the window's, the layout under it never hears of it
        UIComponent? keptSelected = null;
        Label? overCanvas = null;
        test.Run(() =>
        {
            Select(document.Walk().Select(pair => pair.Component).FirstOrDefault());
            keptSelected = document.Selected;
            overCanvas = Descendants(settingsWindow!).OfType<Label>()
                .FirstOrDefault(label => label.Visible && canvas.Bounds.Contains(label.Bounds.Center) && label.Bounds.Width > 20);
        });
        test.Click(At(() => overCanvas, 0.5f, 0.5f));
        test.Wait();
        test.Check("settings: a click on a label of it over the canvas is its own, the layout under it keeps its selection", () =>
            overCanvas is not null && keptSelected is not null && document.Selected == keptSelected && settingsWindow is { Visible: true });

        test.Click(At(() => Setting("theme"), 0.9f));
        test.Settle(0.3f);
        test.Click(() => chrome.ToWorld(((Dropdown)Setting("theme")).RowBounds(1).Center));
        test.Settle(0.3f);
        test.Check("settings: the editor goes into the next theme of its skin there and then", () =>
            settings.EditorTheme != "dark" && settings.EditorSkin == "flat" && compositor.Skin?.Theme == settings.EditorTheme
            && Setting("theme") is Dropdown { Value: var shown } && shown.EndsWith(settings.EditorTheme));
        test.Check("settings: the editor can wear any skin there is, the dropdown has every one of them", () =>
            Setting("theme") is Dropdown { Options: var listed } && listed.Length > UISkins.Available.Count && listed.Any(text => text.StartsWith("Dead Revolver")));
        test.Check("settings: boxes, background and all", () =>
            compositor.Skin is { } worn && treeScroll.Color == worn.PanelColor with { W = 1.0f });
        test.Run(() => UseEditorTheme("light", save: false));

        // The skin swaps on the thread that draws, waited on rather than given a fixed time
        test.WaitFor(() => compositor.Skin is { Theme: "light" }, 2.0f);
        test.Check("settings: the light one is light, and what the editor says quietly can still be read on it", () =>
            compositor.Skin is { Theme: "light", PanelColor.X: > 0.8f, TextColor.X: < 0.3f } && DimColor.X < 0.3f);
        test.Run(() => UseEditorTheme("dark", save: false));

        // The picture behind the code, the girl unless told otherwise, moved, sized and made fainter as it's dragged
        SettingsPage(SETTINGS_BACKDROP);
        test.Wait();
        test.Check("settings: a page for the picture behind the code, the one Hex comes with in every editor", () =>
            settings.BackdropOn && settings.BackdropImage.Length == 0 && code.Backdrop is not null
            && code.BackdropShape is { Rows: > 0 } && Setting("backdrop_wrap") is ToggleButton { State: true } && Setting("backdrop_wrap_margin") is Slider
            && Setting("backdrop_scale") is Slider && Setting("backdrop_across") is Slider && Setting("backdrop_up") is Slider && Setting("backdrop_opacity") is Slider);
        test.Run(() =>
        {
            ((Slider)Setting("backdrop_scale")).OnChanged!(0.8f);
            ((Slider)Setting("backdrop_across")).OnChanged!(-40.0f);
            ((Slider)Setting("backdrop_opacity")).OnChanged!(0.5f);
        });
        test.Wait();
        test.Check("settings: and its sliders move it, size it and make it fainter in the code there and then", () =>
            code.BackdropScale == 0.8f && code.BackdropOffset.X == 40.0f && code.BackdropOpacity == 0.5f);
        test.Run(() =>
        {
            ((Slider)Setting("backdrop_across")).OnChanged!(120.0f);
            ((Slider)Setting("backdrop_up")).OnChanged!(-80.0f);
        });
        test.Wait();
        test.Check("settings: and past the right and the bottom edge as well, the slider the way the picture goes", () =>
            code.BackdropOffset == new Vector2(-120.0f, -80.0f));
        test.Run(() =>
        {
            ((Slider)Setting("backdrop_across")).OnChanged!(-40.0f);
            ((Slider)Setting("backdrop_up")).OnChanged!(0.0f);
        });

        // How far the code keeps off her, every editor told as it's dragged
        float margin = 0.0f;
        test.Run(() =>
        {
            margin = settings.BackdropWrapMargin;
            ((Slider)Setting("backdrop_wrap_margin")).OnChanged!(20.0f);
        });
        test.Wait();
        test.Check("settings: the margin slider says how far the code keeps off her", () =>
            settings.BackdropWrapMargin == 20.0f && code.BackdropMargin == 20.0f);
        test.Run(() => ((Slider)Setting("backdrop_wrap_margin")).OnChanged!(margin));

        // How quickly the words glide, every editor told
        float flowRate = 0.0f;
        test.Run(() =>
        {
            flowRate = settings.WrapFlow;
            ((Slider)Setting("wrap_flow")).OnChanged!(30.0f);
        });
        test.Wait();
        test.Check("settings: the flow slider says how quickly the words glide, and 0 snaps", () =>
            settings.WrapFlow == 30.0f && code.FlowRate == 30.0f && Said("settings.backdrop.flow_off").Length > 0);
        test.Run(() => ((Slider)Setting("wrap_flow")).OnChanged!(flowRate));
        test.Click(At(() => Setting("backdrop"), 0.1f));
        test.Wait();
        test.Check("settings: and it goes when switched off", () => !settings.BackdropOn && code.Backdrop is null);
        test.Click(At(() => Setting("backdrop_wrap"), 0.1f));
        test.Wait();
        test.Check("settings: the code stops running round her when that's switched off", () => !settings.BackdropWrap && code.BackdropShape is null);
        test.Click(At(() => Setting("backdrop_wrap"), 0.1f));
        test.Wait();
        test.Click(At(() => Setting("backdrop"), 0.1f));
        test.Wait();

        SettingsPage(SETTINGS_DESIGNER);
        test.Wait();
        test.Click(At(() => Setting("snap"), 0.9f));
        test.Wait();
        test.Check("settings: the grid is one of a few", () => settings.Snap == HexSettings.Grids[1]);
        test.Run(() => settings.Snap = 0.0f);

        test.Click(At(() => Setting("handles"), 0.1f));
        test.Wait();
        test.Check("settings: the resize handles switch off", () => !settings.CanvasHandles && Setting("handles") is ToggleButton { State: false, Label: "Off" });
        test.Click(At(() => Setting("handles"), 0.1f));
        test.Wait();
        test.Check("settings: and on again", () => settings.CanvasHandles);

        test.Click(At(() => Setting("skin"), 0.92f));
        test.Settle(0.4f);
        test.Check("settings: the layouts go into the next skin, the editor stays in its own", () =>
            UISkins.Current.Name != UISkins.DEFAULT && stage.Skin is { } worn && !ReferenceEquals(worn, compositor.Skin)
            && stage.FollowsSkins && !compositor.FollowsSkins);
        test.Check("settings: the theme of the layouts has a row only for a skin that has themes, inside the window", () =>
            Setting("layout_theme").Parent?.Visible == UISkins.Current.Themes.Count > 0
            && (!Setting("layout_theme").Visible || settingsWindow!.Bounds.Contains(Setting("layout_theme").Bounds.Max)));

        SettingsPage(SETTINGS_EXTENSIONS);
        test.Wait();
        test.Check("settings: the extensions are a page with a row for every extension there is", () =>
            settingsPages!.SelectedPage is StackPanel page && Descendants(page).OfType<ToggleButton>().Count() == extensions.Count && extensions.Count > 0);

        Vector2 closeAt = Vector2.Zero;
        test.Run(() => closeAt = chrome.ToWorld(settingsWindow!.CloseBounds.Center));
        test.Click(() => closeAt);
        test.Wait();
        test.Check("settings: the x closes them", () => settingsWindow is null && !chrome.Root.Children.OfType<WindowPanel>().Any());

        foreach (UISkinChoice skin in UISkins.Available)
        {
            // Every skin there is by its line of the menu, in the first and the last of its themes. Every theme
            // of every skin is the business of the check-ui-skins scene of the examples, which doesn't have to
            // open a menu thirty times to get at them
            string?[] themes = skin.Themes.Count > 0 ? [.. new[] { skin.Themes[0], skin.Themes[^1] }.Distinct()] : [null];
            foreach (string? theme in themes)
            {
                Pick("View", () => theme is null ? skin.Title : $"{skin.Title}, {theme}");
                test.WaitFor(() => stage.Skin is { } worn && worn.Theme == (theme ?? worn.Theme) && UISkins.Current == skin && !worn.HasPendingArt, 2.0f);
                test.Wait();
                test.Check($"skins: {skin.Name}{(theme is null ? string.Empty : " in " + theme)} comes up, with art for a button and a panel, or a colour for them", () =>
                    UISkins.Current == skin && stage.Skin is { } worn && (theme is null || worn.Theme == theme)
                    && (worn.Sheet is null || (worn.Sheet.TryGetSprite("button", worn.Theme, out _) && worn.Sheet.TryGetSprite("panel", worn.Theme, out _)))
                    && box!.Bounds.Width > 0);
            }
        }

        test.Run(() => UISkins.Use(UISkins.DEFAULT));
        test.Settle(0.3f);
        test.Check("skins: and back to the one games come in", () => UISkins.Current.Name == UISkins.DEFAULT && stage.Skin is { Sheet: not null });
    }

    // The side bar slides away and back, the middle widening and narrowing with it, rather than popping
    private void TestSlidingSides(HexPointerScript test)
    {
        float wide = 0.0f, during = 0.0f, after = 0.0f, time = left.SlideTime;
        test.Run(() =>
        {
            left.SlideTime = UIComponent.DEFAULT_SLIDE_TIME;
            wide = center.Bounds.Width;
            ToggleSideBar();
        });
        test.Settle(UIComponent.DEFAULT_SLIDE_TIME * 0.4f);
        test.Run(() => during = center.Bounds.Width);
        test.Check("slide: the side bar goes out sliding, there on screen still and the middle on its way wider", () =>
            !left.Shown && left.Visible && left.Sliding && during > wide);

        test.Settle(UIComponent.DEFAULT_SLIDE_TIME * 1.5f);
        test.Run(() => after = center.Bounds.Width);
        test.Check("slide: and is gone once it's out, the middle all the wider", () => !left.Visible && !left.Sliding && after > during);

        test.Run(ToggleSideBar);
        test.Settle(UIComponent.DEFAULT_SLIDE_TIME * 1.5f);
        test.Check("slide: and comes back the way it went", () => left.Visible && left.Shown && !left.Sliding && Near(center.Bounds.Width, wide, 2));
        test.Run(() => left.SlideTime = time);
    }
}
