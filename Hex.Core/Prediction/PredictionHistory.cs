using System.Text;
using Hex.Core.Git;

namespace Hex.Core;

/// <summary>
/// The last edits made, as unified diffs for the model to see where the typing is going. Each file's text is kept as it
/// was when its last event was closed; noting the text now diffs it against that into an event, which runs on into the
/// last one when it is the same file and near it. An event is every hunk of one change (a rename on lines 10 and 80 is
/// two hunks of one event), trimmed to 1500 bytes by dropping the hunks furthest from the caret and then the edges of
/// what's left, never dropped whole. At most twelve events are kept, a prediction the user took is an event of its own
/// (marked as accepted, the others being typed), and a file's events go when it closes. Safe to use from any thread.
/// </summary>
internal sealed class PredictionHistory
{
    public const int MOST_EVENTS = 12;
    public const int MOST_BYTES = 1500;

    // Lines of context round a change, as git writes them
    private const int CONTEXT = 3;

    // Changes this many lines apart or nearer in the same file are one event
    private const int NEAR = 8;

    private readonly Lock gate = new();

    /// <summary>The renames seen in the events as they close, followed until the old name is typed back or they are done with.</summary>
    public RenameTracker Renames { get; } = new();

    // Each file's text when its last event was closed
    private readonly Dictionary<string, Kept> texts = new(StringComparer.Ordinal);

    // The newest version noted soon for each file, so an older one arriving late is ignored
    private readonly Dictionary<string, int> versions = new(StringComparer.Ordinal);

    // The events oldest first
    private readonly List<Change> changes = [];

    // Texts waiting to be noted, oldest first, one per file
    private readonly List<Waiting> waiting = [];
    private bool draining;
    private Task drained = Task.CompletedTask;

    // The last event: its file's text before it, so a change running on into it can be diffed again from there
    private Kept? lastBefore;

    // The file whose text is kept as lines, the one last noted
    private string? hotPath;

    /// <summary>The background noting started by <see cref="NoteSoon"/>, finished when nothing is waiting; for the tests.</summary>
    internal Task Drained
    {
        get
        {
            lock (gate)
                return drained;
        }
    }

    /// <summary>
    /// Notes a file's lines as they are after an edit, off the caller's thread: every edit is recorded, whether or not a
    /// prediction is asked for. Edits that come before the noting runs are folded into the latest text of their file.
    /// </summary>
    public void NoteSoon(string path, int version, string[] lines, int caretLine = -1)
    {
        lock (gate)
        {
            if (versions.TryGetValue(path, out int seen) && version < seen)
                return;
            versions[path] = version;

            waiting.RemoveAll(entry => entry.Path == path);
            waiting.Add(new Waiting(path, lines, caretLine));
            if (!draining)
            {
                draining = true;
                drained = Task.Run(Drain);
            }
        }
    }

    /// <summary>
    /// Sets a file's text as its starting point when nothing has been noted for it yet, so that its first edit is a change
    /// and not the start (a rename typed over a selection, add to plus, would otherwise begin at its first letter). False
    /// when the file is already known.
    /// </summary>
    public bool Seed(string path, string[] lines)
    {
        lock (gate)
        {
            if (texts.ContainsKey(path) || waiting.Exists(entry => entry.Path == path))
                return false;
            Keep(path, Normal(lines));
            return true;
        }
    }

    /// <summary>Notes whatever <see cref="NoteSoon"/> is still holding, now, on this thread.</summary>
    public void Flush()
    {
        lock (gate)
            NoteWaiting();
    }

    private void Drain()
    {
        lock (gate)
        {
            NoteWaiting();
            draining = false;
        }
    }

    private void NoteWaiting()
    {
        while (waiting.Count > 0)
        {
            Waiting next = waiting[0];
            waiting.RemoveAt(0);
            Note(next.Path, Normal(next.Lines), next.CaretLine);
        }
    }

    // The lines as the diff takes them: without the empty one after a last newline
    private static string[] Normal(string[] lines) => lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;

    /// <summary>
    /// Notes a file's text as it is now: what changed since it was last noted becomes an event, or runs on into the last
    /// event when that was the same file and the change is near it. The first time a file is noted is its start.
    /// </summary>
    public void Note(string path, string text, int caretLine = -1) => Note(path, GitLineDiff.Lines(text), caretLine);

