using System.Diagnostics;
using System.Runtime.Versioning;

using Microsoft.Win32;

namespace Hex.Shell;

/// <summary>
/// Dusk as the system's terminal, the one a console program opens in when it's started from anywhere but a terminal.
/// Whether it is is read off the system every time, the system's settings are the setting, and what was there before is
/// put back when it's switched off.
/// <para>
/// On Windows a console program's console host hands its console on to the default terminal, HKCU\Console\%%Startup
/// says which, a console host (DelegationConsole) and a terminal (DelegationTerminal). Only Windows Terminal's
/// OpenConsole passes a console on to a terminal (ITerminalHandoff3), so it's the console host whatever the terminal is,
/// and Windows Terminal has to be installed for it, it comes with Windows 11. Dusk is a COM server for the terminal's
/// side under HKCU\Software\Classes, Hex.exe --dusk, which Windows starts when no Dusk is running (DuskHandoff). A
/// Windows that won't start a COM server of one user's (one 26H2 didn't, though the documents say it should) gets the
/// class for every user under HKLM instead, behind one admin prompt, and another to take it off again.
/// </para>
/// <para>
/// On Linux it's hex-dusk.desktop first in xdg-terminals.list (xdg-terminal-exec, which GNOME, KDE and the rest are
/// moving to), and GNOME's own setting of a terminal, which takes a program and the argument a command follows, so a
/// little script that starts Dusk is what it's given. Debian's x-terminal-emulator wants root and is left alone. macOS
/// has nothing of the kind.
/// </para>
/// </summary>
public static class HexDefaultTerminal
{
    /// <summary>The class Dusk is registered as, what Windows' console host asks COM for.</summary>
    public static readonly Guid DuskClass = new("80CF73D7-0B2D-411E-8996-3E4D4921CA1D");

    // Windows Terminal's OpenConsole by its flavour, the stable one first, the package each comes in
    private static readonly (string Package, Guid Console)[] Consoles =
    [
        ("Microsoft.WindowsTerminal_", new Guid("2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69")),
        ("Microsoft.WindowsTerminalPreview_", new Guid("06EC847C-C0A5-46B8-92CB-7C92F6E35CD5")),
        ("Microsoft.WindowsTerminalCanary_", new Guid("A854D02A-F2FE-44A5-BB24-D03F4CF830D4"))
    ];

    private const string STARTUP = @"Console\%%Startup", CONSOLE = "DelegationConsole", TERMINAL = "DelegationTerminal";
    private const string PACKAGES = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    // Where what the system had before is kept, so switching it off puts it back
    private const string REMEMBERED = @"Software\Hex\DefaultTerminal";

    /// <summary>The keys of the registry it works under, the user's own, swapped for a key of their own by the tests.</summary>
    [SupportedOSPlatform("windows")]
    internal static Func<RegistryKey> Root { get; set; } = () => Registry.CurrentUser;

    /// <summary>The folders it works in on Linux, XDG's config and data, swapped for folders of their own by the tests.</summary>
    internal static Func<(string Config, string Data)> Folders { get; set; } = () =>
    (
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(Home, ".config"),
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(Home, ".local", "share")
    );

    /// <summary>The program Dusk is, Hex.exe started with --dusk, swapped by the tests.</summary>
    internal static Func<string?> Program { get; set; } = () => Environment.ProcessPath;

    /// <summary>Whether GNOME's setting is touched, off for the tests.</summary>
    internal static bool Gnome { get; set; } = true;

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Whether this system has a default terminal Dusk can be at all, Windows and Linux.</summary>
    public static bool Possible => (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) && Program() is not null;

