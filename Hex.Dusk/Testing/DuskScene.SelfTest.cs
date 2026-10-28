using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Hex.Dusk;

/// <summary>
/// Dusk testing itself, started with <c>--selftest</c>, in parts that run in the order <see cref="SelfTestSuites"/>
/// lists them, any of them on their own with <c>--selftest keys history</c>. Between two parts Dusk is put back to one
/// tab of one shell, so a part never leans on what the one before it left. The keys and history parts press keys for
/// real, through Windows into Dusk's own window (<see cref="RealKeys"/>), the way a person's keys go, everything else
/// drives Dusk from inside. Every check is printed as it runs, with a tally at the end and an exit code of 1 with
/// <c>--exit</c> if anything failed.
/// </summary>
internal sealed partial class DuskScene
{
    private DuskScript? selfTest;
    private bool selfTestReported;
    private float settle;
    private float animation;

    private void UpdateSelfTest(float dt)
    {
        if (selfTest is null)
            return;

        selfTest.Update(dt);
        if (selfTest.IsFinished && !selfTestReported)
        {
            selfTestReported = true;
            if (options.Exit)
                Quit(selfTest.Failed);
        }
    }

    /// <summary>The parts of the self test, in the order they run.</summary>
    private (string Name, string About, Action<DuskScript> Run)[] SelfTestSuites =>
    [
        ("ux", "inline search and returning from scrollback to live output", TestUx),
        ("symbols", "triangle and arrow glyphs on the terminal cell grid", TestSymbols),
        ("start", "the first tab, its shell, the window fitted round it", TestStart),
        ("plugins", "the plugins started, their commands registered, keys.hor read", TestPlugins),
        ("tabs", "tabs by command and by key", TestTabs),
        ("splits", "splits by command and by key, the focus moved about", TestSplits),
        ("mouse", "the strip, a pane and the plus clicked, an edge dragged", TestMouse),
        ("palette", "the command palette up and down", TestPalette),
        ("themes", "a theme stepped, the animations off and on", TestThemes),
        ("closing", "panes and tabs closed by command, by key and by the x", TestClosing),
        ("shell", "a line sent to the shell comes back", TestShell),
        ("keys", "keys pressed for real, typing, Return, a command's key", TestRealKeys),
        ("history", "the arrows through the lines run, the file it is kept in", TestHistory),
        ("files", "keys.hor, settings.hor and a plugin written from outside and taken in", TestFiles),
        ("sessions", "the tabs and their splits kept and put back, a pane zoomed", TestSessions),
        ("repl", "the HIDL REPL as a profile, typed into for real", TestRepl),
        ("line", "the line typed at a prompt coloured, in PowerShell and cmd, cmd's marks out of its prompt", TestLine),
        ("output", "what programs write coloured by rules, the shipped ones and a plugin's", TestOutput),
        ("scroll", "the scroll bar and the minimap, the thumb dragged, the track paged, the minimap pressed and dragged, reading up while more is written", TestScroll),
        ("panes", "tabs moved, panes swapped and renamed, the settings window, the mouse and the bell, the drop down", TestPanes),
        ("tui", "the programs with a screen of their own, the file picker, git, the processes, the REPL and the settings", TestTui),
        ("blocks", "the shell's marks, the commands as blocks, find, rerun, a line in a split, typing into every pane", TestBlocks),
        ("dawn", "the dawn command typed into every shell there is, a file edited in place and saved, no Dawn said", TestDawn),
        ("effects", "the palette over glass, sparks off the cursor at a prompt, a burst for a long command that went well", TestEffects),
        ("path", "a new shell gets the PATH the system has now, not the one Dusk was started with", TestPath),
        ("micro", "micro in the shell, every key, the mouse, a paste, its splits and colours, how a console TUI gets on in Dusk", TestMicro),
        ("tuikeys", "the arrows pressed for real into micro, the whole way a person's keys go", TestTuiKeys),
        ("claude", "Claude Code in the shell, a TUI drawn on the main screen, its list moved with the arrows", TestClaude),
        ("backdrop", "the picture behind the terminals, switched on, moved and made fainter in the one window of settings, laid out by hand with the mouse, and Editor · Text in it as Dawn has", test =>
        {
            TestBackdrop(test);
            TestBackdropArrange(test);
            TestSettingsText(test);
        })
    ];

