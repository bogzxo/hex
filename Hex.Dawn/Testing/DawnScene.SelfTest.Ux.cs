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
        test.Check("ux: panel navigation is aligned left", () => bottom.HeaderAlign == Origin.Left);
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
        test.Run(() => SetScale(2.0f, remember: false));
        test.Settle(0.2f);
        test.Check("ux: narrow editor keeps file navigation on its own row", () => tabRow.Direction == UIDirection.Vertical
            && docTabs.Bounds.Max.X <= center.Bounds.Max.X + 1.0f
            && allTabs.Bounds.Max.X <= center.Bounds.Max.X + 1.0f);
        test.Run(() => Shot("ux-narrow-editor"));
        test.Settle(0.5f);
        test.Run(() => { SetScale(scale, remember: false); CloseAll(); });
        test.Settle(0.2f);
        test.Run(() => Shot("ux-editor"));
        test.Settle(0.1f);
        test.Run(() => { welcomeWanted = plainWelcome = true; CloseAll(); });
        test.Settle(0.3f);
        test.Run(() => Shot("ux-welcome"));
        test.Settle(0.1f);
        test.Run(() => { welcomeWanted = plainWelcome = false; document.View = HexView.Design; ApplyView(); });
    }
}
