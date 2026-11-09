using Horizon.Input;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestModalShortcuts(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "modal-shortcuts.txt");
        HexDocument? opened = null;
        int restarted = 0, chord = 0;
        string? answer = null;
        test.Run(() =>
        {
            File.WriteAllText(file, "keep this document\n");
            Open(file);
            opened = document;
            commands.Keys.Add(Key.F5, () => restarted++, control: true, shift: true, whileTyping: true);
            commands.Keys.Add(Key.R, () => chord++, control: true, shift: false, whileTyping: true,
                then: new UIKeyStroke(Key.A, false, false, false));
            AskText("Rename", "New name", "original", "Rename", value => answer = value);
        });
        test.Settle(0.3f);
        PressKeys(test, "Ctrl+W Ctrl+Shift+F5 Ctrl+R A");
        test.Check("modal: background document remains open", () => OpenDocument(file) == opened);
        test.Check("modal: background single and chord shortcuts do not run", () => restarted == 0 && chord == 0);
        test.Run(() => Descendants(textDialog!.Body).OfType<TextBox>().First().Focus());
        PressKeys(test, "Ctrl+A X");
        test.Check("modal: text selection and typing stay in prompt", () => Descendants(textDialog!.Body).OfType<TextBox>().First().Text == "x");
        PressKeys(test, "Enter");
        test.Settle(0.3f);
        test.Check("modal: Enter accepts the prompt", () => answer == "x" && textDialog is not { IsOpen: true });

        PressKeys(test, "Ctrl+R");
        test.Check("modal: chord starts outside a dialog", () => commands.Keys.Pending is not null);
        test.Run(() => AskText("Rename", "New name", "original", "Rename", _ => { }));
        test.Settle(0.3f);
        test.Check("modal: opening a dialog cancels a pending application chord", () => commands.Keys.Pending is null);
        PressKeys(test, "Escape");
        test.Settle(0.3f);
        PressKeys(test, "A");
        test.Check("modal: dismissed dialog cannot revive a previous chord", () => chord == 0);
        PressKeys(test, "Ctrl+Shift+F5 Ctrl+R A");
        test.Check("modal: ordinary shortcuts resume after dismissal", () => restarted == 1 && chord == 1);
        test.Run(() => { if (OpenDocument(file) is { } doc) { Show(doc); CloseNow(); } });
    }
}
