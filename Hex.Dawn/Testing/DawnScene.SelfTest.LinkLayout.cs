using System.Numerics;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestLinkLayout(HexPointerScript test)
    {
        string[] labels = ["Open merge editor", "Open the Difference", "Keep Mine", "Keep Hex B's"];
        string message = "link-ui-conflict.txt changed here and on Hex B. Nothing was overwritten: theirs is beside it as link-ui-conflict.theirs-hex-b.txt";
        float originalWidth = toasts.Width;
        UINotification card = null!;
        int selected = -1;

        void Show(string[] actions)
        {
            foreach (var other in toasts.Cards) other.Close();
            selected = -1;
            card = toasts.Show(message, seconds: 0, actions: actions.Select((label, i) => (label, (Action)(() => selected = i))).ToArray());
        }

        foreach (var (theme, width) in new[] { ("horizon", 400), ("light", 280), ("horizon", 650) })
        {
            test.Run(() =>
            {
                UseEditorTheme(theme, save: false);
                toasts.Width = width;
                Show(labels);
            });
            test.Settle(0.6f);
            test.Check($"link-layout: all four actions fit at {width} in {theme}", () =>
            {
                UIRect cardBounds = toasts.CardBoundsOf(card);
                UIRect[] buttons = [.. labels.Select((_, i) => toasts.ActionBoundsOf(card, i))];
                return cardBounds.Width > 0 && buttons.All(b => b.Width > 0 && b.Height > 0 &&
                    b.Min.X >= cardBounds.Min.X + 10 && b.Max.X <= cardBounds.Max.X - 10 &&
                    b.Min.Y >= cardBounds.Min.Y + 10 && b.Max.Y < cardBounds.Max.Y - 30) &&
                    buttons.All(b => buttons.Count(a => a.Min.X < b.Max.X && a.Max.X > b.Min.X &&
                        a.Min.Y < b.Max.Y && a.Max.Y > b.Min.Y) == 1) &&
                    (width >= 650 || buttons.Select(b => b.Min.Y).Distinct().Count() > 1);
            });
            test.Run(() =>
            {
                if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                    Engine.CaptureScreenshot(Path.Combine(shots, $"conflict-actions-{theme}-{width}.png"));
            });
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                if (i > 0) { test.Run(() => Show(labels)); test.Settle(0.35f); }
                test.Click(() => chrome.ToWorld(toasts.ActionBoundsOf(card, index).Center));
                test.Check($"link-layout: click {labels[index]} at {width}", () => selected == index && card.Closed);
            }
        }
        test.Run(() =>
        {
            toasts.Width = 280;
            Show([new string('W', 100), "Cancel"]);
            card.Actions[1] = ("Keep the longer device name", () => selected = 1);
        });
        test.Settle(0.5f);
        test.Check("link-layout: long action and replacement label fit in separate rows", () =>
        {
            var a = toasts.ActionBoundsOf(card, 0);
            var b = toasts.ActionBoundsOf(card, 1);
            return a.Width <= 256 && a.Min.X >= toasts.CardBoundsOf(card).Min.X && b.Min.Y > a.Max.Y;
        });
        test.Run(() => { card.Close(); toasts.Width = originalWidth; });
    }
}
