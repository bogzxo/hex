using System.Numerics;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test, going back and forward. A jump of more than a screen and a jump into another file are places to come
// back to, stepping a line isn't. The quiet mode, and F2 renaming a name in the other files of the workspace as well.
internal sealed partial class HexScene
{
    private void TestPlaces(HexPointerScript test)
    {
        string first = Path.Combine(testFolder, "places_one.hor"), second = Path.Combine(testFolder, "places_two.hor");
        test.Run(() =>
        {
            File.WriteAllText(first, string.Concat(Enumerable.Range(0, 60).Select(i => $"let line{i} = {i};\n")));
            File.WriteAllText(second, "let other = 1;\n");
            Open(first);
            code.GoTo(2, 0);
        });
        test.Wait();
        test.Run(() => code.GoTo(40, 4));
        test.Wait();
        test.Run(() => code.Document.MoveVertically(1, false));
        test.Wait();
        test.Run(() => Open(second));
        test.Wait();

        test.Run(() => GoPlace(back: true));
        test.Wait();
        test.Check("places: Alt+Left goes back into the file it came from, where the caret was", () =>
            document.Path == first && code.Document.Caret.Line == 41);

        test.Run(() => GoPlace(back: true));
        test.Wait();
        test.Check("places: and again to before the long jump, a line's step not counting", () => code.Document.Caret.Line == 2);

        test.Run(() => GoPlace(back: false));
        test.Wait();
        test.Check("places: Alt+Right goes forward again", () => document.Path == first && code.Document.Caret.Line == 41);

        test.Run(() => GoPlace(back: false));
        test.Wait();
        test.Check("places: and on into the other file", () => document.Path == second);

        // F11, quiet and back, what was showing put back as it was
        (bool Left, bool Right, bool Panel) before = default;
        test.Run(() =>
        {
            before = (left.Shown, right.Shown, bottom.Shown);
            ToggleQuiet();
        });
        test.Settle(0.3f);
        test.Check("quiet: F11 puts away the sides, the panel, the tabs and the breadcrumbs", () =>
            Quiet && !left.Shown && !right.Shown && !bottom.Shown && !tabRow.Visible && !crumbs.Visible && code.Bounds.Width > 0);
        test.Run(ToggleQuiet);
        test.Settle(0.3f);
        test.Check("quiet: and again puts back what was there", () =>
            !Quiet && (left.Shown, right.Shown, bottom.Shown) == before && tabRow.Visible && crumbs.Visible);

        test.Run(() =>
        {
            CloseNow();
            if (documents.Find(open => open.Path == first) is { } one)
            {
                Show(one);
                CloseNow();
            }
        });
        test.Wait();
        TestRenameEverywhere(test);
    }

    private void TestRenameEverywhere(HexPointerScript test)
    {
        string named = Path.Combine(testFolder, "rename_here.hor"), naming = Path.Combine(testFolder, "rename_there.hor");
        test.Run(() =>
        {
            File.WriteAllText(named, "let quirky_reach = 40;\nlet longer = quirky_reach * 2;\n");
            File.WriteAllText(naming, "let other = quirky_reach + quirky_reachable;\n");
            Open(named);
            code.GoTo(0, 6);
            RenameSymbol();
        });
        test.Wait();
        test.Run(() => Descendants(textDialog!.Body).OfType<TextBox>().First().OnSubmitted!("odd_reach"));
        test.WaitFor(() => chrome.Dialog is { IsOpen: true }, 3.0f);
        test.Settle(0.35f);
        test.Check("rename: F2 on a name the other files have too asks whether to rename it there as well", () => chrome.Dialog is { IsOpen: true });

        Vector2 every = Vector2.Zero;
        test.Run(() => every = chrome.Dialog is { } asked ? chrome.ToWorld(asked.Buttons[0].Bounds.Center) : Vector2.Zero);
        test.Click(() => every);
        test.Wait();
        test.Check("rename: every file, a word of its own only, opened behind with changes and nothing saved", () =>
            code.Text == "let odd_reach = 40;\nlet longer = odd_reach * 2;\n"
            && OpenDocument(Path.GetFullPath(naming)) is { } there && there.Editor.Text == "let other = odd_reach + quirky_reachable;\n"
            && there.Modified && File.ReadAllText(naming).Contains("quirky_reach +", StringComparison.Ordinal));

        test.Run(() =>
        {
            CloseNow();
            if (OpenDocument(Path.GetFullPath(naming)) is { } there)
            {
                Show(there);
                CloseNow();
            }
        });
        test.Wait();
    }
}
