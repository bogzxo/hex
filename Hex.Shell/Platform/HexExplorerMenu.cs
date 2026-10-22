using System.Runtime.Versioning;

using Microsoft.Win32;

namespace Hex.Shell;

/// <summary>
/// Open in Hex on Explorer's right click, for a file, a folder and the inside of a folder. It's the user's own keys
/// under HKCU\Software\Classes, so no admin and nobody else's menu, and Hex.exe started on a path does the rest (a file
/// to the Dawn that's running, a folder as its workspace). Whether it's there is read off the registry every time, the
/// keys are the setting.
/// </summary>
public static class HexExplorerMenu
{
    // The verb's key under each kind of thing Explorer right clicks, and what it hands the command for it
    private const string VERB = "Hex";
    private static readonly (string Under, string Path)[] Places =
    [
        (@"Software\Classes\*\shell\" + VERB, "%1"),
        (@"Software\Classes\Directory\shell\" + VERB, "%1"),
        (@"Software\Classes\Directory\Background\shell\" + VERB, "%V")
    ];

    /// <summary>Whether this can be done at all, it's a Windows thing.</summary>
    public static bool Possible => OperatingSystem.IsWindows() && Environment.ProcessPath is not null;

    /// <summary>Whether the entries are in the menu, every one of them.</summary>
    public static bool Installed
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;
            try
            {
                foreach ((string under, _) in Places)
                {
                    using RegistryKey? command = Registry.CurrentUser.OpenSubKey(under + @"\command");
                    if (command is null)
                        return false;
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Puts the entries in or takes them out. What went wrong when it didn't, null when it did.</summary>
    public static string? Set(bool on)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
            return "the Explorer menu is only a thing on Windows";
        try
        {
            if (on)
                Install(exe);
            else
                Uninstall();
            return null;
        }
        catch (Exception problem)
        {
            return problem.Message;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Install(string exe)
    {
        foreach ((string under, string path) in Places)
        {
            using RegistryKey verb = Registry.CurrentUser.CreateSubKey(under);
            verb.SetValue(null, HexText.Get("settings_catalog.explorer_menu_verb"));
            verb.SetValue("Icon", $"\"{exe}\",0");
            using RegistryKey command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{exe}\" \"{path}\"");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Uninstall()
    {
        foreach ((string under, _) in Places)
            Registry.CurrentUser.DeleteSubKeyTree(under, throwOnMissingSubKey: false);
    }
}
