using System.Globalization;
using System.Numerics;
using Hex.Core.Git;
using Hex.Shell;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// Agentic's Changes tab, what the chat's agent wrote turn by turn, each file against how it was before the turn,
// put back from the turn's checkpoint, and whether a file changed again since
internal sealed partial class DawnScene
{
    private void DrawChanges()
    {
        ClearRows(noonChangesRows);
        if (agentChat is not { } chat || ChangedFilesOf(chat) is not { Count: > 0 } turns)
        {
            StageEmpty(noonChangesRows, "diff", Said("noon.changes_empty"));
            return;
        }

        string? folder = explorer.workspace.Folders.FirstOrDefault();
        foreach (var (asked, number, files) in turns)
        {
            var card = noonChangesRows.Add(new StackPanel
            {
                Fill = UIFill.Horizontal, Spacing = 3, Padding = new UIEdges(12, 10, 12, 10), Radius = 8,
                Color = (Skin?.Palette.Field ?? new Vector4(0.1f, 0.1f, 0.12f, 1.0f)) with { W = 0.6f }, Outline = Skin?.BorderColor ?? Vector4.Zero
            });
            var head = card.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 26), Color = Vector4.Zero });
            bool checkpointed = files.Any(file => CheckpointFor(asked, FullPathOf(file)) is not null);
            // As much of what was asked as the card's width has room for beside its button, measured in the font, it was
            // cut at seventy letters however wide the card was
            string title = Said("noon.changes_turn", ("number", number.ToString(CultureInfo.InvariantCulture)), ("asked", OneLine(asked.AskedText ?? asked.Text, 400)));
            // The card's inside, less the scroll's padding and bar, the card's own padding and the icon, and the button beside
            // it. Measured plain, the title is bold and a bold letter is wider, so a tenth is left over for it
            float room = (noonChanges.Bounds.Width - 70 - (checkpointed ? 170 : 0)) * 0.9f;
            head.Add(new Label
            {
                Text = $"$(history) {UIRichText.Literal(room > 100 ? FitText(title, 0.135f, room) : OneLine(title, 70))}",
                TextScale = 0.135f, Bold = true, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left, Tooltip = asked.AskedText ?? asked.Text
            });
            if (checkpointed)
                head.Add(new Button
                {
                    // Quiet beside the files' Review, putting a whole turn back is the last thing to reach for, not the first
                    Label = Said("noon.changes_restore_turn"), Icon = "discard", Style = "button_flat", Size = new Vector2(150, 24), LabelScale = 0.12f, Animated = false,
                    Anchor = Origin.Right, Pivot = Origin.Right, OnPressed = () => AskRestore(asked, [.. files.Select(FullPathOf)])
                });
            else
                card.Add(ContextText(Said("noon.changes_no_checkpoint"), dim: true));

            foreach (string file in files)
            {
                string path = FullPathOf(file);
                string shown = folder is not null && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(folder, path) : file;
                var row = card.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 24), Color = Vector4.Zero });
                row.Add(new Button
                {
                    Icon = HexExplorer.FileIcon(path), IconColor = explorer.FileIconColor(path), Label = UIRichText.Literal(OneLine(shown, 70)),
                    Style = "button_flat", Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(420, 22), LabelScale = 0.13f, Animated = false,
                    OnPressed = () =>
                    {
                        ShowFace(HexFace.Dawn);
                        Open(path);
                    }
                });
                if (CheckpointFor(asked, path) is null)
                    continue;
                row.Add(new Button
                {
                    Label = Said("noon.changes_restore"), Icon = "discard", Style = "button_flat", Size = new Vector2(90, 22), LabelScale = 0.12f, Animated = false,
                    Anchor = Origin.Right, Pivot = Origin.Right, OnPressed = () => AskRestore(asked, [path])
                });
                row.Add(new Button
                {
                    Label = Said("noon.changes_diff"), Icon = "diff", Style = "button_flat", Size = new Vector2(70, 22), LabelScale = 0.12f, Animated = false,
                    Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-94, 0), OnPressed = () => DiffSinceTurn(asked, path)
                });
                row.Add(ReviewButton(asked, path));
                DrawReview(card, asked, path);
            }
        }
    }

    /// <summary>A file of a turn against how it was before the turn, in a diff tab of the editor, read off the editor's thread.</summary>
    private void DiffSinceTurn(HexAgentEntry asked, string path)
    {
        if (CheckpointFor(asked, path) is not { } at)
            return;
        InBackground("checkpoint", () => (Before: HexCheckpoints.TextAt(at.Top, at.Tree, path) ?? string.Empty, Now: File.Exists(path) ? File.ReadAllText(path) : string.Empty), texts =>
        {
            if (texts.Before.Replace("\r\n", "\n") == texts.Now.Replace("\r\n", "\n"))
            {
                Say(Said("noon.changes_unchanged", ("name", Path.GetFileName(path))));
                return;
            }

            ShowFace(HexFace.Dawn);
            OpenDiff(path, "checkpoint", Said("noon.changes_diff_title", ("name", Path.GetFileName(path))), GitDiffText.Of(texts.Before, texts.Now), 0);
        }, quiet: true);
    }

    /// <summary>Files of a turn put back as they were before it, once the person says so, it can't be undone.</summary>
    private void AskRestore(HexAgentEntry asked, string[] paths)
    {
        string yes = Said("noon.changes_restore_button");
        string what = paths.Length == 1 ? Path.GetFileName(paths[0]) : HexText.Count("file", paths.Length);
        string text = Said("noon.changes_restore_text", ("files", what));
        // Changed again after the turn, by a later one, another agent or by hand, it's said, that's lost as well
        string[] since = [.. paths.Where(path => ChangedSince(asked, path)).Select(Path.GetFileName).OfType<string>()];
        if (since.Length > 0)
            text += "\n\n" + Said("noon.changes_restore_since", ("files", string.Join(", ", since.Take(5))));
        Context.Windows.Ask(Said("noon.changes_restore_title"), text, [Said("git.discard_no"), yes], answer =>
        {
            if (answer != yes)
                return;
            // Looked at again as it's said, a file changed while the question was up is asked about once more
            if (paths.Any(path => ChangedSince(asked, path) && !since.Contains(Path.GetFileName(path))))
            {
                AskRestore(asked, paths);
                return;
            }

            var each = paths.Select(path => (Path: path, At: CheckpointFor(asked, path))).Where(pair => pair.At is not null).ToArray();
            InBackground("restore", () => each.Select(pair => (pair.Path, Why: HexCheckpoints.Restore(pair.At!.Value.Top, pair.At.Value.Tree, pair.Path))).ToArray(), done =>
            {
                int restored = done.Count(each => each.Why is null);
                Say(Said("noon.changes_restored", ("count", HexText.Count("file", restored))));
                // A file left as it is says why, one that wasn't in the checkpoint is left rather than deleted
                if (done.FirstOrDefault(each => each.Why is not null) is { Why: { } why } left)
                    Say(Said("noon.changes_not_restored", ("name", Path.GetFileName(left.Path)), ("why", why)), error: true);
                noonStageDrawn = NOT_DRAWN;
            });
        }, risky: yes);
    }

    // A little longer than a write takes to reach the disk, a file's time within it of the agent's last write is that write
    private const long WRITE_SLACK_MS = 3000;

    /// <summary>
    /// Whether a file of a turn was changed after the turn last wrote it, by a write of any agent after it or on the disk
    /// later than that, which putting it back would lose as well.
    /// </summary>
    private bool ChangedSince(HexAgentEntry asked, string path)
    {
        // When the turn last wrote it, from the turn's own rows, and whether any agent wrote it after that
        var writes = new List<(long Started, long At, bool InTurn)>();
        foreach (HexAgentChat chat in agentChats)
        {
            bool inTurn = false;
            foreach (HexAgentEntry entry in chat.Entries)
            {
                if (entry == asked)
                    inTurn = true;
                else if (entry.Kind == HexAgentEntryKind.User)
                    inTurn = false;
                if (entry.Kind == HexAgentEntryKind.Tool && Writes(entry.ToolName) && FileOfCall(entry) is { } file
                    && PlatformPaths.Comparer.Equals(FullPathOf(file), path))
                    writes.Add((entry.Started, entry.Ended ?? entry.Started, inTurn));
            }
        }

        long last = -1;
        foreach (var write in writes)
        {
            if (write.InTurn)
                last = Math.Max(last, write.At);
        }

        if (last < 0)
            return false;
        foreach (var write in writes)
        {
            if (!write.InTurn && write.Started > last)
                return true;
        }
        try
        {
            // The ticks of the write turned into the clock's time, a file written on the disk well after it changed since
            DateTime wrote = DateTime.UtcNow.AddMilliseconds(last - System.Environment.TickCount64);
            return File.Exists(path) && File.GetLastWriteTimeUtc(path) > wrote.AddMilliseconds(WRITE_SLACK_MS);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
