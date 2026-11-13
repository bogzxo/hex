using System.Numerics;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestInteractions(HexPointerScript test)
    {
        OnCode(test, dataFile);
        int behindClicks = 0, picked = 0;
        Button behind = null!;
        TextBox query = null!;
        UIDialog shown = null!;
        Vector2 Outside() => chrome.ToWorld(behind.Bounds.Center);
        void Remember() { shown = chrome.Dialog!; query = Descendants(shown).OfType<TextBox>().First(); }
        test.Run(() =>
        {
            behind = chrome.AddComponent(new Button("Behind palette") {
                Anchor = Origin.BottomLeft, Pivot = Origin.BottomLeft, Position = new Vector2(70, 50),
                Size = new Vector2(140, 32), OnPressed = () => behindClicks++ });
            code.Focus(); CommandPalette(); Remember();
        });
        test.Settle(0.3f);
        test.Check("interaction: command palette starts with typing focus", () => compositor.Focus == query);
        test.Run(() => Shot("interaction-command-palette"));
        test.Click(Outside);
        test.Check("interaction: outside click dismisses without activating the workspace and restores code focus", () =>
            !shown.IsOpen && behindClicks == 0 && compositor.Focus == code);
        test.Click(Outside);
        test.Check("interaction: the next click can activate the workspace normally", () => behindClicks == 1);

        test.Run(() => { code.Focus(); CommandPalette(); Remember(); });
        test.Settle(0.3f);
        test.Run(() => TypeInto(query, "zzzz-no-matching-command-987654"));
        PressKeys(test, "Enter");
        test.Check("interaction: Enter with no matches keeps the palette and query", () => shown.IsOpen && compositor.Focus == query && query.Text.Length > 0);
        PressKeys(test, "Tab");
        test.Check("interaction: Tab stays in an empty palette", () => compositor.Focus == query);
        test.Click(() => chrome.ToWorld(Descendants(shown).OfType<Label>().First(label => label.Visible && label.Text.StartsWith("Nothing matches")).Bounds.Center));
        test.Check("interaction: clicking the empty message preserves typing focus", () => shown.IsOpen && compositor.Focus == query);
        test.Run(() => TypeInto(query, ""));
        test.Drag(() => chrome.ToWorld(query.Bounds.Center), Outside);
        test.Check("interaction: a text-selection drag leaving the card does not dismiss", () => shown.IsOpen);
        PressKeys(test, "Escape");
        test.Check("interaction: Escape restores the same code focus", () => !shown.IsOpen && compositor.Focus == code);

        test.Run(() => { CommandPalette(); Remember(); });
        test.Settle(0.3f);
        test.RightClick(Outside);
        test.Check("interaction: outside right click cancels without opening a workspace menu", () => !shown.IsOpen && chrome.Popup is null && compositor.Focus == code);

        test.Run(() =>
        {
            ChooseFrom("Interaction picker", "Click a result or press Enter. Escape or click outside to cancel.",
                new List<(string Text, Action Picked)> { ("First", () => picked = 1), ("Second", () => picked = 2) });
            Remember();
        });
        test.Settle(0.3f);
        test.Run(() => Shot("interaction-picker"));
        test.Click(() => { var list = Descendants(shown).OfType<ListBox>().First(); return chrome.ToWorld(new Vector2(list.Bounds.Center.X, list.Bounds.Max.Y - list.RowHeightSeen * 1.5f)); });
        test.Check("interaction: a single click on an unselected picker result runs it", () => picked == 2 && !shown.IsOpen && compositor.Focus == code);

        string theme = "", skin = "";
        test.Run(() => { theme = settings.EditorTheme; skin = settings.EditorSkin; ChooseColorTheme(); Remember(); });
        test.Settle(0.3f);
        PressKeys(test, "Down");
        test.Check("interaction: the theme picker previews a different theme", () => settings.EditorTheme != theme || settings.EditorSkin != skin);
        test.Click(Outside);
        test.Check("interaction: outside dismissal rolls back the theme preview", () => !shown.IsOpen && settings.EditorTheme == theme && settings.EditorSkin == skin && compositor.Focus == code);

        bool motion = true;
        ListBox themeList = null!;
        Vector2 themeRow = default;
        test.Run(() =>
        {
            motion = UIDrawList.Motion; UIDrawList.Motion = true;
            ChooseColorTheme(); Remember(); themeList = Descendants(shown).OfType<ListBox>().First();
        });
        test.Settle(0.3f);
        test.Run(() => themeRow = chrome.ToWorld(new Vector2(themeList.Bounds.Center.X, themeList.Bounds.Max.Y - themeList.RowHeightSeen * 1.5f)));
        test.Press(() => themeRow);
        test.Run(() =>
        {
            var stroke = HexKeyStroke.Parse("Escape") ?? throw new InvalidOperationException("Escape stroke missing");
            Engine.Input.Keyboard.Tap(stroke.Keys);
        });
        test.Check("interaction: Escape cancels the held theme row while its exit is visible", () => !shown.IsOpen && shown.Parent is not null && !themeList.IsPressed);
        test.Release(() => themeRow);
        test.Check("interaction: release after Escape cannot reapply the cancelled theme", () => settings.EditorTheme == theme && settings.EditorSkin == skin && compositor.Focus == code);
        test.Run(() => themeList.OnChanged?.Invoke(themeList.Items[1]));
        test.Check("interaction: a stale preview callback cannot change a closed theme picker", () => settings.EditorTheme == theme && settings.EditorSkin == skin);
        test.Run(() => UIDrawList.Motion = motion);

        UIDialog child = null!;
        Vector2 chainRow = default;
        test.Run(() =>
        {
            code.Focus();
            shown = UIChooser.Show(chrome, "Parent picker", "", new List<(string Text, Action Picked)> {
                ("Open child", () => child = UIChooser.Show(chrome, "Child picker", "", new List<(string Text, Action Picked)> { ("Choose", () => picked = 4) })) });
        });
        test.Settle(0.3f);
        test.Run(() => { var list = Descendants(shown).OfType<ListBox>().First(); chainRow = chrome.ToWorld(new Vector2(list.Bounds.Center.X, list.Bounds.Max.Y - list.RowHeightSeen * 0.5f)); });
        test.Click(() => chainRow);
        test.Click(() => chainRow);
        test.Check("interaction: a double click opening another picker does not choose its result", () => child is { IsOpen: true } && picked == 2);
        test.Settle(0.55f);
        test.Click(() => chainRow);
        test.Check("interaction: a later deliberate click chooses the child result", () => !child.IsOpen && picked == 4);
        test.Settle(0.55f);
        test.Run(() =>
        {
            behind.Anchor = Origin.Center; behind.Pivot = Origin.Center; behind.Position = chrome.ToLocal(chainRow);
            shown = UIChooser.Show(chrome, "Single picker", "", new List<(string Text, Action Picked)> { ("Choose", () => picked = 5) });
        });
        test.Settle(0.3f);
        test.Click(() => chainRow);
        test.Click(() => chainRow);
        test.Check("interaction: the second click of a picker double click cannot activate the workspace", () => !shown.IsOpen && picked == 5 && behindClicks == 1);
        test.Run(() => { behind.Anchor = Origin.BottomLeft; behind.Pivot = Origin.BottomLeft; behind.Position = new Vector2(70, 50); });
        test.Run(() => { picked = 2; ShowPeek("Interaction preview", "Preview content", 0, null, null); shown = chrome.Dialog!; });
        test.Settle(0.3f);
        test.Click(Outside);
        test.Check("interaction: Peek cancels on an outside press and restores code focus", () => !shown.IsOpen && compositor.Focus == code && behindClicks == 1);

        float scale = 1;
        test.Run(() => { scale = compositor.Scale; SetScale(2, remember: false); CommandPalette(); Remember(); });
        test.Settle(0.3f);
        test.Run(() => Shot("interaction-command-palette-200"));
        test.Click(Outside);
        test.Check("interaction: outside dismissal works at 200% without click-through", () => !shown.IsOpen && behindClicks == 1 && compositor.Focus == code);
        test.Run(() => SetScale(scale, remember: false));
        test.Settle(0.2f);

        test.Run(() =>
        {
            shown = UIDialog.Show(chrome, "Confirm interaction", "Outside clicks must not answer this question.",
                new DialogChoice("Confirm", () => picked = 3), new DialogChoice("Cancel"));
        });
        test.Settle(0.3f);
        test.Click(Outside);
        test.Check("interaction: confirmation stays open and keeps its button focus on an outside click", () =>
            shown.IsOpen && shown.Buttons.Contains(compositor.Focus) && picked == 2 && behindClicks == 1);
        PressKeys(test, "Escape");
        test.Check("interaction: cancelling confirmation does not run its action", () => !shown.IsOpen && picked == 2 && compositor.Focus == code);
        test.Run(() => chrome.Root.Remove(behind));

        // Nothing of this suite's left open for the next, whose welcome is only over the empty layout
        test.Run(() => CloseAll());
        test.Settle(0.2f);
    }
}
