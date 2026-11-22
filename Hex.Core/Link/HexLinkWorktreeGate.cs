using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace Hex.Core;

internal sealed record HexLinkWorktreeGateOptions(string? Build = null, string? Tests = null, string? WorkingFolder = null);
internal sealed record HexLinkWorktreeGateResult(int ExitCode, string Output, string Log)
{
    public bool Passed => ExitCode == 0;
}

internal static class HexLinkWorktreeGate
{
    private const int MostOutput = 6000;

    public static async Task<HexLinkWorktreeGateResult> Run(string root, string log, HexLinkWorktreeGateOptions options,
        CancellationToken cancel)
    {
        string working = options.WorkingFolder is { Length: > 0 } relative
            ? HexLinkFiles.Resolve([new HexLinkFolder("gate", root)], "gate/" + relative)
            : root;
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        await using var writer = new StreamWriter(log, append: false);
        StringBuilder errors = new(), tail = new();
        int exit = options.Build is not { Length: > 0 } command
            ? await Stage("dotnet", ["build"], working, writer, errors, tail, cancel)
            : OperatingSystem.IsWindows()
                ? await Stage("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command], working, writer, errors, tail, cancel)
                : await Stage("/bin/sh", ["-c", command], working, writer, errors, tail, cancel);
        if (exit == 0 && options.Tests is { Length: > 0 } filter)
            exit = await Stage("dotnet", ["test", "--filter", filter], working, writer, errors, tail, cancel);
        string output = errors.Length > 0 ? errors.ToString() : tail.ToString();
        if (errors.Length is > 0 and < MostOutput)
            output += tail.ToString(0, Math.Min(tail.Length, MostOutput - output.Length));
        return new(exit, output.TrimEnd(), log);
    }

    private static async Task<int> Stage(string program, string[] args, string working, StreamWriter log,
        StringBuilder errors, StringBuilder tail, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = working,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in args)
            start.ArgumentList.Add(argument);
        cancel.ThrowIfCancellationRequested();
        using Process process = Process.Start(start) ?? throw new IOException($"Could not run {program}.");
        var lines = Channel.CreateBounded<(string Text, bool Error)>(128);

        async Task Read(StreamReader from, bool standardError)
        {
            while (await from.ReadLineAsync(cancel) is { } line)
                await lines.Writer.WriteAsync((line, standardError || line.Contains("error", StringComparison.OrdinalIgnoreCase)), cancel);
        }

        async Task Complete()
        {
            try
            {
                await Task.WhenAll(Read(process.StandardOutput, false), Read(process.StandardError, true));
                lines.Writer.TryComplete();
            }
            catch (Exception error)
            {
                lines.Writer.TryComplete(error);
            }
        }

        _ = Complete();
        try
        {
            await foreach (var (line, error) in lines.Reader.ReadAllAsync(cancel))
            {
                await log.WriteLineAsync(line);
                StringBuilder display = error ? errors : tail;
                if (error && display.Length >= MostOutput)
                    continue;
                if (!error && display.Length + line.Length > MostOutput)
                    display.Clear();
                display.AppendLine(line[..Math.Min(line.Length, Math.Max(0, MostOutput - display.Length - Environment.NewLine.Length))]);
            }
            await process.WaitForExitAsync(cancel);
            await log.FlushAsync(cancel);
            return process.ExitCode;
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }
}
