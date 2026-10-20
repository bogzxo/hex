using System.Numerics;

using Horizon.Graphics;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

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
        ApplyCodeFont();
    }

    /// <summary>Every editor of code shown the picture as the settings have it now, its place, size and how faint.</summary>
    private void ShowBackdropEverywhere()
    {
        foreach (HexDocument open in documents)
            ShowBackdrop(open.Editor);
    }

    /// <summary>Asks for a picture with Windows' own dialog, the backdrop from then on.</summary>
    private void ChooseBackdrop()
    {
        string from = settings.BackdropImage.Length > 0 ? Path.GetDirectoryName(settings.BackdropImage) ?? browseDirectory : browseDirectory;
        Ask(HexDialogs.OpenFile(Said("settings.backdrop.choose_title"), from, [("Pictures (*.png, *.jpg)", "*.png;*.jpg;*.jpeg")], DialogHost), path =>
        {
            settings.BackdropImage = Path.GetFullPath(path);
            SaveSettings();
            ApplyBackdrop();
            SettingsPage?.Refresh("backdrop_image");
        });
    }
}
