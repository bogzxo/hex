using System.Numerics;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestScrollbars(HexPointerScript test)
    {
        WindowPanel window = null!;
        TreeView tree = null!;
        ListBox list = null!;
        ScrollPanel panel = null!;
        CodeEditor editor = null!;
        int activated = 0;
        Func<Vector2> Point(Func<UIRect> rect, float y = 0.5f) => () =>
        {
            UIRect box = rect();
            return chrome.ToWorld(new Vector2(box.Center.X, box.Min.Y + box.Height * y));
        };
        test.Run(() =>
        {
            window = chrome.AddComponent(new WindowPanel("Interactive scrollbars: tree, list, panel, code")
            {
                Anchor = Origin.Center, Pivot = Origin.Center, Size = new Vector2(1180, 410), ResizeGrip = false
            });
            tree = window.Add(new TreeView
            {
                Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(12, -12), Size = new Vector2(270, 320),
                Items = Enumerable.Range(0, 240).Select(i => new UITreeItem { Text = $"File {i:000}.cs", Icon = "file-code" }).ToList(),
                OnPressed = _ => activated++
            });
            list = window.Add(new ListBox
            {
                Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(302, -12), Size = new Vector2(270, 320),
                Rows = 12, Items = Enumerable.Range(0, 240).Select(i => $"Result {i:000}").ToArray(),
                OnChanged = _ => activated++, OnActivated = _ => activated++
            });
            panel = window.Add(new ScrollPanel
            {
                Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(592, -12), Size = new Vector2(270, 320)
            });
            panel.Add(new Button("Tall scrollable content")
            {
                Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(240, 2400), OnPressed = () => activated++
            });
            editor = window.Add(new CodeEditor
            {
                Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(882, -12), Size = new Vector2(270, 320),
                Text = string.Join('\n', Enumerable.Range(0, 240).Select(i => $"line {i:000}"))
            });
        });
        test.Settle(0.3f);
        test.Check("scrollbars: overflowing tree, list and panel expose interactive tracks", () =>
            !tree.ScrollBarTrack.IsEmpty && !list.ScrollBarTrack.IsEmpty && !panel.TrackBounds.IsEmpty && !editor.ScrollBarThumb.IsEmpty);
        test.Drag(Point(() => tree.ScrollBarThumb), Point(() => tree.ScrollBarTrack, 0.1f));
        test.Check("scrollbars: tree thumb scrolls without opening a row", () => tree.ScrollOffset > 500 && activated == 0);
        float offset = 0;
        test.Run(() => offset = tree.ScrollOffset);
        test.Click(Point(() => tree.ScrollBarTrack, 0.9f));
        test.Check("scrollbars: tree track click moves the viewport up", () => tree.ScrollOffset < offset && activated == 0);
        test.Drag(Point(() => list.ScrollBarThumb), Point(() => list.ScrollBarTrack, 0.1f));
        test.Check("scrollbars: list thumb scrolls without choosing a result", () => list.Top > 100 && list.Index == 0 && activated == 0);
        int top = 0;
        test.Run(() => top = list.Top);
        test.Click(Point(() => list.ScrollBarTrack, 0.9f));
        test.Check("scrollbars: list track click moves the viewport up", () => list.Top < top && activated == 0);
        test.Drag(Point(() => panel.ThumbBounds), Point(() => panel.TrackBounds, 0.1f));
        test.Check("scrollbars: panel thumb scrolls without pressing content", () => panel.Offset > 500 && activated == 0);
        test.Run(() => offset = panel.Offset);
        test.Click(Point(() => panel.TrackBounds, 0.9f));
        test.Check("scrollbars: panel track click moves the viewport up", () => panel.Offset < offset && activated == 0);
        test.Drag(Point(() => editor.ScrollBarThumb), Point(() => editor.ScrollBarTrack, 0.1f));
        test.Check("scrollbars: code thumb scrolls without moving the caret", () =>
            editor.ScrollBarThumb.Max.Y < editor.ScrollBarTrack.Center.Y && editor.Document.Caret.Line == 0);
        test.Run(() => offset = editor.ScrollBarThumb.Max.Y);
        test.Click(Point(() => editor.ScrollBarTrack, 0.9f));
        test.Check("scrollbars: code track click moves the viewport up", () => editor.ScrollBarThumb.Max.Y > offset && editor.Document.Caret.Line == 0);
        test.Run(() => Shot("interactive-scrollbars"));
        test.Settle(0.2f);
        test.Run(() => chrome.RemoveComponent(window));
    }
}