    /// <summary>A point inside a rectangle, as far along and up as the shares say, moved across by so much, where the pointer goes.</summary>
    private Func<Vector2> At(Func<UIRect> bounds, float x = 0.5f, float y = 0.5f, float across = 0.0f) => () =>
    {
        UIRect rect = bounds();
        return chrome.ToWorld(new Vector2(rect.Min.X + rect.Width * x + across, rect.Min.Y + rect.Height * y));
    };

    private static UIKeyPress Press(Key key, bool control = true, bool shift = true, bool alt = false) => new(key, control, shift, alt, false);

    private string Shape() => panel.Current?.Tree.Describe(view => view == panel.FocusedView ? "F" : "o") ?? string.Empty;

    private void StartSelfTest()
    {
        var test = selfTest = new DuskScript { Pace = options.Watch ? 8 : 1 };
        compositor.PointerSource = () => test.Pointer;

        // A quick run goes without the animations, a watched one keeps them and waits them out. In the settings and not
        // only on the panel, a settings.hor taken in (the files part writes one) puts the panel back to what they say
        settings.Animations = options.Watch;
        host.ApplySettings();
        animation = panel.AnimationTime;
        settle = animation + 0.1f;

        var known = SelfTestSuites.Select(suite => suite.Name).ToHashSet();
        string[] asked = options.Suites ?? [];
        string[] unknown = [.. asked.Where(name => !known.Contains(name))];
        if (unknown.Length > 0)
        {
            test.Check($"the parts asked for are parts of it ({string.Join(", ", unknown)} aren't, the parts are {string.Join(", ", known)})", () => false);
            return;
        }

        test.Settle(0.5f);
        test.WaitFor(() => started && chrome.Root.Bounds.Width > 0 && panel.Bounds.Width > 100, 5.0f);
        test.Settle(0.2f);

        foreach (var (name, _, run) in SelfTestSuites)
        {
            if (asked.Length > 0 && !asked.Contains(name))
                continue;

            // The parts that press keys for real take the machine's keyboard, so they run only when they are asked for by
            // name or with --realkeys, never in a plain run on a machine somebody is using
            if (RealKeyParts.Contains(name) && !asked.Contains(name) && !options.RealKeys)
            {
                test.Run(() => Console.WriteLine($"[Dusk self-test] {name} left out, it presses real keys, --realkeys or --selftest {name} runs it"));
                continue;
            }

            // Claude Code is somebody's own, signed in, it's only started when it's asked for by name
            if (name == "claude" && !asked.Contains(name))
            {
                test.Run(() => Console.WriteLine("[Dusk self-test] claude left out, it starts your own Claude Code, --selftest claude runs it"));
                continue;
            }

            var took = new System.Diagnostics.Stopwatch();
            test.Run(() =>
            {
                Console.WriteLine($"[Dusk self-test] {name}");
                took.Restart();
            });
            OneShell(test);
            run(test);
            test.Run(() => Console.WriteLine($"[Dusk self-test] {name} took {took.Elapsed.TotalSeconds:0.0} s"));
        }
    }

    /// <summary>Dusk put back to one tab of the usual shell with the keyboard in it, waited for until its prompt is there.</summary>
    private void OneShell(DuskScript test)
    {
        test.Run(() =>
        {
            chrome.Dialog?.Cancel();
            if (panel.Tabs.Count == 1 && panel.Current?.Tree.Count == 1 && panel.FocusedView?.Process is { IsRunning: true })
                return;

            foreach (TerminalTab tab in panel.Tabs.ToArray())
                panel.CloseTab(tab);
            panel.NewTab(host.UsualProfile, options.StartIn);
        });
        test.Settle(settle);
        test.WaitFor(() => panel.FocusedView?.Screen.Text().Trim().Length > 0, 10.0f);
        test.Run(() =>
        {
            if (panel.FocusedView is { } view)
                panel.FocusPane(view);
        });
        test.Wait();
    }

