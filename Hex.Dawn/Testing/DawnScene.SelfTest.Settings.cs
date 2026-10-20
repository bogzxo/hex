using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The self test, the one window of settings and the skins.
internal sealed partial class DawnScene
{
    private void TestSettings(HexPointerScript test)
    {
        /* The settings, one window of them, and the skins */

        IRuntimeValue? Value(string key) => SettingsPage!.ValueOf!(key);
        void Enter(string key, IRuntimeValue value) => test.Run(() => SettingsPage!.Enter(key, value));

        Pick("File", () => "Settings...");
        test.Settle(0.4f);
        test.Check("settings: the file menu opens the one window of them, how Hex looks, the editor's, the terminal's and the extensions'", () =>
            SettingsWindow is { Visible: true } window && window.Parent == chrome.Root && SettingsPage is { } page
            && page.Settings.Any(setting => setting.Key == "editor_skin") && page.Settings.Any(setting => setting.Key == "scrollback")
            && page.Settings.Any(setting => setting.Key == "backdrop_image") && page.Settings.Any(setting => setting.Key == "layout_skin")
            && Value("editor_skin") is StringValue { Value: "Flat, horizon" });
        test.Check("settings: in order, how Hex looks first with both modes' themes in it, then the editor, the terminal and the extensions", () =>
            SettingsPage!.Settings.Select(setting => setting.Group.Split(" · ")[0]).Distinct().SequenceEqual(["Appearance", "Editor", "Terminal", "Extensions"])
            && SettingsPage.Settings.Where(setting => setting.Group == Hex.Shell.EditorSettingsCatalog.THEME).Select(setting => setting.Key).SequenceEqual(["editor_skin", "theme"]));
        test.Check("settings: the window has a title bar, an x and a grip in its corner", () =>
            SettingsWindow is { Closable: true, ResizeGrip: true } window && compositor.Resolve(window.Title) == "Settings" && !window.CloseBounds.IsEmpty && !window.GripBounds.IsEmpty);

        Enter("wrap_code", Values.Bool(true));
        test.Check("settings: a row of the editor's changed is put in force", () => settings.WrapCode && documents.All(open => open.Editor.WordWrap) && SettingsPage!.IsModified!("wrap_code"));
        test.Run(() => SettingsPage!.Reset("wrap_code"));
        test.Check("settings: and its reset puts it back", () => !settings.WrapCode && !SettingsPage!.IsModified!("wrap_code"));
        test.Run(() => SettingsPage!.Find("designer snaps"));
        test.Check("settings: the search finds the designer's snap", () => SettingsPage!.RowsShown == 1);
        test.Run(() => SettingsPage!.Find(string.Empty));

        // Dragged by its title bar, it goes where it is dragged
        Vector2 settingsAt = Vector2.Zero;
        test.Run(() => settingsAt = SettingsWindow!.Bounds.Min);
        Vector2 grabbed = Vector2.Zero;
        test.Run(() => grabbed = In(() => SettingsWindow!.TitleBounds, 0.4f, 0.5f)());
        test.Drag(() => grabbed, () => grabbed + new Vector2(-60, -30));
        test.Wait();
        test.Check("settings: its title bar drags it about", () => Near(SettingsWindow!.Bounds.Min.X, settingsAt.X - 60, 3) && Near(SettingsWindow!.Bounds.Min.Y, settingsAt.Y - 30, 3));

        // And its corner bigger
        Vector2 settingsSize = Vector2.Zero;
        test.Run(() => settingsSize = SettingsWindow!.Bounds.Size);
        test.Run(() => grabbed = In(() => SettingsWindow!.GripBounds, 0.5f, 0.5f)());
        test.Drag(() => grabbed, () => grabbed + new Vector2(40, -20));
        test.Wait();
        test.Check("settings: its corner drags it bigger, its top left staying where it was", () =>
            Near(SettingsWindow!.Bounds.Width, settingsSize.X + 40, 3) && Near(SettingsWindow!.Bounds.Height, settingsSize.Y + 20, 3)
            && Near(SettingsWindow!.Bounds.Min.X, settingsAt.X - 60, 3));

        // A click on the window where it is over the canvas is the window's, the layout under it never hears of it
        UIComponent? keptSelected = null;
        Label? overCanvas = null;
        test.Run(() =>
        {
            designer.Select(document.Walk().Select(pair => pair.Component).FirstOrDefault());
            keptSelected = document.Selected;
            overCanvas = Descendants(SettingsWindow!).OfType<Label>()
                .FirstOrDefault(label => label.Visible && canvas.Bounds.Contains(label.Bounds.Center) && label.Bounds.Width > 20);
        });
        test.Click(At(() => overCanvas, 0.5f, 0.5f));
        test.Wait();
        test.Check("settings: a click on a label of it over the canvas is its own, the layout under it keeps its selection", () =>
            overCanvas is not null && keptSelected is not null && document.Selected == keptSelected && SettingsWindow is { Visible: true });

        // The editor's theme, any theme of any skin there is
        string? next = null;
        test.Run(() =>
        {
            next = SettingsPage!.Settings.First(setting => setting.Key == "editor_skin").Choices!.First(choice => choice.StartsWith("Flat, ") && choice != "Flat, horizon");
            SettingsPage.Enter("editor_skin", new StringValue(next));
        });
        test.Settle(0.3f);
        test.Check("settings: the editor goes into another theme of its skin there and then", () =>
            settings.EditorTheme != "horizon" && settings.EditorSkin == "flat" && compositor.Skin?.Theme == settings.EditorTheme && next!.EndsWith(settings.EditorTheme));
        test.Check("settings: the editor can wear any skin there is, the row has every one of them", () =>
            SettingsPage!.Settings.First(setting => setting.Key == "editor_skin").Choices is { } listed && listed.Length > UISkins.Available.Count && listed.Any(text => text.StartsWith("Dead Revolver")));
        test.Check("settings: boxes, background and all", () =>
            compositor.Skin is { } worn && treeScroll.Color == worn.PanelColor with { W = 1.0f });
        test.Run(() => UseEditorTheme("light", save: false));

        // The skin swaps on the thread that draws, waited on rather than given a fixed time
        test.WaitFor(() => compositor.Skin is { Theme: "light" }, 2.0f);
        test.Check("settings: the light one is light, and what the editor says quietly can still be read on it", () =>
            compositor.Skin is { Theme: "light", PanelColor.X: > 0.8f, TextColor.X: < 0.3f } && DimColor.X < 0.3f);
        test.Run(() => UseEditorTheme("horizon", save: false));

        // The picture behind the code, the girl unless told otherwise, moved, sized and made fainter by its sliders
        test.Run(() => SettingsPage!.GoTo(Hex.Shell.EditorSettingsCatalog.BACKDROP));
        test.Wait();
        test.Check("settings: a section for the picture behind the code, the one Hex comes with in every editor, set with sliders", () =>
            settings.BackdropOn && settings.BackdropImage.Length == 0 && code.Backdrop is not null && code.BackdropShape is { Rows: > 0 }
            && SettingsPage!.Settings.Count(setting => setting.Group == Hex.Shell.EditorSettingsCatalog.BACKDROP && setting.Slider) == 9
            && Descendants(SettingsPage).OfType<Slider>().Count() >= 9);
        Enter("backdrop_scale", new NumberValue(0.8f));
        Enter("backdrop_across", new NumberValue(0.25f));
        Enter("backdrop_opacity", new NumberValue(0.5f));
        test.Wait();
        test.Check("settings: and its sliders move it, size it and make it fainter in the code there and then", () =>
            code.BackdropScale == 0.8f && code.BackdropOffset.X == 0.25f && code.BackdropOpacity == 0.5f);
        Enter("backdrop_across", new NumberValue(-0.2f));
        Enter("backdrop_up", new NumberValue(-0.1f));
        test.Wait();
        test.Check("settings: and past the right and the bottom edge as well", () => code.BackdropOffset == new Vector2(-0.2f, -0.1f));
        test.Run(() =>
        {
            SettingsPage!.Reset("backdrop_across");
            SettingsPage.Reset("backdrop_up");
            SettingsPage.Reset("backdrop_scale");
            SettingsPage.Reset("backdrop_opacity");
        });

        Enter("backdrop_wrap_margin", new NumberValue(20.0f));
        test.Wait();
        test.Check("settings: the margin says how far the code keeps off her", () => settings.BackdropWrapMargin == 20.0f && code.BackdropMargin == 20.0f);
        test.Run(() => SettingsPage!.Reset("backdrop_wrap_margin"));
        Enter("wrap_flow", new NumberValue(30.0f));
        test.Wait();
        test.Check("settings: the flow says how quickly the words glide", () => settings.WrapFlow == 30.0f && code.FlowRate == 30.0f);
        test.Run(() => SettingsPage!.Reset("wrap_flow"));

        Enter("backdrop", Values.Bool(false));
        test.Wait();
        test.Check("settings: and it goes when switched off", () => !settings.BackdropOn && code.Backdrop is null);
        Enter("backdrop", Values.Bool(true));
        Enter("backdrop_wrap", Values.Bool(false));
        test.Wait();
        test.Check("settings: the code stops running round her when that's switched off", () => !settings.BackdropWrap && code.BackdropShape is null);
        Enter("backdrop_wrap", Values.Bool(true));

        // A picture of its own, and the usual one back by the row's button
        string picture = Path.Combine(AppContext.BaseDirectory, USUAL_BACKDROP);
        Enter("backdrop_image", new StringValue(picture));
        test.Wait();
        test.Check("settings: a picture of its own is the backdrop from then on", () => settings.BackdropImage == Path.GetFullPath(picture) && code.Backdrop is not null);
        test.Run(() => SettingsPage!.OnButton!("backdrop_image", Hex.Shell.EditorSettingsCatalog.USUAL_PICTURE));
        test.Wait();
        test.Check("settings: and the row's button puts the usual one back", () => settings.BackdropImage.Length == 0 && Value("backdrop_image") is StringValue { Value: "" });

        // The designer
        Enter("snap", new NumberValue(HexSettings.Grids[1]));
        test.Check("settings: the grid is what it was set to", () => settings.Snap == HexSettings.Grids[1]);
        test.Run(() => SettingsPage!.Reset("snap"));
        Enter("canvas_handles", Values.Bool(false));
        test.Check("settings: the resize handles switch off", () => !settings.CanvasHandles);
        Enter("canvas_handles", Values.Bool(true));
        test.Check("settings: and on again", () => settings.CanvasHandles);

        string? layoutSkin = null;
        test.Run(() =>
        {
            layoutSkin = SettingsPage!.Settings.First(setting => setting.Key == "layout_skin").Choices!.First(choice => !choice.StartsWith(UISkins.Current.Title, StringComparison.Ordinal));
            SettingsPage.Enter("layout_skin", new StringValue(layoutSkin));
        });
        test.Settle(0.4f);
        test.Check("settings: the layouts go into another skin, the editor stays in its own", () =>
            UISkins.Current.Name != UISkins.DEFAULT && stage.Skin is { } worn && !ReferenceEquals(worn, compositor.Skin)
            && stage.FollowsSkins && !compositor.FollowsSkins && Value("layout_skin") is StringValue { Value: var shown } && shown == layoutSkin);

        // The panels back at their sizes by the button of their row
        test.Run(() => SettingsPage!.OnButton!("panels", 0));
        test.Wait();
        test.Check("settings: the panels' row puts them back at their sizes", () => Near(treeScroll.Size.X, usualLeft, 1) && Near(inspectorScroll.Size.X, usualRight, 1));

        // The extensions that are on, a toggle each
        test.Check("settings: the extensions are a row with a toggle for every extension there is", () =>
            SettingsPage!.Settings.First(setting => setting.Key == "disabled_extensions").Choices is { Length: > 0 } listed && listed.Length == extensions.All.Count);

        Vector2 closeAt = Vector2.Zero;
        test.Run(() => closeAt = chrome.ToWorld(SettingsWindow!.CloseBounds.Center));
        test.Click(() => closeAt);
        test.Wait();
        test.Check("settings: the x closes them", () => SettingsWindow is null && !chrome.Root.Children.OfType<WindowPanel>().Any());

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
