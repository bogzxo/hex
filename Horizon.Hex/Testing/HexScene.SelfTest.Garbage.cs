using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Horizon.Hex;

// The self test, how much garbage sweeping the pointer over the editor makes.
internal sealed partial class HexScene
{
    private void TestGarbage(HexPointerScript test)
    {
        /* Garbage. The pointer swept over every part of the editor, the designer's and the code's, as somebody moving the
           mouse about would, and what the editor's thread allocated for it counted. A frame of it used to make a
           kilobyte and more, for a status bar that wrote its words anew every tick */

        long sweptFrom = 0, sweptTicks = 0;
        void Sweep(params Func<Vector2>[] over)
        {
            test.Run(() =>
            {
                sweptFrom = GC.GetAllocatedBytesForCurrentThread();
                sweptTicks = Engine.WindowManager.Tick;
            });

            foreach (Func<Vector2> point in over)
            {
                test.Hover(point);
                test.Wait();
            }
        }

        long SweptPerTick() => (GC.GetAllocatedBytesForCurrentThread() - sweptFrom) / Math.Max(1, Engine.WindowManager.Tick - sweptTicks);

        // Opened, and given time for everything that follows an opening (the parse a moment later, the problems) to be done with
        test.Run(() =>
        {
            Open(savedFile);
            SwitchView(HexView.Design);
        });
        test.Settle(1.0f);
        long designerGarbage = 0;
        Sweep(Part("canvas", 0.2f, 0.2f), Part("canvas", 0.5f, 0.5f), Part("canvas", 0.8f, 0.7f), Part("canvas", 0.3f, 0.9f),
            Part("tree_scroll", 0.5f, 0.1f), Part("tree_scroll", 0.5f, 0.3f), Part("palette_scroll", 0.3f, 0.8f), Part("palette_scroll", 0.7f, 0.5f),
            Part("inspector_scroll", 0.5f, 0.2f), Part("doc_tabs", 0.1f, 0.5f), Part("crumbs", 0.2f, 0.5f), Part("status_bar", 0.9f, 0.5f),
            Part("act_explorer"), Part("act_design"), Part("top", 0.2f, 0.5f), Part("top", 0.98f, 0.5f), Part("canvas", 0.5f, 0.5f));
        test.Run(() => designerGarbage = SweptPerTick());
        test.Check("garbage: sweeping the pointer over the designer makes next to nothing a tick", () =>
        {
            Console.WriteLine($"[Hex self-test]   the designer, {designerGarbage} bytes a tick");
            return designerGarbage < 256;
        });

        test.Run(() => Open(dataFile));
        test.Settle(1.0f);
        long codeGarbage = 0;
        Sweep(() => chrome.ToWorld(code.Bounds.Min + code.Bounds.Size * new Vector2(0.3f, 0.95f)), () => chrome.ToWorld(code.Bounds.Min + code.Bounds.Size * new Vector2(0.6f, 0.9f)),
            () => chrome.ToWorld(code.Bounds.Min + code.Bounds.Size * new Vector2(0.02f, 0.92f)), () => chrome.ToWorld(code.Bounds.Min + code.Bounds.Size * new Vector2(0.5f, 0.4f)),
            Part("tree_scroll", 0.5f, 0.2f), Part("inspector_scroll", 0.5f, 0.2f), Part("doc_tabs", 0.1f, 0.5f), Part("status_bar", 0.8f, 0.5f),
            Part("bottom", 0.3f, 0.1f), Part("act_outline"), () => chrome.ToWorld(code.Bounds.Center));
        test.Run(() => codeGarbage = SweptPerTick());
        test.Check("garbage: and over the code", () =>
        {
            Console.WriteLine($"[Hex self-test]   the code, {codeGarbage} bytes a tick");
            return codeGarbage < 256;
        });

        // And the code of a layout, its code view
        test.Run(() =>
        {
            Open(savedFile);
            SwitchView(HexView.Code);
        });
        test.Settle(1.0f);
        long layoutCodeGarbage = 0;
        Sweep(() => chrome.ToWorld(code.Bounds.Min + code.Bounds.Size * new Vector2(0.3f, 0.95f)), () => chrome.ToWorld(code.Bounds.Min + code.Bounds.Size * new Vector2(0.6f, 0.8f)),
            Part("tree_scroll", 0.5f, 0.2f), Part("inspector_scroll", 0.5f, 0.2f), Part("status_bar", 0.8f, 0.5f), () => chrome.ToWorld(code.Bounds.Center));
        test.Run(() => layoutCodeGarbage = SweptPerTick());
        test.Check("garbage: and over the code of a layout", () =>
        {
            Console.WriteLine($"[Hex self-test]   the code of a layout, {layoutCodeGarbage} bytes a tick");
            return layoutCodeGarbage < 256;
        });

        test.Run(() =>
        {
            SwitchView(HexView.Design);
            Open(savedFile);
        });
        test.Settle(0.3f);
        test.Hover(Part("canvas", 0.04f, 0.06f));
    }
}
