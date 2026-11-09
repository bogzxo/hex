using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Hex.Installation;

public static class InstallerHandoff
{
    private const string CopyMarker = ".hex-setup-copy";
    private const string LauncherId = "HEX_SETUP_LAUNCHER_PID";
    private const string LauncherStart = "HEX_SETUP_LAUNCHER_START";

    /// <summary>Run outside the directory being replaced, including all UI assets and native dependencies.</summary>
    public static string CopyInstaller(string source)
    {
        if (!File.Exists(Path.Combine(source, InstallPaths.InstallerExecutable)))
            throw new FileNotFoundException(InstallText.Get("errors.no_installer"));
        string temp = Path.Combine(Path.GetTempPath(), "hex-setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            InstallPaths.CopyTree(source, temp);
            File.WriteAllText(Path.Combine(temp, CopyMarker), Path.GetFileName(temp));
        }
        catch
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            throw;
        }
        string exe = Path.Combine(temp, InstallPaths.InstallerExecutable);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute);
        return exe;
    }

    /// <summary>The installed launcher exits before its relocated copy opens files from the installation.</summary>
    public static void StartInstallerCopy(string executable, string[] args)
    {
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.Environment[LauncherId] = current.Id.ToString(CultureInfo.InvariantCulture);
        start.Environment[LauncherStart] = current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        try { using var child = Process.Start(start) ?? throw new InvalidOperationException(); }
        catch
        {
            string folder = Path.GetDirectoryName(executable)!;
            if (IsInstallerCopy(folder)) Directory.Delete(folder, true);
            throw;
        }
    }

    public static void WaitForLauncher()
    {
        string? pid = Environment.GetEnvironmentVariable(LauncherId), started = Environment.GetEnvironmentVariable(LauncherStart);
        Environment.SetEnvironmentVariable(LauncherId, null);
        Environment.SetEnvironmentVariable(LauncherStart, null);
        if (int.TryParse(pid, CultureInfo.InvariantCulture, out int id) && long.TryParse(started, CultureInfo.InvariantCulture, out long ticks))
            WaitForParent(id, ticks, default).GetAwaiter().GetResult();
    }

    internal static bool IsInstallerCopy(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string name = Path.GetFileName(folder);
        return string.Equals(Path.GetDirectoryName(folder), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                   OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && name.StartsWith("hex-setup-", StringComparison.Ordinal) && Guid.TryParseExact(name[10..], "N", out _)
            && !File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint)
            && File.Exists(Path.Combine(folder, CopyMarker)) && File.ReadAllText(Path.Combine(folder, CopyMarker)) == name;
    }

    /// <summary>A copied Windows executable cannot delete itself. A hidden OS helper waits for this exact process.</summary>
    public static void CleanupCopyOnExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            string folder = Path.GetDirectoryName(Environment.ProcessPath!)!;
            if (!IsInstallerCopy(folder)) return;
            using var current = Process.GetCurrentProcess();
            string Quoted(string value) => "'" + value.Replace("'", "''") + "'";
            string script = $$"""
                $ErrorActionPreference = 'Stop'
                try {
                    $owner = [System.Diagnostics.Process]::GetProcessById({{current.Id}})
                    if ($owner.StartTime.ToUniversalTime().Ticks -eq {{current.StartTime.ToUniversalTime().Ticks}}) { $owner.WaitForExit() }
                } catch [System.ArgumentException] { }
                $folder = {{Quoted(folder)}}
                $marker = {{Quoted(Path.Combine(folder, CopyMarker))}}
                if ([System.IO.Path]::GetFullPath($folder) -ne {{Quoted(folder)}}) { exit 1 }
                if ([System.IO.Path]::GetDirectoryName($folder) -ne {{Quoted(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))}}) { exit 1 }
                for ($attempt = 0; $attempt -lt 40; $attempt++) {
                    if (-not [System.IO.Directory]::Exists($folder)) { exit 0 }
                    if (([System.IO.File]::GetAttributes($folder) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { exit 1 }
                    if (-not [System.IO.File]::Exists($marker) -or [System.IO.File]::ReadAllText($marker) -ne {{Quoted(Path.GetFileName(folder))}}) { exit 1 }
                    try { Remove-Item -LiteralPath $folder -Recurse -Force; exit 0 }
                    catch { Start-Sleep -Milliseconds 250 }
                }
                """;
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath()
            };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
            using var cleanup = Process.Start(start);
        }
        catch (Exception e)
        {
            // Cleanup must not hide the setup result. Keep diagnostics if the OS refuses the helper.
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "hex-setup-error.txt"), e + Environment.NewLine); } catch { }
        }
    }

    public static IReadOnlyList<string> UpdateArguments(string root, string version)
    {
        using var parent = Process.GetCurrentProcess();
        return ["--target", Path.GetFullPath(root), "--update", "--version", version, "--wait-pid", parent.Id.ToString(CultureInfo.InvariantCulture),
            "--wait-start", parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)];
    }

    public static async Task WaitForParent(int pid, long started, CancellationToken cancel)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            if (parent.StartTime.ToUniversalTime().Ticks != started) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await parent.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { /* Already exited. */ }
    }

    public static void EnsureHexStopped(string root)
    {
        string expected = Path.Combine(root, InstallPaths.Executable);
        foreach (var process in Process.GetProcessesByName("Hex").Concat(Process.GetProcessesByName("Dusk")))
        {
            using (process)
            {
                if (process.HasExited) continue;
                string? executable;
                try { executable = process.MainModule?.FileName; }
                catch (System.ComponentModel.Win32Exception) { throw new IOException(InstallText.Get("errors.close_windows")); }
                if (string.Equals(executable, expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || executable is not null && Path.GetDirectoryName(executable) == root)
                    throw new IOException(InstallText.Get("errors.close_retry"));
            }
        }
    }

    public static void LaunchHex(string root)
    {
        Process.Start(new ProcessStartInfo(Path.Combine(root, InstallPaths.Executable)) { UseShellExecute = false, WorkingDirectory = root });
    }
}
