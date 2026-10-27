using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core.Notebooks;

/// <summary>Where a kernel is, starting, waiting for work, busy with it, or gone (it ended, or never started).</summary>
internal enum KernelState
{
    Starting,
    Idle,
    Busy,
    Dead
}

/// <summary>What a kernel said, on which of its channels, for whoever's drawing it to take in on its own thread.</summary>
internal sealed record KernelEvent(string Channel, JupyterMessage Message);

/// <summary>
/// A Jupyter kernel started from its spec and talked to natively. Its ports picked and written to a connection file with
/// a fresh key, the kernel started with that file, then a socket on each of its channels (shell and control for asking,
/// iopub for everything it says as it works, stdin for when the code asks for input). What comes back is queued
/// (<see cref="Events"/>) for the editor's thread to take in a tick at a time, nothing is called back from the sockets'
/// threads. The kernel goes with Hex however Hex goes, see ChildProcesses.
/// </summary>
internal sealed partial class Kernel : IAsyncDisposable
{
    public const string SHELL = "shell", IOPUB = "iopub", CONTROL = "control", STDIN = "stdin";

    private readonly KernelSpec spec;
    private readonly string folder;
    private readonly string session = Guid.NewGuid().ToString("N");
    private readonly byte[] key = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource stop = new();

    private Process? process;
    private string? connectionFile;
    private ZmtpSocket? shell, iopub, control, stdin;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JupyterMessage>> waiting = new();

    /// <summary>Everything the kernel said, oldest first, for the editor's thread to take a tick at a time.</summary>
    public ConcurrentQueue<KernelEvent> Events { get; } = new();

    /// <summary>Where it is, as its last status said.</summary>
    public KernelState State { get; private set; } = KernelState.Starting;

    /// <summary>What it's called, and its language.</summary>
    public KernelSpec Spec => spec;

    /// <summary>What it said about itself once it was up (its language_info), empty until then.</summary>
    public JsonObject Info { get; private set; } = [];

    /// <summary>What the kernel wrote to its own output as it started and since, for when it never comes up.</summary>
    public StringBuilder Log { get; } = new();

    public Kernel(KernelSpec spec, string folder)
    {
        this.spec = spec;
        this.folder = folder;
    }

