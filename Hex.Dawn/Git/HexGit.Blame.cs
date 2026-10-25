using System.Text;

using Hex.Core.Git;

namespace Hex.Dawn;

// Blame, who last changed the line the caret is on, when and in which commit, said faintly at the end of the line the
// way GitLens does it. git is asked about the editor's text as it is, so lines typed since the last save are nobody's
// yet, once as a file is opened, again when git says something new, and a second after the typing stops. A command puts
// the whole of the line's commit up in a window.
internal sealed partial class HexGit
{
    // How long after the last key the file is blamed again
    private const float BLAME_AFTER = 1.0f;

    /// <summary>What's known of who changed the lines of an open file.</summary>
    private sealed class Blamed
    {
        public IReadOnlyList<GitBlameLine> Lines = [];
        public int Generation = -1, Version = -1;
        public bool Asking;
        public float AskIn = 0.01f;
        public (int Line, int Count) Shown = (-1, -1);
    }

    private readonly Dictionary<HexDocument, Blamed> blamed = [];

    private void FollowForBlame()
    {
        context.Documents.Edited += edited =>
        {
            if (blamed.TryGetValue(edited, out Blamed? known))
                known.AskIn = BLAME_AFTER;
        };
        context.Documents.Closed += closed => blamed.Remove(closed);
        context.Commands.Add("Git/history", context.Strings.Said("git.blame_command"), ShowLineCommit, icon: "git-commit");
    }

    /// <summary>Every tick, the file on screen blamed when it's due, and its caret line's note kept up.</summary>
    private void UpdateBlame(float dt)
    {
        HexDocument document = context.Documents.Current;
        if (!context.Settings.Values.GitBlame || document.Picture is not null || document.Path is not { } path || RepositoryOf(path) is not { } repository)
        {
            if (document.Editor.LineNote is not null)
            {
                document.Editor.LineNote = null;
                context.Redraw();
            }
            return;
        }

        if (!blamed.TryGetValue(document, out Blamed? known))
            blamed[document] = known = new Blamed();

        // Something new from git (a commit, a save) has it asked again
        if (known.Generation != repository.Generation && repository.Status is not null && known.AskIn <= 0.0f)
            known.AskIn = 0.01f;

        if (known.AskIn > 0.0f && !known.Asking && (known.AskIn -= dt) <= 0.0f)
        {
            known.AskIn = 0.0f;
            known.Asking = true;
            int generation = repository.Generation;
            string text = document.Editor.Text;
            int version = document.Editor.Document.Version;
            bool fresh = repository.Status?.StateOf(path).Change == GitChange.Untracked;
            context.Work.InBackground("git blame", () => fresh ? [] : repository.Git.Blame(path, text), lines =>
            {
                known.Asking = false;
                known.Lines = lines;
                known.Generation = generation;
                known.Version = version;
                known.Shown = (-1, -1);
            }, quiet: true);
        }

        // Typed into since it was blamed, the lines have moved under the blame and a line would say somebody else did
        // what was just typed, so there's no note until it's blamed again a second after the typing stops
        int line = document.Editor.Document.Caret.Line;
        int lines = document.Editor.Document.Version == known.Version ? known.Lines.Count : -1;
        if (known.Shown == (line, lines))
            return;

        known.Shown = (line, lines);
        document.Editor.LineNote = line < lines ? new UICodeHint(line, NoteOf(known.Lines[line])) : null;
        context.Redraw();
    }

    // What the note says, the author, how long ago and the commit's first line
    private string NoteOf(GitBlameLine line) => line.Committed
        ? context.Strings.Said("git.blame_note", ("author", line.Author), ("when", Ago(line.When)), ("summary", line.Summary))
        : context.Strings.Said("git.blame_uncommitted");

    /// <summary>How long ago a moment was, the way a person says it, "3 days ago".</summary>
    internal static string Ago(DateTimeOffset when)
    {
        TimeSpan since = DateTimeOffset.Now - when;
        if (since.TotalMinutes < 1) return "just now";
        if (since.TotalHours < 1) return Plural((int)since.TotalMinutes, "minute");
        if (since.TotalDays < 1) return Plural((int)since.TotalHours, "hour");
        if (since.TotalDays < 30) return Plural((int)since.TotalDays, "day");
        if (since.TotalDays < 365) return Plural((int)(since.TotalDays / 30), "month");
        return Plural((int)(since.TotalDays / 365), "year");

        static string Plural(int count, string unit) => count == 1 ? $"a{(unit == "hour" ? "n" : "")} {unit} ago" : $"{count} {unit}s ago";
    }

    /// <summary>The commit of the caret's line put up in a window, its message and the files it changed.</summary>
    private void ShowLineCommit()
    {
        HexDocument document = context.Documents.Current;
        if (document.Path is not { } path || RepositoryOf(path) is not { } repository || !blamed.TryGetValue(document, out Blamed? known))
            return;

        int line = document.Editor.Document.Caret.Line;
        if (line >= known.Lines.Count || known.Lines[line] is not { Committed: true } blame)
        {
            context.Notices.Say(context.Strings.Said("git.blame_uncommitted"));
            return;
        }

        context.Work.InBackground("git show", () => GitProcess.Run(repository.Git.Root, ["show", "--stat", "--format=%H%n%an <%ae>%n%ad%n%n%B", blame.Commit!]), shown =>
        {
            var markdown = new StringBuilder();
            markdown.Append("```\n").Append(shown.Ok ? shown.Output.TrimEnd() : shown.Why).Append("\n```\n");
            context.Windows.ShowText(context.Strings.Said("git.blame_window", ("commit", blame.Short)), markdown.ToString());
        });
    }
}
