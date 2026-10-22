using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32;

namespace Hex.Shell;

/// <summary>
/// Hex.exe's folder on the user's own PATH (HKCU\Environment), so hex can be typed in any terminal. It's read off the
/// registry every time like the Explorer menu is, and Windows is told the environment changed so the terminals opened
/// after it see it. The ones already open keep the PATH they started with, that's how Windows does it.
/// </summary>
public static partial class HexUserPath
{
    private const string ENVIRONMENT = "Environment", PATH = "Path";

    /// <summary>Whether this can be done at all, it's a Windows thing.</summary>
    public static bool Possible => OperatingSystem.IsWindows() && Folder is not null;

    // The folder Hex.exe is in, without a slash on the end, as PATH has it
    private static string? Folder => Path.GetDirectoryName(Environment.ProcessPath)?.TrimEnd('\\', '/');

    /// <summary>Whether the user's PATH has Hex's folder in it.</summary>
    public static bool Installed
    {
        get
        {
            if (!OperatingSystem.IsWindows() || Folder is not { } folder)
                return false;
            try
            {
                return Entries(Read()).Any(entry => Same(entry, folder));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Puts Hex's folder on the user's PATH or takes it off. What went wrong when it didn't, null when it did.</summary>
    public static string? Set(bool on)
    {
        if (!OperatingSystem.IsWindows() || Folder is not { } folder)
            return "the PATH is only set from here on Windows";
        try
        {
            List<string> entries = [.. Entries(Read()).Where(entry => !Same(entry, folder))];
            if (on)
                entries.Add(folder);
            Write(string.Join(';', entries));

            // This Hex's own PATH too, so a terminal Dusk opens next has it without a restart
            string mine = Environment.GetEnvironmentVariable(PATH) ?? string.Empty;
            List<string> own = [.. Entries(mine).Where(entry => !Same(entry, folder))];
            if (on)
                own.Add(folder);
            Environment.SetEnvironmentVariable(PATH, string.Join(';', own));
            return null;
        }
        catch (Exception problem)
        {
            return problem.Message;
        }
    }

    // The user's PATH as it's written, %USERPROFILE% and friends left alone so writing it back doesn't bake them in
    [SupportedOSPlatform("windows")]
    private static string Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ENVIRONMENT);
        return key?.GetValue(PATH, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
    }

    [SupportedOSPlatform("windows")]
    private static void Write(string path)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ENVIRONMENT))
        {
            if (path.Length == 0)
                key.DeleteValue(PATH, throwOnMissingValue: false);
            else
                key.SetValue(PATH, path, RegistryValueKind.ExpandString);
        }

        // Explorer and whatever else is listening read the environment again, a new terminal then has it
        SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, 0, ENVIRONMENT, SMTO_ABORTIFHUNG, 1000, out _);
    }

    private static IEnumerable<string> Entries(string path) =>
        path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Same(string entry, string folder) =>
        string.Equals(Environment.ExpandEnvironmentVariables(entry).TrimEnd('\\', '/'), folder, StringComparison.OrdinalIgnoreCase);

    private const nint HWND_BROADCAST = 0xffff;
    private const uint WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x0002;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeoutW(nint window, uint message, nint wParam, string lParam, uint flags, uint timeout, out nint result);
}
