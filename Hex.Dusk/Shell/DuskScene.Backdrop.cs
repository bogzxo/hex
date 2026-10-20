using System.Collections.Concurrent;
using System.Numerics;

using Hex.Shell;

using Horizon.Graphics;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dusk;

// Dawn's picture behind the code behind the terminals as well, the same picture and faintness (the editor's section of
// the settings), switched on for the terminals on their own (backdrop_terminal, off as it comes) and with a size and a
// place of their own (terminal_backdrop_scale, _across and _up), the terminals aren't the shape of the code. One picture behind the panes of a tab however it's split (TerminalPanel.Backdrop), left off while a
// program has a screen of its own up. The picture's Choose and The usual one buttons work here as they do in Dawn.
internal sealed partial class DuskScene
{
    // The picture that comes with Hex, the one Dawn shows unless told otherwise
    private const string USUAL_BACKDROP = "Assets/hex/images/background_icon.png";

    // The picture loaded, and the file it was loaded from, so a change of anything else doesn't read it again
    private Texture? backdropPicture;
    private string? backdropLoaded;

    // What a dialog that ran on a thread of its own hands back to be done on this one, next tick
    private readonly ConcurrentQueue<Action> later = new();

    /// <summary>The picture as the settings file has it now, put behind the terminals. Once there is a device to load it with.</summary>
    private void ReadBackdrop()
    {
        if (!started)
            return;

        Dictionary<string, IRuntimeValue> editor = SettingsSections.Read(host.Settings.Path) is { } all ? SettingsSections.Section(all, SettingsSections.EDITOR) : [];
        bool on = editor.TryGetValue("backdrop_terminal", out IRuntimeValue? shown) && shown is BooleanValue { Value: true };
        string named = editor.TryGetValue("backdrop_image", out IRuntimeValue? image) && image is StringValue { Value.Length: > 0 } file ? file.Value : string.Empty;
        string path = named.Length > 0 ? named : Path.Combine(AppContext.BaseDirectory, USUAL_BACKDROP);

        if (!on || !File.Exists(path))
        {
            panel.Backdrop = null;
            return;
        }

        if (backdropLoaded != path)
        {
            backdropPicture = Texture.Load(path, smooth: true);
            backdropLoaded = path;
        }

        panel.Backdrop = backdropPicture;
        panel.BackdropOpacity = Number(editor, "backdrop_opacity", 0.22f);
        panel.BackdropScale = Number(editor, "terminal_backdrop_scale", 0.5f);
        // Shares of the window, as Dawn keeps them, a file from before that had pixels pulled back in
        panel.BackdropOffset = Vector2.Clamp(new Vector2(Number(editor, "terminal_backdrop_across", 0.0f), Number(editor, "terminal_backdrop_up", 0.0f)), new Vector2(-0.5f), Vector2.One);
        Engine.WindowManager.RequestFrames();
    }

    private static float Number(Dictionary<string, IRuntimeValue> values, string key, float otherwise) =>
        values.TryGetValue(key, out IRuntimeValue? value) && value is NumberValue number ? number.Value : otherwise;

    /// <summary>A button of the settings pressed, the picture chosen with Hex's own dialog or the usual one back.</summary>
    private void SettingPressed(FileSectionSource editor, string key, int which)
    {
        if (key != "backdrop_image")
            return;

        if (which == EditorSettingsCatalog.USUAL_PICTURE)
        {
            editor.Set(key, new StringValue(string.Empty));
            SettingsPage?.Refresh(key);
            return;
        }

        var dialogs = new HexDialogHost(window =>
        {
            window.Color = compositor.Skin is { } skin ? skin.PanelColor with { W = 1.0f } : null;
            chrome.Root.Add(window);
        }, []);
        HexDialogs.OpenFile("A picture to put behind the terminals", System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures),
            [("Pictures (*.png, *.jpg)", "*.png;*.jpg;*.jpeg")], dialogs).ContinueWith(picked =>
            {
                if (picked.Result is not { Length: > 0 } path)
                    return;

                later.Enqueue(() =>
                {
                    editor.Set(key, new StringValue(Path.GetFullPath(path)));
                    SettingsPage?.Refresh(key);
                });
                Engine.WindowManager.Wake();
            }, TaskScheduler.Default);
    }

    /// <summary>Every tick, what a dialog handed back to be done here.</summary>
    private void UpdateLater()
    {
        while (later.TryDequeue(out Action? action))
            action();
    }
}
