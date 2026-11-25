using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

// The line under a turn that's over: how long it took, how many tools it called and how many files it changed, the files
// a link to them in Agentic's Changes. A long chat had no mark of where one turn stopped and the next began, and what a
// turn did was only in the line over the chat while it was the latest.
internal sealed partial class DawnScene
{
    /// <summary>
    /// Whether the row at <paramref name="index"/> is the last a turn shows: the next row of its own (not a call a group's
    /// row draws) is what was asked next, or there's none and the agent isn't working.
    /// </summary>
    private bool EndsTurn(HexAgentChat chat, int index)
    {
        IReadOnlyList<HexAgentEntry> entries = chat.Entries;
        if (entries[index].Kind == HexAgentEntryKind.User || InToolGroup(chat, index))
            return false;
        for (int next = index + 1; next < entries.Count; next++)
        {
            if (!InToolGroup(chat, next))
                return entries[next].Kind == HexAgentEntryKind.User;
        }

        return !chat.Running && !chat.HasPending;
    }

    /// <summary>The turn's line for a row that ends one, null for any other row.</summary>
    private UIComponent? TurnFooter(HexAgentChat chat, HexAgentEntry entry)
    {
        IReadOnlyList<HexAgentEntry> entries = chat.Entries;
        int at = -1;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(entries[i], entry))
            {
                at = i;
                break;
            }
        }
        if (at < 0 || !EndsTurn(chat, at))
            return null;

        // Back to what was asked, counting the calls and the files they wrote
        int calls = 0;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HexAgentEntry? asked = null;
        for (int i = at; i >= 0; i--)
        {
            HexAgentEntry each = entries[i];
            if (each.Kind == HexAgentEntryKind.User)
            {
                asked = each;
                break;
            }
            if (each.Kind == HexAgentEntryKind.Tool)
            {
                calls++;
                if (Writes(each.ToolName) && FileOfCall(each) is { } file)
                    files.Add(file);
            }
        }
        if (asked is null)
            return null;

        // The time only while it's this run's, a chat read back has its clocks from another
        long ended = entry.Ended ?? entry.Started;
        double seconds = (ended - asked.Started) / 1000.0;
        // Under a second it says nothing worth a line, a turn of only words
        string time = seconds is >= 1 and < 86_400 ? TurnTime(seconds) : string.Empty;
        // A turn of no calls is its time alone, and one with nothing to say has no line
        string counted = calls == 0 ? time
            : calls == 1 ? Said("agents_panel.turn_summary_one", ("time", time)) : Said("agents_panel.turn_summary", ("time", time), ("calls", calls.ToString(CultureInfo.InvariantCulture)));
        counted = counted.Trim().Trim('·').Trim();
        if (counted.Length == 0 && files.Count == 0)
            return null;

        // A tick for a turn that went, the error's mark for one that ended failing
        bool failed = false;
        for (int i = at; i < entries.Count && !failed; i++)
            failed = entries[i].Kind == HexAgentEntryKind.Error;
        var line = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(0, 22), Anchor = Origin.Left, Pivot = Origin.Left };
        if (counted.Length > 0)
            line.Add(new Label { Text = $"$({(failed ? "error" : "check")}) {UIRichText.Literal(counted)}", TextScale = 0.125f, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left });
        // Only under the chat's last turn, what's still to review is the chat's, not a turn's
        bool latest = !entries.Skip(at + 1).Any(each => each.Kind == HexAgentEntryKind.User);
        (int Files, int Added, int Removed) review = latest ? ReviewTotalsOf(chat) : default;
        List<UIComponent>? reviewFiles = null;
        if (review.Files > 0)
        {
            string reviewed = Said(review.Files == 1 ? "agents_review.summary_one" : "agents_review.summary", ("count", review.Files.ToString(CultureInfo.InvariantCulture)),
                ("added", review.Added.ToString(CultureInfo.InvariantCulture)), ("removed", review.Removed.ToString(CultureInfo.InvariantCulture)));
            float wide = Skin is { } skin ? MathF.Ceiling(skin.Font.MeasureLine($"$(diff) {reviewed}", 0.125f, UITextStyle.None, default) + skin.ButtonPadding.Total.X + 6) : 160;
            line.Add(new Button
            {
                Label = reviewed, Icon = "diff", Style = "button_flat", LabelScale = 0.125f, Size = new Vector2(wide, 24), Animated = false,
                Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = Said("agents_review.summary_tip"),
                OnPressed = () => ShowAgentReview(chat)
            });

            // The files under it, each with its lines and a press that opens the review at it
            bool open = reviewOpen.Contains(chat.Id);
            line.Add(new Button
            {
                Icon = open ? "triangle-up" : "triangle-down", Style = "button_flat", Size = new Vector2(24, 24), Animated = false, Anchor = Origin.Left, Pivot = Origin.Left,
                OnPressed = () =>
                {
                    if (!reviewOpen.Remove(chat.Id))
                        reviewOpen.Add(chat.Id);
                    chat.Touch(entry);
                }
            });
            if (open)
            {
                reviewFiles = [];
                foreach (var (path, added, removed) in ReviewFilesOf(chat))
                {
                    string target = path;
                    reviewFiles.Add(new Button
                    {
                        Label = Said("agents_review.file_row", ("name", ReviewName(path)), ("added", added.ToString(CultureInfo.InvariantCulture)), ("removed", removed.ToString(CultureInfo.InvariantCulture))),
                        Icon = "file", Style = "button_flat", LabelScale = 0.125f, Fill = UIFill.Horizontal, Size = new Vector2(0, 22), Animated = false,
                        OnPressed = () => ShowAgentReview(chat, target)
                    });
                }
            }
        }
        if (files.Count > 0)
        {
            string changed = files.Count == 1 ? Said("agents_panel.turn_files_one") : Said("agents_panel.turn_files", ("count", files.Count.ToString(CultureInfo.InvariantCulture)));
            float wide = Skin is { } skin ? MathF.Ceiling(skin.Font.MeasureLine($"$(diff) {changed}", 0.125f, UITextStyle.None, default) + skin.ButtonPadding.Total.X + 6) : 130;
            line.Add(new Button
            {
                Label = changed, Icon = "diff", Style = "button_flat", LabelScale = 0.125f, Size = new Vector2(wide, 24), Animated = false,
                Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = string.Join("\n", files.Take(12)),
                OnPressed = () =>
                {
                    if (Face == Hex.Shell.HexFace.Noon)
                        ShowStage(NoonStage.Changes);
                    else
                        ShowNoonChanges();
                }
            });
        }

        if (reviewFiles is not { Count: > 0 })
            return line;

        var column = new StackPanel { Fill = UIFill.Horizontal, Spacing = 2 };
        column.Add(line);
        foreach (UIComponent each in reviewFiles)
            column.Add(each);
        return column;
    }

    // How long a turn took the way a person says it, 8.4s, 3m 05s, 1h 02m
    private static string TurnTime(double seconds) => seconds switch
    {
        < 60 => seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s",
        < 3600 => $"{(int)seconds / 60}m {(int)seconds % 60:00}s",
        _ => $"{(int)seconds / 3600}h {(int)seconds % 3600 / 60:00}m"
    };

    // The changes of the chat on screen from Dawn, Agentic brought up at them
    private void ShowNoonChanges()
    {
        ShowFace(Hex.Shell.HexFace.Noon);
        ShowStage(NoonStage.Changes);
    }
}
