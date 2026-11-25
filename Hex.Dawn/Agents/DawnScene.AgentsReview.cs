using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

using Hex.Core.Git;

namespace Hex.Dawn;

// Review of an agent's edits, Zed's: the agent writes the files, each one's text from before the chat first touched it is
// kept (HexAgentReviews, on the disk too), and Review Changes opens one tab with every hunk of every file, a little context
// around each, Accept and Reject over it, Accept All and Reject All in the commands. Rejecting puts the baseline's lines
// back through the editor when the file is open, so it undoes, and saves the file; accepting moves the baseline up to what
// the file is.
internal sealed partial class DawnScene
{
    private const int REVIEW_CONTEXT = 2;

    private sealed record ReviewPlace(string Path, GitHunk Hunk);

    // What each line of the open review tab is, by chat
    private readonly Dictionary<int, Dictionary<int, ReviewPlace>> reviewPlaces = [];

    // The chats whose summary has its files listed
    private readonly HashSet<int> reviewOpen = [];

    private void BuildAgentReview()
    {
        commands.Add("Agents/open", Said("agents_review.review_changes"), ReviewAgentChanges, icon: "diff");
        // Always listed in the palette (they say there's no chat or nothing to settle when there isn't), not hidden with the chat
        commands.Add("Agents/open", Said("agents_review.accept_all"), () => SettleAgentChanges(accept: true), icon: "check").IsVisible = () => true;
        commands.Add("Agents/open", Said("agents_review.reject_all"), () => SettleAgentChanges(accept: false), icon: "close").IsVisible = () => true;
        commands.Add("Agents/open", Said("agents_review.undo_reject"), UndoAgentReject, icon: "discard").IsVisible = () => true;
        HexAgentReviews.Shared.Changed += _ => tabsDirty = true;

        // A call told after it ran has the disk's file after the edit, git's is the nearest to how it was
        HexAgentReviews.Shared.LateBaseline = path => git.RepositoryFor(path) is { } repository ? repository.AtCommit(path, "HEAD") : null;
    }

    /// <summary>A chat gone, what it was to be reviewed against with it and its review tab closed.</summary>
    private void ForgetAgentReview(HexAgentChat chat)
    {
        HexAgentReviews.Shared.Forget(chat.Id);
        reviewPlaces.Remove(chat.Id);
        reviewOpen.Remove(chat.Id);
        if (documents.Find(open => open.DiffKind == ReviewKind(chat)) is { } tab)
            CloseDocument(tab);
    }

    private static string ReviewKind(HexAgentChat chat) => "agentreview:" + chat.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Called off the editor's thread for each call an agent makes, the file an edit tool names kept as it is.</summary>
    internal void ReviewObserve(int chat, string tool, JsonObject arguments)
    {
        if (tool is not ("hex_edit" or "hex_write" or "hex_edit_symbol" or "hex_notebook_edit") || arguments.String("path") is not { Length: > 0 } named)
            return;

        try
        {
            string path = FullPathOf(named);
            HexAgentReviews.Shared.Record(chat, path, HexAgentReviews.TextOnDisk(path));
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A path that isn't one, the tool says so itself
        }
    }

    /// <summary>
    /// The files a rename or a fix is about to write, as the language server's edits say them, kept as they are before any
    /// is written. What a file open with unsaved changes is is the editor's text.
    /// </summary>
    private void ReviewObserveEdits(IEnumerable<(string Path, string Was)> files)
    {
        if (McpServer.CallingChat.Value is not { } chat)
            return;

        foreach (var (path, was) in files)
            HexAgentReviews.Shared.Record(chat, path, was);
    }

    // A file as it is now, what's in the editor when it's open, the disk's otherwise, nothing for one that isn't there
    private string ReviewTextOf(string path)
    {
        HexDocument? open = documents.Find(each => each.Path is { } at && each.Picture is null && PlatformPaths.Same(at, path));
        return open?.Editor.Text ?? ReviewTextOnDisk(path) ?? string.Empty;
    }

    // Files read for the summary kept by their size and write time, so a footer drawn again reads (and hashes) nothing of
    // a file that hasn't changed. Few, the most lately asked for
    private const int REVIEW_DISK_KEPT = 16;
    private readonly Dictionary<string, (long Length, DateTime Written, string Text)> reviewDisk = new(StringComparer.OrdinalIgnoreCase);

    private string? ReviewTextOnDisk(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                reviewDisk.Remove(path);
                return null;
            }
            if (reviewDisk.TryGetValue(path, out var kept) && kept.Length == file.Length && kept.Written == file.LastWriteTimeUtc)
                return kept.Text;

