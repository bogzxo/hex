using System.Globalization;
using System.Numerics;
using Hex.Core.Git;
using Hex.Shell;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// Agentic's checkpoints, a git tree of every repository of the workspace taken as a turn begins (HexCheckpoints),
// and the files a turn changed, what the Changes tab shows against them and puts back from them
internal sealed partial class DawnScene
{
    /* Changes */

    /// <summary>
    /// A checkpoint of every repository of the workspace as a turn begins, taken off the editor's thread, for that turn's
    /// changes to be seen against and put back from. Kept on the turn's row (HexAgentEntry.Checkpoints), written down
    /// with the chat, so it outlives Hex starting again, the trees themselves being in the repository's own objects.
    /// </summary>
    private void TakeCheckpoint(HexAgentEntry asked, Action then)
    {
        string[] folders = [.. explorer.workspace.Folders];
        checkpointing.Add(asked);
        InBackground("checkpoint", () =>
        {
            var tops = new HashSet<string>(PlatformPaths.Comparer);
            var made = new List<(string, string)>();
            // Whatever goes wrong, what was taken so far comes back. A throw here never called what's after, and the agent
            // waiting on it never started
            try
            {
                foreach (string folder in folders)
                {
                    foreach (string top in HexCheckpoints.ReposOf(folder))
                    {
                        if (tops.Add(top) && HexCheckpoints.Take(top) is { } tree)
                            made.Add((top, tree));
                    }
                }
            }
            catch (Exception)
            {
            }

            return made;
        }, made =>
        {
            checkpointing.Remove(asked);
            asked.Checkpoints = made;
            // The chat written down again with it
            foreach (HexAgentChat chat in agentChats)
            {
                if (chat.Entries.Contains(asked))
                {
                    chat.Touch(asked);
                    break;
                }
            }
            then();
        }, quiet: true);
    }

    // Whether a tool writes files by its name, the edits and writes of every agent system
    private static bool Writes(string? tool)
    {
        // Without a lower-case copy of the name, asked for every row of a chat each time its changes are counted
        string name = tool ?? string.Empty;
        return name.Contains("edit", StringComparison.OrdinalIgnoreCase) || name.Contains("write", StringComparison.OrdinalIgnoreCase) || name.Contains("patch", StringComparison.OrdinalIgnoreCase)
            || name.Contains("replace", StringComparison.OrdinalIgnoreCase) || name.Contains("filechange", StringComparison.OrdinalIgnoreCase) || name.Contains("sed_file", StringComparison.OrdinalIgnoreCase);
    }

    // Each turn of a chat that wrote files, newest first, what was asked and the files, at most the turns a chat keeps
    private List<(HexAgentEntry Asked, int Number, List<string> Files)> ChangedFilesOf(HexAgentChat chat, bool latestOnly = false)
    {
        var turns = new List<(HexAgentEntry, int, List<string>)>();
        HexAgentEntry? asked = null;
        List<string>? files = null;
        int number = 0;
        foreach (HexAgentEntry entry in chat.Entries)
        {
            if (entry.Kind == HexAgentEntryKind.User)
            {
                if (asked is not null && files is { Count: > 0 })
                    turns.Add((asked, number, files));
                asked = entry;
                files = [];
                number++;
            }
            else if (entry.Kind == HexAgentEntryKind.Tool && files is not null && Writes(entry.ToolName) && FileOfCall(entry) is { } file && !files.Contains(file))
                files.Add(file);
        }

        if (asked is not null && files is { Count: > 0 })
            turns.Add((asked, number, files));
        turns.Reverse();
        if (latestOnly && turns.Count > 1)
            turns.RemoveRange(1, turns.Count - 1);
        return turns;
    }

    // A file as a whole path, from the workspace's first folder for one an agent named from there
    private string FullPathOf(string file)
    {
        string? folder = explorer.workspace.Folders.FirstOrDefault();
        return Path.IsPathRooted(file) || folder is null ? file : Path.GetFullPath(Path.Combine(folder, file));
    }

    // The checkpoint a file of a turn goes back to, the repository it's in, null when the turn has none for it
    private static (string Top, string Tree)? CheckpointFor(HexAgentEntry asked, string path)
    {
        if (asked.Checkpoints is not { } taken)
            return null;
        (string Top, string Tree)? best = null;
        foreach (var each in taken)
        {
            if (PlatformPaths.IsUnder(path, each.Top) && (best is null || each.Top.Length > best.Value.Top.Length))
                best = each;
        }

        return best;
    }

    // The checkpoints of the turn before a row, the one the agent was working on when the row came, null when it had none
    private static IReadOnlyList<(string Top, string Tree)>? EarlierCheckpoints(IReadOnlyList<HexAgentEntry> entries, int before)
    {
        for (int at = before - 1; at >= 0; at--)
        {
            if (entries[at].Kind == HexAgentEntryKind.User)
                return entries[at].Checkpoints is { Count: > 0 } taken ? taken : null;
        }

        return null;
    }

    // The turns being checkpointed now, so a turn is checkpointed once however it began
    private readonly HashSet<HexAgentEntry> checkpointing = [];

    // Each chat's version as it was last looked at for a turn without a checkpoint
    private readonly Dictionary<int, int> checkpointLooked = [];

    /// <summary>
    /// Every tick, a checkpoint for any chat's latest turn that has none, whichever way it began, a message sent while the
    /// agent worked or a chat carried on after Hex started again as much as one started from the box (StartAgent takes
    /// that one before the agent starts). Before, those turns had no Diff, Review or Restore at all. A chat is looked at
    /// again only when it moved.
    /// </summary>
    private void CheckpointNewTurns()
    {
        HexHubChats chats = agentChats;
        for (int i = 0; i < chats.Count; i++)
        {
            HexAgentChat chat = chats[i];
            if (checkpointLooked.TryGetValue(chat.Id, out int seen) && seen == chat.Version)
                continue;
            checkpointLooked[chat.Id] = chat.Version;
            IReadOnlyList<HexAgentEntry> entries = chat.Entries;
            bool wrote = false;
            for (int at = entries.Count - 1; at >= 0; at--)
            {
                HexAgentEntry entry = entries[at];
                if (entry.Kind == HexAgentEntryKind.Tool && Writes(entry.ToolName))
                    wrote = true;
                if (entry.Kind != HexAgentEntryKind.User)
                    continue;
                // Never one for a turn that already wrote something, taken now it would hold the turn's own changes and
                // Diff, Review and Restore would say there were none. A chat read back with no checkpoint on its turn
                // got one that way, after the edits it was meant to be before
                if (entry.Checkpoints is null && !wrote && !checkpointing.Contains(entry) && chat.Running)
                {
                    // Sent while the agent was already working, it goes on with the turn it was working on and its changes
                    // are seen against that turn's checkpoint. One of its own was taken while the agent went on writing,
                    // git going through the repository for a few seconds, and held the very edits it was meant to be before
                    if (EarlierCheckpoints(entries, at) is { } running)
                    {
                        entry.Checkpoints = running;
                        chat.Touch(entry);
                    }
                    else
                        TakeCheckpoint(entry, () => { });
                }
                break;
            }
        }
    }
}
