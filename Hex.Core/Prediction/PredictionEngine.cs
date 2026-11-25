using System.Text;

namespace Hex.Core;

/// <summary>
/// The life of a prediction, with no thread or clock of its own. The editor's thread says a document was edited and
/// asks every tick; once the typing has paused for the debounce, a snapshot of the document is taken and a task off the
/// thread builds the prompt, looks in the cache, asks the provider and reads its answer, stopping as soon as the answer
/// is settled. The editor's thread takes the finished prediction, never one for a version the document has moved past.
/// One request at a time: a newer edit cancels the one in flight, which closes its connection.
/// </summary>
internal sealed class PredictionEngine(TimeProvider time)
{
    // Requests that fail in a row before they're only tried every half minute
    private const int FAILURES_TO_WAIT = 3;
    private static readonly TimeSpan retry = TimeSpan.FromSeconds(30);

    private readonly Lock gate = new();

    // The document being typed in, its version, and what's wanted of it; written on the editor's thread
    private object? document;
    private int version;
    private bool pending, immediate, dismissed;
    private long editedAt;

    // Failures in a row and when the next try may be
    private int failures;
    private long retryAt;

    // The request in flight, and the finished prediction waiting to be taken
    private CancellationTokenSource? flight;
    private Task running = Task.CompletedTask;
    private PredictionResult? ready;

    // The provider made for the options it was made for
    private IPredictionProvider? provider;
    private PredictionOptions? providerFor;

    private int requests, cancelled;

    // Far (jump) predictions (docs/next-edit-plan.md, 2.2 A and 4.4): the places still to ask about once a near request is
    // done, what they are asked of, and the answers waiting to be taken. Guarded by gate.
    private const int FAR_IDLE_MS = 600;
    private const int FAR_KEPT = 8;
    private const int FAR_STALE_MS = 8000;
    private readonly List<NextEditCandidate> farQueue = [];
    private PredictionSnapshot? farSnapshot;
    private string farText = "";
    private PredictionStrategy? farStrategy;
    private long farAt;
    private bool farNow;
    private int farRequests, farWaiting;
    private readonly List<FarAnswer> farReady = [];

    private sealed record FarAnswer(object Document, int Version, string Text, NextEdit Edit);

    /// <summary>
    /// Set while an instruction edit (Ctrl+I) is being made: nothing is asked for beside it, the priority being instruction
    /// edit, then near the caret, then far.
    /// </summary>
    public bool InstructActive { get; set; }

    /// <summary>Far requests sent to the provider, the cache's answers not counted.</summary>
    public int FarRequests => Volatile.Read(ref farRequests);

    /// <summary>Whether a far prediction is waiting to be taken; cheap to ask every tick.</summary>
    public bool HasFar => Volatile.Read(ref farWaiting) > 0;

    /// <summary>The places still to ask a far question about, for the tests.</summary>
    internal int FarPending
    {
        get
        {
            lock (gate)
                return farQueue.Count;
        }
    }

    /// <summary>Whether the window has the user's attention; far questions are not asked while it hasn't. Asked on the editor's thread.</summary>
    public Func<bool> Active { get; set; } = static () => true;

    /// <summary>What the settings say, null when predictions are off; asked on the editor's thread.</summary>
    public Func<PredictionOptions?> Options { get; set; } = () => null;

