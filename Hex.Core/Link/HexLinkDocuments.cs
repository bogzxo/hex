namespace Hex.Core;

internal sealed record HexLinkDocSnapshot(string Path, string Session, long Revision, string Text, string Author, bool Conflict = false);

/// <summary>Only the editor can publish a buffer. Its session survives reconnects and ends when the editor closes it.</summary>
internal sealed class HexLinkDocuments
{
    public HexLinkDocumentRooms Rooms { get; } = new();

    public const int MOST_TEXT = 128 * 1024;
    private const int MOST_DOCUMENTS = 8, HISTORY = 32;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Document> documents = new(StringComparer.Ordinal);

    private sealed class Document(HexLinkDocSnapshot initial)
    {
        public HexLinkDocSnapshot Current = initial;
        public readonly List<Change> History = [];
    }

    private sealed record Change(HexLinkDocSnapshot Before, Splice Applied, long Base, string Proposal, string Author);

    private readonly record struct Splice(int Start, int Removed, string Inserted)
    {
        public int End => Start + Removed;

        public static Splice Between(string before, string after)
        {
            int prefix = 0, suffix = 0, common = Math.Min(before.Length, after.Length);
            while (prefix < common && before[prefix] == after[prefix])
                prefix++;
            while (suffix < common - prefix && before[before.Length - suffix - 1] == after[after.Length - suffix - 1])
                suffix++;
            return new(prefix, before.Length - prefix - suffix, after.Substring(prefix, after.Length - prefix - suffix));
        }

        // Edits touching only at an edge commute. At one insertion point, the host's earlier commit goes first.
        public bool After(Splice accepted, out Splice moved)
        {
            moved = this;
            if (accepted.End <= Start)
            {
                moved = this with { Start = Start + accepted.Inserted.Length - accepted.Removed };
                return true;
            }
            return End <= accepted.Start;
        }

        public string Apply(string basis) => string.Concat(basis.AsSpan(0, Start), Inserted, basis.AsSpan(End));
    }

    public static bool TryMerge(string basis, string local, string authoritative, out string merged)
    {
        merged = local;
        if (local == basis)
        {
            merged = authoritative;
            return true;
        }
        if (!Splice.Between(basis, local).After(Splice.Between(basis, authoritative), out var moved))
            return false;
        merged = moved.Apply(authoritative);
        return true;
    }

    public HexLinkDocSnapshot Share(string path, string text)
    {
        CheckText(text);
        if (path.Length is 0 or > HexLinkFiles.MOST_PATH)
            throw HexLinkException.Invalid("Invalid shared document path.");
        lock (gate)
        {
            if (documents.TryGetValue(path, out var existing))
                return existing.Current;
            if (documents.Count >= MOST_DOCUMENTS)
                throw new HexLinkException(HexLinkErrors.TOO_BIG, "Too many shared documents.");
            var initial = new HexLinkDocSnapshot(path, Guid.NewGuid().ToString("N"), 0, text, string.Empty);
            documents.Add(path, new Document(initial));
            return initial;
        }
    }

    public void Close(string path)
    {
        lock (gate)
            documents.Remove(path);
    }

    private Document Find(string path) => documents.TryGetValue(path, out var doc) ? doc
        : throw new HexLinkException(HexLinkErrors.NOT_FOUND, "This document is not shared.");

    public HexLinkDocSnapshot Snapshot(string path)
    {
        lock (gate)
            return Find(path).Current;
    }

    internal static void CheckText(string text)
    {
        if (text.Length > MOST_TEXT)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, "The shared document exceeds the text limit.");
    }

    public HexLinkDocSnapshot Apply(string path, string session, long revision, string text, HexLinkDevice author)
    {
        CheckText(text);
        lock (gate)
        {
            var doc = Find(path);
            var current = doc.Current;
            if (session != current.Session || revision > current.Revision)
                return current with { Conflict = true };
            var history = doc.History;
            string? basis = revision == current.Revision ? current.Text : history.Find(c => c.Before.Revision == revision)?.Before.Text;
            if (basis is null)
                return current with { Conflict = true };
            if (basis == text || history.Exists(c => c.Base == revision && c.Author == author.Id && c.Proposal == text))
                return current;
            var splice = Splice.Between(basis, text);
            foreach (var change in history)
            {
                if (change.Before.Revision >= revision && !splice.After(change.Applied, out splice))
                    return current with { Conflict = true };
            }

            string merged = splice.Apply(current.Text);
            CheckText(merged);
            if (history.Count == HISTORY)
                history.RemoveAt(0);
            history.Add(new(current, splice, revision, text, author.Id));
            return doc.Current = current with { Revision = current.Revision + 1, Text = merged, Author = author.Id };
        }
    }
}