    /// <summary>Whether Dusk is the system's terminal now.</summary>
    public static bool Installed
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return InstalledOnWindows();
                if (OperatingSystem.IsLinux())
                    return InstalledOnLinux();
            }
            catch (Exception)
            {
            }

            return false;
        }
    }

    /// <summary>Makes Dusk the system's terminal or puts back what was. What went wrong when it didn't, null when it did.</summary>
    public static string? Set(bool on)
    {
        if (Program() is not { } program)
            return "there's no Hex program to point the system at";
        try
        {
            if (OperatingSystem.IsWindows())
                return on ? InstallOnWindows(program) : UninstallOnWindows();
            if (OperatingSystem.IsLinux())
                return on ? InstallOnLinux(program) : UninstallOnLinux();
            return "macOS has no default terminal for Dusk to be";
        }
        catch (Exception problem)
        {
            return problem.Message;
        }
    }

    /* Windows */

    /// <summary>The console host of the Windows Terminal that's installed and the family of its package, null when there's none.</summary>
    [SupportedOSPlatform("windows")]
    internal static (Guid Console, string Family)? WindowsTerminalConsole()
    {
        using RegistryKey? packages = Root().OpenSubKey(PACKAGES);
        string[] installed = packages?.GetSubKeyNames() ?? [];
        foreach (var (package, console) in Consoles)
        {
            if (installed.Any(name => name.StartsWith(package, StringComparison.OrdinalIgnoreCase)))
                return (console, package + PUBLISHER);
        }

        return null;
    }

    // Microsoft's publisher, the end of the family name of every one of Windows Terminal's packages
    private const string PUBLISHER = "8wekyb3d8bbwe";

    /// <summary>
    /// reg.exe run inside a package, which is where Windows Terminal's OpenConsole reads the registry from. A packaged app
    /// sees a copy of HKCU of its own over the real one, and Windows Terminal's had an empty %%Startup that hid what Dusk
    /// wrote, so OpenConsole took it for Windows deciding and passed every console to Windows Terminal. What went wrong,
    /// null when it went. Swapped by the tests.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static Func<string, IReadOnlyList<string>, string?> InPackage { get; set; } = RegInPackage;

    // PowerShell's Invoke-CommandInDesktopPackage, a program started inside an installed package with its view of the registry
    private static string? RegInPackage(string family, IReadOnlyList<string> commands)
    {
        try
        {
            var script = new System.Text.StringBuilder();
            foreach (string arguments in commands)
                script.Append($"Invoke-CommandInDesktopPackage -PackageFamilyName '{family}' -AppId 'App' -Command 'reg.exe' -Args '{arguments.Replace("'", "''")}' -ErrorAction Stop; ");

            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script.ToString() })
                start.ArgumentList.Add(argument);

            using Process? run = Process.Start(start);
            if (run is null)
                return "PowerShell didn't start";
            string said = run.StandardError.ReadToEnd();
            if (!run.WaitForExit(30000))
                return "PowerShell didn't finish";

            // It only starts reg.exe in there, a moment for it to have written
            Thread.Sleep(1500);
            return run.ExitCode == 0 ? null : $"PowerShell couldn't reach into Windows Terminal's package, {said.Trim()}";
        }
        catch (Exception problem)
        {
            return problem.Message;
        }
    }

    // The two values set or taken out of %%Startup, the way reg.exe is told to
    private static string[] SetInPackage(Guid console) =>
    [
        $"add \"HKCU\\{STARTUP}\" /v {CONSOLE} /t REG_SZ /d {Braced(console)} /f",
        $"add \"HKCU\\{STARTUP}\" /v {TERMINAL} /t REG_SZ /d {Braced(DuskClass)} /f"
    ];

    private static string[] TakeOutOfPackage() =>
    [
        $"delete \"HKCU\\{STARTUP}\" /v {CONSOLE} /f",
        $"delete \"HKCU\\{STARTUP}\" /v {TERMINAL} /f"
    ];

    private const string PACKAGE = "Package";

    private static string Braced(Guid guid) => guid.ToString("B").ToUpperInvariant();

    // Dusk's class under a root, and whether it's registered for every user rather than this one (kept with what was before)
    private static string ClassKey => $@"Software\Classes\CLSID\{Braced(DuskClass)}";
    private const string FOR_EVERYONE = "ForEveryone";

    /// <summary>The machine's keys, where Dusk's class goes when Windows won't start one of the user's, swapped by the tests.</summary>
    [SupportedOSPlatform("windows")]
    internal static Func<RegistryKey> MachineRoot { get; set; } = () => Registry.LocalMachine;

    /// <summary>
    /// Whether COM starts a server the user registered under HKCU\Software\Classes. It does for a process that isn't
    /// elevated, the documents say, but one Windows here (26H2) didn't, Dusk's class and a throwaway one alike said
    /// they weren't registered. Swapped by the tests.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static Func<bool> UsersClassesWork { get; set; } = ProbeUsersClasses;

    /// <summary>Dusk's class registered for every user, behind the admin prompt. What went wrong, null when it went. Swapped by the tests.</summary>
    [SupportedOSPlatform("windows")]
    internal static Func<string, string?> RegisterForEveryone { get; set; } = program =>
        Elevated($"add \"HKLM\\{ClassKey}\\LocalServer32\" /ve /d \"\\\"{program}\\\" --dusk\" /f");

    /// <summary>Dusk's class taken back off every user, behind the admin prompt. Swapped by the tests.</summary>
    [SupportedOSPlatform("windows")]
    internal static Func<string?> UnregisterForEveryone { get; set; } = () => Elevated($"delete \"HKLM\\{ClassKey}\" /f");

    /// <summary>Everything the tests swap put back to the system's own.</summary>
    [SupportedOSPlatform("windows")]
    internal static void UseTheSystem()
    {
        Root = () => Registry.CurrentUser;
        MachineRoot = () => Registry.LocalMachine;
        UsersClassesWork = ProbeUsersClasses;
        InPackage = RegInPackage;
        RegisterForEveryone = program => Elevated($"add \"HKLM\\{ClassKey}\\LocalServer32\" /ve /d \"\\\"{program}\\\" --dusk\" /f");
        UnregisterForEveryone = () => Elevated($"delete \"HKLM\\{ClassKey}\" /f");
    }

    [SupportedOSPlatform("windows")]
    private static bool InstalledOnWindows()
    {
        using RegistryKey? startup = Root().OpenSubKey(STARTUP);
        using RegistryKey? server = Root().OpenSubKey(ClassKey + @"\LocalServer32");
        using RegistryKey? everyone = MachineRoot().OpenSubKey(ClassKey + @"\LocalServer32");
        return (server is not null || everyone is not null) && string.Equals(startup?.GetValue(TERMINAL) as string, Braced(DuskClass), StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static string? InstallOnWindows(string program)
    {
        if (WindowsTerminalConsole() is not (var console, var family))
            return "Windows Terminal isn't installed, and it's what passes a console on to a terminal (winget install Microsoft.WindowsTerminal, or the Microsoft Store)";

        // Dusk as a COM server, the class the console host asks for, started as Hex --dusk (COM adds -Embedding)
        using (RegistryKey made = Root().CreateSubKey(ClassKey))
        {
            made.SetValue(null, "Dusk");
            using RegistryKey server = made.CreateSubKey("LocalServer32");
            server.SetValue(null, $"\"{program}\" --dusk");
        }

        // A Windows that won't start a server of the user's gets Dusk's class for every user instead, which wants admin once
        bool forEveryone = false;
        if (!UsersClassesWork())
        {
            if (RegisterForEveryone(program) is { } why)
            {
                Root().DeleteSubKeyTree(ClassKey, throwOnMissingSubKey: false);
                return $"this Windows doesn't start a COM server registered for one user, and registering Dusk for every user needs admin, {why}";
            }
            forEveryone = true;
        }

        using RegistryKey startup = Root().CreateSubKey(STARTUP);
        using RegistryKey remembered = Root().CreateSubKey(REMEMBERED);

        // What was there before, unless it was Dusk already
        if (!string.Equals(startup.GetValue(TERMINAL) as string, Braced(DuskClass), StringComparison.OrdinalIgnoreCase))
        {
            remembered.SetValue(CONSOLE, startup.GetValue(CONSOLE) as string ?? string.Empty);
            remembered.SetValue(TERMINAL, startup.GetValue(TERMINAL) as string ?? string.Empty);
        }
        if (forEveryone)
            remembered.SetValue(FOR_EVERYONE, 1);
        remembered.SetValue(PACKAGE, family);

        startup.SetValue(CONSOLE, Braced(console));
        startup.SetValue(TERMINAL, Braced(DuskClass));

        // And the same where OpenConsole reads them, inside Windows Terminal's package, or it never asks for Dusk
        if (InPackage(family, SetInPackage(console)) is { } unreached)
        {
            UninstallOnWindows();
            return $"Windows Terminal's console host couldn't be told about Dusk, {unreached}";
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? UninstallOnWindows()
    {
        bool forEveryone;
        using (RegistryKey startup = Root().CreateSubKey(STARTUP))
        using (RegistryKey? remembered = Root().OpenSubKey(REMEMBERED))
        {
            // Put back what it was, or nothing, which is Windows deciding
            foreach (string name in new[] { CONSOLE, TERMINAL })
            {
                if (remembered?.GetValue(name) is string { Length: > 0 } before)
                    startup.SetValue(name, before);
                else
                    startup.DeleteValue(name, throwOnMissingValue: false);
            }

            using RegistryKey? everyone = MachineRoot().OpenSubKey(ClassKey);
            forEveryone = remembered?.GetValue(FOR_EVERYONE) is 1 || everyone is not null;

            // Out of Windows Terminal's package again, it had nothing there of its own before (an empty %%Startup)
            if (remembered?.GetValue(PACKAGE) is string { Length: > 0 } family)
                InPackage(family, TakeOutOfPackage());
        }

        Root().DeleteSubKeyTree(REMEMBERED, throwOnMissingSubKey: false);
        Root().DeleteSubKeyTree(ClassKey, throwOnMissingSubKey: false);

        // The default terminal is back whatever happens here, the class for every user left behind only names Dusk
        if (forEveryone && UnregisterForEveryone() is { } why)
            return $"the default terminal is back to what it was, but Dusk's class for every user is still registered, taking it off needs admin, {why}";
        return null;
    }

    // A throwaway class of the user's whose server is a program that isn't there, asked for. Not registered means COM never
    // looked at the user's classes, anything else (the program couldn't be started) means it did. Nothing is ever started
    [SupportedOSPlatform("windows")]
    internal static bool ProbeUsersClasses()
    {
        Guid probe = new("4D7A2E19-63B0-4C8F-A1D5-9E2B7C0F3A68");
        string key = $@"Software\Classes\CLSID\{Braced(probe)}";
        try
        {
            using (RegistryKey server = Registry.CurrentUser.CreateSubKey(key + @"\LocalServer32"))
                server.SetValue(null, $"\"{Path.Combine(Path.GetTempPath(), $"hex-probe-{Guid.NewGuid():N}.exe")}\"");

            // On a thread of its own in the multithreaded apartment, whatever the asking thread's apartment is
            int said = 0;
            var asking = new Thread(() => said = HexCom.CreateLocal(probe)) { IsBackground = true, Name = "hex com probe" };
            asking.Start();
            asking.Join(TimeSpan.FromSeconds(10));
            return said != REGDB_E_CLASSNOTREG;
        }
        catch (Exception)
        {
            // Couldn't tell, the user's classes are what Windows documents
            return true;
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
    }

    private const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);

    // reg.exe run elevated, which is the admin prompt. What went wrong, null when it went
    private static string? Elevated(string arguments)
    {
        try
        {
            using Process? run = Process.Start(new ProcessStartInfo("reg.exe", arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (run is null)
                return "reg.exe didn't start";
            if (!run.WaitForExit(120000))
                return "reg.exe didn't finish";
            return run.ExitCode == 0 ? null : $"reg.exe ended with {run.ExitCode}";
        }
        catch (System.ComponentModel.Win32Exception problem) when (problem.NativeErrorCode == 1223)
        {
            return "the admin prompt was turned down";
        }
    }

    /* Linux */

    private const string DESKTOP = "hex-dusk.desktop", TERMINALS = "xdg-terminals.list";

    // The script GNOME is given, Hex with --dusk in front of what it's asked to run
    private static string Script(string data) => Path.Combine(data, "hex", "dusk");

    private static string RememberedFile(string config) => Path.Combine(config, "hex", "default-terminal");

    internal static bool InstalledOnLinux()
    {
        string list = Path.Combine(Folders().Config, TERMINALS);
        return File.Exists(list) && Entries(list).FirstOrDefault() == DESKTOP;
    }

    // What's listed, without comments and blank lines, in order
    private static List<string> Entries(string list) =>
        [.. File.ReadAllLines(list).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))];

    internal static string? InstallOnLinux(string program)
    {
        var (config, data) = Folders();

        // The script, and a .desktop file for Dusk if install-desktop.sh never made one
        string script = Script(data);
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, $"#!/bin/sh\n# Dusk, for whatever asks for the default terminal. Hex's settings made this, and take it away again\nexec \"{program}\" --dusk \"$@\"\n");
        // Run, the way a program is, which a file of text on Windows (a test of this) has no way of being
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        string desktop = Path.Combine(data, "applications", DESKTOP);
        if (!File.Exists(desktop))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(desktop)!);
            File.WriteAllText(desktop, $"[Desktop Entry]\nType=Application\nName=Dusk\nGenericName=Terminal\nComment=Hex's terminal\nExec=\"{program}\" --dusk\nIcon=hex-dusk\nTerminal=false\nCategories=System;TerminalEmulator;\nStartupWMClass=hex-dusk\nX-TerminalArgExec=-e\nX-TerminalArgDir=--cwd\n");
        }

        // First in xdg-terminals.list, anything else that was there kept after it
        string list = Path.Combine(config, TERMINALS);
        List<string> entries = File.Exists(list) ? Entries(list) : [];
        entries.RemoveAll(entry => entry == DESKTOP);
        entries.Insert(0, DESKTOP);
        Directory.CreateDirectory(config);
        File.WriteAllLines(list, entries);

        // GNOME's own, what it was kept to be put back
        if (Gnome && GSettings("get", "exec") is { } exec && GSettings("get", "exec-arg") is { } argument)
        {
            string remembered = RememberedFile(config);
            if (!File.Exists(remembered))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(remembered)!);
                File.WriteAllLines(remembered, [exec.Trim().Trim('\''), argument.Trim().Trim('\'')]);
            }
            GSettings("set", "exec", script);
            GSettings("set", "exec-arg", "-e");
        }

        return null;
    }

    internal static string? UninstallOnLinux()
    {
        var (config, data) = Folders();
        string list = Path.Combine(config, TERMINALS);
        if (File.Exists(list))
        {
            List<string> entries = Entries(list);
            entries.RemoveAll(entry => entry == DESKTOP);
            if (entries.Count == 0)
                File.Delete(list);
            else
                File.WriteAllLines(list, entries);
        }

        string remembered = RememberedFile(config);
        if (Gnome && File.Exists(remembered) && File.ReadAllLines(remembered) is [var exec, var argument, ..])
        {
            GSettings("set", "exec", exec);
            GSettings("set", "exec-arg", argument);
        }

        if (File.Exists(remembered))
            File.Delete(remembered);
        if (File.Exists(Script(data)))
            File.Delete(Script(data));
        return null;
    }

    // gsettings on GNOME's terminal, what it printed, null when there's no gsettings or no such setting
    private static string? GSettings(string verb, string key, string? value = null)
    {
        try
        {
            var start = new ProcessStartInfo("gsettings") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string argument in new[] { verb, "org.gnome.desktop.default-applications.terminal", key })
                start.ArgumentList.Add(argument);
            if (value is not null)
                start.ArgumentList.Add(value);

            using Process? run = Process.Start(start);
            if (run is null)
                return null;
            string said = run.StandardOutput.ReadToEnd();
            run.WaitForExit(5000);
            return run.ExitCode == 0 ? said : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