    public bool ShortcutOnly
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            ShutDown();
            immediate = false;
            SetStatus(PredictionStatus.Ok, "");
        }
    }

    /// <summary>
    /// The document as it is, taken on the editor's thread when the debounce is over, null when nothing is to be asked
    /// for it now (the completion list open, text selected, the file excluded, the window not focused).
    /// </summary>
    public Func<object, PredictionSnapshot?> Snapshot { get; set; } = _ => null;

    /// <summary>The provider asked in place of the one the options make, for the tests.</summary>
    public IPredictionProvider? ProviderOverride { get; set; }

    /// <summary>The recent edits sent with each prompt; the glue notes accepted predictions and closed files here.</summary>
    public PredictionHistory History { get; } = new();

    /// <summary>The predictions already had, by the text round the caret.</summary>
    public PredictionCache Cache { get; } = new();

    /// <summary>How predictions stand, for the status bar.</summary>
    public PredictionStatus Status { get; private set; } = PredictionStatus.Off;

    /// <summary>The status in words: what went wrong, or where predictions come from.</summary>
    public string StatusText { get; private set; } = "";

    /// <summary>How long the last prediction took.</summary>
    public PredictionTiming LastTiming { get; private set; }

    /// <summary>Requests sent to the provider, the cache's answers not counted.</summary>
    public int Requests => Volatile.Read(ref requests);

    /// <summary>Requests cancelled because the document changed before they were answered.</summary>
    public int Cancelled => Volatile.Read(ref cancelled);

    /// <summary>The request in flight, or the last one, finished; for the tests to wait on.</summary>
    internal Task Running => running;

    /// <summary>
    /// A document was edited (typing, not caret moves). Whatever was in flight or waiting for it is dropped and the
    /// debounce starts again, unless the edit typed through a prediction that's showing, which leaves it be. Allocates
    /// nothing.
    /// </summary>
    public void Edited(object document, int version, bool typedThrough = false)
    {
        bool same = ReferenceEquals(this.document, document);
        this.document = document;
        this.version = version;
        dismissed = false;
        editedAt = time.GetTimestamp();
        immediate = false;
        pending = !ShortcutOnly && !(typedThrough && same);
        lock (gate)
        {
            // A keystroke ends the far questions: the ones in flight and the ones still to ask
            farQueue.Clear();
            farSnapshot = null;
            farNow = false;
            if (flight is not null)
            {
                Stop(counted: true);
                if (!pending && Status == PredictionStatus.Waiting)
                    SetStatus(PredictionStatus.Ok, "");
            }
        }
        if (ready is not null)
            Volatile.Write(ref ready, null);
    }

    /// <summary>Asked every tick on the editor's thread. Allocates nothing while there's nothing to ask.</summary>
    public void Tick()
    {
        if (!pending || document is null)
        {
            TickFar();
            return;
        }

        long now = time.GetTimestamp();
        if (failures >= FAILURES_TO_WAIT && now < retryAt)
            return;

        PredictionOptions? options = Options();
        if (options is not { On: true })
        {
            pending = false;
            SetStatus(PredictionStatus.Off, "Edit predictions are off.");
            return;
        }
        if (!immediate && time.GetElapsedTime(editedAt, now).TotalMilliseconds < options.DebounceMs)
            return;

        // An instruction edit comes first: the request waits, and is asked for when it is over
        if (InstructActive)
            return;

        pending = false;
        immediate = false;
        if (dismissed || Snapshot(document) is not { } snapshot)
            return;

        IPredictionProvider? asked = ProviderOverride ?? Provider(options);
        if (asked is null)
        {
            SetStatus(PredictionStatus.Off, "No provider is set up: the settings need a URL and a model.");
            return;
        }

        StartRun(snapshot, options, asked);
    }

    // Apart from Tick so the closure the run needs is made only when there is a run: a closure over what Tick holds would be
    // made at every tick, idle ones too
    private void StartRun(PredictionSnapshot snapshot, PredictionOptions options, IPredictionProvider asked)
    {
        Stop(counted: true);
        var cancel = new CancellationTokenSource();
        flight = cancel;
        SetStatus(PredictionStatus.Waiting, "Waiting for a prediction.");
        running = Task.Run(() => Run(snapshot, options, asked, cancel.Token));
    }

    /// <summary>
    /// The finished prediction, once, on the editor's thread; null when there's none or it was made for a version or a
    /// document that's no longer the one being typed in. The glue still checks the caret hasn't moved.
    /// </summary>
    public PredictionResult? Take()
    {
        if (Volatile.Read(ref ready) is null)
            return null;
        PredictionResult? result = Interlocked.Exchange(ref ready, null);
        if (result is null || !ReferenceEquals(result.Document, document) || result.Version != version)
            return null;
        return result;
    }

    /// <summary>The serving device changed. Drop its result and any failure backoff.</summary>
    public void LinkSourceChanged()
    {
        lock (gate)
        {
            bool asking = flight is not null && !running.IsCompleted;
            Stop(counted: true);
            ClearFar(answers: true);
            Volatile.Write(ref ready, null);
            failures = 0;
            retryAt = 0;
            LastTiming = default;
            SetStatus(PredictionStatus.Off, "");
            pending |= asking && !ShortcutOnly;
        }
    }

    /// <summary>A prediction was taken: the next is asked for at once, without the debounce, while the hand is still on Tab.</summary>
    public void Accepted(object document)
    {
        if (ShortcutOnly)
            return;
        this.document = document;
        editedAt = time.GetTimestamp();
        pending = true;
        immediate = true;
        dismissed = false;
        farNow = true;
    }

    /// <summary>A prediction was dismissed: nothing more is asked for until something is typed.</summary>
    public void Dismissed(object document)
    {
        if (!ReferenceEquals(this.document, document))
            return;
        lock (gate)
        {
            Stop(counted: true);
            ClearFar();
        }
        Volatile.Write(ref ready, null);
        pending = false;
        dismissed = true;
    }

    /// <summary>A prediction asked for by hand: now, whatever was dismissed and however many failed.</summary>
    public void PredictNow(object document)
    {
        this.document = document;
        pending = true;
        immediate = true;
        dismissed = false;
        retryAt = 0;
    }

    /// <summary>A document closed or left: what was in flight or waiting for it goes.</summary>
    public void Forget(object document)
    {
        if (!ReferenceEquals(this.document, document))
            return;
        lock (gate)
        {
            Stop(counted: false);
            ClearFar(answers: true);
            Volatile.Write(ref ready, null);
            this.document = null;
            pending = false;
            if (Status == PredictionStatus.Waiting)
                SetStatus(PredictionStatus.Ok, "");
        }
    }

    /// <summary>
    /// Asked when predictions are about to be wanted (switched on, a code file on screen): an Ollama server is told to load
    /// the model, once per set of options, so the first keystroke doesn't wait for the load. A server or model that isn't
    /// there is the status at once, not only after the first request.
    /// </summary>
    public void Warm()
    {
        if (ShortcutOnly)
            return;
        PredictionOptions? options = Options();
        if (options is not { On: true } || options == warmedFor)
            return;
        warmedFor = options;
        if (ProviderOverride is null)
            _ = PredictionModels.Probe(PredictionProviders.Shared, options, CancellationToken.None);
        if ((ProviderOverride ?? Provider(options)) is not OllamaProvider ollama)
            return;

        var warming = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref preload, warming);
        _ = previous?.CancelAsync();
        _ = Task.Run(async () =>
        {
            PredictionException? problem = await ollama.Preload(warming.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (warmedFor != options || Status is PredictionStatus.Waiting)
                    return;
                if (problem is not null)
                    SetStatus(problem.Status, problem.Message);
                else if (Status is PredictionStatus.Unreachable or PredictionStatus.NoSuchModel)
                    SetStatus(PredictionStatus.Ok, "");
            }
        });
    }

    /// <summary>
    /// The far predictions made, once, on the editor's thread, as suggestions for the document as it is now (<paramref name="text"/>
    /// at <paramref name="version"/>). One made for an older text is moved along with what was typed since, and let go of
    /// when that touched it. Those made for another document are dropped.
    /// </summary>
    public IReadOnlyList<NextEdit> TakeFar(object document, int version, string text)
    {
        lock (gate)
        {
            if (farReady.Count == 0)
                return [];
            var taken = new List<NextEdit>(farReady.Count);
            foreach (FarAnswer answer in farReady)
            {
                if (!ReferenceEquals(answer.Document, document))
                    continue;
                NextEdit? edit = answer.Version == version ? answer.Edit : NextEditMerge.Rebased(answer.Edit, answer.Text, text);
                if (edit is not null)
                    taken.Add(edit with { Version = version });
            }
            farReady.Clear();
            Volatile.Write(ref farWaiting, 0);
            return taken;
        }
    }

    // The far questions and, when asked, the answers go. Called with the gate held.
    private void ClearFar(bool answers = false)
    {
        farQueue.Clear();
        farSnapshot = null;
        farNow = false;
        if (answers)
        {
            farReady.Clear();
            Volatile.Write(ref farWaiting, 0);
        }
    }

    // The next far question, when nothing else is being asked: no near request waiting or in flight, no instruction edit,
    // the typing paused (the questions were made at the end of a near request, a tick later than that when it is idle)
    private void TickFar()
    {
        if (farQueue.Count == 0 || InstructActive || ShortcutOnly || document is null)
            return;
        if (flight is not null && !running.IsCompleted)
            return;

        long now = time.GetTimestamp();

        // Far questions are for someone at the keyboard: none are asked in an unfocused window, or after the typing (or the
        // last prediction taken) has been left for FAR_STALE_MS
        if (!Active() || time.GetElapsedTime(editedAt, now).TotalMilliseconds > FAR_STALE_MS)
        {
            lock (gate)
                ClearFar();
            return;
        }

        if (failures >= FAILURES_TO_WAIT && now < retryAt)
            return;

        PredictionOptions? options = Options();
        if (options is not { On: true })
            return;

        PredictionSnapshot snapshot;
        string text;
        PredictionStrategy strategy;
        NextEditCandidate candidate;
        lock (gate)
        {
            if (farQueue.Count == 0 || farSnapshot is not { } held || farStrategy is not { } how || now < farAt)
                return;
            if (!ReferenceEquals(held.Document, document) || held.Version != version)
            {
                ClearFar();
                return;
            }
            snapshot = held;
            text = farText;
            strategy = how;
            candidate = farQueue[0];
            farQueue.RemoveAt(0);
        }

        IPredictionProvider? asked = ProviderOverride ?? Provider(options);
        if (asked is null)
            return;

        StartFar(snapshot, text, candidate, options, strategy, asked);
    }

    // Apart from TickFar for the same reason as StartRun: no closure at an idle tick
    private void StartFar(PredictionSnapshot snapshot, string text, NextEditCandidate candidate, PredictionOptions options,
        PredictionStrategy strategy, IPredictionProvider asked)
    {
        Stop(counted: true);
        var cancel = new CancellationTokenSource();
        flight = cancel;
        running = Task.Run(() => RunFar(snapshot, text, candidate, options, strategy, asked, cancel.Token));
    }

    // At the end of a near request: the places to ask about, the nearest first, started once the typing has been quiet
    // (at once after a prediction was taken)
    private void ScheduleFar(PredictionSnapshot snapshot, string text, int caret, PredictionStrategy strategy, PredictionOptions options, CancellationToken cancel)
    {
        if (!options.Far || strategy.Far == PredictionFar.None || ShortcutOnly || snapshot.Notebook is not null)
            return;
        IReadOnlyList<NextEditCandidate> candidates = NextEditLocator.Candidates(text, History.Events(snapshot.Path), caret, snapshot.Path);
        if (candidates.Count == 0)
            return;

        lock (gate)
        {
            if (cancel.IsCancellationRequested || dismissed || !ReferenceEquals(snapshot.Document, document) || snapshot.Version != version)
                return;
            farQueue.Clear();
            farQueue.AddRange(candidates);
            farSnapshot = snapshot;
            farText = text;
            farStrategy = strategy;
            farAt = time.GetTimestamp() + (farNow ? 0 : (long)(FAR_IDLE_MS / 1000.0 * time.TimestampFrequency));
        }
    }

    // Off the editor's thread: one far question, the prompt for the region at the candidate's line, the cache, the request
    // and its answer. An answer that changes nothing is "no edit here"; one that changes something is made ready.
    private async Task RunFar(PredictionSnapshot snapshot, string text, NextEditCandidate candidate, PredictionOptions options,
        PredictionStrategy strategy, IPredictionProvider asked, CancellationToken cancel)
    {
        long started = time.GetTimestamp();
        IPredictionFormat format = strategy.Near;
        try
        {
            var input = new PredictionInput(snapshot.Path, snapshot.Language, text, candidate.Start,
                History.Events(snapshot.Path), snapshot.Related?.Invoke() ?? [], null, History.Renames.Hint(snapshot.Path, text));

            PredictionPrompt? prompt;
            if (strategy.Far == PredictionFar.Chat)
            {
                // The chat prompt needs the turns, which Hex Link can't carry
                if (asked is LinkProvider)
                    return;
                format = ChatEditFormat.Near;
                prompt = ChatEditFormat.Near.Build(input, options.MaxTokens);
            }
            else
            {
                var zeta = strategy.Near as Zeta2Format ?? PredictionFormats.Named(PredictionFormats.ZETA21) as Zeta2Format;
                if (zeta is null)
                    return;
                format = zeta;
                var (regionStart, regionEnd, contextStart, contextEnd) = Zeta2Format.Ranges(text, candidate.Start);
                prompt = zeta.Build(input, options.MaxTokens, regionStart, regionEnd, contextStart, contextEnd);
            }
            if (prompt is null)
                return;

            // The same region with other history behind it is another question
            PredictionCache? cache = asked is LinkProvider link ? link.Cache : Cache;
            string key = PredictionCache.FarKey(prompt.Region, options.Model, format.Name, PredictionHistory.Hash(input.Events));
            if (cache is not null && cache.TryGet(key, prompt.RegionStart, out IReadOnlyList<PredictionEdit> cached, out _))
            {
                FarDone(snapshot, text, candidate, cached, cancel);
                return;
            }

            Interlocked.Increment(ref requests);
            Interlocked.Increment(ref farRequests);
            PredictionLog.FarAsked();
            var output = new StringBuilder();
            PredictionParsed? parsed = null;
            await foreach (string piece in asked.Complete(prompt.Request, cancel).ConfigureAwait(false))
            {
                output.Append(piece);
                parsed = format.Parse(prompt, output.ToString(), false);
                if (parsed.Done)
                    break;
            }

            cancel.ThrowIfCancellationRequested();
            if (parsed is not { Done: true })
                parsed = format.Parse(prompt, output.ToString(), true);

            cache?.Put(key, prompt.RegionStart, parsed.Edits, null);
            FarDone(snapshot, text, candidate, parsed.Edits, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // A keystroke, a dismissal or a closed document: nothing to say
        }
        catch (PredictionException exception)
        {
            Fail(exception.Status, exception.Message, started, format, cancel);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fail(PredictionStatus.BadAnswer, "The prediction failed: " + exception.GetType().Name + ".", started, format, cancel);
        }
    }

    // A far answer in: nothing changed is "no edit here", a change is kept for the editor's thread to take
    private void FarDone(PredictionSnapshot snapshot, string text, NextEditCandidate candidate, IReadOnlyList<PredictionEdit> edits, CancellationToken cancel)
    {
        NextEdit? made = NextEditMerge.FromModel(snapshot.Path, text, snapshot.Version, edits, candidate.Reason);
        lock (gate)
        {
            if (cancel.IsCancellationRequested)
                return;
            failures = 0;
            // The next question waits for nothing more: this one is over
            if (farQueue.Count > 0)
                farAt = time.GetTimestamp();
            if (made is null)
                return;
            PredictionLog.FarFound();
            farReady.Add(new FarAnswer(snapshot.Document, snapshot.Version, text, made));
            while (farReady.Count > FAR_KEPT)
                farReady.RemoveAt(0);
            Volatile.Write(ref farWaiting, farReady.Count);
        }
    }

    // Cancels the request in flight, which closes its connection
    private void Stop(bool counted)
    {
        CancellationTokenSource? cancel = flight;
        if (cancel is null)
            return;
        flight = null;
        if (counted && !running.IsCompleted)
            Interlocked.Increment(ref cancelled);
        // Mark the request cancelled now, but run provider callbacks off the editor's thread.
        _ = cancel.CancelAsync();
    }

    private CancellationTokenSource? preload;
    private PredictionOptions? warmedFor;

    public void ShutDown()
    {
        lock (gate)
        {
            Stop(counted: false);
            ClearFar(answers: true);
            warmedFor = null;
            pending = false;
            document = null;
            Volatile.Write(ref ready, null);
        }
        _ = Interlocked.Exchange(ref preload, null)?.CancelAsync();
    }

    private IPredictionProvider? Provider(PredictionOptions options)
    {
        if (provider is null || providerFor != options || provider is LinkProvider { IsCurrent: false })
        {
            provider = PredictionProviders.Make(options);
            providerFor = options;
        }
        return provider;
    }

    // Off the editor's thread: the prompt, the cache, the request and its answer
    private async Task Run(PredictionSnapshot snapshot, PredictionOptions options, IPredictionProvider asked, CancellationToken cancel)
    {
        long started = time.GetTimestamp();
        PredictionStrategy strategy = PredictionStrategy.For(options);
        IPredictionFormat format = strategy.Near;
        try
        {
            string text = string.Join('\n', snapshot.Lines);
            int caret = Offset(snapshot.Lines, snapshot.CaretLine, snapshot.CaretColumn);

            // Every edit is already noted soon by the glue; this settles those still waiting, then the text as asked for
            History.Flush();
            History.Note(snapshot.Path, text, snapshot.CaretLine);
            var input = new PredictionInput(snapshot.Path, snapshot.Language, text, caret,
                History.Events(snapshot.Path), snapshot.Related?.Invoke() ?? [], snapshot.Notebook,
                History.Renames.Hint(snapshot.Path, text));

            PredictionCache? cache = asked is LinkProvider link ? link.Cache : Cache;
            string? key = cache is null ? null : PredictionCache.Key(text, caret, options.Model, format.Name);
            if (key is not null && snapshot.Notebook is { } notebook)
                key = string.Join('\0', key, snapshot.Path, snapshot.Language, notebook.Before, notebook.After);
            if (cache is not null && key is not null && cache.TryGet(key, caret, out IReadOnlyList<PredictionEdit> cached, out int? cachedCaret))
            {
                var timing = new PredictionTiming(0, Elapsed(started), 0, 0);
                Finish(snapshot, text, caret, cached, cachedCaret, true, timing, format, cancel);
                ScheduleFar(snapshot, text, caret, strategy, options, cancel);
                return;
            }

            if (format.Build(input, options.MaxTokens) is not { } prompt)
            {
                Finish(snapshot, text, caret, [], null, false, default, format, cancel);
                return;
            }

            Interlocked.Increment(ref requests);
            var output = new StringBuilder();
            double first = 0;
            int pieces = 0;
            PredictionParsed? parsed = null;

            // Leaving the loop early disposes the provider's stream, which closes the connection
            await foreach (string piece in asked.Complete(prompt.Request, cancel).ConfigureAwait(false))
            {
                if (pieces++ == 0)
                    first = Elapsed(started);
                output.Append(piece);
                parsed = format.Parse(prompt, output.ToString(), false);
                if (parsed.Done)
                    break;
            }

            cancel.ThrowIfCancellationRequested();
            if (parsed is not { Done: true })
                parsed = format.Parse(prompt, output.ToString(), true);

            var took = new PredictionTiming(first, Elapsed(started), PredictionTokens.Of(prompt.Request.Prompt), pieces);
            if (key is not null)
                cache?.Put(key, caret, parsed.Edits, parsed.CaretAfter);
            Finish(snapshot, text, caret, parsed.Edits, parsed.CaretAfter, false, took, format, cancel, parsed.Note ?? "");
            ScheduleFar(snapshot, text, caret, strategy, options, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // A newer edit, a dismissal or a closed document: nothing to say
        }
        catch (PredictionException exception)
        {
            Fail(exception.Status, exception.Message, started, format, cancel);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fail(PredictionStatus.BadAnswer, "The prediction failed: " + exception.GetType().Name + ".", started, format, cancel);
        }
    }

    // A prediction made: put where Take finds it, unless it was cancelled or changes nothing
    private void Finish(PredictionSnapshot snapshot, string text, int caret, IReadOnlyList<PredictionEdit> edits, int? caretAfter,
        bool fromCache, PredictionTiming timing, IPredictionFormat format, CancellationToken cancel, string note = "")
    {
        PredictionLog.Request(PredictionStatus.Ok, timing, fromCache, edits.Count, format.Name);
        lock (gate)
        {
            if (cancel.IsCancellationRequested)
                return;
            failures = 0;
            LastTiming = timing;
            SetStatus(PredictionStatus.Ok, note);
            if (edits.Count == 0)
                return;
            bool ghost = PredictionDiff.IsGhost(edits, text, caret);
            Volatile.Write(ref ready, new PredictionResult(snapshot.Document, snapshot.Version, edits, caretAfter, ghost, fromCache, timing));
        }
    }

    // A request that failed: its status shown, and after three in a row only one try every half minute
    private void Fail(PredictionStatus status, string message, long started, IPredictionFormat format, CancellationToken cancel)
    {
        PredictionLog.Request(status, new PredictionTiming(0, Elapsed(started), 0, 0), false, 0, format.Name);
        lock (gate)
        {
            if (cancel.IsCancellationRequested)
                return;
            failures++;
            if (failures >= FAILURES_TO_WAIT)
                retryAt = time.GetTimestamp() + (long)(retry.TotalSeconds * time.TimestampFrequency);
            SetStatus(status, message);
        }
    }

    private void SetStatus(PredictionStatus status, string text)
    {
        Status = status;
        StatusText = text;
    }

    private double Elapsed(long started) => time.GetElapsedTime(started).TotalMilliseconds;

    // The caret's offset into the lines joined with '\n'
    private static int Offset(string[] lines, int line, int column)
    {
        line = Math.Clamp(line, 0, Math.Max(0, lines.Length - 1));
        int offset = 0;
        for (int i = 0; i < line; i++)
            offset += lines[i].Length + 1;
        return lines.Length == 0 ? 0 : offset + Math.Clamp(column, 0, lines[line].Length);
    }
}
