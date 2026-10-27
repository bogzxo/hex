using System.Numerics;

using Hex.Core;
using Hex.Shell;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dusk;

// The picture behind the terminals moved and sized with the mouse (the engine's ArrangeOverlay over the panes), from its
// row of the settings or the palette, the way Dawn does the code's. A drag goes on the panel there and then, Done writes
// it to the editor's section of the file (terminal_backdrop_scale, _across, _up and _stretch) and switches the picture on
// for the terminals, Cancel and Escape read back what the file still has.
internal sealed partial class DuskScene
{
    private ArrangeOverlay? backdropArrange;

    // Whether the settings window was put out of the way for it, to bring it back
    private bool settingsHiddenForArrange;

    /// <summary>The overlay while the picture is being laid out, for the self test.</summary>
    internal ArrangeOverlay? BackdropArrange => backdropArrange;

    /// <summary>Puts the overlay over the terminals, the picture shown with a frame and handles to move it and size it by.</summary>
    private void ArrangeBackdrop()
    {
        if (backdropArrange is not null || !started)
            return;

        if (chooser is { IsOpen: true })
            chooser.Close();

        // Shown while it's laid out even when it's off for the terminals, at the place the file has
        HexSettings settings = new(host.Settings.Path);
        if (BackdropPicture(settings) is not { } picture)
            return;

        panel.Backdrop = picture;
        panel.BackdropOpacity = settings.BackdropOpacity;
        panel.BackdropScale = settings.TerminalBackdropScale;
        panel.BackdropOffset = new Vector2(settings.TerminalBackdropAcross, settings.TerminalBackdropUp);
        panel.BackdropStretch = settings.TerminalBackdropStretch;

        settingsHiddenForArrange = false;
        if (settingsView?.Window is { Parent: not null } window)
        {
            window.Visible = false;
            settingsHiddenForArrange = true;
        }

        var overlay = new ArrangeOverlay
        {
            OnChanged = PlaceBackdropByHand,
            OnDone = () => EndBackdropArrange(keep: true),
            OnCancel = () => EndBackdropArrange(keep: false),
            OnReset = ResetBackdropPlace
        };
        overlay.Hint.Text = HexText.Get("backdrop_arrange.hint");
        overlay.DoneButton.Label = HexText.Get("backdrop_arrange.done");
        overlay.ResetButton.Label = HexText.Get("backdrop_arrange.reset");
        overlay.CancelButton.Label = HexText.Get("backdrop_arrange.cancel");
        overlay.Bar.Glass = effectGlass ? popupGlass : null;
        overlay.Bar.Color = compositor.Skin?.Palette.Raised;
        overlay.Bar.Lift = 18.0f;

        backdropArrange = chrome.Root.Add(overlay);
        FollowBackdrop();
        overlay.Focus();
        Engine.WindowManager.RequestFrames();
    }

    /// <summary>Every tick, the frame kept on the picture while nothing has hold of it, the panes can change size.</summary>
    private void UpdateBackdropArrange()
    {
        if (backdropArrange is not { } overlay)
            return;

        if (overlay.Held == UIResize.None && !overlay.Moving)
            FollowBackdrop();
        else
            overlay.Room = BackdropRoom();
    }

    /// <summary>The room the picture is placed in, every pane of the tab together, null with no terminal up.</summary>
    private UIRect? BackdropRoom() => panel.FocusedView is { } view ? view.BackdropArea ?? view.Bounds : null;

    // The overlay told where the picture is as the panes draw it
    private void FollowBackdrop()
    {
        if (backdropArrange is not { } overlay)
            return;

        overlay.Room = BackdropRoom();
        overlay.Picture = panel.Backdrop;
        overlay.Rect = overlay.Room is { } room && panel.Backdrop is { IsValid: true } picture
            ? CodeEditor.PlaceBackdrop(room, chrome.Frame, picture, panel.BackdropScale, panel.BackdropOffset, panel.BackdropStretch)
            : default;
    }

    /// <summary>Where the picture was dragged to, as shares of the window the way the file keeps it, clamped as it is.</summary>
    private void PlaceBackdropByHand(UIRect rect)
    {
        if (BackdropRoom() is not { } room || panel.Backdrop is not { IsValid: true } picture)
            return;

        var (scale, offset, stretch) = CodeEditor.BackdropFrom(rect, room, chrome.Frame, picture);
        panel.BackdropScale = Math.Clamp(scale, 0.05f, 1.5f);
        panel.BackdropOffset = new Vector2(Math.Clamp(offset.X, -0.5f, 1.0f), Math.Clamp(offset.Y, -0.5f, 1.0f));
        panel.BackdropStretch = Math.Clamp(stretch, HexSettings.LEAST_STRETCH, HexSettings.MOST_STRETCH);
        FollowBackdrop();
        Engine.WindowManager.RequestFrames();
    }

    // Reset, the picture back where it comes, the bottom right corner at half the window's height and its own shape
    private void ResetBackdropPlace()
    {
        panel.BackdropScale = EditorSettingsCatalog.UsualNumber("terminal_backdrop_scale");
        panel.BackdropOffset = new Vector2(EditorSettingsCatalog.UsualNumber("terminal_backdrop_across"), EditorSettingsCatalog.UsualNumber("terminal_backdrop_up"));
        panel.BackdropStretch = EditorSettingsCatalog.UsualNumber("terminal_backdrop_stretch");
        FollowBackdrop();
        Engine.WindowManager.RequestFrames();
    }

    /// <summary>Takes the overlay away, the place written to the file or read back from it, and the settings window back if it was up.</summary>
    private void EndBackdropArrange(bool keep)
    {
        if (backdropArrange is not { } overlay)
            return;

        overlay.Parent?.Remove(overlay);
        backdropArrange = null;

        if (keep)
        {
            var editor = new FileSectionSource(host.Settings.Path, SettingsSections.EDITOR, EditorSettingsCatalog.Settings, EditorSettingsCatalog.Usual);
            editor.Set("backdrop_terminal", Values.Bool(true));
            Keep(editor, "terminal_backdrop_scale", panel.BackdropScale);
            Keep(editor, "terminal_backdrop_across", panel.BackdropOffset.X);
            Keep(editor, "terminal_backdrop_up", panel.BackdropOffset.Y);
            Keep(editor, "terminal_backdrop_stretch", panel.BackdropStretch);
            Say(HexText.Get("backdrop_arrange.kept"));
        }
        else
        {
            Say(HexText.Get("backdrop_arrange.put_back"));
        }

        ReadBackdrop();

        if (settingsHiddenForArrange && settingsView?.Window is { Parent: not null } window)
        {
            window.Visible = true;
            SettingsPage?.Refresh();
            settingsView.Page.Search.Focus();
        }
        else
        {
            panel.FocusedView?.Focus();
        }

        settingsHiddenForArrange = false;
    }

    // A number written to the file, or taken out of it when it's what it comes as anyway
    private static void Keep(FileSectionSource editor, string key, float value)
    {
        if (MathF.Abs(value - EditorSettingsCatalog.UsualNumber(key)) < 0.0001f)
            editor.Reset(key);
        else
            editor.Set(key, new NumberValue(value));
    }
}
