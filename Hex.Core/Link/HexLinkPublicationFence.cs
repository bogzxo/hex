namespace Hex.Core;

internal enum HexLinkFenceKind { Publication, GitFollow, BulkApply, Hold }

internal readonly record struct HexLinkFenceStatus(HexLinkFenceKind Kind, bool Recovering);

/// <summary>Shared Link leases for absolute filesystem scopes. An exclusive lease covers its entire subtree.</summary>
internal sealed class HexLinkPublicationFence
{
    private static readonly Lock Gate = new();
    private static readonly List<Lease> Active = [];
    private static readonly List<Request> Waiting = [];
    private static readonly Dictionary<(string Scope, Guid Id), HexLinkFenceKind> Recovery = [];
    private static TaskCompletionSource? changed;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly string scope;

    private HexLinkPublicationFence(string scope) => this.scope = scope;

    public static HexLinkPublicationFence ForRoot(string root) =>
        new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));

    public HexLinkFenceStatus? Status
    {
        get
        {
            lock (Gate)
            {
                foreach (var (key, kind) in Recovery)
                    if (Overlaps(scope, key.Scope))
                        return new(kind, true);
                foreach (Lease lease in Active)
                    if (lease.Request.Kind is { } kind && Overlaps(scope, lease.Request.Scope))
                        return new(kind, false);
                foreach (Request request in Waiting)
                    if (request.Kind is { } kind && Overlaps(scope, request.Scope))
                        return new(kind, false);
                return null;
            }
        }
    }

    // Restore durable incomplete work before starting live sync. Clear only after projection and its receipt are durable.
    public void HoldRecovery(Guid id, HexLinkFenceKind kind)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A recovery needs a transaction ID.", nameof(id));
        lock (Gate)
        {
            RemoveRecovery(id);
            Recovery[(scope, id)] = kind;
            Signal();
        }
    }

    public void ReleaseRecovery(Guid id)
    {
        lock (Gate)
        {
            if (RemoveRecovery(id))
                Signal();
        }
    }

    private bool RemoveRecovery(Guid id)
    {
        foreach (var key in Recovery.Keys)
            if (key.Id == id && PlatformPaths.Comparer.Equals(key.Scope, scope))
                return Recovery.Remove(key);
        return false;
    }

    public ValueTask<Lease> EnterLive(CancellationToken cancel) =>
        Enter(new Request(scope, null, null), DefaultTimeout, cancel);

    public ValueTask<Lease> EnterExclusive(HexLinkFenceKind kind, CancellationToken cancel,
        TimeSpan? timeout = null, Guid? recovery = null) =>
        Enter(new Request(scope, kind, recovery), timeout ?? DefaultTimeout, cancel);

    private static ValueTask<Lease> Enter(Request request, TimeSpan timeout, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (Gate)
        {
            if (CanEnter(request))
                return ValueTask.FromResult(Grant(request, timeout, cancel));
            Waiting.Add(request);
        }
        return Wait(request, timeout, cancel);
    }

    private static async ValueTask<Lease> Wait(Request request, TimeSpan timeout, CancellationToken cancel)
    {
        using var until = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        until.CancelAfter(timeout);
        try
        {
            while (true)
            {
                until.Token.ThrowIfCancellationRequested();
                Task wake;
                lock (Gate)
                {
                    if (CanEnter(request))
                        return Grant(request, timeout, cancel);
                    changed ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                    wake = changed.Task;
                }
                await wake.WaitAsync(until.Token);
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new HexLinkException(HexLinkErrors.BUSY, "Link is waiting for another change in this folder.");
        }
        finally
        {
            lock (Gate)
            {
                Waiting.Remove(request);
                Signal();
            }
        }
    }

    private static bool CanEnter(Request request)
    {
        bool recovering = false;
        foreach (var (key, kind) in Recovery)
        {
            if (!Overlaps(request.Scope, key.Scope))
                continue;
            if (request.Kind != kind || request.Recovery != key.Id || !PlatformPaths.Comparer.Equals(request.Scope, key.Scope))
                return false;
            recovering = true;
        }
        foreach (Lease active in Active)
        {
            if ((request.Kind is not null || active.Request.Kind is not null) && Overlaps(request.Scope, active.Request.Scope))
                return false;
        }
        foreach (Request earlier in Waiting)
        {
            if (ReferenceEquals(earlier, request))
                break;
            if (earlier.Kind is not null && Overlaps(earlier.Scope, request.Scope) &&
                (!recovering || earlier.Recovery == request.Recovery))
                return false;
        }
        return true;
    }

    private static Lease Grant(Request request, TimeSpan timeout, CancellationToken cancel)
    {
        var lease = new Lease(request, timeout, cancel);
        Active.Add(lease);
        return lease;
    }

    private static bool Overlaps(string first, string second) =>
        Contains(first, second) || Contains(second, first);

    private static bool Contains(string parent, string child) => child.StartsWith(parent, PlatformPaths.Comparison) &&
        (child.Length == parent.Length || Path.EndsInDirectorySeparator(parent) || child[parent.Length] == Path.DirectorySeparatorChar);

    private static void Signal()
    {
        changed?.TrySetResult();
        changed = null;
    }

    internal sealed class Request(string scope, HexLinkFenceKind? kind, Guid? recovery)
    {
        public string Scope { get; } = scope;
        public HexLinkFenceKind? Kind { get; } = kind;
        public Guid? Recovery { get; } = recovery;
    }

    internal sealed class Lease : IAsyncDisposable
    {
        private readonly CancellationTokenSource? deadline;
        internal Request Request { get; }
        public CancellationToken Stop { get; }

        internal Lease(Request request, TimeSpan timeout, CancellationToken cancel)
        {
            Request = request;
            if (request.Kind is not null)
            {
                deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                deadline.CancelAfter(timeout);
            }
            Stop = deadline?.Token ?? cancel;
        }

        public ValueTask DisposeAsync()
        {
            lock (Gate)
            {
                if (!Active.Remove(this))
                    return ValueTask.CompletedTask;
                Signal();
            }
            deadline?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
