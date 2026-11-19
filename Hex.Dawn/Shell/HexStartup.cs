using Microsoft.Win32;

namespace Hex.Dawn;

/// <summary>
/// Hex started asleep as the person signs in (the persistent_at_login setting), the persistent process ready before the
/// first start: Hex.exe --background, its window never shown and no editor made until something asks for one. A value of
/// the user's Run key on Windows, nothing elsewhere yet.
/// </summary>
internal static class HexStartup
{
    /// <summary>The flag a start with the system has, see DawnProgram.</summary>
    public const string ARGUMENT = "--background";

    private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string NAME = "Hex";

    /// <summary>Has Hex start with the system or not, this Hex.exe the one that does.</summary>
    public static void Apply(bool on)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
            return;

        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RUN_KEY, writable: true);
            if (run is null)
                return;
            if (on)
                run.SetValue(NAME, $"\"{exe}\" {ARGUMENT}");
            else if (run.GetValue(NAME) is not null)
                run.DeleteValue(NAME, throwOnMissingValue: false);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Hex] starting with the system couldn't be {(on ? "set" : "taken off")}, {e.Message}");
        }
    }
}