            string? text = HexAgentReviews.TextOnDisk(path);
            if (text is null)
                return null;
            if (reviewDisk.Count >= REVIEW_DISK_KEPT)
                reviewDisk.Clear();
            reviewDisk[path] = (file.Length, file.LastWriteTimeUtc, text);
            return text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The files a chat changed and the lines, for its summary: what differs from the baselines now.</summary>
    private (int Files, int Added, int Removed) ReviewTotalsOf(HexAgentChat chat) =>
        HexAgentReviews.Shared.Totals(chat.Id, ReviewTextOf, ReviewStampOf);

    /// <summary>Each file that differs from its baseline with the lines added and taken out in it.</summary>
    private List<(string Path, int Added, int Removed)> ReviewFilesOf(HexAgentChat chat) =>
        HexAgentReviews.Shared.Differing(chat.Id, ReviewTextOf, ReviewStampOf);

    // What a file's text is as of, so it isn't read and hashed again while it's the same: an open file's document and its
    // version (every edit moves it), a file on the disk its size and time. Zero for none, the text is asked for then.
    private long ReviewStampOf(string path)
    {
        HexDocument? open = documents.Find(each => each.Path is { } at && each.Picture is null && PlatformPaths.Same(at, path));
        if (open is not null)
            return ((long)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(open) << 32 | (uint)open.Editor.Document.Version) | long.MinValue;
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.LastWriteTimeUtc.Ticks * 31 + file.Length) & long.MaxValue | 1 : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private string ReviewName(string path)
    {
        string? folder = explorer.workspace.Folders.FirstOrDefault(each => PlatformPaths.IsUnder(path, each));
        return folder is null ? Path.GetFileName(path) : Path.GetRelativePath(folder, path).Replace('\\', '/');
    }

    /// <summary>The review tab of the chat on screen opened, or made again.</summary>
    private void ReviewAgentChanges()
    {
        if (agentChat is not { } chat)
        {
            Say(Said("agents_review.no_chat"));
            return;
        }

        ShowAgentReview(chat);
    }

    /// <summary>The review tab of a chat opened or made again, at the first hunk of <paramref name="focus"/> when it's given.</summary>
    private void ShowAgentReview(HexAgentChat chat, string? focus = null)
    {
        string kind = ReviewKind(chat);
        HexDocument? shown = documents.Find(open => open.DiffKind == kind);
        var text = new StringBuilder();
        var places = new Dictionary<int, ReviewPlace>();
        var runs = new List<GitDiffRun>();
        var headers = new List<int>();
        int hunksCount = 0, line = 0, focused = -1;
        string? first = null;
        string[] actions = [Said("agents_review.accept"), Said("agents_review.reject"), Said("agents_review.open_file")];

        foreach (string path in HexAgentReviews.Shared.Paths(chat.Id))
        {
            if (HexAgentReviews.Shared.Baseline(chat.Id, path) is not { } baseline)
                continue;

            string current = ReviewTextOf(path);
            string[] old = HexAgentReviews.Lines(baseline), now = HexAgentReviews.Lines(current);
            foreach (GitHunk hunk in HexAgentReviews.HunksOf(baseline, current))
            {
                first ??= path;
                hunksCount++;
                if (focused < 0 && focus is not null && PlatformPaths.Same(path, focus))
                    focused = line;
                headers.Add(line);
                places[line] = new ReviewPlace(path, hunk);
                text.Append($"{ReviewName(path)}:{hunk.NewStart + 1}  +{hunk.NewCount} -{hunk.OldCount}\n");
                line++;

                for (int i = Math.Max(0, hunk.NewStart - REVIEW_CONTEXT); i < hunk.NewStart; i++, line++)
                    text.Append(now[i]).Append('\n');
                if (hunk.OldCount > 0)
                {
                    runs.Add(new GitDiffRun(line, hunk.OldCount, Added: false));
                    for (int i = 0; i < hunk.OldCount; i++, line++)
                        text.Append(old[hunk.OldStart + i]).Append('\n');
                }
                if (hunk.NewCount > 0)
                {
                    runs.Add(new GitDiffRun(line, hunk.NewCount, Added: true));
                    for (int i = 0; i < hunk.NewCount; i++, line++)
                        text.Append(now[hunk.NewStart + i]).Append('\n');
                }
                for (int i = hunk.NewStart + hunk.NewCount; i < Math.Min(now.Length, hunk.NewStart + hunk.NewCount + REVIEW_CONTEXT); i++, line++)
                    text.Append(now[i]).Append('\n');
                text.Append('\n');
                line++;
            }
        }

        if (hunksCount == 0 || first is null)
        {
            if (shown is not null)
                CloseDocument(shown);
            reviewPlaces.Remove(chat.Id);
            Say(Said("agents_review.none"));
            return;
        }

        bool fresh = shown is null;
        if (shown is null)
        {
            shown = new HexDocument(Said("agents_review.tab_title", ("count", hunksCount.ToString(CultureInfo.InvariantCulture))), stage.CreateModule())
            {
                IsText = true,
                DiffOf = first,
                DiffKind = kind,
                FallbackDirectory = Path.GetDirectoryName(first) ?? browseDirectory
            };
            shown.Module.Interactive = false;
            PrepareEditor(shown);
            documents.Add(shown);
        }

        shown.Name = Said("agents_review.tab_title", ("count", hunksCount.ToString(CultureInfo.InvariantCulture)));
        shown.DiffOf = first;
        shown.Editor.ReadOnly = true;
        shown.Editor.CheckSyntax = false;
        shown.Editor.Highlighter = CodeLanguages.ForFile(first);
        SetCode(shown, text.ToString(), fresh);
        shown.SavedCode = shown.Editor.Text;
        shown.DiffChanges = headers;

        Vector4 gone = ErrorColor with { W = 0.18f }, came = (compositor.Skin?.Palette.Success ?? new Vector4(0.36f, 0.85f, 0.45f, 1.0f)) with { W = 0.16f };
        var lit = new List<UICodeLineHighlight>();
        foreach (int header in headers)
            lit.Add(new UICodeLineHighlight(header, 1, DimColor with { W = 0.22f }));
        foreach (GitDiffRun run in runs)
            lit.Add(new UICodeLineHighlight(run.Line, run.Count, run.Added ? came : gone));
        shown.Editor.LineHighlights = lit;
        shown.Editor.LineChanges = [.. runs.Select(run => new UICodeLineChange(run.Line, run.Count, run.Added ? UILineChangeKind.Added : UILineChangeKind.Removed))];
        shown.Editor.Lenses = [.. headers.Select(header => new UICodeLens(header, actions))];
        shown.Editor.OnLensPressed = (lens, action) => PressReviewLens(chat, lens.Line, action);

        reviewPlaces[chat.Id] = places;
        if (documents.Contains(shown) && document != shown)
            Show(shown);
        if (focused >= 0)
            shown.Editor.GoTo(focused, 0);
        tabsDirty = true;
    }