    // The same from the text's lines, as the diff takes them, which the history keeps (the caller doesn't change them after)
    private void Note(string path, string[] lines, int caretLine)
    {
        lock (gate)
        {
            if (!texts.TryGetValue(path, out Kept? beforeKept))
            {
                Keep(path, lines);
                return;
            }
            string[] before = beforeKept.Lines;
            List<Hunk> hunks = Diffed(before, lines);
            if (hunks.Count == 0)
                return;
            Keep(path, lines);

            // Typing on in the same place runs on into the last event, diffed again from before it; the hunks' context
            // isn't counted in how near they are
            bool observed = false;
            if (changes.Count > 0 && changes[^1] is { Predicted: false } last && last.Path == path && lastBefore is not null
                && hunks[0].OldStart <= last.NewEnd + NEAR - 2 * CONTEXT && hunks[^1].OldEnd + NEAR >= last.NewStart + 2 * CONTEXT)
            {
                // The rename tracker sees the whole event, so a name typed in a letter at a time is one swap, and one typed back none
                List<Hunk> merged = Diffed(lastBefore.Lines, lines);
                Renames.ObserveHunks(path, lines, merged);
                observed = true;
                if (merged.Count > 0)
                {
                    changes.RemoveAt(changes.Count - 1);
                    Add(path, lastBefore, merged, false, caretLine);
                    return;
                }
            }

            if (!observed)
                Renames.ObserveHunks(path, lines, hunks);
            Add(path, beforeKept, hunks, false, caretLine);
        }
    }

    // A file's text as its last event closed: the file being edited keeps its lines as they are, one at a time, the others
    // (and the text before the last event, once another file's is being noted) are packed
    private Kept Keep(string path, string[] lines)
    {
        if (hotPath is not null && hotPath != path && texts.TryGetValue(hotPath, out Kept? cooled))
            cooled.Pack();
        if (changes.Count > 0 && changes[^1].Path != path)
            lastBefore?.Pack();
        hotPath = path;
        return texts[path] = Kept.Of(lines);
    }

    /// <summary>
    /// A prediction the user took: what was typed before it noted first, then the prediction's own change as an event
    /// marked as one.
    /// </summary>
    public void Accepted(string path, string before, string after)
    {
        lock (gate)
        {
            NoteWaiting();
            Note(path, before);
            string[] afterLines = GitLineDiff.Lines(after);
            string[] beforeLines = texts.TryGetValue(path, out Kept? known) ? known.Lines : GitLineDiff.Lines(before);
            Keep(path, afterLines);
            List<Hunk> hunks = Diffed(beforeLines, afterLines);
            Renames.ObserveHunks(path, after, hunks);
            if (hunks.Count > 0)
                Add(path, null, hunks, true, -1);
        }
    }

    /// <summary>Forgets a file that closed: its text, its events and whatever was waiting to be noted for it.</summary>
    public void Closed(string path)
    {
        lock (gate)
        {
            texts.Remove(path);
            if (hotPath == path)
                hotPath = null;
            versions.Remove(path);
            Renames.Forget(path);
            waiting.RemoveAll(entry => entry.Path == path);
            if (changes.Count > 0 && changes[^1].Path == path)
                lastBefore = null;
            changes.RemoveAll(change => change.Path == path);
        }
    }

    /// <summary>
    /// The events for a prediction in a file, oldest first: other files' before the file's own, so that the file's own
    /// are nearest the caret in the prompt and the last a budget drops. Each file's keep the order they happened in.
    /// </summary>
    public IReadOnlyList<PredictionEvent> Events(string path)
    {
        lock (gate)
        {
            var events = new List<PredictionEvent>(changes.Count);
            foreach (Change change in changes)
            {
                if (change.Path != path)
                    events.Add(change.Event);
            }
            foreach (Change change in changes)
            {
                if (change.Path == path)
                    events.Add(change.Event);
            }
            return events;
        }
    }

