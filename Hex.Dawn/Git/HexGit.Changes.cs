using Hex.Core.Git;

namespace Hex.Dawn;

// The lines of every open file that changed since git's copy of it (the index, what the next commit takes, which is the
// last commit until something's staged), a bar beside each run of them in the gutter. git's copy is read once per look
// at the repository and the diff is worked out a moment after the typing stops, both off the editor's thread, so a big
// file being typed into fast never waits on either.
internal sealed partial class HexGit
{
    // How long after the last keystroke the lines are measured again
    private const float DIFF_AFTER = 0.25f;

    private readonly Dictionary<HexDocument, Tracked> tracked = [];

    /// <summary>What's known of an open file, git's copy of it and the runs of lines that differ from it.</summary>
    private sealed class Tracked
    {
        public string? Staged;
        public int StagedGeneration = -1;
        public bool Fetching, Diffing;
        public float DiffIn = 0.01f;
        public IReadOnlyList<GitHunk> Hunks = [];
    }

    /// <summary>The runs of lines of an open file that differ from git's copy, in order, none for a file git doesn't have.</summary>
    public IReadOnlyList<GitHunk> HunksOf(HexDocument document) => tracked.TryGetValue(document, out Tracked? known) ? known.Hunks : [];

    /// <summary>git's copy of an open file as last read, null when it has none (new, or not in a repository).</summary>
    public string? StagedOf(HexDocument document) => tracked.TryGetValue(document, out Tracked? known) ? known.Staged : null;

    private void FollowDocuments()
    {
        context.Documents.Edited += edited =>
        {
            if (tracked.TryGetValue(edited, out Tracked? known))
                known.DiffIn = DIFF_AFTER;
        };
        context.Documents.Closed += closed => tracked.Remove(closed);
    }

    /// <summary>Every tick, git's copy read again for the files whose repository was looked at, and their lines measured once typing stops.</summary>
    private void UpdateChanges(float dt)
    {
        // By index and with the work handed off to methods of their own, the closures they need are made only when
        // there's something to ask git and not for every open file on every tick
        IReadOnlyList<HexDocument> all = context.Documents.All;
        for (int i = 0; i < all.Count; i++)
        {
            HexDocument document = all[i];
            if (document.Path is not { } path || document.Picture is not null || RepositoryOf(path) is not { } repository)
                continue;

            if (!tracked.TryGetValue(document, out Tracked? known))
            {
                tracked[document] = known = new Tracked();
                FollowGutter(document);
            }

            // A new look at the repository (a save, a commit, a stage) has git's copy read again
            if (known.StagedGeneration != repository.Generation && !known.Fetching && repository.Status is not null)
                ReadStaged(known, repository, path);

            if (known.DiffIn <= 0.0f || known.Diffing || known.StagedGeneration < 0 || (known.DiffIn -= dt) > 0.0f)
                continue;

            Diff(known, document);
        }
    }

    // Helper method to read git's copy of a file in the background
    private void ReadStaged(Tracked known, Repository repository, string path)
    {
        known.Fetching = true;
        int generation = repository.Generation;
        bool fresh = repository.Status!.StateOf(path).Change == GitChange.Untracked;
        context.Work.InBackground("git show", () => fresh ? null : repository.Git.Staged(path), staged =>
        {
            known.Fetching = false;
            known.StagedGeneration = generation;
            known.Staged = staged;
            known.DiffIn = 0.01f;
        }, quiet: true);
    }

    // Helper method to measure the lines of a file against git's copy in the background
    private void Diff(Tracked known, HexDocument document)
    {
        known.DiffIn = 0.0f;
        known.Diffing = true;
        string? before = known.Staged;
        string after = document.Editor.Text;
        context.Work.InBackground("git diff", () => before is null ? [] : GitLineDiff.Diff(GitLineDiff.Lines(before), GitLineDiff.Lines(after)), hunks =>
        {
            known.Diffing = false;
            known.Hunks = hunks;
            document.Editor.LineChanges = [.. hunks.SelectMany(LineChangesOf)];
            context.Redraw();
        }, quiet: true);
    }

    // A hunk as the gutter draws it, lines taken out marked where they were in the new text. One line changed into three is
    // a changed line and two added ones, three into one a changed line and a wedge for the two that went
    private static IEnumerable<UICodeLineChange> LineChangesOf(GitHunk hunk)
    {
        if (hunk.IsDeleted)
        {
            yield return new UICodeLineChange(hunk.NewStart, 0, UILineChangeKind.Deleted);
            yield break;
        }

        if (hunk.IsAdded)
        {
            yield return new UICodeLineChange(hunk.NewStart, hunk.NewCount, UILineChangeKind.Added);
            yield break;
        }

        int changed = Math.Min(hunk.OldCount, hunk.NewCount);
        yield return new UICodeLineChange(hunk.NewStart, changed, UILineChangeKind.Modified);
        if (hunk.NewCount > changed)
            yield return new UICodeLineChange(hunk.NewStart + changed, hunk.NewCount - changed, UILineChangeKind.Added);
        else if (hunk.OldCount > changed)
            yield return new UICodeLineChange(hunk.NewStart + changed, 0, UILineChangeKind.Deleted);
    }
}