    private void PressReviewLens(HexAgentChat chat, int line, int action)
    {
        if (!reviewPlaces.TryGetValue(chat.Id, out var places) || !places.TryGetValue(line, out ReviewPlace? place))
            return;

        // What the agent has written since the view was made is looked at again before anything's settled
        string baseline = HexAgentReviews.Shared.Baseline(chat.Id, place.Path) ?? string.Empty;
        if (!HexAgentReviews.HunksOf(baseline, ReviewTextOf(place.Path)).Contains(place.Hunk))
        {
            Say(Said("agents_review.changed_since"));
            ShowAgentReview(chat);
            return;
        }

        if (action == 2)
        {
            if (Open(place.Path))
                code.GoTo(Math.Max(0, place.Hunk.NewStart), 0);
            return;
        }

        if (action == 0)
            AcceptHunk(chat.Id, place.Path, place.Hunk);
        else
            RejectHunk(chat.Id, place.Path, place.Hunk);
        ShowAgentReview(chat);
    }

    private void AcceptHunk(int chat, string path, GitHunk hunk)
    {
        string baseline = HexAgentReviews.Shared.Baseline(chat, path) ?? string.Empty, current = ReviewTextOf(path);
        string moved = HexAgentReviews.Accept(baseline, current, hunk);
        HexAgentReviews.Shared.SetBaseline(chat, path, moved);
        if (HexAgentReviews.HunksOf(moved, current).Count == 0)
            HexAgentReviews.Shared.Drop(chat, path);
    }

    // What the last reject took out, to put back from Agent: Undo Reject. The review tab is on screen when a hunk is
    // rejected, so Ctrl+Z goes to it and not to the file's own undo stack
    private sealed record RejectUndo(int Chat, string Path, string Baseline, string Before);
    private List<RejectUndo> lastRejects = [];

    /// <summary>The files the last reject changed put back as they were, and their baselines with them.</summary>
    private void UndoAgentReject()
    {
        if (lastRejects.Count == 0)
        {
            Say(Said("agents_review.nothing_to_undo"));
            return;
        }

        List<RejectUndo> undo = lastRejects;
        lastRejects = [];
        foreach (RejectUndo each in undo)
        {
            HexDocument? open = documents.Find(doc => doc.Path is { } at && doc.Picture is null && PlatformPaths.Same(at, each.Path));
            if (open is not null)
            {
                UITextDocument text = open.Editor.Document;
                int last = text.LineCount - 1;
                text.CollapseCarets();
                text.Select(new UITextPosition(0, 0), new UITextPosition(last, text.LineAt(last).Length));
                text.Insert(each.Before);
                ReviewSave(open);
            }
            else if (!ReviewWrite(each.Path, each.Before))
                continue;

            HexAgentReviews.Shared.Record(each.Chat, each.Path, each.Baseline);
            Say(Said("agents_review.undone", ("name", Path.GetFileName(each.Path))));
        }

        if (agentChat is { } chat)
            ShowAgentReview(chat);
    }

