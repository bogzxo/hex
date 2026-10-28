using System.Diagnostics;
using Hex.Dawn;
using Horizon.Core.Processes;

namespace Hex.Tests;

public class InstallCommandTests
{
    [Fact]
    public async Task Quoted_programs_and_shell_chains_preserve_paths_and_exit_codes()
    {
        if (!OperatingSystem.IsWindows()) return;
        string folder = Path.Combine(Path.GetTempPath(), "hex install & space " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string script = Path.Combine(folder, "install.cmd");
            File.WriteAllText(script, "@echo off\r\necho quoted argument: %~1\r\nexit /b 0\r\n");
            var info = HexLanguages.InstallStartInfo($"\"{script}\" \"package with spaces\" && echo second-command && exit /b 7");
            using var process = Process.Start(info)!;
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(7, process.ExitCode);
            Assert.Contains("quoted argument: package with spaces", await output);
            Assert.Contains("second-command", await output);
            Assert.Equal(string.Empty, await errors);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Installers_receive_current_machine_and_user_path()
    {
        var info = HexLanguages.InstallStartInfo("npm --version");
        Assert.Equal(ProcessEnvironment.Path(), info.Environment["PATH"]);
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardOutput && info.RedirectStandardError && info.RedirectStandardInput);
    }
}
