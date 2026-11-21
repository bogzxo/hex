using System.Text;
using Hex.Core.Git;

namespace Hex.Core;

/// <summary>
/// The last edits made, as unified diffs for the model to see where the typing is going. Each file's text is kept as it
/// was when its last event was closed; noting the text now diffs it against that into an event, which runs on into the
/// last one when it is the same file and near it. At most six events of at most 1500 bytes are kept, a prediction the
/// user took is an event of its own, and a file's events go when it closes. Safe to use from any thread.
/// </summary>
internal sealed class PredictionHistory
{
    public const int MOST_EVENTS = 6;
    public const int MOST_BYTES = 1500;

    // Lines of context round a change, as git writes them
    private const int CONTEXT = 3;

    // Changes this many lines apart or nearer in the same file are one event
    private const int NEAR = 8;

    private readonly Lock gate = new();

    // Each file's text when its last event was closed
    private readonly Dictionary<string, string> texts = new(StringComparer.Ordinal);

    // The events oldest first
    private readonly List<Change> changes = [];

    // The last event: its file's text before it, so a change running on into it can be diffed again from there
    private string? lastBefore;

    /// <summary>
    /// Notes a file's text as it is now: what changed since it was last noted becomes an event, or runs on into the last
    /// event when that was the same file and the change is near it. The first time a file is noted is its start.
    /// </summary>
    public void Note(string path, string text)
    {
        lock (gate)
        {
            if (!texts.TryGetValue(path, out string? before))
            {
                texts[path] = text;
                return;
            }
            if (before == text)
                return;
            texts[path] = text;

            if (Diffed(before, text) is not { } hunk)
                return;

            // Typing on in the same place runs on into the last event, diffed again from before it; the hunks' context
            // isn't counted in how near they are
            if (changes.Count > 0 && changes[^1] is { Predicted: false } last && last.Path == path && lastBefore is not null
                && hunk.OldStart <= last.NewEnd + NEAR - 2 * CONTEXT && hunk.OldEnd + NEAR >= last.NewStart + 2 * CONTEXT
                && Diffed(lastBefore, text) is { } merged)
            {
                changes.RemoveAt(changes.Count - 1);
                Add(path, lastBefore, merged, false);
                return;
            }

            Add(path, before, hunk, false);
        }
    }

    /// <summary>
    /// A prediction the user took: what was typed before it noted first, then the prediction's own change as an event
    /// marked as one.
    /// </summary>
    public void Accepted(string path, string before, string after)
    {
        lock (gate)
        {
            Note(path, before);
            texts[path] = after;
            if (Diffed(before, after) is { } hunk)
                Add(path, before, hunk, true);
        }
    }

    /// <summary>Forgets a file that closed: its text and its events.</summary>
    public void Closed(string path)
    {
        lock (gate)
        {
            texts.Remove(path);
            if (changes.Count > 0 && changes[^1].Path == path)
                lastBefore = null;
            changes.RemoveAll(change => change.Path == path);
        }
    }

    /// <summary>
    /// The events for a prediction in a file, oldest first: other files' before the file's own, so that the file's own
    /// are nearest the caret in the prompt and the last a budget drops.
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

    // Keeps an event, the oldest going past six, and remembers the text before it for the next to run on into
    private void Add(string path, string before, Hunk hunk, bool predicted)
    {
        lastBefore = predicted ? null : before;
        if (Encoding.UTF8.GetByteCount(hunk.Diff) > MOST_BYTES)
            return;
        changes.Add(new Change(path, hunk.NewStart, hunk.NewEnd, new PredictionEvent(path, hunk.Diff, predicted)));
        if (changes.Count > MOST_EVENTS)
            changes.RemoveAt(0);
    }

    /// <summary>
    /// One unified-diff hunk from one text to another, from the first line that changed to the last with three lines of
    /// context each side, null when they're the same.
    /// </summary>
    internal static Hunk? Diffed(string before, string after)
    {
        string[] old = GitLineDiff.Lines(before), updated = GitLineDiff.Lines(after);
        List<GitHunk> hunks = GitLineDiff.Diff(old, updated);
        if (hunks.Count == 0)
            return null;

        GitHunk first = hunks[0], last = hunks[^1];
        int oldStart = Math.Max(0, first.OldStart - CONTEXT);
        int oldEnd = Math.Min(old.Length, last.OldStart + last.OldCount + CONTEXT);
        int shift = first.NewStart - first.OldStart;
        int newStart = oldStart + shift;
        int newEnd = Math.Min(updated.Length, last.NewStart + last.NewCount + CONTEXT);

        var diff = new StringBuilder();
        diff.Append("@@ -").Append(oldStart + 1).Append(',').Append(oldEnd - oldStart)
            .Append(" +").Append(newStart + 1).Append(',').Append(newEnd - newStart).Append(" @@\n");

        int oldAt = oldStart, newAt = newStart;
        foreach (GitHunk hunk in hunks)
        {
            while (oldAt < hunk.OldStart)
            {
                diff.Append(' ').Append(old[oldAt++]).Append('\n');
                newAt++;
            }
            for (int i = 0; i < hunk.OldCount; i++)
                diff.Append('-').Append(old[oldAt++]).Append('\n');
            for (int i = 0; i < hunk.NewCount; i++)
                diff.Append('+').Append(updated[newAt++]).Append('\n');
        }
        while (oldAt < oldEnd)
            diff.Append(' ').Append(old[oldAt++]).Append('\n');

        return new Hunk(diff.ToString(), oldStart, oldEnd, newStart, newEnd);
    }

    /// <summary>A hunk as written and the lines it covers, from 0, in the old text and the new.</summary>
    internal readonly record struct Hunk(string Diff, int OldStart, int OldEnd, int NewStart, int NewEnd);

    // An event kept, with the lines it covers to tell whether the next change is near it
    private sealed record Change(string Path, int NewStart, int NewEnd, PredictionEvent Event)
    {
        public bool Predicted => Event.Predicted;
    }
}
