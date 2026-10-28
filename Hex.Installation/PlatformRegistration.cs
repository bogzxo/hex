using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace Hex.Installation;

public static class PlatformRegistration
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Hex";
    private const string AppKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\Hex.exe";
    private static string Setup(string root) => Path.Combine(root, "installer", InstallPaths.InstallerExecutable);
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    public static string DesktopQuote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"";

    public static void Register(string root, string version)
    {
        var choices = InstallTransaction.ReadChoices(root);
        if (OperatingSystem.IsWindows()) RegisterWindows(root, version, choices);
        else if (OperatingSystem.IsLinux()) RegisterLinux(root, choices);
        else if (OperatingSystem.IsMacOS()) RegisterMac(root, version, choices);
        else throw new PlatformNotSupportedException();
    }

    public static void Unregister(string root)
    {
        if (OperatingSystem.IsWindows()) UnregisterWindows(root);
        else if (OperatingSystem.IsLinux())
        {
            foreach (string name in new[] { "hex-dawn.desktop", "hex-dusk.desktop", "hex-uninstall.desktop" })
                DeleteOwned(Path.Combine(LinuxApplications, name), root);
            DeleteOwned(Path.Combine(Desktop, "hex-dawn.desktop"), root);
            DeleteOwned(Path.Combine(Desktop, "hex-dusk.desktop"), root);
        }
        else if (OperatingSystem.IsMacOS())
        {
            DeleteOwned(Path.Combine(InstallPaths.Home, "Applications", "Uninstall Hex.command"), root);
            string shortcut = Path.Combine(Desktop, "Hex.app");
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
    private static void RegisterWindows(string root, string version, InstallChoices choices)
    {
        string exe = Path.Combine(root, InstallPaths.Executable);
        bool owned;
        using (var existing = Registry.CurrentUser.OpenSubKey(UninstallKey))
        {
            owned = existing?.GetValue("InstallLocation") is string location && location.Equals(root, StringComparison.OrdinalIgnoreCase);
            if (existing?.GetValue("InstallLocation") is string installed && !installed.Equals(root, StringComparison.OrdinalIgnoreCase))
                throw new IOException(InstallText.Get("errors.already_registered", ("path", installed)));
        }
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", InstallText.Get("brand.hex"));
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
            using var key = Registry.CurrentUser.CreateSubKey(AppKey);
            key.SetValue("", exe); key.SetValue("Path", root);
        }
        else RemoveAppPath(root);
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Hex");
        void SetShortcut(bool enabled, string path, string target, string arguments)
        {
            if (enabled) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); Shortcut(path, target, arguments, root); }
            else if (owned) DeleteShortcutOwned(path, root);
        }
        SetShortcut(choices.ApplicationMenu, Path.Combine(programs, "Hex.lnk"), exe, "");
        SetShortcut(choices.ApplicationMenu && choices.MenuDusk, Path.Combine(programs, "Dusk.lnk"), exe, "--dusk");
        SetShortcut(choices.ApplicationMenu, Path.Combine(programs, "Uninstall Hex.lnk"), Setup(root), $"--uninstall --target {Quote(root)}");
        SetShortcut(choices.DesktopHex, Path.Combine(Desktop, "Hex.lnk"), exe, "");
        SetShortcut(choices.DesktopDusk, Path.Combine(Desktop, "Dusk.lnk"), exe, "--dusk");
        if (Directory.Exists(programs) && !Directory.EnumerateFileSystemEntries(programs).Any()) Directory.Delete(programs);
    }

    [SupportedOSPlatform("windows")]
    private static void UnregisterWindows(string root)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            if (key?.GetValue("InstallLocation") is not string installed || !installed.Equals(root, StringComparison.OrdinalIgnoreCase)) return;
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        RemoveAppPath(root);
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Hex");
        foreach (string name in new[] { "Hex.lnk", "Dusk.lnk", "Uninstall Hex.lnk" }) DeleteShortcutOwned(Path.Combine(programs, name), root);
        if (Directory.Exists(programs) && !Directory.EnumerateFileSystemEntries(programs).Any()) Directory.Delete(programs);
        DeleteShortcutOwned(Path.Combine(Desktop, "Hex.lnk"), root);
        DeleteShortcutOwned(Path.Combine(Desktop, "Dusk.lnk"), root);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveAppPath(string root)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AppKey);
        if (key?.GetValue("") is string exe && exe.Equals(Path.Combine(root, InstallPaths.Executable), StringComparison.OrdinalIgnoreCase))
            Registry.CurrentUser.DeleteSubKeyTree(AppKey, false);
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

    public static string DesktopEntry(string root, bool dusk = false, bool uninstall = false)
    {
        string exe = uninstall ? Setup(root) : Path.Combine(root, InstallPaths.Executable);
        string args = uninstall ? " --uninstall --target " + DesktopQuote(root) : dusk ? " --dusk" : " %F";
        string name = InstallText.Get(uninstall ? "actions.uninstall" : dusk ? "brand.dusk" : "brand.hex").Replace('\n', ' ').Replace('\r', ' ');
        return $"[Desktop Entry]\nType=Application\nName={name}\nExec={DesktopQuote(exe)}{args}\nIcon={Path.Combine(root, "Assets", "hex_icon.png")}\nTerminal=false\nCategories={(dusk ? "System;TerminalEmulator;" : "Development;IDE;")}\nStartupWMClass={(dusk ? "hex-dusk" : "hex-dawn")}\nX-Hex-Install={root}\n";
    }

    private static void RegisterLinux(string root, InstallChoices choices) => RegisterLinux(root, choices, LinuxApplications, Desktop);

    internal static void RegisterLinux(string root, InstallChoices choices, string applications, string desktop)
    {
        void SetEntry(bool enabled, string path, bool dusk = false, bool uninstall = false, bool executable = false)
        {
            if (!enabled) { DeleteOwned(path, root); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DesktopEntry(root, dusk, uninstall));
            if (executable && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        SetEntry(choices.ApplicationMenu, Path.Combine(applications, "hex-dawn.desktop"));
        SetEntry(choices.ApplicationMenu && choices.MenuDusk, Path.Combine(applications, "hex-dusk.desktop"), dusk: true);
        // Keep a discoverable uninstaller even when launchers are disabled.
        SetEntry(true, Path.Combine(applications, "hex-uninstall.desktop"), uninstall: true);
        SetEntry(choices.DesktopHex, Path.Combine(desktop, "hex-dawn.desktop"), executable: true);
        SetEntry(choices.DesktopDusk, Path.Combine(desktop, "hex-dusk.desktop"), dusk: true, executable: true);
    }

    private static string MacBundle(string root)
    {
        if (Path.GetFileName(root) != "MacOS" || Path.GetFileName(Path.GetDirectoryName(root)) != "Contents" || !Path.GetDirectoryName(Path.GetDirectoryName(root))!.EndsWith(".app", StringComparison.Ordinal))
            throw new IOException(InstallText.Get("errors.mac_destination"));
        return Path.GetDirectoryName(Path.GetDirectoryName(root))!;
    }

    public static string MacPlist(string version) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>CFBundleExecutable</key><string>Hex</string>
          <key>CFBundleIdentifier</key><string>com.bogzxo.hex</string>
          <key>CFBundleName</key><string>{SecurityElement.Escape(InstallText.Get("brand.hex"))}</string>
          <key>CFBundlePackageType</key><string>APPL</string>
          <key>CFBundleIconFile</key><string>hex.icns</string>
          <key>CFBundleShortVersionString</key><string>{SecurityElement.Escape(version.TrimStart('v'))}</string>
          <key>NSHighResolutionCapable</key><true/>
        </dict></plist>
        """;

    private static void RegisterMac(string root, string version, InstallChoices choices)
    {
        string bundle = MacBundle(root);
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), MacPlist(version));
        string icon = Path.Combine(root, "Assets", "hex_icon.icns");
        if (File.Exists(icon))
        {
            string resources = Path.Combine(bundle, "Contents", "Resources");
            Directory.CreateDirectory(resources);
            File.Copy(icon, Path.Combine(resources, "hex.icns"), true);
        }
        string command = Path.Combine(InstallPaths.Home, "Applications", "Uninstall Hex.command");
        File.WriteAllText(command, $"#!/bin/sh\n# {root}\nexec {ShellQuote(Setup(root))} --uninstall --target {ShellQuote(root)}\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Desktop);
        string shortcut = Path.Combine(Desktop, "Hex.app");
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
