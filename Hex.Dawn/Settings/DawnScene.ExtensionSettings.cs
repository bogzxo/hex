using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// What an extension can be told, kept in the settings file, and its section of the settings window. The rows of the
// settings it described are ExtensionSettingsSource's, the section has what it is over them and its own layout when it
// has one.
internal sealed partial class DawnScene
{
    // What the extensions made of their own layouts of settings, let go of as the window goes
    private readonly List<UIScriptedLayout> settingsMade = [];

    /// <summary>Lets go of what the extensions put on their sections, as the settings go.</summary>
    private void ForgetExtensionTabs()
    {
        foreach (UIScriptedLayout page in settingsMade)
            page.Closed = true;
        settingsMade.Clear();
    }

    /// <summary>
    /// What the extensions are called on the row of the ones switched off, in the order they're listed. A name two of them
    /// share has the folder it's in after it, or the row had two buttons nobody could tell apart.
    /// </summary>
    private List<string> ExtensionNames()
    {
        var all = extensions.All;
        var named = new List<string>(all.Count);
        foreach (HexExtension extension in all)
        {
            bool shared = all.Count(other => string.Equals(other.Name, extension.Name, StringComparison.OrdinalIgnoreCase)) > 1;
            named.Add(shared ? $"{extension.Name} ({extension.Id})" : extension.Name);
        }

        return named;
    }

    /// <summary>The folder of an extension by what the row calls it, what was said taken for the folder when no extension is called that.</summary>
    private string ExtensionIdOf(string name)
    {
        List<string> named = ExtensionNames();
        int at = named.FindIndex(shown => string.Equals(shown, name, StringComparison.OrdinalIgnoreCase));
        return at >= 0 ? extensions.All[at].Id : name;
    }

    private string AboutExtension(HexExtension extension)
    {
        string by = extension.Author.Length > 0 ? Said("extensions.by", ("author", UIRichText.Literal(extension.Author))) : string.Empty;
        string where = extension.BuiltIn ? Said("extensions.built_in") : Said("extensions.own");
        string text = $"**{UIRichText.Literal(extension.Name)}** {UIRichText.Literal(extension.Version)}  {by}\n{UIRichText.Literal(extension.Description)}\n[color:dim]{where}[/color]";
        return extension.Failed is { } why ? text + "\n[color:error]" + UIRichText.Literal(why) + "[/color]" : text;
    }
}
