using System.Diagnostics;

namespace Hex.Core;

internal static class PythonDebuggerSetup
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hex", "Tools", "debugpy");
    public static bool Ready(string folder) => File.Exists(PythonEnvironment.ExecutableIn(folder)) && File.Exists(Path.Combine(folder, "debugpy.ready"));

    public static async Task Install(string python, string folder, Action<string> output, CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        string marker = Path.Combine(folder, "debugpy.ready");
        if (File.Exists(marker)) File.Delete(marker);
        await Run(python, ["-m", "venv", folder], output, cancel);
        string adapter = PythonEnvironment.ExecutableIn(folder);
        await Run(adapter, ["-m", "pip", "install", "--disable-pip-version-check", "debugpy"], output, cancel);
        await Run(adapter, ["-c", "import debugpy.adapter; import debugpy; print(debugpy.__version__)"], output, cancel);
        cancel.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(marker, "debugpy import verified", cancel);
    }

    private static async Task Run(string program, string[] arguments, Action<string> output, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Python.");
        process.StandardInput.Close();
        async Task Read(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line) output(line);
        }
        Task stdout = Read(process.StandardOutput), stderr = Read(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            cancel.ThrowIfCancellationRequested();
            throw new TimeoutException("Python debugger setup timed out.");
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0) throw new IOException($"Python debugger setup exited with code {process.ExitCode}. See Output for details.");
    }
}
