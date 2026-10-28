using Hex.Installation;
using Horizon.Content;

namespace Hex.Installer;

internal static class InstallerStrings
{
    public const string File = "Assets/installer/localisation/en-gb.hor";

    public static IReadOnlyDictionary<string, string> Read()
    {
        var strings = Localisation.ReadFile(Path.Combine(AppContext.BaseDirectory, File));
        InstallText.Use(strings);
        return strings;
    }
}