    /// <summary>
    /// Starts it and waits until it answers, up to so long (a first start of a Python can take a while). Throws with what
    /// it wrote when it never does.
    /// </summary>
    public async Task StartAsync(TimeSpan patience)
    {
        int[] ports = FreePorts(5);
        connectionFile = Path.Combine(Path.GetTempPath(), $"hex-kernel-{session}.json");
        var connection = new JsonObject
        {
            ["shell_port"] = ports[0],
            ["iopub_port"] = ports[1],
            ["stdin_port"] = ports[2],
            ["control_port"] = ports[3],
            ["hb_port"] = ports[4],
            ["ip"] = "127.0.0.1",
            ["key"] = Encoding.ASCII.GetString(key),
            ["transport"] = "tcp",
            ["signature_scheme"] = "hmac-sha256",
            ["kernel_name"] = spec.Name
        };
        await File.WriteAllTextAsync(connectionFile, connection.ToJsonString()).ConfigureAwait(false);

        var start = new ProcessStartInfo(spec.Argv[0])
        {
            WorkingDirectory = Directory.Exists(folder) ? folder : Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        foreach (string part in spec.Argv.Skip(1))
            start.ArgumentList.Add(part.Replace("{connection_file}", connectionFile, StringComparison.Ordinal).Replace("{resource_dir}", spec.Folder, StringComparison.Ordinal));
        foreach (var (name, value) in spec.Env)
            start.Environment[name] = value;
        start.Environment["PYTHONUNBUFFERED"] = "1";
        PrepareInterrupt(start);

        process = Process.Start(start) ?? throw new IOException($"{spec.Argv[0]} didn't start");
        ChildProcesses.Adopt(process);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Died();
        process.OutputDataReceived += (_, said) => Note(said.Data);
        process.ErrorDataReceived += (_, said) => Note(said.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(patience);
        try
        {
            shell = await Connect(ZmtpKind.Dealer, ports[0], timeout.Token).ConfigureAwait(false);
            iopub = await Connect(ZmtpKind.Sub, ports[1], timeout.Token).ConfigureAwait(false);
            await iopub.SubscribeAsync(string.Empty, timeout.Token).ConfigureAwait(false);
            stdin = await Connect(ZmtpKind.Dealer, ports[2], timeout.Token).ConfigureAwait(false);
            control = await Connect(ZmtpKind.Dealer, ports[3], timeout.Token).ConfigureAwait(false);

            Listen(shell, SHELL);
            Listen(iopub, IOPUB);
            Listen(stdin, STDIN);
            Listen(control, CONTROL);

            // Asked until it answers, the first few can go before it's listening, and iopub only hears what's published
            // after its subscription got there, so the answer's waited for on both
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                Task<JupyterMessage> asked = RequestAsync(SHELL, "kernel_info_request", [], timeout.Token);
                if (await Task.WhenAny(asked, Task.Delay(1000, timeout.Token)).ConfigureAwait(false) == asked)
                {
                    Info = asked.Result.Content["language_info"] as JsonObject ?? [];
                    break;
                }
            }

            if (State == KernelState.Starting)
                State = KernelState.Idle;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            Died();
            throw new TimeoutException($"{spec.DisplayName} didn't answer in {patience.TotalSeconds:0} seconds. {Log}".Trim());
        }
    }

    // Helper method for a socket on one of the kernel's ports, tried again until the kernel's listening on it
    private async Task<ZmtpSocket> Connect(ZmtpKind kind, int port, CancellationToken cancel)
    {
        while (true)
        {
            if (process is { HasExited: true })
                throw new IOException($"{spec.DisplayName} ended as it started. {Log}".Trim());

            var socket = new ZmtpSocket(kind, kind == ZmtpKind.Dealer ? System.Text.Encoding.ASCII.GetBytes(session) : null);
            try
            {
                await socket.ConnectAsync("127.0.0.1", port, cancel).ConfigureAwait(false);
                return socket;
            }
            catch (Exception e) when (e is SocketException or IOException)
            {
                await socket.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(100, cancel).ConfigureAwait(false);
            }
        }
    }

    // Helper method for ports nobody's listening on, asked of the system one at a time and let go of again
    private static int[] FreePorts(int count)
    {
        var listeners = new List<TcpListener>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
            }

            return [.. listeners.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port)];
        }
        finally
        {
            foreach (TcpListener listener in listeners)
                listener.Stop();
        }
    }

    private void Note(string? line)
    {
        if (line is null)
            return;
        lock (Log)
        {
            if (Log.Length < 64 * 1024)
                Log.AppendLine(line);
        }
    }

    private void Died()
    {
        if (State == KernelState.Dead)
            return;
        State = KernelState.Dead;
        Events.Enqueue(new KernelEvent(IOPUB, JupyterMessage.Make(session, "status", new JsonObject { ["execution_state"] = "dead" })));
        foreach (var asked in waiting.Values)
            asked.TrySetCanceled();
        waiting.Clear();
    }

    /// <summary>Asks it to stop, waits a moment for it to, then ends it whatever it said.</summary>
    public async Task ShutdownAsync()
    {
        if (process is { HasExited: false } && control is { Ready: true })
        {
            try
            {
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await RequestAsync(CONTROL, "shutdown_request", new JsonObject { ["restart"] = false }, patience.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(patience.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // It didn't, it's ended below
            }
        }

        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already
        }

        Died();
        foreach (ZmtpSocket? socket in (ZmtpSocket?[])[shell, iopub, control, stdin])
        {
            if (socket is not null)
                await socket.DisposeAsync().ConfigureAwait(false);
        }

        ReleaseInterrupt();
        try
        {
            if (connectionFile is not null)
                File.Delete(connectionFile);
        }
        catch (IOException)
        {
        }
        process?.Dispose();
    }
}
