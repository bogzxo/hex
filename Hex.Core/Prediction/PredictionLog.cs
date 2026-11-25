using System.Globalization;

namespace Hex.Core;

/// <summary>What was counted of predictions and jumps (docs/next-edit-plan.md, 7), shown in the status item's tooltip.</summary>
internal readonly record struct PredictionCounters(long Shown, long JumpsShown, long JumpsAccepted, long JumpsDismissed, long FarAsked, long FarFound)
{
    /// <summary>The counts in one line.</summary>
    public override string ToString() =>
        $"shown {Shown}, jumps shown {JumpsShown}, taken {JumpsAccepted}, dismissed {JumpsDismissed}, far asked {FarAsked}, far found {FarFound}";
}

/// <summary>
/// HEX_PREDICT_LOG=&lt;file&gt; writes a line there for every prediction: its status, the times, the tokens and whether
/// the cache answered, for benchmarks and bug reports. Never the prompt, the answer or the key.
/// </summary>
internal static class PredictionLog
{
    private static readonly Lock gate = new();

    /// <summary>The file written to, null when the variable isn't set.</summary>
    public static string? File { get; set; } = Environment.GetEnvironmentVariable("HEX_PREDICT_LOG") is { Length: > 0 } file ? file : null;

    private static long shown, jumpsShown, jumpsAccepted, jumpsDismissed, farAsked, farFound;

    /// <summary>A prediction near the caret was shown.</summary>
    public static void Shown() => Interlocked.Increment(ref shown);

    /// <summary>A jump (a suggestion away from the caret) was shown.</summary>
    public static void JumpShown() => Interlocked.Increment(ref jumpsShown);

    /// <summary>A jump was taken.</summary>
    public static void JumpAccepted() => Interlocked.Increment(ref jumpsAccepted);

    /// <summary>A jump was let go of.</summary>
    public static void JumpDismissed() => Interlocked.Increment(ref jumpsDismissed);

    /// <summary>A far request was sent to the model.</summary>
    public static void FarAsked() => Interlocked.Increment(ref farAsked);

    /// <summary>A far request found an edit.</summary>
    public static void FarFound() => Interlocked.Increment(ref farFound);

    /// <summary>What was counted since the start, or since <see cref="Reset"/>.</summary>
    public static PredictionCounters Counters => new(Volatile.Read(ref shown), Volatile.Read(ref jumpsShown),
        Volatile.Read(ref jumpsAccepted), Volatile.Read(ref jumpsDismissed), Volatile.Read(ref farAsked), Volatile.Read(ref farFound));

    /// <summary>Sets every counter to 0, for the tests.</summary>
    public static void Reset()
    {
        shown = jumpsShown = jumpsAccepted = jumpsDismissed = farAsked = farFound = 0;
        Interlocked.MemoryBarrier();
    }

    /// <summary>Writes a request's line, when there's a file to write it to.</summary>
    public static void Request(PredictionStatus status, PredictionTiming timing, bool fromCache, int edits, string format)
    {
        if (File is not { } file)
            return;

        string line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} status={status} format={format} first_ms={timing.FirstMs:0} total_ms={timing.TotalMs:0} prompt_tokens={timing.PromptTokens} output_tokens={timing.OutputTokens} cache={(fromCache ? 1 : 0)} edits={edits}\n");
        try
        {
            lock (gate)
                System.IO.File.AppendAllText(file, line);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A log that can't be written isn't worth a prediction
        }
    }
}
