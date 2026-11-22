using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace Hex.Core;

internal sealed record HexLinkWorktreeGateOptions(string? Build = null, string? Tests = null, string? WorkingFolder = null,
    string? TestCommand = null, int TimeoutSeconds = HexLinkWorktreeGatePolicy.DefaultTimeout, string? Target = null)
{
    public string Describe() => $"{WorkingFolder ?? "."} | {Build ?? ("dotnet build" + (Target is null ? "" : " " + Target))}" +
        (TestCommand is { Length: > 0 } test ? $" | {test}" : Tests is { Length: > 0 } filter ? $" | dotnet test --filter {filter}" : "") +
        $" | {TimeoutSeconds}s";
}

internal sealed record HexLinkWorktreeGateResult(int ExitCode, string Output, string Log, IReadOnlyList<string> Ran)
{
    public bool Passed => ExitCode == 0;
}

internal static class HexLinkWorktreeGate
{
    private const int MostOutput = 6000;

    public static Task<HexLinkWorktreeGateResult> Run(string root, string log, HexLinkWorktreeGateOptions options,
        CancellationToken cancel) => Run(root, log, [options], cancel);

    public static async Task<HexLinkWorktreeGateResult> Run(string root, string log, IReadOnlyList<HexLinkWorktreeGateOptions> gates,
        CancellationToken cancel)
    {
        if (gates.Count == 0)
            throw new IOException("No publish gate covers these changes.");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        await using var writer = new StreamWriter(log, append: false);
        StringBuilder errors = new(), tail = new();
        var ran = new List<string>();
        int exit = 0;
        foreach (var options in gates)
        {
            string working = options.WorkingFolder is { Length: > 0 } relative
                ? HexLinkFiles.Resolve([new HexLinkFolder("gate", root)], "gate/" + relative)
                : root;
            if (!Directory.Exists(working))
                throw new IOException($"Publish gate directory '{options.WorkingFolder}' does not exist in the candidate.");
            string description = options.Describe();
            ran.Add(description);
            await writer.WriteLineAsync(description);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                exit = options.Build is { Length: > 0 } command
                    ? await Shell(command, working, writer, errors, tail, deadline.Token)
                    : await Stage("dotnet", options.Target is { } project ? ["build", project] : ["build"],
                        working, writer, errors, tail, deadline.Token);
                if (exit == 0 && options.TestCommand is { Length: > 0 } test)
                    exit = await Shell(test, working, writer, errors, tail, deadline.Token);
                if (exit == 0 && options.Tests is { Length: > 0 } filter)
                    exit = await Stage("dotnet", options.Target is { } target ? ["test", target, "--filter", filter] : ["test", "--filter", filter],
                        working, writer, errors, tail, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                string timeout = $"Publish gate timed out after {options.TimeoutSeconds} seconds.";
                await writer.WriteLineAsync(timeout);
                errors.Insert(0, timeout + Environment.NewLine);
                exit = 124;
            }
            if (exit != 0)
                break;
        }

        string output = errors.Length > 0 ? errors.ToString(0, Math.Min(errors.Length, MostOutput)) : tail.ToString();
        if (errors.Length is > 0 and < MostOutput)
            output += tail.ToString(0, Math.Min(tail.Length, MostOutput - output.Length));
        return new(exit, output.TrimEnd(), log, ran);
    }

    private static Task<int> Shell(string command, string working, StreamWriter log, StringBuilder errors, StringBuilder tail,
        CancellationToken cancel) =>
        OperatingSystem.IsWindows()
            ? Stage("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command], working, log, errors, tail, cancel)
            : Stage("/bin/sh", ["-c", command], working, log, errors, tail, cancel);

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
