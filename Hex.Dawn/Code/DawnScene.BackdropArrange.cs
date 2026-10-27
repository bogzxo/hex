using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The picture behind the code moved and sized with the mouse (the engine's ArrangeOverlay over the editor), from its row
// of the settings or the palette. Every drag is turned back into the shares of the window the settings keep it as
// (backdrop_scale, _across, _up and _stretch) and put on every editor there and then, Done saves them, Cancel and Escape
// put back what there was. A picture that was switched off is switched on for it and stays on after Done.
internal sealed partial class DawnScene
{
    private ArrangeOverlay? backdropArrange;

    // The editor it's laid out over, and the settings window put out of the way while it's up
    private CodeEditor? arrangedOver;
    private bool settingsHiddenForArrange;

    // The picture's place and whether it was on before, for Cancel
    private (bool On, float Scale, float Across, float Up, float Stretch) backdropBefore;

    /// <summary>The overlay while the picture is being laid out, for the self test.</summary>
    internal ArrangeOverlay? BackdropArrange => backdropArrange;

    /// <summary>Puts the overlay over the code, the picture shown with a frame and handles to move it and size it by.</summary>
    private void ArrangeBackdrop()
    {
        if (backdropArrange is not null)
            return;

        CodeEditor editor = code;
        if (!editor.IsShownInHierarchy || editor.Module is not { } module)
        {
            Say(Said("backdrop_arrange.no_room"), error: true);
            return;
        }

        backdropBefore = (settings.BackdropOn, settings.BackdropScale, settings.BackdropAcross, settings.BackdropUp, settings.BackdropStretch);
        if (!settings.BackdropOn)
        {
            settings.BackdropOn = true;
            ApplyBackdrop();
        }

        // The settings window would sit right over what's being moved, it comes back afterwards
        settingsHiddenForArrange = false;
        if (SettingsWindow is { } window)
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
        overlay.Hint.Text = Said("backdrop_arrange.hint");
        overlay.DoneButton.Label = Said("backdrop_arrange.done");
        overlay.ResetButton.Label = Said("backdrop_arrange.reset");
        overlay.CancelButton.Label = Said("backdrop_arrange.cancel");
        overlay.Bar.Glass = Glassy ? PopupGlass : null;
        overlay.Bar.Color = compositor.Skin?.Palette.Raised;
        overlay.Bar.Lift = 18.0f;

        arrangedOver = editor;
        backdropArrange = module.Root.Add(overlay);
        FollowBackdrop();
        overlay.Focus();
    }

    /// <summary>Every tick, the frame kept on the picture while nothing has hold of it, the room it's in can change size.</summary>
    private void UpdateBackdropArrange()
    {
        if (backdropArrange is not { } overlay || arrangedOver is not { } editor)
            return;

        // The editor it was over went away somehow, it's put back as it was rather than left half done
        if (!editor.IsShownInHierarchy)
        {
            EndBackdropArrange(keep: false);
            return;
        }

        if (overlay.Held == UIResize.None && !overlay.Moving)
            FollowBackdrop();
        else
            overlay.Room = editor.BackdropRoom;
    }

    // The overlay told where the picture is as the editor draws it
    private void FollowBackdrop()
    {
        if (backdropArrange is not { } overlay || arrangedOver is not { } editor)
            return;

        overlay.Room = editor.BackdropRoom;
        overlay.Picture = backdropPicture;
        overlay.Rect = editor.BackdropPlaced ?? default;
    }

    /// <summary>Where the picture was dragged to, kept as shares of the window the way the settings have it, clamped as they are.</summary>
    private void PlaceBackdropByHand(UIRect rect)
    {
        if (arrangedOver is not { BackdropRoom: { } room } editor || backdropPicture is not { IsValid: true } picture)
            return;

        var (scale, offset, stretch) = CodeEditor.BackdropFrom(rect, room, editor.Module?.Frame ?? room, picture);
        settings.BackdropScale = Math.Clamp(scale, 0.05f, 1.5f);
        settings.BackdropAcross = Math.Clamp(offset.X, -0.5f, 1.0f);
        settings.BackdropUp = Math.Clamp(offset.Y, -0.5f, 1.0f);
        settings.BackdropStretch = Math.Clamp(stretch, HexSettings.LEAST_STRETCH, HexSettings.MOST_STRETCH);
        ShowBackdropEverywhere();
        FollowBackdrop();
    }

    // Reset, the picture back where it comes, the bottom right corner at half the window's height and its own shape
    private void ResetBackdropPlace()
    {
        settings.BackdropScale = EditorSettingsCatalog.UsualNumber("backdrop_scale");
        settings.BackdropAcross = EditorSettingsCatalog.UsualNumber("backdrop_across");
        settings.BackdropUp = EditorSettingsCatalog.UsualNumber("backdrop_up");
        settings.BackdropStretch = EditorSettingsCatalog.UsualNumber("backdrop_stretch");
        ShowBackdropEverywhere();
        FollowBackdrop();
    }

    /// <summary>Takes the overlay away, the place kept and saved or put back as it was before, and the settings window back if it was up.</summary>
    private void EndBackdropArrange(bool keep)
    {
        if (backdropArrange is not { } overlay)
            return;

        if (keep)
        {
            SaveSettings();
            Say(Said("backdrop_arrange.kept"));
        }
        else
        {
            bool wasOn = settings.BackdropOn;
            (settings.BackdropOn, settings.BackdropScale, settings.BackdropAcross, settings.BackdropUp, settings.BackdropStretch) = backdropBefore;
            if (wasOn != settings.BackdropOn)
                ApplyBackdrop();
            Say(Said("backdrop_arrange.put_back"));
        }

        overlay.Parent?.Remove(overlay);
        backdropArrange = null;
        CodeEditor? editor = arrangedOver;
        arrangedOver = null;
        ShowBackdropEverywhere();

        if (settingsHiddenForArrange && SettingsWindow is { } window)
        {
            window.Visible = true;
            SettingsPage?.Refresh();
            settingsView?.Page.Search.Focus();
        }
        else
        {
            editor?.Focus();
        }

        settingsHiddenForArrange = false;
    }
}