    private void RejectHunk(int chat, string path, GitHunk hunk)
    {
        string baseline = HexAgentReviews.Shared.Baseline(chat, path) ?? string.Empty;
        lastRejects = [new RejectUndo(chat, path, baseline, ReviewTextOf(path))];
        HexDocument? open = documents.Find(each => each.Path is { } at && each.Picture is null && PlatformPaths.Same(at, path));
        if (open is null)
        {
            if (!ReviewWrite(path, HexAgentReviews.Reject(baseline, ReviewTextOf(path), hunk)))
                return;
        }
        else
        {
            PutBack(open.Editor.Document, HexAgentReviews.Lines(baseline), hunk);
            ReviewSave(open);
        }

        if (HexAgentReviews.HunksOf(baseline, ReviewTextOf(path)).Count == 0)
            HexAgentReviews.Shared.Drop(chat, path);
    }

    private bool ReviewWrite(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say(Said("agents_review.write_failed", ("name", Path.GetFileName(path)), ("reason", e.Message)), error: true);
            return false;
        }
    }

    // A file open in a tab saved after a reject, quietly and where it was, the tab on screen left as it was. The undo of the
    // reject is still there in the editor
    private void ReviewSave(HexDocument open)
    {
        if (open.Path is null)
            return;

        HexDocument was = document;
        Show(open);
        savingQuietly = true;
        try
        {
            Save();
        }
        finally
        {
            savingQuietly = false;
        }

        if (documents.Contains(was))
            Show(was);
    }

    // The lines of a hunk in the editor swapped for the baseline's, one step to undo
    private static void PutBack(UITextDocument text, string[] old, GitHunk hunk)
    {
        string block = string.Join('\n', old.Skip(hunk.OldStart).Take(hunk.OldCount));
        text.CollapseCarets();
        int end = hunk.NewStart + hunk.NewCount;
        if (hunk.NewCount == 0)
        {
            if (hunk.NewStart < text.LineCount)
            {
                text.MoveTo(new UITextPosition(hunk.NewStart, 0));
                text.Insert(block + "\n");
            }
            else
            {
                int last = text.LineCount - 1;
                text.MoveTo(new UITextPosition(last, text.LineAt(last).Length));
                text.Insert("\n" + block);
            }
        }
        else if (end < text.LineCount)
        {
            text.Select(new UITextPosition(hunk.NewStart, 0), new UITextPosition(end, 0));
            text.Insert(hunk.OldCount > 0 ? block + "\n" : string.Empty);
        }
        else if (hunk.OldCount == 0 && hunk.NewStart > 0)
        {
            text.Select(new UITextPosition(hunk.NewStart - 1, text.LineAt(hunk.NewStart - 1).Length), new UITextPosition(end - 1, text.LineAt(end - 1).Length));
            text.Insert(string.Empty);
        }
        else
        {
            text.Select(new UITextPosition(hunk.NewStart, 0), new UITextPosition(end - 1, text.LineAt(end - 1).Length));
            text.Insert(block);
        }
    }

    /// <summary>Every hunk of the chat on screen accepted (the baselines become what the files are) or rejected (put back).</summary>
    private void SettleAgentChanges(bool accept)
    {
        if (agentChat is not { } chat)
            return;

        int count = 0;
        var rejected = new List<RejectUndo>();
        foreach (string path in HexAgentReviews.Shared.Paths(chat.Id))
        {
            string baseline = HexAgentReviews.Shared.Baseline(chat.Id, path) ?? string.Empty;
            List<GitHunk> hunks = HexAgentReviews.HunksOf(baseline, ReviewTextOf(path));
            count += hunks.Count;
            if (!accept && hunks.Count > 0)
            {
                rejected.Add(new RejectUndo(chat.Id, path, baseline, ReviewTextOf(path)));
                HexDocument? open = documents.Find(each => each.Path is { } at && each.Picture is null && PlatformPaths.Same(at, path));
                if (open is not null)
                {
                    // From the last hunk up, so the lines of the ones before it stay where they were
                    string[] old = HexAgentReviews.Lines(baseline);
                    for (int i = hunks.Count - 1; i >= 0; i--)
                        PutBack(open.Editor.Document, old, hunks[i]);
                    ReviewSave(open);
                }
                else if (!ReviewWrite(path, baseline))
                {
                    continue;
                }
            }
            HexAgentReviews.Shared.Drop(chat.Id, path);
        }

        if (rejected.Count > 0)
            lastRejects = rejected;
        ShowAgentReview(chat);
        Say(count == 0 ? Said("agents_review.none_to_settle") : Said(accept ? "agents_review.accepted" : "agents_review.rejected", ("count", HexText.Count("change", count))));
    }
}
