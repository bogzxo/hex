using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

using Horizon.Core.Platform;

namespace Hex.Core;

/// <summary>
/// The one process of a shared MCP server, as <see cref="McpSharedServer"/> sees it: lines said to it, lines heard from
/// it, and its end. <see cref="Send"/> never waits, what's said is written in order by a thread of its own, so the
/// multiplexer can say it while it holds its lock.
/// </summary>
internal interface IMcpUpstream : IDisposable
{
    /// <summary>Its process, 0 for one that isn't a process (a test's).</summary>
    int Pid { get; }

    /// <summary>A line of JSON to it. Dropped once it's gone.</summary>
    void Send(string line);
}

/// <summary>How a shared server's process is started: the key, what to do with each line it says, and with its end.</summary>
internal delegate IMcpUpstream McpUpstreamStart(McpSharedKey key, Action<string> heard, Action<string> ended);

/// <summary>
/// A stdio MCP server started for a key, in the worktree's root, tied to Hex's life (ChildProcesses) so a Hex that's
/// killed doesn't leave a gigabyte of Roslyn behind. Its stdout read a line at a time, its stderr kept the last few lines
/// of to say when it ends, which is when its stdout does.
/// </summary>
internal sealed class McpProcessUpstream : IMcpUpstream
{
    private const int ERROR_LINES = 12;

    private readonly Process process;
    private readonly BlockingCollection<string> outgoing = new();
    private readonly Queue<string> errors = new();
    private int disposed;

    public int Pid { get; }

    private McpProcessUpstream(Process process)
    {
        this.process = process;
        Pid = process.Id;
    }

    /// <summary>Starts the key's command, throws when it can't be found or started.</summary>
    public static IMcpUpstream Start(McpSharedKey key, Action<string> heard, Action<string> ended)
    {
        string found = HexAgentProcess.Find(key.Command) ?? throw new FileNotFoundException($"{key.Command} isn't on the PATH");
        ProcessStartInfo info = HexAgentProcess.StartInfo(found, key.Arguments, Directory.Exists(key.Root) ? key.Root : Environment.CurrentDirectory);
        if (Environment.GetEnvironmentVariable("MSBUILDDISABLENODEREUSE") is null)
            info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        Process process = Process.Start(info) ?? throw new InvalidOperationException($"{key.Command} didn't start");
        ChildProcesses.Adopt(process);

        var upstream = new McpProcessUpstream(process);
        new Thread(upstream.Write) { IsBackground = true, Name = $"mcp shared {key.Name} in" }.Start();
        new Thread(upstream.ReadErrors) { IsBackground = true, Name = $"mcp shared {key.Name} err" }.Start();
        new Thread(() => upstream.Read(heard, ended)) { IsBackground = true, Name = $"mcp shared {key.Name} out" }.Start();
        return upstream;
    }

    public void Send(string line)
    {
        try
        {
            outgoing.Add(line);
        }
        catch (InvalidOperationException)
        {
            // Gone, said nowhere
        }
    }

    private void Write()
    {
        Stream input = process.StandardInput.BaseStream;
        try
        {
            foreach (string line in outgoing.GetConsumingEnumerable())
            {
                input.Write(Encoding.UTF8.GetBytes(line + "\n"));
                input.Flush();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        try { input.Dispose(); } catch (Exception) { }
    }

    private void Read(Action<string> heard, Action<string> ended)
    {
        try
        {
            using var reader = new StreamReader(process.StandardOutput.BaseStream, new UTF8Encoding(false), false, 64 * 1024);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 0)
                    heard(line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        string how;
        try
        {
            how = process.WaitForExit(3000) ? $"exit code {process.ExitCode}" : "its output closed";
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            how = "it went";
        }

        lock (errors)
        {
            if (errors.Count > 0)
                how += ", it said: " + string.Join(" | ", errors);
        }

        outgoing.CompleteAdding();
        ended(how);
    }

    private void ReadErrors()
    {
        try
        {
            while (process.StandardError.ReadLine() is { } line)
            {
                lock (errors)
                {
                    errors.Enqueue(line.Length > 300 ? line[..300] : line);
                    while (errors.Count > ERROR_LINES)
                        errors.Dequeue();
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>Its stdin closed for it to end by itself, and after a moment the whole tree of it killed.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
            return;
        try { outgoing.CompleteAdding(); } catch (Exception) { }
        _ = Task.Run(() =>
        {
            try
            {
                if (!process.WaitForExit(3000))
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
        });
    }
}
