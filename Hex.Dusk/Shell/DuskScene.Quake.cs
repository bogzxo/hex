namespace Hex.Dusk;

// The quake drop down. A key that works whatever is in front (quake_key in settings.hor, Ctrl+` say) slides Dusk down
// from the top of the screen and back up out of sight, the window manager doing the sliding on the window's thread and
// Windows' hotkeys doing the hearing (WindowManager.Quake). The quake command does the same from inside.
internal sealed partial class DuskScene
{
    private int quakeHotKey = -1;
    private string quakeKeyHeld = string.Empty;

    /// <summary>The key of the drop down taken from the settings, registered again when it changed.</summary>
    private void UpdateQuakeKey()
    {
        string wanted = settings.QuakeKey.Trim();
        if (wanted == quakeKeyHeld)
            return;

        Engine.WindowManager.UnregisterHotKey(quakeHotKey);
        quakeHotKey = -1;
        quakeKeyHeld = wanted;
        if (wanted.Length == 0)
            return;

        quakeHotKey = Engine.WindowManager.RegisterHotKey(wanted);
        if (quakeHotKey < 0)
            Say($"{wanted} can't be a key for everywhere here", true);
    }

    /// <summary>Whether the key was pressed, every tick.</summary>
    private void UpdateQuake()
    {
        if (quakeHotKey > 0 && Engine.WindowManager.TakeHotKey(quakeHotKey))
            ToggleQuake();
    }

    /// <summary>Dusk dropped down from the top of the screen, or taken back up out of sight.</summary>
    private void ToggleQuake()
    {
        bool down = !Engine.WindowManager.IsDropped;
        Engine.WindowManager.Drop(down, 0.45f, settings.Animations ? Math.Max(0.12f, settings.AnimationTime) : 0.0f);
        if (down)
            panel.FocusedView?.Focus();
    }
}