    private void TestStart(DuskScript test)
    {
        test.Check("starts with one tab of the usual shell", () => panel.Tabs.Count == 1 && panel.FocusedView?.Process is { IsRunning: true });
        test.Check("the shell wrote a prompt", () => panel.FocusedView?.Screen.Text().Trim().Length > 0);
        test.Check("the panel is fitted between the bars", () => Math.Abs(panel.Bounds.Height - (chrome.Root.Bounds.Height - top.Bounds.Height - statusBar.Bounds.Height)) < 3.0f);
        test.Check("the window says which tab", () => top.Title.Contains("Dusk"));
        test.Check("the terminal has the keyboard", () => compositor.Focus == panel.FocusedView);
    }

    private void TestPlugins(DuskScript test)
    {
        test.Check("the core, themes and profiles plugins started", () => host.Plugins.Count(plugin => plugin.Enabled && plugin.Failed is null && plugin.Runtime is not null) >= 3);
        test.Check("the plugins registered the commands", () => host.Commands.Contains("new_tab") && host.Commands.Contains("split_right") && host.Commands.Contains("theme_next") && host.Commands.Count > 25);
        test.Check("keys.hor put new_tab on Ctrl+Shift+T", () => KeyOf("new_tab") == "Ctrl+Shift+T" && KeyOf("palette") == "Ctrl+Shift+P");
        test.Check("the window plugin puts the swap to Dawn on Ctrl+Alt+D", () => KeyOf("dawn_mode") == "Ctrl+Alt+D");
        test.Check("the menus say the keys", () => menu.Menus[0].Items.First(item => item.Label == "New tab").Shortcut == "Ctrl+Shift+T");
        test.Check("a profile a shell, the machine's in it", () => OperatingSystem.IsWindows()
            ? panel.Profiles.Count >= 2 && panel.Profiles.Exists(profile => profile.Id == "cmd")
            : panel.Profiles.Exists(profile => profile.Id == Path.GetFileName(Environment.GetEnvironmentVariable("SHELL") ?? "sh") || profile.Id is "bash" or "zsh"));
        test.Check("the dusk object has exactly the names the tests know it by", () =>
            host.AnyObject is { } any && any.Properties.Keys.Order().SequenceEqual(ApiNames.Order()));
        test.Check("every command keys.hor names is one a plugin registered", () =>
            TerminalCommands.ReadKeys(Path.Combine(AppContext.BaseDirectory, DEFAULT_KEYS)).All(bound => host.Commands.Contains(bound.Name)));
    }

    private void TestTabs(DuskScript test)
    {
        test.Run(() => Run("new_tab"));
        test.Settle(settle);
        test.Check("new_tab opens a second tab and selects it", () => panel.Tabs.Count == 2 && panel.CurrentIndex == 1);
        test.Check("the strip shows two tabs", () => Descendants(panel).OfType<TabStrip>().First().Tabs.Count == 2);

        test.Run(() => KeyPressed(Press(Key.T)));
        test.Settle(settle);
        test.Check("Ctrl+Shift+T in a terminal opens a third", () => panel.Tabs.Count == 3 && panel.CurrentIndex == 2);
        test.Check("a key the shell wants goes to the shell", () => !KeyPressed(new UIKeyPress(Key.C, true, false, false, false)));
        test.Check("so does Return", () => !KeyPressed(new UIKeyPress(Key.Enter, false, false, false, false)));

        test.Run(() => KeyPressed(Press(Key.Tab, shift: false)));
        test.Settle(settle);
        test.Check("Ctrl+Tab goes round to the first", () => panel.CurrentIndex == 0);

        test.Run(() => Run("tab_3"));
        test.Settle(settle);
        test.Check("tab_3 picks the third", () => panel.CurrentIndex == 2);
        test.Check("the selected tab's terminal has the keyboard", () => compositor.Focus == panel.FocusedView);

        // More than fit, named long enough that three don't even across a 4K screen, and a chevron lists them all
        test.Check("tabs that fit have no chevron", () => !panel.TabsOverflow);
        test.Run(() =>
        {
            foreach (TerminalTab tab in panel.Tabs)
                panel.Rename(tab, string.Concat(Enumerable.Repeat("a tab with a name long enough that three run off the strip ", 10)) + panel.IndexOf(tab));
        });
        test.Wait();
        test.Check("tabs that don't fit bring up a chevron before the plus", () => panel.TabsOverflow && panel.TabsListBounds.Width > 0.0f);
        test.Run(() => Shot("tabs-overflow"));
        test.Click(At(() => panel.TabsListBounds));
        test.Wait();
        test.Check("which lists every tab, the open one ticked", () =>
            chrome.Root.Children.OfType<ContextMenu>().FirstOrDefault()?.Showing is { } shown && shown.Items.Count == panel.Tabs.Count
            && shown.Items[panel.CurrentIndex].IsChecked?.Invoke() == true);
        test.Click(At(() => chrome.Root.Children.OfType<ContextMenu>().First().ItemBounds(0)));
        test.Wait();
        test.Settle(settle);
        test.Check("and a pick opens that one", () => panel.CurrentIndex == 0);
        test.Run(() =>
        {
            foreach (TerminalTab tab in panel.Tabs)
                panel.Rename(tab, null);
        });
        test.Wait();
        test.Check("named short again, the chevron goes", () => !panel.TabsOverflow);
    }

