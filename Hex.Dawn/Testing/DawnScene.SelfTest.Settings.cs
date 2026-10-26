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

        // As a run starts, nothing folded yet, so this is the first time the settings open whatever ran before
        test.Run(Horizon.UI.Components.SettingsPage.ForgetFolding);
        Pick("File", () => "Settings...");
        test.Settle(0.4f);
        test.Run(() => Shot("settings-first-open"));
        test.Check("nav: other parts folded on first open", () =>
            SettingsPage is { } page && !Horizon.UI.Components.SettingsPage.IsPartCollapsed("Editor")
            && Horizon.UI.Components.SettingsPage.IsPartCollapsed("Terminal") && Horizon.UI.Components.SettingsPage.IsPartCollapsed("Appearance")
            && page.PartBounds("Extensions") is { IsEmpty: false } extensions && page.NavViewport is var view
            && extensions.Min.Y >= view.Min.Y - 1.0f && extensions.Max.Y <= view.Max.Y + 1.0f);
        test.Check("settings: the file menu opens the one window of them, how Hex looks, the editor's, the terminal's and the extensions'", () =>
            SettingsWindow is { Visible: true } window && window.Parent == chrome.Root && SettingsPage is { } page
            && page.Settings.Any(setting => setting.Key == "editor_skin") && page.Settings.Any(setting => setting.Key == "scrollback")
            && page.Settings.Any(setting => setting.Key == "backdrop_image") && page.Settings.Any(setting => setting.Key == "layout_skin")
            && Value("editor_skin") is StringValue { Value: "Horizon" });
        test.Check("settings: in order, how Hex looks first with both modes' themes in it, then the editor, the terminal and the extensions", () =>
            SettingsPage!.Settings.Select(setting => setting.Group.Split(" · ")[0]).Distinct().SequenceEqual(Hex.Shell.EditorSettingsCatalog.Parts)
            && SettingsPage.Settings.Where(setting => setting.Group == Hex.Shell.EditorSettingsCatalog.THEME).Select(setting => setting.Key).SequenceEqual(["editor_skin", "theme"]));
        // The pointer down the sections a few pixels at a time, a picture at each, for whoever's looking at how they light
        Label[] links = [];
        test.Run(() => links = [.. Descendants(SettingsWindow!).OfType<Label>().Where(label => label.GetType().Name == "SectionLink").Take(6)]);
        for (int step = 0; step < 24; step++)
        {
            int at = step;
            test.Hover(() => links.Length == 0 ? default : chrome.ToWorld(Vector2.Lerp(links[0].Bounds.Center, links[^1].Bounds.Center, at / 23.0f)));
            test.Run(() => Shot($"nav-hover-{at:00}"));
        }

        // A look saved in the settings is in View, Look beside Hex's, and gone from it once it's deleted
        test.Check("settings: a look saved is in View, Look as well, and deleted it's gone", () =>
        {
            const string NAME = "Selftest look";
            bool Listed() => lookMenu?.Everything().Any(pair => pair.Item.Label == NAME) == true;
            HexLooks.Save(settings, NAME, terminals.Settings.Theme);
            ((IHexLookOwner)this).Keep(settings, null);
            bool saved = Listed();
            HexLooks.Delete(settings, NAME);
            ((IHexLookOwner)this).Keep(settings, null);
            return saved && !Listed() && lookMenu!.Everything().Any(pair => pair.Item.Label == HexLooks.Fluid.Name);
        });

        test.Check("settings: the window has a title bar, an x and a grip in its corner", () =>
            SettingsWindow is { Closable: true, ResizeGrip: true } window && compositor.Resolve(window.Title) == "Settings" && !window.CloseBounds.IsEmpty && !window.GripBounds.IsEmpty);
        test.Check("settings: the window opens at most of the screen, not at a fixed 1040 by 700", () =>
            SettingsWindow is { } window && window.Size == Horizon.UI.Components.SettingsWindow.SizeFor(chrome.Root.Bounds.Size, window.MinSize, Horizon.UI.Components.SettingsWindow.ScaleIn(chrome))
            && Near(window.Bounds.Center.X, chrome.Root.Bounds.Center.X, 2) && Near(window.Bounds.Center.Y, chrome.Root.Bounds.Center.Y, 2));

        Enter("wrap_code", Values.Bool(true));
        test.Check("settings: a row of the editor's changed is put in force", () => settings.WrapCode && documents.All(open => open.Editor.WordWrap) && SettingsPage!.IsModified!("wrap_code"));
        test.Run(() => SettingsPage!.Reset("wrap_code"));
        test.Check("settings: and its reset puts it back", () => !settings.WrapCode && !SettingsPage!.IsModified!("wrap_code"));
        test.Run(() => SettingsPage!.Find("designer snaps"));
        test.Check("settings: the search finds the designer's snap", () => SettingsPage!.RowsShown == 1);
        test.Wait();
        test.Run(() => Shot("settings-search"));
        test.Check("settings: and says how many it found, with an x to empty it", () => SettingsPage!.FoundText == "1 setting found");
        test.Wait();
        test.Check("settings: the sections down the left narrow to what the search left", () =>
            SettingsPage!.SectionsShown is { Count: > 0 } shown && shown.Contains(SettingsPage.Settings.First(setting => setting.Key == "snap").Group)
            && shown.Count < SettingsPage.Settings.Select(setting => setting.Group).Distinct().Count() / 2);
        test.Check("settings: Escape with a search typed clears it and leaves the window up", () =>
            SettingsPage!.Escape() && SettingsPage.Search.Text.Length == 0 && SettingsPage.RowsShown > 1 && SettingsPage.FoundText.Length == 0 && SettingsWindow is { Visible: true });

        // What a search found lit in the accent in a row's name the way VS Code does, and let go once it's emptied
        test.Run(() => SettingsPage!.Find("wrap"));
        test.Wait();
        test.Run(() => Shot("settings-search-lit"));
        test.Wait();
        test.Check("search: matched words lit in accent", () =>
            SettingsPage!.IsRowShown("wrap_code") && SettingsPage.TitleShown("wrap_code") is { } lit
            && lit.Contains("[color:accent]wrap[/color]", StringComparison.OrdinalIgnoreCase));
        test.Run(() => SettingsPage!.Find(string.Empty));
        test.Check("search: and the light goes once the search is emptied", () =>
            SettingsPage!.TitleShown("wrap_code") is { } plain && !plain.Contains("[color:accent]", StringComparison.Ordinal));
        test.Run(() => SettingsPage!.Find("subtle"));
        test.Check("search: a choice's value finds its row, subtle the effects", () => SettingsPage!.IsRowShown("effects"));
        test.Run(() => SettingsPage!.Find("fast-forward"));
        test.Check("search: a choice's label finds its row, fast-forward how to pull", () => SettingsPage!.IsRowShown("git_pull_mode"));

        // A choice in words with a line under it of what the one picked does, VS Code's descriptions of a setting's values
        test.Run(() => SettingsPage!.Find(string.Empty));
        Enter("git_pull_mode", new StringValue("ff_only"));
        test.Wait();
        string? ffHelp = null;
        test.Check("choices: a choice says its value in words, Fast-forward only and not Ff only, with a line of what it does under it", () =>
            SettingsPage!.ChoiceShown("git_pull_mode") is ("Fast-forward only", { Length: > 0 } help) && (ffHelp = help) is not null
            && SettingsPage.ChoiceShown("git_smart_commit").Label == "Ask each time" && SettingsPage.ChoiceShown("effects").Label == "None");
        test.Run(() => SettingsPage!.Reveal("git_pull_mode"));
        test.Settle(1.0f);
        test.Run(() => Shot("settings-choice-help"));
        test.Wait();
        Enter("git_pull_mode", new StringValue("rebase"));
        test.Wait();
        test.Check("choices: and the line follows the pick", () =>
            SettingsPage!.ChoiceShown("git_pull_mode") is ("Rebase", { Length: > 0 } help) && help != ffHelp && settings.GitPullMode == "rebase");
        test.Run(() => SettingsPage!.Reset("git_pull_mode"));

        // @id:, VS Code's, the one setting of that key, and nothing with @modified while it's what it comes as
        test.Run(() => SettingsPage!.Find("@id:git_blame"));
        test.Check("search: @id leaves one row", () =>
            SettingsPage!.RowsShown == 1 && SettingsPage.IsRowShown("git_blame") && SettingsPage.FoundText == "1 setting found");
        test.Run(() => SettingsPage!.Find("@id:git_blame @modified"));
        test.Wait();
        test.Run(() => Shot("settings-nothing-found"));
        test.Wait();
        test.Check("search: and @modified with it leaves none while it's what it comes as, with a way back out", () =>
            SettingsPage!.IsModified!("git_blame") ? SettingsPage.RowsShown == 1 : SettingsPage.RowsShown == 0 && SettingsPage.SaysNothingFound
                && Descendants(SettingsPage).OfType<Button>().Any(button => button.Visible && button.Label == "Clear search (Esc)"));

        // The funnel at the end of the box, what narrows a search without having to know the words
        test.Run(() => SettingsPage!.Find(string.Empty));
        test.Wait();
        Button? funnel = null;
        test.Run(() => funnel = Descendants(SettingsPage!).OfType<Button>().FirstOrDefault(button => button.Tooltip == Said("settings_page.filter_tip")));
        test.Click(At(() => funnel, 0.5f, 0.5f));
        test.Wait();
        test.Run(() => Shot("settings-funnel"));
        test.Wait();
        test.Check("search: the funnel at the end of the box offers @modified, @id: and @section:", () =>
            chrome.Root.Children.OfType<ContextMenu>().FirstOrDefault()?.Showing is { } menu
            && menu.Items.Select(item => item.Shortcut).Take(3).SequenceEqual([Horizon.UI.Components.SettingsPage.MODIFIED, Horizon.UI.Components.SettingsPage.ID, Horizon.UI.Components.SettingsPage.SECTION])
            && menu.Items.Any(item => item.Label == Said("settings_page.reset_all")));
        test.Run(() =>
        {
            ContextMenu? shown = chrome.Root.Children.OfType<ContextMenu>().FirstOrDefault();
            shown?.Showing?.Items[0].OnPicked!();
            shown?.Close();
        });
        test.Wait();
        test.Check("search: and picking one puts it in the search with the keyboard there", () =>
            SettingsPage!.Search.Text == Horizon.UI.Components.SettingsPage.MODIFIED + " " && SettingsPage.Search.IsFocused);

        // Ctrl+F from a row gives the search the keyboard, all of it picked out to type over
        test.Run(() =>
        {
            SettingsPage!.FocusFirstRow();
            SettingsPage.FocusSearch();
        });
        test.Check("search: Ctrl+F in the page gives the search the keyboard", () => SettingsPage!.Search.IsFocused && SettingsPage.Search.Caret == SettingsPage.Search.Text.Length);
        test.Run(() => SettingsPage!.Find(string.Empty));

        // @modified, VS Code's, only what isn't what it comes as, and a word after it narrows that
        Enter("wrap_code", Values.Bool(true));
        test.Run(() => SettingsPage!.Find("@modified"));
        test.Check("settings: @modified leaves only the changed rows", () =>
            SettingsPage!.RowsShown > 0 && SettingsPage.RowsShown == SettingsPage.Settings.Count(setting => SettingsPage.IsModified!(setting.Key))
            && SettingsPage.FoundText.StartsWith($"{SettingsPage.RowsShown} setting", StringComparison.Ordinal));
        int modifiedRows = 0;
        test.Run(() =>
        {
            modifiedRows = SettingsPage!.RowsShown;
            SettingsPage.Find("@modified wrap code");
        });
        test.Check("settings: and a word after it narrows them further", () => SettingsPage!.RowsShown >= 1 && SettingsPage.RowsShown <= modifiedRows);
        test.Run(() => SettingsPage!.Find(string.Empty));
        test.Wait();
        test.Run(() => Shot("settings-modified-dot"));
        test.Check("settings: out of a search a section with a change in it has a dot down the left", () =>
            SettingsPage!.SectionsChanged.Contains(SettingsPage.Settings.First(setting => setting.Key == "wrap_code").Group));

        // A right click on a row has its reset, its key and its line of the file
        Label? rowTitle = null;
        test.Run(() =>
        {
            SettingsPage!.GoTo(SettingsPage.Settings.First(setting => setting.Key == "wrap_code").Group);
        });
        test.Wait();
        test.Run(() => rowTitle = Descendants(SettingsPage!).OfType<Label>().FirstOrDefault(label =>
            label.Visible && label.Text.Contains(SettingsPage!.Settings.First(setting => setting.Key == "wrap_code").Title, StringComparison.Ordinal)
            && SettingsWindow!.Bounds.Contains(label.Bounds.Center)));
        test.RightClick(At(() => rowTitle, 0.2f, 0.5f));
        test.Wait();
        test.Check("settings: a right click on a row offers its reset, its key and its line of HIDL", () =>
            chrome.Root.Children.OfType<ContextMenu>().FirstOrDefault()?.Showing is { } menu
            && menu.Items.Select(item => item.Label).Where(label => label.Length > 0).SequenceEqual(["Reset setting", "Copy setting ID", "Copy as HIDL line"])
            && SettingsPage!.LineOf("wrap_code") == "wrap_code: true");
        test.Run(() => chrome.Root.Children.OfType<ContextMenu>().FirstOrDefault()?.Close());
        test.Run(() => SettingsPage!.Reset("wrap_code"));

        // Straight to one setting from the top of the page with a search typed, the search emptied, the designer's
        // section scrolled to, the row in sight and flashing, the keyboard on its number
        test.Run(() =>
        {
            SettingsPage!.GoTo(SettingsPage.Settings[0].Group);
            SettingsPage.Find("wrap");
        });
        test.Wait();
        test.Run(() => SettingsPage!.Reveal("snap"));
        test.Settle(0.3f);
        test.Run(() => Shot("settings-reveal"));
        test.Wait();
        test.Check("reveal: a setting scrolled to and flashed", () =>
            SettingsPage!.Search.Text.Length == 0 && SettingsPage.FlashingKey == "snap" && SettingsPage.IsRowInView("snap") && SettingsPage.KeyWithKeyboard == "snap");
        test.Settle(1.0f);
        test.Check("reveal: and the flash fades, the row staying where it was put", () => SettingsPage!.FlashingKey is null && SettingsPage.IsRowInView("snap"));
        TestSettingsKeys(test);
        TestSettingsRows(test);
        TestSettingsSearch(test);
        TestSettingsPairs(test);

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
        test.Settle(0.3f);
        test.Check("settings: and the size it was dragged to stays, it's only sized to the screen as it opens", () =>
            Near(SettingsWindow!.Bounds.Width, settingsSize.X + 40, 3) && Near(SettingsWindow!.Bounds.Height, settingsSize.Y + 20, 3));

        // A click on the window where it is over the canvas is the window's, the layout under it never hears of it
        UIComponent? keptSelected = null;
        Label? overCanvas = null;
        test.Run(() =>
        {
            // Something to have selected, a stack added when the layout has nothing in it (a part before this one that
            // failed can leave it empty, and then this failed with nothing to keep)
            if (!document.Walk().Any())
            {
                Menu layoutMenu = menu.Menus.First(each => each.Title == "Layout");
                layoutMenu.Items.First(item => item.Submenu is not null && item.Label == Said("designer_selection.add")).Submenu!.Everything().Select(pair => pair.Item).First(item => item.Label == "stack").OnPicked!();
            }

            designer.Select(document.Walk().Select(pair => pair.Component).FirstOrDefault());
            keptSelected = document.Selected;
            // One inside the window as well, a row's label scrolled out of the rows still has bounds, out over the canvas
            // where a click is the canvas's
            overCanvas = Descendants(SettingsWindow!).OfType<Label>()
                .FirstOrDefault(label => label.Visible && canvas.Bounds.Contains(label.Bounds.Center) && SettingsWindow!.Bounds.Contains(label.Bounds.Center) && label.Bounds.Width > 20);
        });
        test.Click(At(() => overCanvas, 0.5f, 0.5f));
        test.Wait();
        test.Check("settings: a click on a label of it over the canvas is its own, the layout under it keeps its selection", () =>
            overCanvas is not null && keptSelected is not null && document.Selected == keptSelected && SettingsWindow is { Visible: true });

        // The editor's theme, any theme of any skin there is
        string? next = null;
        test.Run(() =>
        {
            next = EditorSkins().First(choice => choice.Skin.Name == TOOL_SKIN_NAME && choice.Theme is not null and not "horizon").Text;
            SettingsPage!.Enter("editor_skin", new StringValue(next));
        });
        test.Settle(0.3f);
        test.Check("settings: the editor goes into another theme of its skin there and then", () =>
            settings.EditorTheme != "horizon" && settings.EditorSkin == "flat" && compositor.Skin?.Theme == settings.EditorTheme && next!.Replace(' ', '_').EndsWith(settings.EditorTheme, StringComparison.OrdinalIgnoreCase));
        test.Check("settings: the editor can wear any skin there is, the row has every one of them", () =>
            SettingsPage!.Settings.First(setting => setting.Key == "editor_skin").Choices is { } listed && listed.Length > UISkins.Available.Count && listed.Any(text => text.Contains("Dead Revolver")));
        // The lists of the sides are the panel's colour, see through on glass, and the code's box the panel itself
        test.Check("settings: boxes, background and all", () =>
            compositor.Skin is { } worn && treeScroll.Color is { } box && box with { W = 1.0f } == worn.PanelColor with { W = 1.0f }
            && codeScroll.Color == worn.PanelColor with { W = 1.0f });
        test.Run(() => UseEditorTheme("light", save: false));

        // The skin swaps on the thread that draws, waited on rather than given a fixed time
        test.WaitFor(() => compositor.Skin is { Theme: "light" }, 2.0f);
        test.Check("settings: the light one is light, and what the editor says quietly can still be read on it", () =>
            compositor.Skin is { Theme: "light", PanelColor.X: > 0.8f, TextColor.X: < 0.3f } && DimColor.X < 0.3f);
        test.Run(() => UseEditorTheme("horizon", save: false));

        // The picture behind the code, off as it comes (it took from the contrast of the code) and switched on here. The
        // girl unless told otherwise, moved, sized and made fainter by its sliders
        test.Check("settings: the picture behind the code is off as it comes", () => !settings.BackdropOn && code.Backdrop is null);
        test.Run(() => SettingsPage!.GoTo(Hex.Shell.EditorSettingsCatalog.BACKDROP));
        Enter("backdrop", Values.Bool(true));
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
        test.Run(() => SettingsPage!.Reset("backdrop"));
        test.Wait();
        test.Check("settings: and its reset puts it off again", () => !settings.BackdropOn && code.Backdrop is null);

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
        TestSideKeys(test);

        foreach (UISkinChoice skin in UISkins.Available)
        {
            // Every skin there is by its line of the menu, in the first and the last of its themes. Every theme
            // of every skin is the business of the check-ui-skins scene of the examples, which doesn't have to
            // open a menu thirty times to get at them
            string?[] themes = skin.Themes.Count > 0 ? [.. new[] { skin.Themes[0], skin.Themes[^1] }.Distinct()] : [null];
            foreach (string? theme in themes)
            {
                Pick("Layout", () => theme is null ? skin.Title : $"{skin.Title}, {theme}");
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

    // Ctrl+Shift+G, Source Control's and the designer's Ungroup's both, pressed through the real table of keys with a
    // layout open. Without a group selected it's Source Control with its list given the keyboard and nothing said of
    // groups, with one it's Ungroup
    private void TestSideKeys(HexPointerScript test)
    {
        void PressCtrlShiftG() => test.Run(() =>
        {
            bool typing = compositor.Focus is not (null or TreeView) || stage.Focus is not null;
            commands.Keys.Update(key => key == Silk.NET.Input.Key.G, control: true, shift: true, alt: false, typing);
        });

        string noGroup = Said("designer_selection.no_group");
        test.Run(() =>
        {
            designer.Select(null);
            compositor.Focus = null;
            messageItem.Text = string.Empty;
        });
        PressCtrlShiftG();
        test.Wait();
        test.Check("keys: Ctrl+Shift+G with a layout open and no group selected shows Source Control and says nothing of groups", () =>
            LayoutTools && Side == HexSide.SourceControl && left.Shown && !messageItem.Text.Contains(noGroup, StringComparison.Ordinal)
            && !toasts.Cards.Any(card => card.Text.Contains(noGroup, StringComparison.Ordinal)));
        test.Check("keys: and the side's list has the keyboard, Up, Down and Enter go down it at once", () => sideTree.Visible && compositor.Focus == sideTree);
        test.Check("keys: with a row with rows under it picked, so Down goes into it rather than onto Commit all", () =>
            sideTree.Selected is null && !sideTree.Items.Any(item => item.HasChildren && item.Expanded) || sideTree.Selected is { HasChildren: true });

        // A stack added and made a group, selected, and the same key takes it apart
        UIComponent? group = null;
        test.Run(() =>
        {
            Menu layoutMenu = menu.Menus.First(each => each.Title == "Layout");
            layoutMenu.Items.First(item => item.Submenu is not null && item.Label == Said("designer_selection.add")).Submenu!.Everything().Select(pair => pair.Item).First(item => item.Label == "stack").OnPicked!();
            designer.ConvertSelected("group", false);
            group = document.Selected;
        });
        test.Wait();
        PressCtrlShiftG();
        test.Wait();
        test.Check("keys: with a group selected Ctrl+Shift+G is Ungroup", () =>
            group is Group && group.Parent is null && !document.Walk().Any(pair => pair.Component == group) && !messageItem.Text.Contains(noGroup, StringComparison.Ordinal));

        test.Run(() =>
        {
            ShowSide(HexSide.Design);
            compositor.Focus = null;
        });
        test.Wait();
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
