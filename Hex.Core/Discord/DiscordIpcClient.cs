using System.IO.Pipes;
using System.Net.Sockets;

namespace Hex.Core;

/// <summary>Opens the byte stream to a running Discord, null when none answers. Faked in tests.</summary>
internal interface IDiscordConnector
{
    ValueTask<Stream?> ConnectAsync(CancellationToken cancel);
}

/// <summary>
/// Where Discord listens: a named pipe discord-ipc-0..9 on Windows, a unix socket of the same name in the runtime or
/// temp folder elsewhere (and in the folders snap and flatpak builds keep it in).
/// </summary>
internal sealed class DiscordSystemConnector : IDiscordConnector
{
    private static readonly string[] Subfolders = ["", "snap.discord/", "app/com.discordapp.Discord/", ".flatpak/dev.vencord.Vesktop/xdg-run/"];

    /// <summary>The places to try in order: pipe names on Windows, socket paths elsewhere.</summary>
    public static IEnumerable<string> Candidates(bool windows, Func<string, string?> environment, Func<string, bool> folderExists)
    {
        if (windows)
        {
            for (int at = 0; at < 10; at++)
                yield return $"discord-ipc-{at}";
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? root in new[] { environment("XDG_RUNTIME_DIR"), environment("TMPDIR"), environment("TMP"), environment("TEMP"), "/tmp" })
        {
            if (string.IsNullOrEmpty(root) || !seen.Add(root))
                continue;

            foreach (string sub in Subfolders)
            {
                string folder = root.TrimEnd('/') + "/" + sub;
                if (!folderExists(folder))
                    continue;
                for (int at = 0; at < 10; at++)
                    yield return folder + $"discord-ipc-{at}";
            }
        }
    }

    public async ValueTask<Stream?> ConnectAsync(CancellationToken cancel)
    {
        foreach (string candidate in Candidates(OperatingSystem.IsWindows(), System.Environment.GetEnvironmentVariable, Directory.Exists))
        {
            cancel.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows() && !File.Exists(candidate))
                continue;
            if (await OpenAsync(candidate, cancel).ConfigureAwait(false) is { } stream)
                return stream;
        }

        return null;
    }

    /// <summary>One pipe name (Windows) or socket path, null when nothing is listening there.</summary>
    public static async ValueTask<Stream?> OpenAsync(string candidate, CancellationToken cancel)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeClientStream(".", candidate, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(250, cancel).ConfigureAwait(false);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(candidate), cancel).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or SocketException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// A connection to Discord's IPC: the handshake, SET_ACTIVITY and answering pings. Reads on its own task, writes one at a
/// time. <see cref="Closed"/> completes when the other end goes away or sends a close.
/// </summary>
internal sealed class DiscordIpcClient : IAsyncDisposable
{
    private readonly Stream stream;
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private Task? reading;

    private DiscordIpcClient(Stream stream) => this.stream = stream;

    /// <summary>Completes when the connection is over.</summary>
    public Task Closed { get; private set; } = Task.CompletedTask;

    /// <summary>Connects and shakes hands, null when Discord isn't there or doesn't say READY in time.</summary>
    public static async Task<DiscordIpcClient?> ConnectAsync(IDiscordConnector connector, string applicationId, TimeSpan readyWithin, CancellationToken cancel)
    {
        Stream? opened = await connector.ConnectAsync(cancel).ConfigureAwait(false);
        if (opened is null)
            return null;

        var client = new DiscordIpcClient(opened);
        try
        {
            await client.WriteAsync(DiscordFrame.HANDSHAKE, DiscordJson.Handshake(applicationId), cancel).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(readyWithin);
            while (true)
            {
                var frame = await DiscordFrame.ReadAsync(opened, timeout.Token).ConfigureAwait(false);
                if (frame is not { } read || read.Opcode == DiscordFrame.CLOSE)
                    throw new IOException("Discord closed the connection");
                if (read.Opcode == DiscordFrame.PING)
                    await client.WriteAsync(DiscordFrame.PONG, read.Payload, cancel).ConfigureAwait(false);
                else if (DiscordJson.EventOf(read.Payload) == "READY")
                    break;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            cancel.ThrowIfCancellationRequested();
            await client.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        client.reading = client.ReadLoop();
        client.Closed = client.reading;
        return client;
    }

    /// <summary>Sends the presence, null clears it.</summary>
    public Task SetActivityAsync(DiscordActivity? activity, CancellationToken cancel) =>
        WriteAsync(DiscordFrame.FRAME, DiscordJson.SetActivity(System.Environment.ProcessId, activity, Guid.NewGuid().ToString()), cancel);

    private async Task WriteAsync(int opcode, byte[] json, CancellationToken cancel)
    {
        byte[] frame = DiscordFrame.Encode(opcode, json);
        await writing.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, cancel).ConfigureAwait(false);
            await stream.FlushAsync(cancel).ConfigureAwait(false);
        }
        finally
        {
            writing.Release();
        }
    }

    // Answers pings and ends when the other end does
    private async Task ReadLoop()
    {
        try
        {
            while (true)
            {
                var frame = await DiscordFrame.ReadAsync(stream, stop.Token).ConfigureAwait(false);
                if (frame is not { } read || read.Opcode == DiscordFrame.CLOSE)
                    return;
                if (read.Opcode == DiscordFrame.PING)
                    await WriteAsync(DiscordFrame.PONG, read.Payload, stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // The connection is gone, which is what Closed says
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // A polite close, when there's still someone to hear it
            using var brief = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await WriteAsync(DiscordFrame.CLOSE, "{}"u8.ToArray(), brief.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
        }

        await stop.CancelAsync().ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
        if (reading is not null)
            await reading.ConfigureAwait(false);
        stop.Dispose();
        writing.Dispose();
    }
}
