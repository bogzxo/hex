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
        if (flight is not null)
        {
            lock (gate)
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
            return;

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
        pending = true;
        immediate = true;
        dismissed = false;
    }

    /// <summary>A prediction was dismissed: nothing more is asked for until something is typed.</summary>
    public void Dismissed(object document)
    {
        if (!ReferenceEquals(this.document, document))
            return;
        Stop(counted: true);
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
        IPredictionFormat format = PredictionFormats.For(options);
        try
        {
            string text = string.Join('\n', snapshot.Lines);
            int caret = Offset(snapshot.Lines, snapshot.CaretLine, snapshot.CaretColumn);

            History.Note(snapshot.Path, text);
            var input = new PredictionInput(snapshot.Path, snapshot.Language, text, caret,
                History.Events(snapshot.Path), snapshot.Related?.Invoke() ?? [], snapshot.Notebook);

            PredictionCache? cache = asked is LinkProvider link ? link.Cache : Cache;
            string? key = cache is null ? null : PredictionCache.Key(text, caret, options.Model, format.Name);
            if (key is not null && snapshot.Notebook is { } notebook)
                key = string.Join('\0', key, snapshot.Path, snapshot.Language, notebook.Before, notebook.After);
            if (cache is not null && key is not null && cache.TryGet(key, caret, out IReadOnlyList<PredictionEdit> cached, out int? cachedCaret))
            {
                var timing = new PredictionTiming(0, Elapsed(started), 0, 0);
                Finish(snapshot, text, caret, cached, cachedCaret, true, timing, format, cancel);
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
            Finish(snapshot, text, caret, parsed.Edits, parsed.CaretAfter, false, took, format, cancel);
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
        bool fromCache, PredictionTiming timing, IPredictionFormat format, CancellationToken cancel)
    {
        PredictionLog.Request(PredictionStatus.Ok, timing, fromCache, edits.Count, format.Name);
        lock (gate)
        {
            if (cancel.IsCancellationRequested)
                return;
            failures = 0;
            LastTiming = timing;
            SetStatus(PredictionStatus.Ok, "");
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
