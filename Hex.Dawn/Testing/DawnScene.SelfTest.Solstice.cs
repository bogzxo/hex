using System.Numerics;

using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The Solstice looks, Hex's own: the plum slate and the paper, both drawn flush with Dawn's orange for an accent, and
// the welcome page in them. HEX_SELFTEST_SHOTS names a folder for pictures of each.
internal sealed partial class DawnScene
{
    private void TestSolstice(HexPointerScript test)
    {
        string themeBefore = settings.EditorTheme, skinBefore = settings.EditorSkin;
        string file = Path.Combine(Path.GetTempPath(), $"hex-solstice-{System.Environment.ProcessId}.cs");

        Func<Vector2> InChrome(Func<Vector2> point) => () => chrome.ToWorld(point());

        void Shot(string name)
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, name + ".png"));
        }

        test.Run(() => PickLook(HexLooks.Solstice));
        test.Settle(0.5f);
        test.Check("solstice: the look puts the editor in its theme", () =>
            settings.EditorTheme == "solstice" && compositor.Skin?.Theme == "solstice"
            && HexLooks.Current(settings, terminals.Settings.Theme)?.Name == HexLooks.Solstice.Name);
        test.Check("solstice: the panes are flush and square, on the window's darker tone, the controls still round", () =>
            compositor.Skin is { } skin && HexLooks.Flush(skin) && !Square(skin) && skin.CornerRadius > 0.0f
            && left.Radius == 0.0f && left.Inset == 0.0f && center.Radius == 0.0f && center.Inset == 0.0f
            && codeScroll.Color == skin.FieldColor with { W = 1.0f });
        test.Check("solstice: Dawn's accent is the orange, and a warning isn't taken for it", () =>
            compositor.Skin is { } skin && skin.Palette.Accent.X > 0.9f && skin.Palette.Accent.Z < 0.4f && skin.Palette.Warning != skin.Palette.Accent);

        // Whatever an earlier suite left open is closed, the welcome is only over the empty layout Hex started with
        test.Run(() =>
        {
            welcomeWanted = true;
            Show(NewDocument());
            CloseAll();
        });
        test.Settle(0.3f);
        test.Check("solstice: the welcome page starts at the left with what's new on it", () =>
            (welcome is { Visible: true } ? welcome : plainPage) is { Visible: true } page
            && Descendants(page).OfType<StackPanel>().Any(stack => stack.Children.OfType<Button>().Any(button => button.Label == Said("whats_new.go_profiles"))));
        test.Run(() => Shot("solstice-welcome"));
        test.Settle(0.8f);
        test.Run(() => welcomeWanted = false);
        test.Wait();

        test.Run(() =>
        {
            File.WriteAllText(file, "namespace Signal;\n\n// Keep the interesting bits. Leave the noise behind.\npublic static class Program\n{\n    private static readonly string[] Looks = [\"Solstice\", \"Solstice Day\"];\n\n    public static void Main()\n    {\n        var readings = new[] { 18.4, 19.2, 18.8 };\n        foreach (var look in Looks)\n            Console.WriteLine($\"{look}: {readings.Average():F1}\");\n    }\n}\n");
            Open(file);
        });
        test.Settle(0.5f);
        test.Run(() => Shot("solstice-code"));
        test.Settle(0.8f);
        test.Check("solstice: the horizon is under the title bar and empty with nothing going", () => HorizonLine is { Shown: true, Share: 0.0f });
        test.Run(() => NoteTaskEnded(1));
        test.Settle(0.2f);
        test.Check("solstice: a task that ended badly turns the whole line the error's red", () =>
            HorizonLine is { Share: 1.0f } line && compositor.Skin is { } skin && line.FillColour == skin.Palette.Error);
        test.Run(() => Shot("solstice-failed"));
        test.Settle(0.8f);
        test.Run(() => horizonFailedUntil = 0);
        test.Settle(0.2f);
        test.Check("solstice: and the line is empty again after", () => HorizonLine is { Share: 0.0f });

        // Work in the background that says how far it got fills the line that far, a check for updates or a download
        var gate = new System.Threading.ManualResetEventSlim(false);
        bool told = false;
        test.Run(() => StartWork("solstice-progress", "Solstice self test", context =>
        {
            context.Report(0.4f, "nearly half");
            told = true;
            gate.Wait(TimeSpan.FromSeconds(10));
        }, seen: false));
        test.WaitFor(() => told, 3.0f);
        test.Settle(0.3f);
        test.Check("solstice: work that's 40% done fills two fifths of the line, in the accent", () =>
            HorizonLine is { } line && MathF.Abs(line.Share - 0.4f) < 0.01f && compositor.Skin is { } skin && line.FillColour == skin.Palette.Accent);
        test.Run(() => Shot("solstice-progress"));
        test.Settle(0.8f);
        test.Run(gate.Set);
        test.WaitFor(() => !work.IsActive("solstice-progress"), 3.0f);
        test.Settle(0.3f);
        test.Check("solstice: and done, the line is empty", () => HorizonLine is { Share: 0.0f });

        // And work that goes wrong turns it red like a task that did
        test.Run(() => StartWork("solstice-failing", "Solstice self test", _ => throw new InvalidOperationException("thrown on purpose"), seen: false));
        test.WaitFor(() => !work.IsActive("solstice-failing"), 3.0f);
        test.Settle(0.3f);
        test.Check("solstice: work that failed turns the line red", () =>
            HorizonLine is { Share: 1.0f } line && compositor.Skin is { } skin && line.FillColour == skin.Palette.Error);
        test.Run(() => horizonFailedUntil = 0);
        test.Settle(0.2f);

        // The panel under the editor, on its terminal: open it's dragged by its top edge, on the line between it and the code
        float panelTall = 0;
        Vector2 panelEdge = default;
        test.Run(() =>
        {
            CollapsePanel(false);
            ShowPanel(CONSOLE_PAGE);
        });
        test.Settle(0.6f);
        test.Run(() =>
        {
            panelTall = bottom.Size.Y;
            panelEdge = new Vector2(bottom.Bounds.Center.X, bottom.Bounds.Max.Y);
        });
        test.Check("solstice: the panel with the terminal in it can be dragged by its top", () => bottom.Shown && bottom.Resizable == UIResize.Top);

        // Its titles are the handle: the one of the page that's up folds it down to them, again opens it, another's turns the page
        Vector2 TitleOf(int page) => chrome.ToWorld(new Vector2(bottom.TabBounds(page).Center.X, bottom.TabBounds(page).Center.Y));
        test.Click(() => TitleOf(CONSOLE_PAGE));
        test.WaitFor(() => bottom.Folded && !bottom.IsFolding, 2.0f);
        test.Check("solstice: the title of the page that's showing folds the panel down to its titles, which stay", () =>
            settings.PanelCollapsed && bottom.Folded && bottom.Shown && bottom.Selected == CONSOLE_PAGE && !PanelOpen && bottom.Resizable == UIResize.None);
        test.Click(() => TitleOf(CONSOLE_PAGE));
        test.WaitFor(() => !bottom.Folded && !bottom.IsFolding, 2.0f);
        test.Check("solstice: and pressed again opens it on the same page", () => !settings.PanelCollapsed && PanelOpen && bottom.Selected == CONSOLE_PAGE);
        test.Click(() => TitleOf(OUTPUT_PAGE));
        test.Settle(0.3f);
        test.Check("solstice: another title turns the page and leaves it open", () => PanelOpen && bottom.Selected == OUTPUT_PAGE);
        test.Run(TogglePanel);
        test.WaitFor(() => bottom.Folded && !bottom.IsFolding, 2.0f);
        test.Check("solstice: Ctrl+J folds it to its titles too, never away", () => bottom.Shown && bottom.Folded && bottom.Bounds.Height > 0.0f);
        test.Click(() => TitleOf(CONSOLE_PAGE));
        test.WaitFor(() => !bottom.Folded && !bottom.IsFolding, 2.0f);
        test.Check("solstice: a title of a folded panel opens it on that page", () => PanelOpen && bottom.Selected == CONSOLE_PAGE);
        test.Run(() =>
        {
            panelTall = bottom.Size.Y;
            panelEdge = new Vector2(bottom.Bounds.Center.X, bottom.Bounds.Max.Y);
        });
        test.Drag(InChrome(() => panelEdge), InChrome(() => panelEdge + new Vector2(0, 40)));
        test.Wait();
        test.Check("solstice: and dragging that edge up makes it taller", () => bottom.Size.Y > panelTall + 30.0f && !bottom.IsResizing);
        test.Run(() => Shot("solstice-terminal-panel"));
        test.Settle(0.8f);

        test.Run(() => PickLook(HexLooks.SolsticeDay));
        test.Settle(0.5f);
        test.Check("solstice: the day look is its paper, flush as well", () =>
            compositor.Skin is { Theme: "solstice_day" } skin && HexLooks.Flush(skin) && skin.Palette.LightField && left.Radius == 0.0f);
        test.Run(() => Shot("solstice-day-code"));
        test.Settle(0.8f);

        test.Run(() =>
        {
            CloseAll();
            try { File.Delete(file); } catch (IOException) { }
            settings.EditorSkin = skinBefore;
            UseEditorTheme(themeBefore, save: false);
        });
        test.Settle(0.5f);
        test.Check("solstice: a look that isn't flush has no horizon", () => compositor.Skin is { } skin && (HexLooks.Flush(skin) || HorizonLine is { Shown: false }));
    }
}
