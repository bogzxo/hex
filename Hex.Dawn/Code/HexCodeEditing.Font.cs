using Horizon.UI.Drawing;

namespace Hex.Dawn;

// The font the code is written in. Hack unless the settings say otherwise, a font made for code, while the menus and
// the panels keep the skin's Cascadia Mono. A font is a folder of Assets/fonts with a .ttf of the same name in it.
internal sealed partial class HexCodeEditing
{
    internal const string HACK = "hack";

    // How many pixels to the em the font is made at, what the flat skin makes Cascadia Mono at, so the code comes out
    // about as big as it was in that
    internal const int CODE_FONT_EM = 96;

    internal UIFont? codeFont;

    /// <summary>The font the settings say made (the first time) and handed to every editor of code, null for the skin's.</summary>
    internal void ApplyCodeFont()
    {
        // A font that isn't there is Hack, a monospace whatever was asked for. A name typed wrong made a font with no lines
        // in it and took the code down, and then the skin's, which isn't one, and the columns went wandering
        string name = settings.CodeFont;
        if (name.Length > 0 && !File.Exists(FontFile(name)))
        {
            context.Notices.Say(context.Strings.Said("code.no_font", ("name", name)), true);
            name = HACK;
        }

        codeFont = name.Length > 0 ? UIFont.Shared(Path.GetDirectoryName(FontFile(name))!, name + ".ttf", CODE_FONT_EM) : null;
        foreach (HexDocument open in documents)
            open.Editor.Font = codeFont;
    }

    // Where a font's file is, Assets/fonts/name/name.ttf
    private static string FontFile(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", name, name + ".ttf");

    /// <summary>The code in Hack, or in the skin's font, kept in the settings.</summary>
    internal void ToggleCodeFont()
    {
        settings.CodeFont = settings.CodeFont.Length > 0 ? string.Empty : HACK;
        ApplyCodeFont();
        context.Settings.Save();
        context.Notices.Say(settings.CodeFont.Length > 0 ? "the code is in Hack" : "the code is in the skin's font");
    }
}
