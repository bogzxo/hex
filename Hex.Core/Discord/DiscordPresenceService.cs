namespace Hex.Core;

/// <summary>
/// When to connect and when to send, as plain arithmetic over a clock in milliseconds so a test can drive it without
/// waiting. Connecting is tried again after 5 s, doubling to 2 minutes while Discord isn't there; presence is sent at
/// most every 15 s (Discord rate limits it), the latest state and not every one in between, and straight away on a
/// connection that was just made.
/// </summary>
internal sealed class DiscordPacing(long firstRetry = DiscordPacing.FIRST_RETRY_MS, long longestRetry = DiscordPacing.LONGEST_RETRY_MS,
    long sendEvery = DiscordPacing.SEND_EVERY_MS)
{
    public const long FIRST_RETRY_MS = 5_000, LONGEST_RETRY_MS = 120_000, SEND_EVERY_MS = 15_000;

    private long retryAfter;
    private long lastSent = long.MinValue / 2;

    /// <summary>Whether there is a state the last send didn't have, a clear included.</summary>
    public bool Pending { get; private set; }

    /// <summary>Whether a connection is wanted: there's a presence to show or a clear to send.</summary>
    public bool Wanted { get; private set; }

    public bool Connected { get; private set; }

    /// <summary>The wait after the last failed connect, 0 when it didn't fail.</summary>
    public long Backoff { get; private set; }

    /// <summary>A new state to show, or a clear, came in.</summary>
    public void Offer()
    {
        Wanted = true;
        Pending = true;
    }

    /// <summary>Nothing to show and nothing to clear: a presence switched off while not connected has nothing to take down.</summary>
    public void Drop()
    {
        Wanted = false;
        Pending = false;
    }

    public bool ShouldConnect(long now) => Wanted && !Connected && now >= retryAfter;

    public void OnConnected()
    {
        Connected = true;
        Backoff = 0;
        lastSent = long.MinValue / 2;
        Pending = Wanted;
    }

    public void OnConnectFailed(long now)
    {
        Connected = false;
        Backoff = Backoff == 0 ? firstRetry : Math.Min(Backoff * 2, longestRetry);
        retryAfter = now + Backoff;
    }

    /// <summary>A connection that was up went away, tried again soon and the last state sent again.</summary>
    public void OnLost(long now)
    {
        Connected = false;
        Backoff = 0;
        retryAfter = now + firstRetry;
        Pending = Wanted;
    }

    public bool ShouldSend(long now) => Connected && Pending && now - lastSent >= sendEvery;

    public void OnSent(long now)
    {
        lastSent = now;
        Pending = false;
    }

    /// <summary>How long to wait before there is something to do, null for until something is offered.</summary>
    public long? WaitMs(long now)
    {
        if (!Wanted)
            return null;
        if (!Connected)
            return Math.Max(0, retryAfter - now);
        return Pending ? Math.Max(0, lastSent + sendEvery - now) : null;
    }
}

/// <summary>
/// Shows what's being edited on Discord. A background task does all of it, so nothing here blocks the editor: it connects
/// when there's a presence to show and Discord is running, tries again with backoff when it isn't, sends the latest
/// <see cref="Update"/> at most every 15 s, and clears the presence when it's switched off or disposed.
/// </summary>
internal sealed class DiscordPresenceService : IAsyncDisposable
{
    private readonly string applicationId;
    private readonly IDiscordConnector connector;
    private readonly TimeProvider time;
    private readonly DiscordPacing pacing;
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DiscordActivity? latest;

    public DiscordPresenceService(string applicationId, IDiscordConnector connector, DiscordPacing? pacing = null, TimeProvider? time = null)
    {
        this.applicationId = applicationId;
        this.connector = connector;
        this.time = time ?? TimeProvider.System;
        this.pacing = pacing ?? new DiscordPacing();
        loop = Task.Run(Loop);
    }

    /// <summary>Whether Discord is connected right now.</summary>
    public bool Connected
    {
        get { lock (gate) return pacing.Connected; }
    }

    /// <summary>The state to show, null to show nothing (which takes down what was shown). Never blocks.</summary>
    public void Update(DiscordActivity? activity)
    {
        lock (gate)
        {
            latest = activity;
            if (activity is null && !pacing.Connected)
                pacing.Drop();
            else
                pacing.Offer();
            signal.TrySetResult();
        }
    }

    private long Now => time.GetTimestamp() * 1000 / time.TimestampFrequency;

    private async Task Loop()
    {
        DiscordIpcClient? client = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                bool connect, send;
                long? wait;
                DiscordActivity? toSend;
                Task changed;
                lock (gate)
                {
                    long now = Now;
                    signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    changed = signal.Task;
                    connect = client is null && pacing.ShouldConnect(now);
                    send = client is not null && pacing.ShouldSend(now);
                    toSend = latest;
                    wait = pacing.WaitMs(now);
                }

                if (client is not null && client.Closed.IsCompleted)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    client = null;
                    lock (gate)
                        pacing.OnLost(Now);
                    continue;
                }

                if (connect)
                {
                    client = await DiscordIpcClient.ConnectAsync(connector, applicationId, TimeSpan.FromSeconds(5), stop.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (client is null)
                            pacing.OnConnectFailed(Now);
                        else
                            pacing.OnConnected();
                    }

                    continue;
                }

                if (send && client is not null)
                {
                    try
                    {
                        await client.SetActivityAsync(toSend, stop.Token).ConfigureAwait(false);
                        lock (gate)
                        {
                            // A state that came in while this went out is still to be sent
                            if (ReferenceEquals(toSend, latest))
                            {
                                pacing.OnSent(Now);
                                if (toSend is null)
                                    pacing.Drop();
                            }
                            else
                            {
                                pacing.OnSent(Now);
                                pacing.Offer();
                            }
                        }
                    }
                    catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
                    {
                        await client.DisposeAsync().ConfigureAwait(false);
                        client = null;
                        lock (gate)
                            pacing.OnLost(Now);
                    }

                    continue;
                }

                // Nothing to do until a state comes in, the time for the next step, or Discord goes away
                using var timerStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                Task timer = wait is { } ms ? Task.Delay(TimeSpan.FromMilliseconds(Math.Max(ms, 1)), time, timerStop.Token) : Task.Delay(Timeout.Infinite, timerStop.Token);
                Task closed = client?.Closed ?? timer;
                await Task.WhenAny(changed, timer, closed).ConfigureAwait(false);
                await timerStop.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (client is not null)
            {
                try
                {
                    // Leaving takes the presence down, so it doesn't stay up after Hex is gone
                    using var brief = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await client.SetActivityAsync(null, brief.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
                {
                }

                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        stop.Dispose();
    }
}
