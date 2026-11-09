using System.Numerics;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestReloadIntegrity(HexPointerScript test)
    {
        const string original = "// Keep this comment.\ncompositor.design({ size: vec(320, 180), fit: \"contain\" });\nlet kept = compositor.button({ label: \"Keep me\" });\n";
        HexDocument? target = null;
        UIComponent? root = null;
        bool loaded = false, accepted = true;
        int undoCount = 0, moduleCount = 0, moduleIndex = 0;
        test.Run(() =>
        {
            target = new HexDocument("reload-integrity", stage.CreateModule());
            target.Module.Enabled = false;
            target.Module.Position = new Vector2(37, 29);
            target.Module.Scale = new Vector2(1.5f);
            loaded = target.Load(original).Success;
            target.SavedCode = original;
            target.DiskText = original;
            root = target.Find("kept");
            target.Selected = root;
            undoCount = target.UndoCount;
            moduleCount = stage.Modules.Count;
            moduleIndex = stage.Modules.ToList().IndexOf(target.Module);
        });
        test.Check("reload integrity: fixture builds a named component", () => loaded && root is not null);
        foreach (string rejected in new[]
        {
            "let broken = {;",
            "compositor.design({ size: vec(900, 600), fit: \"stretch\" }); let partial = compositor.button({ label: \"Partial\" }); missing_reload_function();"
        })
        {
            test.Run(() => accepted = TakeFromDisk(target!, rejected));
            test.Check("reload integrity: rejected source is not accepted as the disk baseline", () =>
                !accepted && target!.DiskText == original && target.SavedCode == original);
            test.Check("reload integrity: failure preserves the live tree, selection and source", () =>
                ReferenceEquals(target!.Find("kept"), root) && ReferenceEquals(target.Selected, root)
                && target.Module.Root.Children.Count == 1 && target.GenerateCode() == original
                && target.UndoCount == undoCount && stage.Modules.Count == moduleCount
                && stage.Modules[moduleIndex] == target.Module && target.Design == new Vector2(320, 180)
                && target.Fit == UIFit.Contain);
        }
        const string replacement = "let next = compositor.button({ label: \"Replacement\" });\n";
        test.Run(() => accepted = target!.Load(replacement, keepHistory: true).Success);
        test.Check("reload integrity: successful replacement commits in the same compositor slot", () =>
            accepted && target!.Find("next") is not null && target.Find("kept") is null
            && target.Selection.Count == 0 && target.GenerateCode() == replacement
            && stage.Modules.Count == moduleCount && stage.Modules[moduleIndex] == target.Module
            && !target.Module.Enabled && target.Design is null
            && target.Module.Position == new Vector2(37, 29) && target.Module.Scale == new Vector2(1.5f));
        test.Run(() => accepted = target!.Undo());
        test.Check("reload integrity: successful replacement remains undoable", () =>
            accepted && target!.Find("kept") is not null && target.GenerateCode() == original);
        test.Run(() => stage.RemoveModule(target!.Module));
    }
}
