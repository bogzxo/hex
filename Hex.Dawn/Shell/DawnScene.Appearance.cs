using Horizon.Platform;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The system's light or dark appearance and accent colour (a Mac's, the engine's IPlatformAppearance, which only a Mac's desktop
// host has). The platform raises Changed on its own thread, so here it only sets a flag and wakes the editor, and the editor's
// own thread, which is the only one that touches its skin, looks at what the system says on its next tick. The theme the person
// picked is never changed by it: with ThemeFollowSystem on the theme drawn is its other half in the system's mode (HexAppearance),
// off, the person's own is back.
internal sealed partial class DawnScene
{
    // The system's, null on everything but a Mac and while the editor is under test
    private IPlatformAppearance? systemAppearance;

    // Set by the platform's thread when the system changed, taken by the editor's
    private volatile bool appearanceChanged;

    /// <summary>The theme the editor is drawn in for the one picked: the same, or its other half in the system's mode while following it.</summary>
    private string ThemeShown(string skin, string theme) =>
        systemAppearance is { } system ? Hex.Shell.HexAppearance.ThemeFor(settings.ThemeFollowSystem, system.IsDark, skin, theme) : theme;

    /// <summary>Every tick, a Mac's appearance is taken once the host has it, and a change of it is put on screen.</summary>
    private void UpdateAppearance()
    {
        if (systemAppearance is null)
        {
            if (!OperatingSystem.IsMacOS() || options.SelfTest || Engine.WindowManager.Host.Appearance is not { } found)
                return;

            systemAppearance = found;
            found.Changed += () =>
            {
                appearanceChanged = true;
                Engine.WindowManager.Wake();
            };
            appearanceChanged = true;
        }

        if (!appearanceChanged)
            return;

        appearanceChanged = false;
        ApplyAppearance();
    }

    /// <summary>The system's accent goes where a theme says <c>accent_color: "system"</c>, and the theme drawn is the system's mode of the one picked. Not saved.</summary>
    private void ApplyAppearance()
    {
        if (systemAppearance?.Accent is { } accent)
            UIPalette.SystemAccent = new System.Numerics.Vector4(accent.R, accent.G, accent.B, 1.0f);

        UseEditorSkin(settings.EditorSkin, settings.EditorTheme, save: false);
    }

    /// <summary>Following the system was turned on or off, the theme drawn changes at once.</summary>
    internal void AppearanceSettingChanged()
    {
        if (systemAppearance is not null)
            ApplyAppearance();
    }
}
