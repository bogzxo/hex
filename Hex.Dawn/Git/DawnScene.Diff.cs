using System.Numerics;

using Hex.Core.Git;

namespace Hex.Dawn;

// Diffs, a file's changes in a tab of their own, the whole of it as it is now with what was taken out put back in
// above what replaced it, red behind what went and green behind what came, VS Code's inline diff. What's changed and
// not staged against what git has, what's staged against the last commit, or what isn't saved against the disk. Read
// only, coloured as the file is, and opened at the change that was asked for. Alt+F5 and Shift+Alt+F5 go from one
// change to the next, in a diff or in a file with changes of git's.
internal sealed partial class DawnScene
{
    private void BuildDiffs()
    {
        git.ShowChange = (repository, path, staged) => ShowGitDiff(repository, path, staged);
        git.ShowChangeAt = (repository, path, line) => ShowGitDiff(repository, path, staged: false, line);
        commands.Add("Git/changes", Said("diff.open_changes"), OpenChangesHere, icon: "diff");
        commands.Add("File/path", Said("diff.compare_saved"), CompareWithSaved, icon: "diff");
        commands.Add("Go/places", Said("diff.next_change"), () => GoToChange(1), "Alt+F5", "arrow-down", UIKeyUse.BoundWhileTyping);
        commands.Add("Go/places", Said("diff.previous_change"), () => GoToChange(-1), "Shift+Alt+F5", "arrow-up", UIKeyUse.BoundWhileTyping);
    }

    /// <summary>The changes of the file on screen that aren't staged, in a diff.</summary>
    private void OpenChangesHere()
    {
        if (document.Path is not { } path || git.RepositoryFor(path) is not { } repository)
        {
            Say(Said("diff.not_in_git"));
            return;
        }

        ShowGitDiff(repository, path, staged: false, code.Document.Caret.Line);
    }

    /// <summary>
    /// A file's changes of git's in a diff, the ones not staged (against the index, with the editor's text when the file's
    /// open) or the staged ones (the index against the last commit). <paramref name="line"/> is a line of the file as it
    /// is now to open the diff at, -1 for the first change.
    /// </summary>
    private void ShowGitDiff(GitRepository repository, string path, bool staged, int line = -1)
    {
        string? editorText = staged ? null : documents.Find(open => open.Path is { } openPath && open.Picture is null && PlatformPaths.Same(openPath, path))?.Editor.Text;
        InBackground("git diff", () =>
        {
            string before = (staged ? repository.AtCommit(path, "HEAD") : repository.Staged(path)) ?? string.Empty;
            string after = staged ? repository.Staged(path) ?? string.Empty : editorText ?? (File.Exists(path) ? File.ReadAllText(path) : string.Empty);
            return GitDiffText.Of(before, after);
        }, diff => OpenDiff(path, staged ? "staged" : "changes", Said(staged ? "diff.title_staged" : "diff.title", ("name", Path.GetFileName(path))), diff, line));
    }

    /// <summary>What isn't saved of the file on screen against what's on the disk, in a diff.</summary>
    private void CompareWithSaved()
    {
        if (document.Path is not { } path || !document.IsText || document.Picture is not null || document.DiffOf is not null)
            return;

        string disk = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        OpenDiff(path, "saved", Said("diff.title_saved", ("name", Path.GetFileName(path))), GitDiffText.Of(disk, code.Text), code.Document.Caret.Line);
    }

    /// <summary>Opens a diff in its tab, the one it had already if it's open (made again), at a line of the file as it is now.</summary>
    private void OpenDiff(string path, string kind, string title, GitDiffText diff, int line)
    {
        if (diff.Runs.Count == 0)
        {
            Say(Said("diff.no_changes", ("name", Path.GetFileName(path))));
            return;
        }

        HexDocument? shown = documents.Find(open => open.DiffOf is { } of && PlatformPaths.Same(of, path) && open.DiffKind == kind);
        if (shown is null)
        {
            shown = new HexDocument(title, stage.CreateModule())
            {
                IsText = true,
                DiffOf = path,
                DiffKind = kind,
                FallbackDirectory = Path.GetDirectoryName(path) ?? browseDirectory
            };
            shown.Module.Interactive = false;
            PrepareEditor(shown);
            documents.Add(shown);
        }

        shown.Name = title;
        shown.Editor.ReadOnly = true;
        shown.Editor.Highlighter = CodeLanguages.ForFile(path);
        SetCode(shown, diff.Text, fresh: true);
        shown.SavedCode = shown.Editor.Text;
        shown.DiffChanges = diff.Changes;

        Vector4 gone = ErrorColor with { W = 0.18f }, came = (compositor.Skin?.Palette.Success ?? new Vector4(0.36f, 0.85f, 0.45f, 1.0f)) with { W = 0.16f };
        shown.Editor.LineHighlights = [.. diff.Runs.Select(run => new UICodeLineHighlight(run.Line, run.Count, run.Added ? came : gone))];
        shown.Editor.LineChanges = [.. diff.Runs.Select(run => new UICodeLineChange(run.Line, run.Count, run.Added ? UILineChangeKind.Added : UILineChangeKind.Removed))];

        // Opened from the Source Control side it stays on that side, going to a tab put the side back to the one of its kind
        HexSide was = Side;
        Show(shown);
        if (was == HexSide.SourceControl)
            Side = was;
        tabsDirty = true;

        // At the change the line asked about is in or nearest after it, the first change when there's no line
        int at = line >= 0 && diff.NewLines.Length > 0 ? diff.NewLines[Math.Clamp(line, 0, diff.NewLines.Length - 1)] : diff.Changes[0];
        if (line >= 0)
        {
            int change = diff.Changes.FirstOrDefault(start => start >= at - 1, -1);
            if (change >= 0 && change - at <= 1)
                at = change;
        }
        code.GoTo(at, 0);
        Say(Said("diff.shown", ("name", Path.GetFileName(path)), ("count", HexText.Count("change", diff.Changes.Count))));
    }

    /// <summary>The caret to the next change or the one before, a diff's or git's in a file.</summary>
    private void GoToChange(int direction)
    {
        IEnumerable<int> starts = document.DiffOf is not null ? document.DiffChanges
            : conflicts.Count > 0 && conflictsFor.Of == document ? conflicts.Select(conflict => conflict.Start)
            : git.HunksOf(document).Select(hunk => hunk.NewStart);
        int caret = code.Document.Caret.Line;
        int? next = direction > 0
            ? starts.Where(start => start > caret).Cast<int?>().FirstOrDefault()
            : starts.Where(start => start < caret).Cast<int?>().LastOrDefault();

        if (next is { } line)
            code.GoTo(line, 0);
        else
            Say(Said(direction > 0 ? "diff.last_change" : "diff.first_change"));
    }
}
