using System.Numerics;

using Horizon.Core;
using Horizon.UI.Components;

namespace Hex.Dusk;

// Files dragged onto Dusk from outside (DuskScene.FileDrop), faked through the engine's event the way the platform says
// it: held over a pane, it says the paths would be pasted into its shell; dropped there, the path is typed at the prompt
// quoted the way that shell quotes, not run; held over the title bar it says they'd open in Hex instead.
internal sealed partial class DuskScene
{
    private void TestFileDrop(DuskScript test)
    {
        TerminalView View() => panel.FocusedView!;
        string folder = Path.Combine(Path.GetTempPath(), "dusk file drop");
        string file = Path.Combine(folder, "dropped file.txt");
        Vector2 overPane = Vector2.Zero, overTop = Vector2.Zero;
        string Said() => dropOverlay is { Visible: true } shown ? shown.Text : string.Empty;
        void Post(FileDropKind kind, string[] paths, Func<Vector2> at) => test.Run(() => Engine.WindowManager.PostFileDrop(new FileDrop(kind, paths, at())));

        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, "dropped\n");
        });
        test.WaitFor(() => panel.FocusedView is { } view && view.Screen.MarksSeen && view.Screen.AtPrompt, 15.0f);
        test.Run(() =>
        {
            overPane = camera.WorldToScreen(chrome.ToWorld(View().Bounds.Center));
            overTop = camera.WorldToScreen(chrome.ToWorld(top.Bounds.Center));
        });

        Post(FileDropKind.Enter, [file], () => overPane);
        test.WaitFor(() => Said().Length > 0, 3.0f);
        test.Check("file drop: a file held over a pane says its path would be pasted into the shell", () => Said().StartsWith("Paste 1 path", StringComparison.Ordinal) && dropOverlay?.Place is not null);
        Post(FileDropKind.Over, [], () => overTop);
        test.WaitFor(() => Said().Contains("in Hex", StringComparison.Ordinal), 3.0f);
        test.Check("file drop: held over the title bar it says it'd open in Hex", () => Said().Contains("in Hex", StringComparison.Ordinal) && dropOverlay?.Place is null);

        string typed = string.Empty;
        test.Run(() => typed = TerminalDroppedPaths.Typed([file], View().Profile).Trim());
        Post(FileDropKind.Drop, [file], () => overPane);
        test.WaitFor(() => View().Screen.Text().Contains(typed, StringComparison.Ordinal), 5.0f);
        test.Check("file drop: dropped on a pane its path is at the prompt, quoted for the shell", () =>
            typed.Length > file.Length && View().Screen.Text().Contains(typed, StringComparison.Ordinal));
        test.Check("file drop: what it said while held is gone once dropped", () => Said().Length == 0);

        // The line let go of rather than run, and the file with it
        test.Run(() => View().Send("\u0003"));
        test.Settle(0.3f);
        test.Run(() => Directory.Delete(folder, recursive: true));
    }
}
