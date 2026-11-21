using System.Numerics;

using Hex.Shell;

using Horizon.Core;
using Horizon.UI.Components;

namespace Hex.Dusk;

// Files and folders dragged onto Dusk from outside. Let go of over a terminal's pane, their paths are pasted into its
// shell quoted the way that shell quotes, a space after each, the way Windows Terminal takes a drop (TerminalView.PastePaths),
// and the pane gets the keyboard. Let go of anywhere else (the title bar, the tabs, the status bar) they're opened in
// Dawn, the files in tabs and a folder as its workspace (HexLauncher). Where the platform doesn't say where they were let
// go of, the pane with the keyboard takes them. While they're held, a hairline round where they'd land and a line say
// what letting go would do (HexDropOverlay).
internal sealed partial class DuskScene
{
    private HexDropOverlay? dropOverlay;

    private void UpdateFileDrops()
    {
        // With Dawn and Dusk in the one window, what's dropped is for the one on screen
        if (HexModes.Together && HexModes.Current != HexMode.Dusk)
            return;

        var window = Engine.WindowManager;
        while (window.TryTakeFileDrop(out FileDrop drop))
            TakeFileDrop(drop.Paths, drop.HasPosition ? drop.Position : null);

        if (window.FileDrag is { Paths.Count: > 0 } held)
        {
            TerminalView? pane = PaneFor(held.HasPosition ? held.Position : null);
            string text = pane is not null
                ? HexText.Get("file_drop.paste_paths", ("paths", HexText.Count("path", held.Paths.Count)), ("shell", pane.Profile?.Name is { Length: > 0 } shell ? shell : HexText.Get("file_drop.the_shell")))
                : HexText.Get("file_drop.open_in_hex", ("items", HexText.Count("item", held.Paths.Count)));
            dropOverlay ??= chrome.AddComponent(new HexDropOverlay());
            dropOverlay.Show(text, pane is not null ? "terminal" : "go-to-file", pane?.Bounds);
        }
        else if (dropOverlay is { Visible: true })
        {
            dropOverlay.Hide();
        }
    }

    /// <summary>Files dropped at a pixel of the window (null for wherever): pasted into the pane there, or opened in Dawn.</summary>
    internal void TakeFileDrop(IReadOnlyList<string> paths, Vector2? at)
    {
        dropOverlay?.Hide();
        if (paths.Count == 0)
            return;

        if (PaneFor(at) is { } pane)
        {
            panel.FocusPane(pane);
            pane.PastePaths(paths);
            return;
        }

        foreach (string path in paths)
        {
            bool opened = Directory.Exists(path) ? HexLauncher.OpenFolder(path) : File.Exists(path) && HexLauncher.Open(path);
            if (!opened)
                Say(HexText.Get("file_drop.no_hex", ("name", Path.GetFileName(path.TrimEnd('\\', '/')))), error: true);
        }
    }

    /// <summary>
    /// The pane a drop at a pixel goes to: the one under it, none over the rest of the window, and the one with the
    /// keyboard when the platform didn't say where.
    /// </summary>
    private TerminalView? PaneFor(Vector2? at)
    {
        if (at is not { } pixel)
            return panel.FocusedView;

        Vector2 local = chrome.ToLocal(camera.ScreenToWorld(pixel));
        if (panel.Current is not { } tab)
            return null;

        foreach (TerminalView view in tab.Views)
            if (view.Visible && view.Bounds.Contains(local))
                return view;
        return null;
    }
}