    private void TestSplits(DuskScript test)
    {
        test.Run(() => Run("split_right"));
        test.Settle(settle);
        test.Check("split_right makes two panes side by side, the new one focused", () => Shape() == "[o|F]");
        test.WaitFor(() => panel.FocusedView?.Process is { IsRunning: true }, 3.0f);
        test.Check("the new pane has a shell of its own", () => panel.FocusedView?.Process is { IsRunning: true });

        test.Run(() => KeyPressed(Press(Key.E)));
        test.Settle(settle);
        test.Check("Ctrl+Shift+E splits the focused pane down", () => Shape() == "[o|[o/F]]");
        test.Check("the panes have edges to drag between them", () => panel.FocusedView?.Resizable == (UIResize.Left | UIResize.Top));
        test.Check("the new panes are on screen", () => panel.Current!.Views.All(view => view.Visible));
        test.WaitFor(() => panel.FocusedView?.Screen.Text().Trim().Length > 0, 8.0f);

        // The test's pointer starts in the middle of the window, which is right on the edge between the panes, and lit it
        // up in every shot. Put over the status bar it's on no edge, and none of them shows its handle
        test.Move(At(() => statusBar.Bounds, 0.5f, 0.5f));
        test.Wait();
        test.Check("the edges between the panes show nothing while the pointer isn't on them", () =>
            panel.Current!.Views.All(view => view.ActiveGrip == UIResize.None));
        test.Run(() => Shot("splits"));
        test.Check("every pane has its title along its top, split or not", () => panel.Current!.Views.All(view => view.ShowPaneTitle && view.PaneBadge.Height > 0.0f));

        test.Run(() => Run("focus_left"));
        test.Check("focus_left goes to the pane on the left", () => Shape() == "[F|[o/o]]");
        test.Run(() => Run("focus_right"));
        test.Run(() => Run("focus_down"));
        test.Check("focus_right and focus_down come back", () => Shape() == "[o|[o/F]]");
        test.Check("the focused pane is framed, the panel knows which", () => panel.Current?.Focused?.Content == panel.FocusedView);
        test.Run(() => KeyPressed(Press(Key.Up)));
        test.Check("Ctrl+Shift+Up goes up a pane", () => Shape() == "[o|[F/o]]");
    }

