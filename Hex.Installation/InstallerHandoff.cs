using System.Diagnostics;
using System.Globalization;

namespace Hex.Installation;

public static class InstallerHandoff
{
    /// <summary>Run outside the directory being replaced, including all UI assets and native dependencies.</summary>
    public static string CopyInstaller(string source)
    {
        if (!File.Exists(Path.Combine(source, InstallPaths.InstallerExecutable)))
            throw new FileNotFoundException(InstallText.Get("errors.no_installer"));
        string temp = Path.Combine(Path.GetTempPath(), "hex-setup-" + Guid.NewGuid().ToString("N"));
        InstallPaths.CopyTree(source, temp);
        string exe = Path.Combine(temp, InstallPaths.InstallerExecutable);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute);
        return exe;
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
