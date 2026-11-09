using System.Diagnostics;

namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    internal static bool IsNativeSource(string? path) => path is not null && Path.GetExtension(path).ToLowerInvariant() is ".c" or ".cpp" or ".cc" or ".cxx";

    private void StartDebuggingNative(string file)
    {
        if (CMakeProject.RootOf(file, context.Workspace.Roots) is { } cmakeRoot)
        {
            StartDebuggingCMake(file, cmakeRoot);
            return;
        }
        string? adapter = LldbDap();
        bool cpp = !file.EndsWith(".c", StringComparison.OrdinalIgnoreCase);
        string? compiler = LanguageServerProgram.Locate(cpp ? "clang++" : "clang") ?? LanguageServerProgram.Locate(cpp ? "g++" : "gcc");
        if (adapter is null || compiler is null)
        {
            context.Notices.Say(context.Strings.Said("debugging.native_tools_required"), error: true);
            return;
        }
        SaveAllCode();
        CancelDebugBuild();
        var attempt = new CancellationTokenSource();
        debugBuild = attempt;
        building = true;
        conditionFailures.Clear();
        file = Path.GetFullPath(file);
        debuggedProject = file;
        restart = () => StartDebuggingNative(file);
        context.Panels.ShowOutput();
        context.Panels.ShowSide(HexSide.Debug);
        var breakpointsNow = BreakpointsNow();
        _ = Task.Run(async () =>
        {
            try
            {
                string folder = Path.GetDirectoryName(file)!;
                string buildFolder = Path.Combine(Path.GetTempPath(), "hex-native-debug-" + Guid.NewGuid());
                Directory.CreateDirectory(buildFolder);
                string program = Path.Combine(buildFolder, Path.GetFileNameWithoutExtension(file) + (OperatingSystem.IsWindows() ? ".exe" : ""));
                var info = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { "-g", "-O0", file, "-o", program }) info.ArgumentList.Add(argument);
                attempt.Token.ThrowIfCancellationRequested();
                using var process = Process.Start(info) ?? throw new IOException("Could not start the C/C++ compiler.");
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
                limit.CancelAfter(TimeSpan.FromMinutes(2));
                try { await process.WaitForExitAsync(limit.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    await process.WaitForExitAsync();
                    await Task.WhenAll(stdout, stderr);
                    attempt.Token.ThrowIfCancellationRequested();
                    throw new TimeoutException("C/C++ compilation exceeded two minutes.");
                }
                string said = await stdout + await stderr;
                if (!ReportBuild(said, folder, process.ExitCode, Path.GetFileName(file), file, attempt.Token)) return;
                await StartUnder(DebugSession.Start(adapter, []), Path.GetFileName(file), LLDB,
                    session => session.Launch(program, [], folder, breakpointsNow, LLDB, ProgramOutputFile()), attempt.Token);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
            catch (Exception error)
            {
                PostBuild(attempt.Token, () =>
                {
                    building = false;
                    context.Notices.Say(context.Strings.Said("debugging.launch_failed", ("why", error.Message)), error: true);
                });
            }
            finally
            {
                debugWork.Enqueue(() => { if (ReferenceEquals(debugBuild, attempt)) debugBuild = null; attempt.Dispose(); });
            }
        });
    }
}
