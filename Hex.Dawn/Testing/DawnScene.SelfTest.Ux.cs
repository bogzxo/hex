using Hex.Shell;
using Horizon.Rendering;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestUx(HexPointerScript test)
    {
        OnCode(test, dataFile);
        float scale = compositor.Scale;
        float keptLeft = settings.LeftWidth;
        float keptRight = settings.RightWidth;
        float wideSideWidth = 0;
        HexDocument? overflowPicked = null;
        test.Check("ux: panel navigation is aligned left", () => bottom.HeaderAlign == Origin.Left);
        test.Check("ux: mode switch sits immediately beside the window controls", () =>
            modeToggle.Parent is { } modes && MathF.Abs(modes.Bounds.Max.X - ((TitleBar)top).ButtonBounds(0).Min.X) < 1);
        test.Run(() => { sideTree.Focus(); LetGo(); OnReturned(); });
        test.Check("ux: returning from the other mode restores the sidebar's input", () => compositor.Focus == sideTree);
        test.Run(() =>
        {
            code.Focus();
            for (int i = 0; i < 12; i++)
            {
                string path = Path.Combine(testFolder, $"ux-document-{i}.txt");
                File.WriteAllText(path, "one\ntwo\n");
                Open(path);
            }
        });
        test.Settle(0.2f);
        test.Check("ux: opening text keeps the code visible and designer controls hidden", () => document.IsText && code.Text.StartsWith("one")
            && codeScroll.Visible && !canvas.Visible && !nameBox.Visible && !layout.Get<Button>("design_view").Visible);
        test.Check("ux: crowded editor tabs have a visible list button", () => allTabs.Visible);
        test.Run(() => allTabs.OnPressed!());
        test.Wait();
        test.Check("ux: the editor list contains every open document", () => textDialog is { IsOpen: true }
            && Descendants(textDialog).OfType<ListBox>().First().Items.Length == documents.Count);
        test.Run(() => textDialog?.Cancel());
        test.Wait();
        test.Check("ux: dismissing the editor list returns input to the editor", () => compositor.Focus == code);
        test.Run(() => { wideSideWidth = fittedLeft; SetScale(2.0f, remember: false); });
        test.Settle(0.2f);
        test.Check("ux: mobile title bar and menus follow the chosen UI zoom", () =>
        {
            bool nativeChrome = (Engine.WindowManager.Features & Horizon.Platform.PlatformFeatures.Window) != 0;
            return ((TitleBar)top).PixelSized == nativeChrome
                && MathF.Abs(menu.TextScale - MENU_TEXT / (nativeChrome ? 2.0f : 1.0f)) < 0.0001f
                && MathF.Abs(modeToggle.LabelScale - 0.14f / (nativeChrome ? 2.0f : 1.0f)) < 0.0001f;
        });
        test.Check("ux: narrow editor keeps file navigation on its own row", () => tabRow.Direction == UIDirection.Vertical
            && docTabs.Bounds.Max.X <= center.Bounds.Max.X + 1.0f
            && allTabs.Bounds.Max.X <= center.Bounds.Max.X + 1.0f);
        test.Check("ux: enlarged sidebar leaves the document most of the workspace", () =>
            left.Bounds.Width < chrome.Root.Bounds.Width * 0.4f && center.Bounds.Max.X <= chrome.Root.Bounds.Max.X + 1);
        test.Check("ux: fitting preserves the chosen panel widths", () => settings.LeftWidth == keptLeft && settings.RightWidth == keptRight);
        test.Check("ux: active crowded tab is wholly visible", () =>
            docTabs.TabBounds(docTabs.Selected).Min.X >= docTabs.Bounds.Min.X - 1
            && docTabs.TabBounds(docTabs.Selected).Max.X <= docTabs.Bounds.Max.X + 1);
        test.Check("ux: clipped inactive tabs have no pointer target", () =>
        {
            for (int i = 0; i < docTabs.Tabs.Count; i++)
            {
                var area = docTabs.TabBounds(i);
                if (i == docTabs.Selected || area.Max.X <= docTabs.Bounds.Min.X || area.Min.X >= docTabs.Bounds.Max.X)
                    continue;
                if (area.Min.X < docTabs.Bounds.Min.X - 1 || area.Max.X > docTabs.Bounds.Max.X + 1)
                {
                    var point = new System.Numerics.Vector2(Math.Clamp(area.Center.X, docTabs.Bounds.Min.X + 1, docTabs.Bounds.Max.X - 1), area.Center.Y);
                    if (docTabs.TabAt(point) == i) return false;
                }
            }
            return true;
        });
        test.Click(() => chrome.ToWorld(allTabs.Bounds.Center));
        test.Wait();
        test.Run(() =>
        {
            overflowPicked = documents.First(open => open.Path == Path.Combine(testFolder, "ux-document-0.txt"));
            ListBox list = Descendants(textDialog!.Body).OfType<ListBox>().First();
            int at = Array.FindIndex(list.Items, item => item.Contains("ux-document-0.txt", StringComparison.Ordinal));
            list.Choose(at);
            list.OnActivated!(list.Items[at]);
        });
        test.Settle(0.3f);
        test.Check("ux: selecting an overflowed file opens it and focuses code at 200%", () =>
            document == overflowPicked && compositor.Focus == code && textDialog is not { IsOpen: true });
        test.Check("ux: whole tab navigation starts at the strip edge", () =>
            Enumerable.Range(0, docTabs.Tabs.Count).Any(i => MathF.Abs(docTabs.TabBounds(i).Min.X - docTabs.Bounds.Min.X) <= 1));
        test.Run(() => Shot("ux-narrow-editor"));
        test.Settle(0.5f);
        test.Run(() => SetScale(scale, remember: false));
        test.Settle(0.2f);
        test.Check("ux: widening retains the overflow selection and editor focus", () => document == overflowPicked && compositor.Focus == code);
        test.Check("ux: widening restores the sidebar preference", () => MathF.Abs(fittedLeft - wideSideWidth) <= 1);
        test.Run(() => SetScale(2.0f, remember: false));
        test.Settle(0.2f);
        test.Check("ux: narrowing again retains the overflow selection and editor focus", () => document == overflowPicked && compositor.Focus == code
            && docTabs.TabBounds(docTabs.Selected).Min.X >= docTabs.Bounds.Min.X - 1
            && docTabs.TabBounds(docTabs.Selected).Max.X <= docTabs.Bounds.Max.X + 1);
        test.Run(() => { SetScale(scale, remember: false); CloseAll(); });
        test.Settle(0.2f);
        test.Run(() => Shot("ux-editor"));
        test.Settle(0.1f);
        TestWelcomeContinuity(test);
    }

    private void TestWelcomeContinuity(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "welcome-recent-continuity.txt");
        string second = Path.Combine(testFolder, "welcome-second.txt");
        string[] kept = recent.ToArray();
        bool RowsMatch() => welcomeSizes.Count == 2 && welcomeSizes.All(s =>
            s.List.Children.OfType<Button>().Select(b => b.Tooltip).SequenceEqual(recent));
        test.Run(() =>
        {
            File.WriteAllText(file, "welcome continuity\n");
            File.WriteAllText(second, "second recent file\n");
            testRecent = true;
            ForgetRecent();
            welcomeWanted = true;
            plainWelcome = false;
            CloseAll();
            document.View = HexView.Design;
            ApplyView();
        });
        test.Settle(0.3f);
        test.Check("ux: designer welcome shows the empty recent state", () => welcome is { Visible: true }
            && welcomeSizes.Single().List.Children.OfType<Label>().Any());
        test.Run(() => { plainWelcome = true; });
        test.Settle(0.3f);
        test.Check("ux: plain welcome also shows empty recents", () => plainPage is { Visible: true } && RowsMatch()
            && welcomeSizes.All(s => s.List.Children.OfType<Label>().Any()));
        test.Check("ux: plain welcome puts away an empty Outline", () => Side != HexSide.Outline || !left.Shown);
        test.Run(() => Shot("ux-welcome"));
        test.Settle(0.2f);
        test.Run(() => { Remember(file); Remember(second); });
        test.Settle(0.3f);
        test.Check("ux: remembering updates visible and cached welcome lists and File history", () => RowsMatch()
            && recentMenu.Items[0].Label.Contains(Path.GetFileName(second)));
        test.Check("ux: welcome rows fit their recent column with readable targets", () =>
            welcomeSizes.Last().List.Children.OfType<Button>().All(b => b.Bounds.Height >= 29
                && b.Bounds.Max.X <= b.Parent!.Bounds.Max.X + 1 && b.Tooltip.Length > 0));
        test.Run(() => Shot("ux-welcome-populated"));
        test.Settle(0.1f);
        test.Run(() => welcomeSizes.Last().List.Children.OfType<Button>().Last().OnPressed!());
        test.Settle(0.3f);
        test.Check("ux: welcome recent reopens the file and promotes it everywhere", () => document.Path == file
            && recent[0] == file && RowsMatch() && recentMenu.Items[0].Label.Contains(Path.GetFileName(file)));
        test.Check("ux: opening a recent document restores Outline", () => Side != HexSide.Outline || settings.SideBarHidden || left.Shown);
        test.Run(() => { plainWelcome = false; CloseAll(); document.View = HexView.Design; ApplyView(); });
        test.Settle(0.3f);
        test.Check("ux: returning to cached designer welcome retains the updated order", () => welcome is { Visible: true } && RowsMatch());
        test.Run(() => recentMenu.Items.Last().OnPicked!());
        test.Settle(0.3f);
        test.Check("ux: clearing File history clears both cached welcome surfaces", () => recent.Count == 0 && RowsMatch()
            && welcomeSizes.All(s => s.List.Children.OfType<Label>().Count() == 1));
        test.Run(() => { plainWelcome = true; });
        test.Settle(0.3f);
        test.Check("ux: cleared plain welcome has no stale file actions", () => plainPage is { Visible: true }
            && !welcomeSizes.Last().List.Children.OfType<Button>().Any());
        test.Run(() =>
        {
            testRecent = false;
            recent.Clear(); recent.AddRange(kept); WriteRecent();
            welcomeWanted = plainWelcome = false;
            document.View = HexView.Design; ApplyView();
        });
    }
}
