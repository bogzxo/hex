using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace Hex.Installation;

public static class PlatformRegistration
{
    // The registry keys of a channel's installation, the dev build's its own so it never takes the stable one's place
    private static string UninstallKey(InstallChannel channel) => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + InstallChannels.Names(channel).Key;
    private static string AppKey(InstallChannel channel) => @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + InstallChannels.Names(channel).Command;
    private static string Setup(string root) => Path.Combine(root, "installer", InstallPaths.InstallerExecutable);
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    public static string DesktopQuote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"";

    public static void Register(string root, string version)
    {
        var choices = InstallTransaction.ReadChoices(root);
        InstallChannel channel = InstallChannels.Of(root);
        if (OperatingSystem.IsWindows()) RegisterWindows(root, version, choices, channel);
        else if (OperatingSystem.IsLinux()) RegisterLinux(root, choices, channel);
        else if (OperatingSystem.IsMacOS()) RegisterMac(root, version, choices, channel);
        else throw new PlatformNotSupportedException();
    }

    public static void Unregister(string root) => Unregister(root, InstallChannels.Of(root));

    /// <summary>A channel's entries taken off the system, said by the caller when the installation's record is already gone.</summary>
    public static void Unregister(string root, InstallChannel channel)
    {
        string prefix = InstallChannels.Names(channel).Prefix;
        if (OperatingSystem.IsWindows()) UnregisterWindows(root, channel);
        else if (OperatingSystem.IsLinux())
        {
            foreach (string name in new[] { $"{prefix}-dawn.desktop", $"{prefix}-dusk.desktop", $"{prefix}-uninstall.desktop" })
                DeleteOwned(Path.Combine(LinuxApplications, name), root);
            DeleteOwned(Path.Combine(Desktop, $"{prefix}-dawn.desktop"), root);
            DeleteOwned(Path.Combine(Desktop, $"{prefix}-dusk.desktop"), root);
        }
        else if (OperatingSystem.IsMacOS())
        {
            string name = InstallChannels.Names(channel).Name;
            DeleteOwned(Path.Combine(InstallPaths.Home, "Applications", $"Uninstall {name}.command"), root);
            string shortcut = Path.Combine(Desktop, $"{name}.app");
            if (new DirectoryInfo(shortcut).LinkTarget == MacBundle(root)) Directory.Delete(shortcut);
            string plist = Path.Combine(root, "..", "Info.plist");
            if (File.Exists(plist) && File.ReadAllText(plist).Contains("com.bogzxo.hex"))
            {
                File.Delete(plist);
                string resources = Path.Combine(root, "..", "Resources");
                File.Delete(Path.Combine(resources, "hex.icns"));
                if (Directory.Exists(resources) && !Directory.EnumerateFileSystemEntries(resources).Any()) Directory.Delete(resources);
            }
        }
    }

