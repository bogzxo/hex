using Hex.Core.Git;

namespace Hex.Dawn;

// What a file was. Local history, a copy every time it's saved, git or no git, picked from a list by how long ago and
// compared with the file as it is or put back (one step to undo). And git's history of it, the commits that changed it
// followed through renames, each opened as a diff of what that commit did to it.
internal sealed partial class DawnScene
{
    private const string LOCAL_HISTORY = "local_history";

    private static string LocalHistoryRoot => HexFiles.Kept(LOCAL_HISTORY);

    private void BuildHistory()
    {
        documentSaved += KeepLocalHistory;
        documentEdited += KeepWhatItWas;
        commands.Add("File/path", Said("history.local_command"), ShowLocalHistory, icon: "history");
        commands.Add("Git/history", Said("history.git_command"), ShowFileHistory, icon: "git-commit");
    }

    /// <summary>A copy of a file as it was saved, in the background, the copy given the text as it was then.</summary>
    private void KeepLocalHistory(HexDocument saved)
    {
        if (saved.Path is not { } path || !saved.IsText || saved.Picture is not null || saved.DiffOf is not null)
            return;

        string text = saved.Editor.Text, root = LocalHistoryRoot;
        InBackground("local history", () => { HexLocalHistory.Keep(root, path, text); return true; }, _ => { }, quiet: true);
    }

    // The files whose text as it was on the disk has been seen to, so it's looked at once a file and not every key
    private readonly HashSet<string> keptBefore = new(PlatformPaths.Comparer);

    /// <summary>
    /// The first edit of a file with nothing kept of it yet keeps the text it had on the disk, so the first save has
    /// something before it to go back to, the file as it was before Hex touched it.
    /// </summary>
    private void KeepWhatItWas(HexDocument edited)
    {
        if (edited.Path is not { } path || !edited.IsText || edited.Picture is not null || edited.DiffOf is not null || !keptBefore.Add(path))
            return;

        string text = edited.SavedCode, root = LocalHistoryRoot;
        InBackground("local history", () =>
        {
            if (!HexLocalHistory.Has(root, path))
                HexLocalHistory.Keep(root, path, text, File.Exists(path) ? File.GetLastWriteTime(path) : null);
            return true;
        }, _ => { }, quiet: true);
    }

    /// <summary>The copies kept of the file on screen to pick from, compared or put back.</summary>
    private void ShowLocalHistory()
    {
        if (document.Path is not { } path || document.DiffOf is not null || document.Picture is not null)
            return;

        List<HexKeptVersion> kept = HexLocalHistory.Versions(LocalHistoryRoot, path);
        if (kept.Count == 0)
        {
            Say(Said("history.none_kept", ("name", Path.GetFileName(path))));
            return;
        }

        HexDocument of = document;
        // To the millisecond, two saves in the same second read the same otherwise (a number counted from the oldest moved
        // along as the oldest went)
        var choices = kept.Select(version => (
            $"{HexGit.Ago(version.When)}: {version.When:yyyy-MM-dd HH:mm:ss.fff}   {Math.Max(1, version.Bytes / 1024)} KB",
            (Action)(() => PickedVersion(of, path, version)))).ToList();
        ChooseFrom(Said("history.local_title", ("name", Path.GetFileName(path))), Said("history.local_text"), choices);
    }

    private void PickedVersion(HexDocument of, string path, HexKeptVersion version)
    {
        string compare = Said("history.compare"), restore = Said("history.restore");
        ((IWindows)this).Ask(Said("history.version_title", ("when", HexGit.Ago(version.When))), Said("history.version_text"), [compare, restore, Said("keys.leave_it")], answer =>
        {
            if (!documents.Contains(of) || !File.Exists(version.Copy))
                return;

            string old = File.ReadAllText(version.Copy);
            if (answer == compare)
            {
                Show(of);
                OpenDiff(path, "history", Said("history.diff_title", ("name", Path.GetFileName(path)), ("when", HexGit.Ago(version.When))), GitDiffText.Of(old, of.Editor.Text), of.Editor.Document.Caret.Line);
            }
            else if (answer == restore)
            {
                Show(of);
                Rewrite(of, old);
                Say(Said("history.restored", ("when", HexGit.Ago(version.When))));
            }
        });
    }

    /// <summary>git's history of the file on screen, a commit picked opens what it did to the file.</summary>
    private void ShowFileHistory()
    {
        if (document.Path is not { } path || git.RepositoryFor(path) is not { } repository)
        {
            Say(Said("diff.not_in_git"));
            return;
        }

        InBackground("git log", () => repository.FileLog(path), log =>
        {
            if (log.Count == 0)
            {
                Say(Said("history.no_commits", ("name", Path.GetFileName(path))));
                return;
            }

            // Each commit its hash, subject, author and how long ago, every one a colour of its own the way VS Code's
            // timeline has them, known by the line it used to be written as
            var choices = new List<UIChoice>();
            for (int i = 0; i < log.Count; i++)
            {
                GitCommitOfFile commit = log[i];
                GitCommitOfFile? before = i + 1 < log.Count ? log[i + 1] : null;
                string ago = HexGit.Ago(commit.When);
                choices.Add(new UIChoice(commit.Subject, () => ShowCommitOfFile(repository, path, commit, before))
                {
                    Segments = [(commit.Short, UIChoiceTone.Hash), (commit.Subject, UIChoiceTone.Primary), (commit.Author, UIChoiceTone.Author), (ago, UIChoiceTone.Date)],
                    Id = $"{commit.Short} {commit.Author}, {ago}: {commit.Subject}"
                });
            }

            ChooseFrom(Said("history.git_title", ("name", Path.GetFileName(path))), Said("history.git_text"), choices);
        });
    }

    // What one commit did to a file, its version against the commit's own parent (the one before it in the log is often
    // another branch's once there are merges), by the name the file had before it if the commit renamed it
    private void ShowCommitOfFile(GitRepository repository, string path, GitCommitOfFile commit, GitCommitOfFile? before)
    {
        InBackground("git show", () =>
        {
            string after = repository.AtCommitPath(commit.Path, commit.Hash) ?? string.Empty;
            string old = repository.AtCommitPath(commit.Path, commit.Hash + "^")
                ?? (before is null ? null : repository.AtCommitPath(before.Path, commit.Hash + "^"))
                ?? string.Empty;
            return GitDiffText.Of(old, after);
        }, diff => OpenDiff(path, "commit " + commit.Hash, Said("history.commit_title", ("name", Path.GetFileName(path)), ("commit", commit.Short)), diff, -1));
    }
}
