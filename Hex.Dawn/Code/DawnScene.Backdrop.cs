using System.Numerics;

using Horizon.Graphics;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The picture behind the code, in the bottom right corner of the editor, faint, the code written over it.
// With the code wrapped (Alt+Z) the lines run round it rather
// than over it, the shape of what is drawn in the picture worked out once from its file (UIWrapShape).
internal sealed partial class DawnScene
{
    public const string USUAL_BACKDROP = HexFrame.USUAL_BACKDROP;

    private Texture? backdropPicture;
    private UIWrapShape? backdropShape;

    // Whether the picture has been read the first time, which waits for the first tick, there's no graphics device as
    // the editor is made
    private bool backdropRead;

    // Whether the first tick has given the editors of code their font, which doesn't wait for the picture
    private bool codeFontRead;

    // The picture being read ahead, and the shape it made for the picture and wrapping it was read for (null when the
    // code doesn't wrap round it). Null when nothing was read ahead
    private Task<UIWrapShape?>? backdropAhead;
    private (string Path, bool Wrap) backdropAheadFor;

    /// <summary>The picture the settings say, empty for the usual one.</summary>
    private string BackdropPath => settings.BackdropImage.Length > 0 ? settings.BackdropImage : Path.Combine(Hex.Core.HexHost.ProgramFolder, USUAL_BACKDROP);

    /// <summary>The picture read again as the settings say, and handed to every editor of code.</summary>
    /// <param name="shaped">The shape already made of the picture the settings say, made again when there's none.</param>
    private void ApplyBackdrop(UIWrapShape? shaped = null)
    {
        backdropPicture = settings.BackdropOn && File.Exists(BackdropPath) ? Texture.Load(BackdropPath, smooth: true) : null;
        MakeBackdropShape(shaped);
        ShowBackdropEverywhere();
    }

    // The shape the wrapped code runs round, the outline of what is drawn in the picture's file to the pixel
    private void MakeBackdropShape(UIWrapShape? shaped = null) =>
        backdropShape = backdropPicture is not null && settings.BackdropWrap ? shaped ?? UIWrapShape.FromFile(BackdropPath) : null;

    private void ShowBackdrop(CodeEditor editor)
    {
        // The editor draws it once the texture is made, which can be a frame after it's asked for
        editor.Backdrop = backdropPicture;
        editor.BackdropShape = backdropShape;
        editor.BackdropMargin = settings.BackdropWrapMargin;
        editor.FlowRate = settings.WrapFlow;
        editor.BackdropScale = settings.BackdropScale;
        editor.BackdropOffset = new Vector2(settings.BackdropAcross, settings.BackdropUp);
        editor.BackdropStretch = settings.BackdropStretch;
        editor.BackdropOpacity = settings.BackdropOpacity;
    }

    /// <summary>
    /// The picture read and decoded on another thread as the editor is made, its shape too, so the first tick only has
    /// to send it up. Decoded on the first tick it was a good 30 ms of the editor's start, twice over with the shape.
    /// </summary>
    private void ReadBackdropAhead()
    {
        if (!settings.BackdropOn)
            return;

        string path = BackdropPath;
        bool wrap = settings.BackdropWrap;
        backdropAheadFor = (path, wrap);
        backdropAhead = Task.Run(() =>
        {
            try
            {
                Texture.DecodeAhead(path);
                return wrap ? UIWrapShape.FromFile(path) : null;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Hex] the backdrop's picture went wrong off the editor's thread, {e.GetType().Name}: {e.Message}");
                return null;
            }
        });
    }

    /// <summary>
    /// The first tick, the code's font, and the picture read now there's somewhere to put it, once it's been read ahead.
    /// The first tick read the picture and made its shape again itself when it came before the read ahead was done, the
    /// picture decoded twice at once and the first frame waiting on it. Every tick after, the frame of a picture being
    /// laid out by hand.
    /// </summary>
    private void UpdateBackdrop()
    {
        UpdateBackdropArrange();

        if (!codeFontRead)
        {
            codeFontRead = true;
            codeEditing.ApplyCodeFont();
        }

        if (backdropRead || backdropAhead is { IsCompleted: false })
            return;

        backdropRead = true;

        // The shape made ahead taken when it was made for the picture and wrapping the settings still say
        UIWrapShape? shaped = backdropAhead is { IsCompletedSuccessfully: true } ahead && backdropAheadFor == (BackdropPath, settings.BackdropWrap) ? ahead.Result : null;
        backdropAhead = null;
        ApplyBackdrop(shaped);
    }

    /// <summary>Every editor of code shown the picture as the settings have it now, its place, size and how faint, the notebook view too.</summary>
    private void ShowBackdropEverywhere()
    {
        foreach (HexDocument open in documents)
            ShowBackdrop(open.Editor);

        // The cells have no text area of their own to hold it, the view behind them does, one picture however many there are
        notebookBackdrop.Picture = backdropPicture;
        notebookBackdrop.PictureScale = settings.BackdropScale;
        notebookBackdrop.PictureOffset = new Vector2(settings.BackdropAcross, settings.BackdropUp);
        notebookBackdrop.PictureStretch = settings.BackdropStretch;
        notebookBackdrop.PictureOpacity = settings.BackdropOpacity;
        foreach (NotebookCellView view in cellViews)
            view.Editor.Color = CellEditorColor;
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