    private void TestMouse(DuskScript test)
    {
        test.Run(() => Run("new_tab"));
        test.Settle(settle);
        test.Click(At(() => Descendants(panel).OfType<TabStrip>().First().TabBounds(0)));
        test.Wait();
        test.Settle(settle);
        test.Check("a click on a tab selects it", () => panel.CurrentIndex == 0);
        test.Check("and its terminal has the keyboard", () => compositor.Focus == panel.FocusedView);

        test.Run(() => Run("split_right"));
        test.Settle(settle);
        test.Click(At(() => panel.Current!.Tree.Leaves.First().Bounds));
        test.Wait();
        test.Check("a click into a pane gives it the keyboard", () => panel.Current?.Tree.Leaves.First().Content == panel.FocusedView && compositor.Focus == panel.FocusedView);

        // The edge is taken hold of where it is and let go 120 pixels to the right, both worked out before the drag,
        // a target read off the pane as it widens would run away from the pointer
        float before = 0.0f;
        Vector2 grab = Vector2.Zero, drop = Vector2.Zero;
        test.Run(() =>
        {
            before = panel.Current!.Tree.Leaves.First().Bounds.Width;
            grab = At(() => panel.Current!.Tree.Leaves.First().Bounds, 1.0f, 0.5f)();
            drop = At(() => panel.Current!.Tree.Leaves.First().Bounds, 1.0f, 0.5f, 120.0f)();
        });
        test.Drag(() => grab, () => drop);
        test.Wait();
        test.Run(() => Shot("dragged"));
        test.Check("dragging the edge widens the pane by as far as the pointer went", () => Math.Abs(panel.Current!.Tree.Leaves.First().Bounds.Width - (before + 120.0f)) < 12.0f);
        test.Check("and the ratio of the split says so", () => panel.Current!.Tree.Root!.Ratio > 0.55f);

        // The x on the other tab
        test.Click(At(() => Descendants(panel).OfType<TabStrip>().First().CloseBounds(1)));
        test.Wait();
        test.Settle(settle);
        test.Check("the x on a tab closes it", () => panel.Tabs.Count == 1);

        // The dropdown chooses a profile; the plus starts the default shell directly.
        test.Click(At(() => panel.ProfilePickerBounds));
        test.Wait();
        test.Check("the dropdown lists the profiles", () => chrome.Root.Children.OfType<ContextMenu>().FirstOrDefault()?.Showing is { } shown && shown.Items.Count == panel.Profiles.Count);
        test.Click(At(() => chrome.Root.Children.OfType<ContextMenu>().First().ItemBounds(0)));
        test.Wait();
        test.Settle(settle);
        test.Check("and a pick opens a tab of that profile", () => panel.Tabs.Count == 2 && panel.Current?.Profile == panel.Profiles[0]);
    }