    /// <summary>
    /// A short hash of the newest of some events (and how many there are), for a cache key: the same region asked about
    /// with other history behind it must not find an answer made without that history. Empty for none.
    /// </summary>
    public static string Hash(IReadOnlyList<PredictionEvent> events)
    {
        if (events.Count == 0)
            return string.Empty;
        PredictionEvent newest = events[^1];
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(newest.Path + "\0" + newest.Predicted + "\0" + newest.Diff));
        return events.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(hash, 0, 8);
    }

    // Keeps an event, the oldest going past twelve, and remembers the text before it for the next to run on into
    private void Add(string path, Kept? before, List<Hunk> hunks, bool predicted, int caretLine)
    {
        lastBefore = predicted ? null : before;
        List<Hunk> kept = Trimmed(hunks, caretLine);
        if (kept.Count == 0)
            return;

        var diff = new StringBuilder();
        foreach (Hunk hunk in kept)
            diff.Append(hunk.Diff);
        changes.Add(new Change(path, kept[0].NewStart, kept[^1].NewEnd, new PredictionEvent(path, diff.ToString(), predicted)));
        if (changes.Count > MOST_EVENTS)
            changes.RemoveAt(0);
    }

    // The hunks that fit the byte budget: all of them, or the ones nearest the caret (the last change when it isn't
    // known), the first of them cut down to fit if it alone is over
    private static List<Hunk> Trimmed(List<Hunk> hunks, int caretLine)
    {
        int total = 0;
        foreach (Hunk hunk in hunks)
            total += Encoding.UTF8.GetByteCount(hunk.Diff);
        if (total <= MOST_BYTES)
            return hunks;

        int at = caretLine >= 0 ? caretLine : hunks[^1].NewStart;
        List<Hunk> byDistance = [.. hunks.OrderBy(hunk => Distance(hunk, at))];
        var kept = new List<Hunk>();
        int left = MOST_BYTES;
        foreach (Hunk hunk in byDistance)
        {
            int bytes = Encoding.UTF8.GetByteCount(hunk.Diff);
            if (bytes <= left)
            {
                kept.Add(hunk);
                left -= bytes;
            }
            else if (kept.Count == 0)
            {
                kept.Add(Fitted(hunk, left));
                break;
            }
        }
        kept.Sort((a, b) => a.NewStart.CompareTo(b.NewStart));
        return kept;
    }

    private static int Distance(Hunk hunk, int line) =>
        line < hunk.NewStart ? hunk.NewStart - line : line >= hunk.NewEnd ? line - hunk.NewEnd + 1 : 0;

    // A hunk cut down to a budget: the context at its ends goes first, then the lines at the end, then the end of a line
    private static Hunk Fitted(Hunk hunk, int budget)
    {
        List<string> rows = [.. hunk.Rows];
        int oldStart = hunk.OldStart, newStart = hunk.NewStart;
        int bytes = Size(rows);

        while (bytes > budget && rows.Count > 1 && rows[0][0] == ' ')
        {
            bytes -= Encoding.UTF8.GetByteCount(rows[0]) + 1;
            rows.RemoveAt(0);
            oldStart++;
            newStart++;
        }
        while (bytes > budget && rows.Count > 1 && rows[^1][0] == ' ')
        {
            bytes -= Encoding.UTF8.GetByteCount(rows[^1]) + 1;
            rows.RemoveAt(rows.Count - 1);
        }
        if (bytes > budget)
        {
            // Long lines are cut short before whole lines go, so the change itself stays
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Length > MOST_ROW)
                    rows[i] = rows[i][..MOST_ROW];
            }
            bytes = Size(rows);
        }
        while (bytes > budget && rows.Count > 1)
        {
            bytes -= Encoding.UTF8.GetByteCount(rows[^1]) + 1;
            rows.RemoveAt(rows.Count - 1);
        }
        if (bytes > budget)
        {
            // One line too long to fit alone
            int room = Math.Max(1, budget - HEADER_BYTES - 2);
            string row = rows[0];
            while (row.Length > 1 && Encoding.UTF8.GetByteCount(row) > room)
                row = row[..^1];
            rows[0] = row;
        }
        return Render(rows, oldStart, newStart);
    }

    // What the header of a hunk comes to at most, with room for large line numbers
    private const int HEADER_BYTES = 40;

    // The longest a line is kept when a hunk is cut down
    private const int MOST_ROW = 300;

    private static int Size(List<string> rows)
    {
        int bytes = HEADER_BYTES;
        foreach (string row in rows)
            bytes += Encoding.UTF8.GetByteCount(row) + 1;
        return bytes;
    }

    // A hunk written out from its rows (each prefixed by ' ', '-' or '+') and the lines it starts at, from 0
    private static Hunk Render(List<string> rows, int oldStart, int newStart)
    {
        int oldCount = 0, newCount = 0;
        foreach (string row in rows)
        {
            if (row[0] != '+')
                oldCount++;
            if (row[0] != '-')
                newCount++;
        }

        var diff = new StringBuilder();
        diff.Append("@@ -").Append(oldStart + 1).Append(',').Append(oldCount)
            .Append(" +").Append(newStart + 1).Append(',').Append(newCount).Append(" @@\n");
        foreach (string row in rows)
            diff.Append(row).Append('\n');
        return new Hunk(diff.ToString(), oldStart, oldStart + oldCount, newStart, newStart + newCount, [.. rows]);
    }

    /// <summary>
    /// The unified-diff hunks from one text to another, three lines of context each side, changes closer than the context
    /// of both of them being one hunk, none when they're the same.
    /// </summary>
    internal static List<Hunk> Diffed(string before, string after) => Diffed(GitLineDiff.Lines(before), GitLineDiff.Lines(after));

    /// <summary>The same from the lines of the two texts, as <see cref="GitLineDiff.Lines"/> makes them.</summary>
    internal static List<Hunk> Diffed(string[] old, string[] updated)
    {
        List<GitHunk> changed = GitLineDiff.Diff(old, updated);
        var hunks = new List<Hunk>();

        int i = 0;
        while (i < changed.Count)
        {
            int j = i;
            while (j + 1 < changed.Count && changed[j + 1].OldStart - (changed[j].OldStart + changed[j].OldCount) <= 2 * CONTEXT)
                j++;

            GitHunk first = changed[i], last = changed[j];
            int oldStart = Math.Max(0, first.OldStart - CONTEXT);
            int oldEnd = Math.Min(old.Length, last.OldStart + last.OldCount + CONTEXT);
            int newStart = oldStart + (first.NewStart - first.OldStart);

            var rows = new List<string>();
            int oldAt = oldStart, newAt = newStart;
            for (int k = i; k <= j; k++)
            {
                GitHunk hunk = changed[k];
                while (oldAt < hunk.OldStart)
                {
                    rows.Add(" " + old[oldAt++]);
                    newAt++;
                }
                for (int n = 0; n < hunk.OldCount; n++)
                    rows.Add("-" + old[oldAt++]);
                for (int n = 0; n < hunk.NewCount; n++)
                    rows.Add("+" + updated[newAt++]);
            }
            while (oldAt < oldEnd)
                rows.Add(" " + old[oldAt++]);

            hunks.Add(Render(rows, oldStart, newStart));
            i = j + 1;
        }
        return hunks;
    }

    /// <summary>A hunk as written, the lines it covers, from 0, in the old text and the new, and its rows.</summary>
    internal readonly record struct Hunk(string Diff, int OldStart, int OldEnd, int NewStart, int NewEnd, string[] Rows);

    private sealed record Waiting(string Path, string[] Lines, int CaretLine);

    /// <summary>
    /// A file's text as it was kept: as it is when short, Brotli-packed when long (a file of a megabyte keeps a tenth of
    /// that), the history being a few files' worth for as long as they're open.
    /// </summary>
    private sealed class Kept
    {
        private const int PACK_FROM = 16_384;

        private string[]? lines;
        private byte[]? packed;

        private Kept(string[] lines) => this.lines = lines;

        public static Kept Of(string[] lines) => new(lines);

        /// <summary>The text's lines: those kept, or those the packed text unpacks to (made again each time).</summary>
        public string[] Lines
        {
            get
            {
                if (lines is not null)
                    return lines;

                using var from = new MemoryStream(packed!);
                using var brotli = new System.IO.Compression.BrotliStream(from, System.IO.Compression.CompressionMode.Decompress);
                using var bytes = new MemoryStream(packed!.Length * 4);
                brotli.CopyTo(bytes);
                return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length).Split('\n');
            }
        }

        /// <summary>Keeps a long text as Brotli and lets its lines go; a short one stays as it is.</summary>
        public void Pack()
        {
            if (lines is null)
                return;

            long chars = 0;
            foreach (string line in lines)
                chars += line.Length + 1;
            if (chars < PACK_FROM)
                return;

            int size = lines.Length - 1;
            foreach (string line in lines)
                size += Encoding.UTF8.GetByteCount(line);
            byte[] bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(size);
            try
            {
                int at = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0)
                        bytes[at++] = (byte)'\n';
                    at += Encoding.UTF8.GetBytes(lines[i], bytes.AsSpan(at));
                }

                using var to = new MemoryStream(size / 4 + 16);
                using (var brotli = new System.IO.Compression.BrotliStream(to, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                    brotli.Write(bytes.AsSpan(0, size));
                packed = to.ToArray();
                lines = null;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
            }
        }
    }

    // An event kept, with the lines it covers to tell whether the next change is near it
    private sealed record Change(string Path, int NewStart, int NewEnd, PredictionEvent Event)
    {
        public bool Predicted => Event.Predicted;
    }
}
