using System.Globalization;
using System.Numerics;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestTitlebarTargets(HexPointerScript test)
    {
        string fixture = Path.Combine(testFolder, "titlebar-targets.txt");
        string text = string.Concat(Enumerable.Repeat("0123456789abcdef", 8)) + "\nsecond line\n";
        test.Run(() =>
        {
            File.WriteAllText(fixture, text);
            Open(fixture);
            SwitchView(HexView.Code);
        });
        test.Settle(1);

        test.Check("titlebar-targets: measured a safe caption point and an editor text point", () =>
        {
            if (top is not TitleBar { Module: { } barModule, Visible: true } bar ||
                !document.IsText || document.Editor.Text != text || Engine.WindowManager is not { } window ||
                window.PixelScale.X <= 0 || window.PixelScale.Y <= 0)
                return false;

            Vector4 barPixels = McpBox(barModule, bar.Bounds);
            Vector2 scale = window.PixelScale;
            Vector4 scaleBox = new(scale.X, scale.Y, scale.X, scale.Y);
            Vector4 frame = barPixels / scaleBox;
            float left = Math.Max(frame.X + 10, 10);
            float right = Math.Min(frame.X + frame.Z - 10, window.PointSize.X - 10);
            float y = frame.Y + frame.W / 2;
            if (left >= right || y < 9)
                return false;

            // Match TitleBar's drag-area holes: its own window buttons and each visible child.
            var blocked = bar.Children.Where(child => child.Visible)
                .Select(child => McpBox(barModule, child.Bounds) / scaleBox).ToList();
            if (bar.WindowButtons)
                blocked.AddRange(Enumerable.Range(0, 3).Select(index => McpBox(barModule, bar.ButtonBounds(index)) / scaleBox));

            float width = 0, captionX = 0;
            void Consider(float end)
            {
                if (end - left > width)
                {
                    width = end - left;
                    captionX = (left + end) / 2;
                }
            }

            foreach (Vector4 bounds in blocked.Where(bounds => bounds.Y <= y && y <= bounds.Y + bounds.W).OrderBy(bounds => bounds.X))
            {
                Consider(Math.Clamp(bounds.X, left, right));
                left = Math.Clamp(bounds.X + bounds.Z, left, right);
            }
            Consider(right);
            if (width < 20)
                return false;

            var editor = document.Editor;
            Vector2 at = editor.PlaceOf(new UITextPosition(0, 3));
            Vector2 control = camera.WorldToScreen(document.Module.ToWorld(at)) / scale;
            if (!editor.Bounds.Contains(at) || control.X < 10 || control.Y < 10 ||
                control.X + 64 > window.PointSize.X - 10 || control.Y > window.PointSize.Y - 10)
                return false;

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"TITLEBAR TARGETS caption,{captionX:0.###},{y:0.###} control,{control.X:0.###},{control.Y:0.###} contentPoints={window.PointSize.X:0.###}x{window.PointSize.Y:0.###} backingScale={scale.X:0.###}x{scale.Y:0.###}"));
            return true;
        });
    }
}
