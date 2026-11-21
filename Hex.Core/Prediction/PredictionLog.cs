using System.Globalization;

namespace Hex.Core;

/// <summary>
/// HEX_PREDICT_LOG=&lt;file&gt; writes a line there for every prediction: its status, the times, the tokens and whether
/// the cache answered, for benchmarks and bug reports. Never the prompt, the answer or the key.
/// </summary>
internal static class PredictionLog
{
    private static readonly Lock gate = new();

    /// <summary>The file written to, null when the variable isn't set.</summary>
    public static string? File { get; set; } = Environment.GetEnvironmentVariable("HEX_PREDICT_LOG") is { Length: > 0 } file ? file : null;

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
