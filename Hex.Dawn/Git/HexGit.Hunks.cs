using System.Text;

using Hex.Core.Git;

namespace Hex.Dawn;

// One change at a time, from the bar beside it in the gutter. A click on the bar has a menu, the change shown in a diff,
// staged on its own (the index's text with just that change made to it, written into the index, what's on the disk
// left alone), or reverted (the editor's lines put back to the index's, one step to undo). The change is worked out
// again from the editor's text as it is when it's picked, not from the bars, which can be a keystroke behind.
internal sealed partial class HexGit
{
    /// <summary>Shows a file's changes in a diff at a line of it, given by the scene that opens diffs.</summary>
    public Action<GitRepository, string, int>? ShowChangeAt { get; set; }

    /// <summary>Has the gutter's change bars of a document open the menu of their change.</summary>
    private void FollowGutter(HexDocument document) => document.Editor.OnChangePressed = change => ChangeMenu(document, change);

    private void ChangeMenu(HexDocument document, UICodeLineChange pressed)
    {
        if (document.Path is not { } path || RepositoryOf(path) is not { } repository)
            return;

        var menu = new Menu("git_change");
        menu.Add(context.Strings.Said("git.show_change"), () => ShowChangeAt?.Invoke(repository.Git, path, pressed.Line), icon: "diff");
        menu.Add(context.Strings.Said("git.stage_change"), () => StageChange(document, repository, pressed.Line), icon: "add");
        menu.Add(context.Strings.Said("git.revert_change"), () => RevertChange(document, pressed.Line), icon: "discard");
        context.Windows.Menu(menu);
    }

    /// <summary>The change a line of the editor is in (or the lines taken out just above it), with the two versions' lines, null for none.</summary>
    private (GitHunk Hunk, string[] Old, string[] New, string Staged)? ChangeAt(HexDocument document, int line)
    {
        if (!tracked.TryGetValue(document, out Tracked? known))
            return null;

        string staged = known.Staged ?? string.Empty;
        string[] old = staged.Length == 0 ? [] : GitLineDiff.Lines(staged);
        string[] now = GitLineDiff.Lines(document.Editor.Text);
        foreach (GitHunk hunk in GitLineDiff.Diff(old, now))
        {
            bool on = hunk.NewCount == 0
                ? line == hunk.NewStart || line == hunk.NewStart - 1
                : line >= hunk.NewStart && line < hunk.NewStart + hunk.NewCount;
            if (on)
                return (hunk, old, now, staged);
        }

        return null;
    }

    private void StageChange(HexDocument document, Repository repository, int line)
    {
        if (document.Path is not { } path || ChangeAt(document, line) is not { } change)
        {
            context.Notices.Say(context.Strings.Said("git.no_change_here"));
            return;
        }

        // The index's lines with the change made to them, its own line ending and its last newline kept
        var (hunk, old, now, staged) = change;
        var lines = new List<string>(old.Length + hunk.NewCount);
        lines.AddRange(old.Take(hunk.OldStart));
        lines.AddRange(now.Skip(hunk.NewStart).Take(hunk.NewCount));
        lines.AddRange(old.Skip(hunk.OldStart + hunk.OldCount));

        string ending = staged.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool lastNewline = staged.Length == 0 ? document.Editor.Text.EndsWith('\n') : staged.EndsWith('\n');
        var text = new StringBuilder(string.Join(ending, lines));
        if (lastNewline && lines.Count > 0)
            text.Append(ending);

        string written = text.ToString();
        Run(repository, "git update-index", () => repository.Git.StageText(path, written), context.Strings.Said("git.change_staged"));
    }

    private void RevertChange(HexDocument document, int line)
    {
        if (ChangeAt(document, line) is not { } change)
        {
            context.Notices.Say(context.Strings.Said("git.no_change_here"));
            return;
        }

        // The editor's lines of the change for the index's, as one step to undo, every other line as it is to the last empty
        // one (the diff's lines leave the empty line after the last newline out, which a revert lost)
        var (hunk, old, _, _) = change;
        string[] every = document.Editor.Text.Replace("\r\n", "\n").Split('\n');
        var lines = new List<string>(every.Length);
        lines.AddRange(every.Take(hunk.NewStart));
        lines.AddRange(old.Skip(hunk.OldStart).Take(hunk.OldCount));
        lines.AddRange(every.Skip(hunk.NewStart + hunk.NewCount));
        context.Documents.Rewrite(document, string.Join('\n', lines));
        context.Notices.Say(context.Strings.Said("git.change_reverted"));
    }
}
