namespace Hex.Core;

/// <summary>
/// The last 64 predictions by what was before and after the caret (2000 characters before, 500 after) and the model and
/// format asked, so that backspacing and typing it again, or undoing, shows the old answer at once with no request. The
/// edits are kept relative to the caret, so the same text further down a file finds them too. Safe from any thread.
/// </summary>
internal sealed class PredictionCache
{
    public const int SIZE = 64;
    private const int BEFORE = 2000;
    private const int AFTER = 500;

    private readonly Lock gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Entry Value)>> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, Entry Value)> order = new();

    /// <summary>How many it holds.</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    /// <summary>The key for a caret in a text asked of a model in a format.</summary>
    public static string Key(string text, int caret, string model, string format)
    {
        int start = Math.Max(0, caret - BEFORE), end = Math.Min(text.Length, caret + AFTER);
        return string.Concat(model + "\0" + format + "\0", text.AsSpan(start, end - start), "\0" + (caret - start).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The key for a far prediction: the region's text (not where it is) with the hash of the newest history event, so a
    /// region that is the same but has other history behind it doesn't find the answer (docs/next-edit-plan.md, 4.4).
    /// Used with the region's start as the caret, so that the edits are kept relative to it.
    /// </summary>
    public static string FarKey(string regionText, string model, string format, string historyHash) =>
        string.Concat("far\0", model, "\0", format, "\0", historyHash, "\0", regionText);

    // What is kept in place of a key's text: 128 bits of its SHA-256, so an entry holds 32 characters, not the 2500 of the text round the caret
    private static string Digest(string key) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(key.AsSpan())), 0, 16);

    /// <summary>The prediction kept for a key, its edits moved to the caret it's asked for at.</summary>
    public bool TryGet(string key, int caret, out IReadOnlyList<PredictionEdit> edits, out int? caretAfter)
    {
        key = Digest(key);
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var node))
            {
                edits = [];
                caretAfter = null;
                return false;
            }

            order.Remove(node);
            order.AddFirst(node);
            Entry entry = node.Value.Value;
            var moved = new PredictionEdit[entry.Edits.Length];
            for (int i = 0; i < moved.Length; i++)
                moved[i] = entry.Edits[i] with { Start = entry.Edits[i].Start + caret, End = entry.Edits[i].End + caret };
            edits = moved;
            caretAfter = entry.CaretAfter is { } after ? after + caret : null;
            return true;
        }
    }

    /// <summary>
    /// Keeps a prediction made at a caret. One whose edits reach past what the key covers isn't kept: the text there
    /// could be different the next time the key is.
    /// </summary>
    public void Put(string key, int caret, IReadOnlyList<PredictionEdit> edits, int? caretAfter)
    {
        key = Digest(key);
        var relative = new PredictionEdit[edits.Count];
        for (int i = 0; i < relative.Length; i++)
        {
            PredictionEdit edit = edits[i];
            if (edit.Start < caret - BEFORE || edit.End > caret + AFTER)
                return;
            relative[i] = edit with { Start = edit.Start - caret, End = edit.End - caret };
        }

        lock (gate)
        {
            if (entries.TryGetValue(key, out var old))
            {
                order.Remove(old);
                entries.Remove(key);
            }

            entries[key] = order.AddFirst((key, new Entry(relative, caretAfter - caret)));
            while (entries.Count > SIZE)
            {
                var last = order.Last!;
                order.RemoveLast();
                entries.Remove(last.Value.Key);
            }
        }
    }

    // Edits and caret relative to the caret they were made at
    private sealed record Entry(PredictionEdit[] Edits, int? CaretAfter);
}