    private static void DeleteOwned(string path, string root)
    {
        if (File.Exists(path) && File.ReadAllText(path).Contains(root, StringComparison.Ordinal)) File.Delete(path);
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterWindows(string root, string version, InstallChoices choices, InstallChannel channel)
    {
        string exe = Path.Combine(root, InstallPaths.Executable);
        var names = InstallChannels.Names(channel);
        bool owned;
        using (var existing = Registry.CurrentUser.OpenSubKey(UninstallKey(channel)))
        {
            owned = existing?.GetValue("InstallLocation") is string location && location.Equals(root, StringComparison.OrdinalIgnoreCase);
            if (existing?.GetValue("InstallLocation") is string installed && !installed.Equals(root, StringComparison.OrdinalIgnoreCase))
                throw new IOException(InstallText.Get("errors.already_registered", ("path", installed)));
        }
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey(channel)))
        {
            key.SetValue("DisplayName", names.Name);
            key.SetValue("DisplayVersion", version.TrimStart('v'));
            key.SetValue("Publisher", "bogzxo");
            key.SetValue("InstallLocation", root);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("UninstallString", $"{Quote(Setup(root))} --uninstall --target {Quote(root)}");
            key.SetValue("URLInfoAbout", "https://github.com/bogzxo/hex");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        if (choices.WindowsAppPaths)
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppKey(channel));
            key.SetValue("", exe); key.SetValue("Path", root);
        }
        else RemoveAppPath(root, channel);
        // The dev build's shortcuts say Dev and sit in a Start menu folder of their own
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), names.Name);
        string suffix = channel == InstallChannel.Dev ? " Dev" : "";
        void SetShortcut(bool enabled, string path, string target, string arguments)
        {
            if (enabled) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); Shortcut(path, target, arguments, root); }
            else if (owned) DeleteShortcutOwned(path, root);
        }
        SetShortcut(choices.ApplicationMenu, Path.Combine(programs, $"Hex{suffix}.lnk"), exe, "");
        SetShortcut(choices.ApplicationMenu && choices.MenuDusk, Path.Combine(programs, $"Dusk{suffix}.lnk"), exe, "--dusk");
        SetShortcut(choices.ApplicationMenu, Path.Combine(programs, $"Uninstall Hex{suffix}.lnk"), Setup(root), $"--uninstall --target {Quote(root)}");
        SetShortcut(choices.DesktopHex, Path.Combine(Desktop, $"Hex{suffix}.lnk"), exe, "");
        SetShortcut(choices.DesktopDusk, Path.Combine(Desktop, $"Dusk{suffix}.lnk"), exe, "--dusk");
        if (Directory.Exists(programs) && !Directory.EnumerateFileSystemEntries(programs).Any()) Directory.Delete(programs);
    }

    [SupportedOSPlatform("windows")]
    private static void UnregisterWindows(string root, InstallChannel channel)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey(channel)))
            if (key?.GetValue("InstallLocation") is not string installed || !installed.Equals(root, StringComparison.OrdinalIgnoreCase)) return;
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey(channel), false);
        RemoveAppPath(root, channel);
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), InstallChannels.Names(channel).Name);
        string suffix = channel == InstallChannel.Dev ? " Dev" : "";
        foreach (string name in new[] { $"Hex{suffix}.lnk", $"Dusk{suffix}.lnk", $"Uninstall Hex{suffix}.lnk" }) DeleteShortcutOwned(Path.Combine(programs, name), root);
        if (Directory.Exists(programs) && !Directory.EnumerateFileSystemEntries(programs).Any()) Directory.Delete(programs);
        DeleteShortcutOwned(Path.Combine(Desktop, $"Hex{suffix}.lnk"), root);
        DeleteShortcutOwned(Path.Combine(Desktop, $"Dusk{suffix}.lnk"), root);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveAppPath(string root, InstallChannel channel)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AppKey(channel));
        if (key?.GetValue("") is string exe && exe.Equals(Path.Combine(root, InstallPaths.Executable), StringComparison.OrdinalIgnoreCase))
            Registry.CurrentUser.DeleteSubKeyTree(AppKey(channel), false);
    }

    private static void Shortcut(string path, string exe, string arguments, string cwd)
    {
        static string Ps(string s) => "'" + s.Replace("'", "''") + "'";
        string script = $"$ErrorActionPreference='Stop'; $w=New-Object -ComObject WScript.Shell; $s=$w.CreateShortcut({Ps(path)}); $s.TargetPath={Ps(exe)}; $s.Arguments={Ps(arguments)}; $s.WorkingDirectory={Ps(cwd)}; $s.IconLocation={Ps(Path.Combine(cwd, "Hex.exe"))}; $s.Save()";
        Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
    }

    private static void DeleteShortcutOwned(string path, string root)
    {
        if (!File.Exists(path)) return;
        static string Ps(string s) => "'" + s.Replace("'", "''") + "'";
        string script = $"$ErrorActionPreference='Stop'; $w=New-Object -ComObject WScript.Shell; $s=$w.CreateShortcut({Ps(path)}); if ($s.TargetPath -ieq {Ps(Path.Combine(root, InstallPaths.Executable))} -or $s.TargetPath -ieq {Ps(Setup(root))}) {{ Remove-Item -LiteralPath {Ps(path)} }}";
        Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
    }

    private static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) is { Length: > 0 } desktop ? desktop : Path.Combine(InstallPaths.Home, "Desktop");
    private static string LinuxApplications => Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data && Path.IsPathRooted(data) ? data : Path.Combine(InstallPaths.Home, ".local", "share"), "applications");

    public static string DesktopEntry(string root, bool dusk = false, bool uninstall = false, InstallChannel channel = InstallChannel.Stable)
    {
        string exe = uninstall ? Setup(root) : Path.Combine(root, InstallPaths.Executable);
        string args = uninstall ? " --uninstall --target " + DesktopQuote(root) : dusk ? " --dusk" : " %F";
        string name = InstallText.Get(uninstall ? "actions.uninstall" : dusk ? "brand.dusk" : "brand.hex").Replace('\n', ' ').Replace('\r', ' ');
        // The dev build's launchers say so, beside the stable one's in the same menu
        if (channel == InstallChannel.Dev)
            name += " Dev";
        string icon = channel == InstallChannel.Dev ? "hex_icon_dev.png" : "hex_icon.png";
        return $"[Desktop Entry]\nType=Application\nName={name}\nExec={DesktopQuote(exe)}{args}\nIcon={Path.Combine(root, "Assets", icon)}\nTerminal=false\nCategories={(dusk ? "System;TerminalEmulator;" : "Development;IDE;")}\nStartupWMClass={(dusk ? "hex-dusk" : "hex-dawn")}\nX-Hex-Install={root}\n";
    }

    private static void RegisterLinux(string root, InstallChoices choices, InstallChannel channel) => RegisterLinux(root, choices, LinuxApplications, Desktop, channel);

    internal static void RegisterLinux(string root, InstallChoices choices, string applications, string desktop, InstallChannel channel = InstallChannel.Stable)
    {
        string prefix = InstallChannels.Names(channel).Prefix;
        void SetEntry(bool enabled, string path, bool dusk = false, bool uninstall = false, bool executable = false)
        {
            if (!enabled) { DeleteOwned(path, root); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DesktopEntry(root, dusk, uninstall, channel));
            if (executable && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        SetEntry(choices.ApplicationMenu, Path.Combine(applications, $"{prefix}-dawn.desktop"));
        SetEntry(choices.ApplicationMenu && choices.MenuDusk, Path.Combine(applications, $"{prefix}-dusk.desktop"), dusk: true);
        // Keep a discoverable uninstaller even when launchers are disabled.
        SetEntry(true, Path.Combine(applications, $"{prefix}-uninstall.desktop"), uninstall: true);
        SetEntry(choices.DesktopHex, Path.Combine(desktop, $"{prefix}-dawn.desktop"), executable: true);
        SetEntry(choices.DesktopDusk, Path.Combine(desktop, $"{prefix}-dusk.desktop"), dusk: true, executable: true);
    }

    private static string MacBundle(string root)
    {
        if (Path.GetFileName(root) != "MacOS" || Path.GetFileName(Path.GetDirectoryName(root)) != "Contents" || !Path.GetDirectoryName(Path.GetDirectoryName(root))!.EndsWith(".app", StringComparison.Ordinal))
            throw new IOException(InstallText.Get("errors.mac_destination"));
        return Path.GetDirectoryName(Path.GetDirectoryName(root))!;
    }

    // The dev build its own bundle identifier and name, so the system keeps the two apart
    public static string MacPlist(string version, InstallChannel channel = InstallChannel.Stable) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>CFBundleExecutable</key><string>Hex</string>
          <key>CFBundleIdentifier</key><string>{(channel == InstallChannel.Dev ? "com.bogzxo.hex.dev" : "com.bogzxo.hex")}</string>
          <key>CFBundleName</key><string>{SecurityElement.Escape(channel == InstallChannel.Dev ? InstallChannels.Names(channel).Name : InstallText.Get("brand.hex"))}</string>
          <key>CFBundlePackageType</key><string>APPL</string>
          <key>CFBundleIconFile</key><string>hex.icns</string>
          <key>CFBundleShortVersionString</key><string>{SecurityElement.Escape(version.TrimStart('v'))}</string>
          <key>NSHighResolutionCapable</key><true/>
        </dict></plist>
        """;

    private static void RegisterMac(string root, string version, InstallChoices choices, InstallChannel channel)
    {
        string bundle = MacBundle(root);
        string named = InstallChannels.Names(channel).Name;
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), MacPlist(version, channel));
        string icon = Path.Combine(root, "Assets", "hex_icon.icns");
        if (File.Exists(icon))
        {
            string resources = Path.Combine(bundle, "Contents", "Resources");
            Directory.CreateDirectory(resources);
            File.Copy(icon, Path.Combine(resources, "hex.icns"), true);
        }
        string command = Path.Combine(InstallPaths.Home, "Applications", $"Uninstall {named}.command");
        File.WriteAllText(command, $"#!/bin/sh\n# {root}\nexec {ShellQuote(Setup(root))} --uninstall --target {ShellQuote(root)}\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Desktop);
        string shortcut = Path.Combine(Desktop, $"{named}.app");
        if (!choices.DesktopHex && new DirectoryInfo(shortcut).LinkTarget == bundle) Directory.Delete(shortcut);
        if (choices.DesktopHex && !Directory.Exists(shortcut) && !File.Exists(shortcut) && new DirectoryInfo(shortcut).LinkTarget is null)
            Directory.CreateSymbolicLink(shortcut, bundle);
        const string register = "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";
        if (File.Exists(register)) Run(register, "-f", bundle);
    }

    private static void Run(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException(InstallText.Get("errors.process_start", ("program", exe)));
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(InstallText.Get("errors.registration", ("error", error)));
    }
}
