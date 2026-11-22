using System.ComponentModel;
using System.Diagnostics;

namespace Hex.Core;

// The process PATH is the first choice. A desktop editor can outlive the PATH and installers that started it.
internal sealed record HexAgentProgramPaths(bool Windows, bool Mac, string Home, string? LocalAppData,
    string? AppData, string? NpmPrefix, Func<string?> LivePath)
{
    internal static HexAgentProgramPaths Current() => new(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX"),
        OperatingSystem.IsWindows() ? WindowsPath : OperatingSystem.IsMacOS() ? LoginPath : static () => null);

    internal IEnumerable<string> Folders()
    {
        if (Windows)
        {
            foreach (string folder in Split(LivePath(), ';'))
                yield return folder;

            if (!string.IsNullOrEmpty(LocalAppData))
            {
                string winGet = Path.Combine(LocalAppData, "Microsoft", "WinGet");
                yield return Path.Combine(winGet, "Links");
                foreach (string installed in PackagesIn(Path.Combine(winGet, "Packages")))
                {
                    yield return installed;
                    yield return Path.Combine(installed, "bin");
                }
            }
            if (!string.IsNullOrEmpty(AppData))
                yield return Path.Combine(AppData, "npm");
        }

        if (!string.IsNullOrEmpty(NpmPrefix))
            yield return Windows ? NpmPrefix : Path.Combine(NpmPrefix, "bin");
        if (Home.Length > 0)
        {
            yield return Path.Combine(Home, ".local", "bin");
            if (!Windows)
                yield return Path.Combine(Home, ".npm-global", "bin");
        }
        if (Mac)
        {
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
            foreach (string folder in Split(LivePath(), ':'))
                yield return folder;
        }
    }

    private static string[] Split(string? path, char separator) =>
        path?.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private static string[] PackagesIn(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return [];
            return [.. Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static string WindowsPath() => Environment.ExpandEnvironmentVariables(
        $"{Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)};{Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)}");

    private static string? LoginPath()
    {
        string? preferred = Environment.GetEnvironmentVariable("SHELL");
        string shell = preferred is not null && Path.IsPathFullyQualified(preferred) && File.Exists(preferred) ? preferred : "/bin/zsh";
        const string marker = "__HEX_LOGIN_PATH__";
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Login and interactive profiles both commonly set PATH. Keep their startup output separate from the answer.
        start.ArgumentList.Add("-lic");
        start.ArgumentList.Add($"/usr/bin/printf '\\n{marker}\\n'; /usr/bin/printenv PATH");
        try
        {
            using Process? process = Process.Start(start);
            if (process is null)
                return null;
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                Task.WhenAll(output, error).GetAwaiter().GetResult();
                string[] lines = output.Result.ReplaceLineEndings("\n").Split('\n');
                int at = Array.LastIndexOf(lines, marker);
                return at >= 0 && at + 1 < lines.Length ? lines[at + 1] : null;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                return null;
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
