using System.Numerics;

using Horizon.Graphics;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// The picture behind the code, in the bottom right corner of the editor, faint, the code written over it.
// With the code wrapped (Alt+Z) the lines run round it rather
// than over it, the shape of what is drawn in the picture worked out once from its file (UIWrapShape).
internal sealed partial class HexScene
{
    public const string USUAL_BACKDROP = "Assets/hex/images/background_icon.png";

    private Texture? backdropPicture;
    private UIWrapShape? backdropShape;

    // Whether the picture has been read the first time, which waits for the first tick, there's no graphics device as
    // the editor is made
    private bool backdropRead;

    /// <summary>The picture the settings say, empty for the usual one.</summary>
    private string BackdropPath => settings.BackdropImage.Length > 0 ? settings.BackdropImage : Path.Combine(AppContext.BaseDirectory, USUAL_BACKDROP);

    /// <summary>The picture read again as the settings say, and handed to every editor of code.</summary>
    private void ApplyBackdrop()
    {
        backdropPicture = settings.BackdropOn && File.Exists(BackdropPath) ? Texture.Load(BackdropPath, smooth: true) : null;
        MakeBackdropShape();
        foreach (HexDocument open in documents)
            ShowBackdrop(open.Editor);
    }

    // The shape the wrapped code runs round, the outline of what is drawn in the picture's file to the pixel
    private void MakeBackdropShape() =>
        backdropShape = backdropPicture is not null && settings.BackdropWrap ? UIWrapShape.FromFile(BackdropPath) : null;

    private void ShowBackdrop(CodeEditor editor)
    {
        // The editor draws it once the texture is made, which can be a frame after it's asked for
        editor.Backdrop = backdropPicture;
        editor.BackdropShape = backdropShape;
        editor.BackdropMargin = settings.BackdropWrapMargin;
        editor.FlowRate = settings.WrapFlow;
        editor.BackdropScale = settings.BackdropScale;
        editor.BackdropOffset = new Vector2(settings.BackdropAcross, settings.BackdropUp);
        editor.BackdropOpacity = settings.BackdropOpacity;
    }

    /// <summary>The first tick, the picture read now there's somewhere to put it.</summary>
    private void UpdateBackdrop()
    {
        if (backdropRead)
            return;

        backdropRead = true;
        ApplyBackdrop();
    }

    /* The Behind the code page of the settings */

    private void FillBackdropPage(UILayout made)
    {
        SettingSwitch(made, "backdrop", settings.BackdropOn, on =>
        {
            settings.BackdropOn = on;
            ApplyBackdrop();
        });

        SettingSwitch(made, "backdrop_wrap", settings.BackdropWrap, on =>
        {
            settings.BackdropWrap = on;
            ApplyBackdrop();
        });

        var picked = made.Get<Label>("backdrop_file");
        void Named() => picked.Text = settings.BackdropImage.Length > 0 ? Path.GetFileName(settings.BackdropImage) : Said("settings.backdrop.usual");
        Named();

        var choose = made.Get<Button>("backdrop_choose");
        choose.OnPressed = () =>
        {
            string from = settings.BackdropImage.Length > 0 ? Path.GetDirectoryName(settings.BackdropImage) ?? browseDirectory : browseDirectory;
            Ask(HexDialogs.OpenFile(Said("settings.backdrop.choose_title"), from, [("Pictures (*.png, *.jpg)", "*.png;*.jpg;*.jpeg")], WindowHandle), path =>
            {
                settings.BackdropImage = Path.GetFullPath(path);
                SaveSettings();
                ApplyBackdrop();
                Named();
            });
        };
        settingControls["backdrop_choose"] = choose;

        var usual = made.Get<Button>("backdrop_usual");
        usual.OnPressed = () =>
        {
            settings.BackdropImage = string.Empty;
            SaveSettings();
            ApplyBackdrop();
            Named();
        };
        settingControls["backdrop_usual"] = usual;

        // How far the code keeps off her, every editor shown it as it's dragged
        BackdropSlider(made, "backdrop_wrap_margin", settings.BackdropWrapMargin, value => settings.BackdropWrapMargin = value, value => $"{value:0} px");

        // How quickly the words glide, said as about how long one takes to get there (95% of the way), or that it snaps
        BackdropSlider(made, "wrap_flow", settings.WrapFlow, value => settings.WrapFlow = value,
            value => value <= 0.0f ? Said("settings.backdrop.flow_off") : $"{3000.0f / value:0} ms");

        BackdropSlider(made, "backdrop_scale", settings.BackdropScale, value => settings.BackdropScale = value, value => $"{value * 100:0}%");
        // The slider goes the way the picture does, the setting is how far in from the right edge it is (the old way round)
        BackdropSlider(made, "backdrop_across", -settings.BackdropAcross, value => settings.BackdropAcross = -value, value => $"{value:0}");
        BackdropSlider(made, "backdrop_up", settings.BackdropUp, value => settings.BackdropUp = value, value => $"{value:0}");
        BackdropSlider(made, "backdrop_opacity", settings.BackdropOpacity, value => settings.BackdropOpacity = value, value => $"{value * 100:0}%");
    }

    // A slider of the page, the number it's at written next to it, every editor shown the change as it's dragged
    private void BackdropSlider(UILayout made, string key, float now, Action<float> set, Func<float, string> said)
    {
        var slider = made.Get<Slider>(key);
        var shown = made.Get<Label>(key + "_value");
        slider.Value = now;
        shown.Text = said(slider.Value);
        slider.OnChanged = value =>
        {
            set(value);
            shown.Text = said(value);
            foreach (HexDocument open in documents)
                ShowBackdrop(open.Editor);
            SaveSettings();
        };
        settingControls[key] = slider;
    }
}