    private void TestPalette(DuskScript test)
    {
        test.Run(() => Run("palette"));
        test.Wait();
        test.Run(() => Shot("palette"));
        // A title once, Close pane is on two keys and the palette lists it the one time. Not Tab 1 to Tab 9, their keys are enough.
        // Every look, and laying the backdrop out by hand
        test.Check("the command palette comes up with every command, and every look", () => chrome.Dialog is { IsOpen: true }
            && Descendants(chrome.Dialog).OfType<ListBox>().First().Items.Length == host.Commands.All.Where(command => !IsTabNumber(command.Name)).Select(command => command.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                + Hex.Shell.HexLooks.All(new Hex.Core.HexSettings(host.Settings.Path)).Count + 1);
        test.Check("a few letters narrow it down", () =>
        {
            var box = Descendants(chrome.Dialog!).OfType<TextBox>().First();
            box.Text = "splitr";
            box.OnChanged?.Invoke(box.Text);
            var list = Descendants(chrome.Dialog!).OfType<ListBox>().First();
            return list.Items.Length > 0 && list.Items.Length < host.Commands.Count && list.Items[0].StartsWith("Split right");
        });
        test.Run(() => chrome.Dialog?.Cancel());
        test.Wait();
        test.Check("and goes away", () => chrome.Dialog is null);
    }

    private void TestThemes(DuskScript test)
    {
        string themeBefore = string.Empty;
        test.Run(() => themeBefore = settings.Theme);
        test.Run(() => Run("theme_next"));
        test.Wait();
        test.Wait();
        test.Check("theme_next changes the theme", () => settings.Theme != themeBefore && compositor.Skin?.Theme == settings.Theme);
        test.Check("the terminals take their colours from it", () => panel.Palette.Background == (compositor.Skin!.PanelColor with { W = 1.0f }) && panel.FocusedView?.Palette == panel.Palette);

        // Visual Studio's, the panes flush and square and the terminals in Campbell, its magenta not the keywords' blue
        test.Run(() => UseTheme(Hex.Shell.HexLooks.VISUAL_STUDIO_THEME));
        test.Wait();
        test.Wait();
        test.Settle(0.5f);
        test.Run(() => Shot("visual_studio"));
        test.Check("Visual Studio's theme has the panes square and the terminals in Campbell", () =>
            compositor.Skin is { CornerRadius: 0.0f } && panel.PaneRadius == 0.0f
            && System.Numerics.Vector4.Distance(panel.Palette.Ansi[5], new System.Numerics.Vector4(0x88 / 255.0f, 0x17 / 255.0f, 0x98 / 255.0f, 1.0f)) < 0.01f);
        test.Run(() => UseTheme(themeBefore));
        test.Wait();
        test.Wait();
        test.Check("and back", () => settings.Theme == themeBefore);

        // One setting, the panel's tweens and the cursor's glide together, on and off and back to how the test has them
        bool animated = false;
        test.Run(() => animated = settings.Animations);
        test.Run(() => Run("animations"));
        test.Check("the animations go on and off with one setting", () => settings.Animations != animated
            && (panel.AnimationTime > 0.0f) == (settings.Animations && uiAnimation > 0.0f)
            && panel.FocusedView?.Animated == (settings.Animations && uiAnimation > 0.0f));
        test.Run(() => Run("animations"));
        test.Check("and back", () => settings.Animations == animated && panel.AnimationTime == animation);
    }

    private void TestClosing(DuskScript test)
    {
        test.Run(() => Run("split_right"));
        test.Settle(settle);
        test.Run(() => Run("split_down"));
        test.Settle(settle);
        test.Run(() => Run("close_pane"));
        test.Settle(settle);
        test.Check("close_pane leaves two panes, the sibling taking the room", () => panel.Current?.Tree.Count == 2 && panel.FocusedView is not null);
        test.Run(() => KeyPressed(Press(Key.Q)));
        test.Settle(settle);
        test.Check("Ctrl+Shift+Q closes another", () => panel.Current?.Tree.Count == 1);
        test.Check("the last pane has no edges", () => panel.FocusedView?.Resizable == UIResize.None);

        // Closing closes the active pane, the newest split unless another was picked, the tab only with its last
        test.Run(() => Run("split_right"));
        test.Settle(settle);
        test.Run(() => Run("split_down"));
        test.Settle(settle);
        TerminalView? innermost = null;
        test.Run(() => innermost = panel.FocusedView);
        test.Check("a new split is the active pane", () => Shape() == "[o|[o/F]]");
        test.Run(() => KeyPressed(Press(Silk.NET.Input.Key.W)));
        test.Settle(settle);
        test.Check("Ctrl+Shift+W closes the active pane and not the tab", () => panel.Tabs.Count == 1 && panel.Current?.Tree.Count == 2 && panel.TabOf(innermost!) is null);
        test.Check("and the keyboard goes to the innermost pane left", () => Shape() == "[o|F]");
        test.Run(() => panel.FocusPane(panel.Current!.Tree.Leaves.First().Content!));
        test.Run(() => Run("close_tab"));
        test.Settle(settle);
        test.Check("one picked by hand is the one closed", () => panel.Current?.Tree.Count == 1 && Shape() == "F");
        test.Run(() => Run("split_right"));
        test.Settle(settle);
        test.Run(() => Run("close_whole_tab"));
        test.Settle(settle);
        test.Check("close_whole_tab takes the tab with all its panes", () => panel.Tabs.Count == 0);
        test.Run(() => panel.NewTab(host.UsualProfile, options.StartIn));
        test.Settle(settle);

        test.Run(() => Run("new_tab"));
        test.Settle(settle);
        test.Run(() => Run("close_tab"));
        test.Settle(settle);
        test.Check("close_tab on a tab of one pane closes the tab, its neighbour selected", () => panel.Tabs.Count == 1 && panel.Current is not null && panel.FocusedView?.Visible == true);
        test.Check("and it has the keyboard", () => compositor.Focus == panel.FocusedView);
    }

    private void TestShell(DuskScript test)
    {
        test.Run(() => panel.FocusedView?.Send("echo dusk-self-test\r"));
        test.WaitFor(() => Count(panel.FocusedView, "dusk-self-test") >= 2, 8.0f);
        test.Check("a line sent to the shell runs and comes back", () => Count(panel.FocusedView, "dusk-self-test") >= 2);
    }

    /* Keys pressed for real */

    // Windows takes keys from SendInput for whichever window is in front, so Dusk is brought there and asked again
    // until it is, and the parts that press keys say so when it never is (a locked screen, another program that
    // wouldn't let go) rather than pressing keys into something else
    private bool keysReach;

    /// <summary>The parts that press keys for real.</summary>
    private static readonly HashSet<string> RealKeyParts = ["keys", "history", "repl", "tuikeys"];

    private void BringToFront(DuskScript test)
    {
        test.Run(() => keysReach = RealKeys.Focus());
        test.WaitFor(() => keysReach || (keysReach = RealKeys.Focus()), 3.0f);
        test.Check("Dusk's window is in front, so the keys go to it", () => keysReach);
        test.Settle(0.2f);
    }

    private void TypeText(DuskScript test, string text)
    {
        test.Run(() =>
        {
            if (keysReach && RealKeys.InFront())
                RealKeys.Type(text);
            else
                keysReach = false;
        });
        test.Settle(0.3f);
    }

    private void PressKey(DuskScript test, ushort key)
    {
        test.Run(() =>
        {
            if (keysReach && RealKeys.InFront())
                RealKeys.Press(key);
            else
                keysReach = false;
        });
        test.Settle(0.2f);
    }

    private void TestRealKeys(DuskScript test)
    {
        if (!RealKeys.Available)
        {
            test.Check("keys pressed for real are Windows only, skipped", () => true);
            return;
        }

        BringToFront(test);
        TypeText(test, "echo dusk-real-keys");
        test.WaitFor(() => Count(panel.FocusedView, "dusk-real-keys") >= 1, 3.0f);
        test.Check("what is typed reaches the shell", () => Count(panel.FocusedView, "dusk-real-keys") == 1);

        PressKey(test, RealKeys.ENTER);
        test.WaitFor(() => Count(panel.FocusedView, "dusk-real-keys") >= 2, 5.0f);
        test.Run(() => Shot("return"));
        test.Check("Return runs the line", () => Count(panel.FocusedView, "dusk-real-keys") >= 2);

        TypeText(test, "echo dusk-backspace-x");
        PressKey(test, RealKeys.BACKSPACE);
        TypeText(test, "y");
        PressKey(test, RealKeys.ENTER);
        test.WaitFor(() => Count(panel.FocusedView, "dusk-backspace-y") >= 2, 5.0f);
        test.Check("Backspace takes a letter off", () => Count(panel.FocusedView, "dusk-backspace-y") >= 2 && Count(panel.FocusedView, "dusk-backspace-x") == 0);

        // A command's key, pressed for real, goes to Dusk and not to the shell
        test.Run(() => RealKeys.Chord(RealKeys.CONTROL, RealKeys.SHIFT, (ushort)'T'));
        test.Check("the keys reached Dusk and nothing else", () => keysReach && RealKeys.HeldBack == 0);
        test.Settle(settle + 0.3f);
        test.Check("Ctrl+Shift+T pressed for real opens a tab", () => panel.Tabs.Count == 2);
        test.WaitFor(() => panel.FocusedView?.Screen.Text().Trim().Length > 0, 10.0f);
        TypeText(test, "echo dusk-second-tab");
        PressKey(test, RealKeys.ENTER);
        test.WaitFor(() => Count(panel.FocusedView, "dusk-second-tab") >= 2, 5.0f);
        test.Check("and Return runs a line in the new tab", () => Count(panel.FocusedView, "dusk-second-tab") >= 2);

        // A split, then Return in it
        test.Run(() => RealKeys.Chord(RealKeys.CONTROL, RealKeys.SHIFT, (ushort)'D'));
        test.Settle(settle + 0.3f);
        test.WaitFor(() => panel.FocusedView?.Screen.Text().Trim().Length > 0, 10.0f);
        TypeText(test, "echo dusk-in-split");
        PressKey(test, RealKeys.ENTER);
        test.WaitFor(() => Count(panel.FocusedView, "dusk-in-split") >= 2, 5.0f);
        test.Check("Return runs a line in a split", () => Shape() == "[o|F]" && Count(panel.FocusedView, "dusk-in-split") >= 2);
    }

    private void TestHistory(DuskScript test)
    {
        test.Run(() => host.ClearHistory());
        test.Check("the history is kept in its file, in the home the self test has", () => host.History.Path?.StartsWith(TerminalFiles.UserFolder) == true);

        if (!RealKeys.Available)
        {
            test.Check("the arrows pressed for real are Windows only, skipped", () => true);
            return;
        }

        BringToFront(test);
        TypeText(test, "echo dusk-history-one");
        PressKey(test, RealKeys.ENTER);
        test.WaitFor(() => Count(panel.FocusedView, "dusk-history-one") >= 2, 5.0f);
        TypeText(test, "echo dusk-history-two");
        PressKey(test, RealKeys.ENTER);
        test.WaitFor(() => Count(panel.FocusedView, "dusk-history-two") >= 2, 5.0f);

        test.Check("both lines went into the history", () => host.History.Lines.Count >= 2 && host.History.Lines[^2] == "echo dusk-history-one" && host.History.Lines[^1] == "echo dusk-history-two");
        test.Check("and into the file", () => File.Exists(host.History.Path) && File.ReadAllText(host.History.Path!).Contains("echo dusk-history-one\necho dusk-history-two\n"));

        PressKey(test, RealKeys.UP);
        test.Run(() => Shot("history_up"));
        test.Check("Up puts the last line on the prompt", () => PromptLine().EndsWith("echo dusk-history-two"));
        PressKey(test, RealKeys.UP);
        test.Check("Up again the one before it", () => PromptLine().EndsWith("echo dusk-history-one"));
        PressKey(test, RealKeys.DOWN);
        test.Check("Down comes back", () => PromptLine().EndsWith("echo dusk-history-two"));
        PressKey(test, RealKeys.DOWN);
        test.Check("Down past the newest is the line that was being typed, nothing", () => !PromptLine().Contains("dusk-history"));

        TypeText(test, "echo dusk-history-half");
        PressKey(test, RealKeys.UP);
        PressKey(test, RealKeys.DOWN);
        test.Check("a line half typed comes back after Up and Down", () => PromptLine().EndsWith("echo dusk-history-half"));
        PressKey(test, RealKeys.ENTER);
        test.WaitFor(() => Count(panel.FocusedView, "dusk-history-half") >= 2, 5.0f);

        PressKey(test, RealKeys.UP);
        PressKey(test, RealKeys.UP);
        PressKey(test, RealKeys.ENTER);
        test.WaitFor(() => Count(panel.FocusedView, "dusk-history-two") >= 4, 5.0f);
        test.Check("a line brought up and run, runs", () => Count(panel.FocusedView, "dusk-history-two") >= 4);
        test.Check("and the same line twice is kept once", () => host.History.Lines.Count(line => line == "echo dusk-history-two") == 2);

        // The history is shared, a new tab steps the same lines
        test.Run(() => Run("new_tab"));
        test.Settle(settle);
        test.WaitFor(() => panel.FocusedView?.Screen.Text().Trim().Length > 0, 10.0f);
        BringToFront(test);
        PressKey(test, RealKeys.UP);
        test.Check("a new tab steps the same history", () => PromptLine().EndsWith("echo dusk-history-two"));
        PressKey(test, RealKeys.DOWN);

        // Off, the arrows go to the shell
        test.Run(() => settings.History = false);
        test.Run(() => panel.FocusedView?.Send("\u001b"));
        test.Check("with the history off, Up is the shell's", () => !KeyPressed(new UIKeyPress(Silk.NET.Input.Key.Up, false, false, false, false)));
        test.Run(() => settings.History = true);
    }

    /// <summary>The text of the line the cursor of the focused terminal is on, trimmed at its end.</summary>
    private string PromptLine()
    {
        if (panel.FocusedView is not { } view)
            return string.Empty;

        TerminalScreen screen = view.Screen;
        return screen.Grid[screen.CursorY].Text().TrimEnd();
    }

    /// <summary>How many times a text is on a terminal's screen and in its scrollback, a row the edge wrapped joined to the next.</summary>
    private static int Count(TerminalView? view, string text)
    {
        if (view is null)
            return 0;

        string all = view.Screen.AllText();
        int count = 0;
        for (int at = all.IndexOf(text, StringComparison.Ordinal); at >= 0; at = all.IndexOf(text, at + text.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>A screenshot of this moment into the folder DUSK_SELFTEST_SHOTS names, for whoever wants to see, nothing without it.</summary>
    private void Shot(string name)
    {
        if (System.Environment.GetEnvironmentVariable("DUSK_SELFTEST_SHOTS") is { Length: > 0 } folder)
            Engine.CaptureScreenshot(Path.Combine(folder, name + ".png"));
    }

    /// <summary>Everything inside of a component, all the way down.</summary>
    private static IEnumerable<UIComponent> Descendants(UIComponent of)
    {
        foreach (UIComponent child in of.Children)
        {
            yield return child;
            foreach (UIComponent inside in Descendants(child))
                yield return inside;
        }
    }
}
