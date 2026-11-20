using System.Numerics;

using Horizon.Rendering;

namespace Hex.Dawn;

// The agents' data on the disk (HexData): how much each part of it takes, said in the Agents menu's Agent Data and at the
// foot of Agentic's fleet, and its folders opened and tidied from there. Measured on the pool while either is on screen,
// at most every half a minute, and again after anything was tidied. What deletes asks first, it's gone for good.
internal sealed partial class DawnScene
{
    // How long a measure is good for while it's looked at
    private const float AGENT_DATA_FRESH = 30.0f;

    // The days a chat hasn't moved in for Delete Old Chats to offer it
    private static readonly int[] OLD_CHAT_DAYS = [90, 30, 7];

    private HexDataUsage? agentData;
    private float agentDataAge = float.MaxValue;
    private bool agentDataMeasuring;
    private Button? noonAgentData;

    // The items that say a size, put right when a measure comes back
    private readonly List<(MenuItem Item, Func<HexDataUsage?, string> Label)> agentDataItems = [];

    /// <summary>The Agent Data menu in the Agents menu, and its line at the foot of Agentic's fleet.</summary>
    private void BuildAgentData()
    {
        AddAgentDataItems((group, label, picked, icon, destructive) => commands.Add($"Agents/data/agent_data:{group}", label, picked, icon: icon, destructive: destructive), track: true);
        noonAgentData = noonFleet.Add(new Button
        {
            Style = "button_flat",
            Fill = UIFill.Horizontal,
            Size = new Vector2(0, NOON_HARNESS),
            Align = Origin.Left,
            LabelScale = 0.13f,
            Animated = false,
            Label = $"$(database) {Said("agent_data.title")}",
            OnPressed = () => ContextMenu.Show(chrome, noonAgentData!.Bounds.PointAt(Origin.TopLeft), AgentDataMenu())
        });
    }

    // The same items as a menu of their own, for the line in Agentic
    private Menu AgentDataMenu()
    {
        var menu = new Menu(Said("agent_data.title"));
        string? last = null;
        AddAgentDataItems((group, label, picked, icon, destructive) =>
        {
            if (last is not null && last != group)
                menu.AddSeparator();
            last = group;
            return menu.Add(label, picked, icon: icon, destructive: destructive);
        }, track: false);
        return menu;
    }

    // What the data's menus have, in their groups: the folder, what each part takes (opening its folder), and the
    // tidying, which deletes and so goes last and in the error's colour
    private void AddAgentDataItems(Func<string, string, Action, string, bool, MenuItem> add, bool track)
    {
        void Sized(string group, Func<HexDataUsage?, string> label, Action picked, string icon)
        {
            MenuItem item = add(group, label(agentData), picked, icon, false);
            if (track)
                agentDataItems.Add((item, label));
        }

        Sized("folder", usage => usage is null ? Said("agent_data.open") : Said("agent_data.open_sized", ("size", HexDataTidy.Bytes(usage.Total))),
            () => OpenDataFolder(HexData.Root), "folder-opened");
        Sized("usage", usage => SizeLabel("agent_data.chats", usage?.Chats), () => OpenDataFolder(HexData.AgentChats), "comment-discussion");
        Sized("usage", usage => SizeLabel("agent_data.images", usage?.Images), () => OpenDataFolder(HexData.AgentImages), "file-media");
        Sized("usage", usage => SizeLabel("agent_data.temp", usage?.Temp), () => OpenDataFolder(HexData.AgentTemp), "file");
        Sized("usage", usage => SizeLabel("agent_data.cache", usage?.Cache), () => OpenDataFolder(HexData.Cache), "database");
        add("clean", Said("agent_data.delete_chats"), AskDeleteOldChats, "trash", true);
        add("clean", Said("agent_data.delete_images"), AskDeleteUnusedImages, "trash", true);
        add("clean", Said("agent_data.clear_temp"), AskClearTemp, "clear-all", true);
    }

    private string SizeLabel(string key, long? size) =>
        Said(key, ("size", size is { } bytes ? HexDataTidy.Bytes(bytes) : Said("agent_data.counting")));

    /// <summary>Measured again when it's looked at and the last measure is old, the items put right when it's back.</summary>
    private void UpdateAgentData(float dt)
    {
        agentDataAge += dt;
        bool looked = menu.OpenMenu?.Title == "Agents" || (Face == HexFace.Noon && noonFleet.Visible);
        if (looked && agentDataAge > AGENT_DATA_FRESH)
            MeasureAgentData();
    }

    private void MeasureAgentData()
    {
        if (agentDataMeasuring)
            return;
        agentDataMeasuring = true;
        agentDataAge = 0.0f;
        InBackground("measure agent data", () => HexDataTidy.Measure(DateTime.UtcNow), usage =>
        {
            agentDataMeasuring = false;
            agentData = usage;
            foreach (var (item, label) in agentDataItems)
                item.Label = label(usage);
            if (noonAgentData is not null)
            {
                noonAgentData.Label = $"$(database) {Said("agent_data.noon", ("size", HexDataTidy.Bytes(usage.Total)))}";
                noonAgentData.Tooltip = Said("agent_data.noon_tip", ("chats", usage.ChatCount.ToString()), ("images", usage.ImageCount.ToString()), ("folder", HexData.Root))
                    + (HexData.Fallback ? "\n" + Said("agent_data.fallback") : string.Empty);
            }
            Engine.WindowManager.RequestFrames();
        }, quiet: true);
    }

    // A folder of the data opened in the system's file manager, made first so there's something to open
    private void OpenDataFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
            if (HexData.Fallback)
                Say(Said("agent_data.fallback"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Say(Said("agent_data.open_failed", ("why", e.Message)), error: true);
        }
    }

    // The keys of this workspace's chats that can be closed for being old: not the open one, not running, and with every
    // chat they started closable too (closing a chat closes those)
    private HashSet<string> ClosableChats()
    {
        var closable = new HashSet<string>(StringComparer.Ordinal);
        bool Closable(HexAgentChat chat) => chat != agentChat && !chat.Running && agentChats.Where(each => each.Parent == chat.Id).All(Closable);
        foreach (HexAgentChat chat in agentChats)
        {
            if (Closable(chat))
                closable.Add(chat.Key);
        }

        return closable;
    }

    /// <summary>Delete Old Chats: how many each age would take found, then asked which, the open chat and running ones kept.</summary>
    private void AskDeleteOldChats()
    {
        HashSet<string> closable = ClosableChats();
        string? open = chatsKept?.Folder;
        DateTime now = DateTime.UtcNow;
        InBackground("find old chats", () => OLD_CHAT_DAYS.Select(days => HexDataTidy.OldChats(HexData.AgentChats, now.AddDays(-days), open, closable)).ToArray(), found =>
        {
            var offered = OLD_CHAT_DAYS.Zip(found).Where(each => each.Second.Count > 0).ToArray();
            if (offered.Length == 0)
            {
                Say(Said("agent_data.no_old_chats", ("days", OLD_CHAT_DAYS[^1].ToString())));
                return;
            }

            // Cancel last, where the dialog puts the keyboard and Escape, and the most it would take in red
            string[] buttons = [.. offered.Select(each => Said("agent_data.delete_older", ("days", each.First.ToString()))), Said("agent_data.cancel")];
            string counts = string.Join("\n", offered.Select(each => Said(each.Second.Count == 1 ? "agent_data.old_line_one" : "agent_data.old_line",
                ("days", each.First.ToString()), ("count", each.Second.Count.ToString()), ("size", HexDataTidy.Bytes(each.Second.Sum(chat => chat.Size))))));
            Context.Windows.Ask(Said("agent_data.chats_title"), counts + "\n\n" + Said("agent_data.chats_text"), buttons, answer =>
            {
                int picked = Array.IndexOf(buttons, answer);
                if (picked >= 0 && picked < offered.Length)
                    DeleteOldChats(offered[picked].Second, open);
            }, risky: buttons[^2]);
        }, quiet: true);
    }

    // The old chats of the open workspace closed here, which takes their files with them, the other workspaces' deleted
    private void DeleteOldChats(List<HexOldChat> chats, string? open)
    {
        HashSet<string> closable = ClosableChats();
        bool OpenFolder(HexOldChat chat) => open is not null && string.Equals(Path.GetFullPath(chat.Folder), Path.GetFullPath(open), Horizon.Core.Platform.PlatformPaths.Comparison);
        var keys = chats.Where(OpenFolder).Select(chat => chat.Key).ToHashSet(StringComparer.Ordinal);
        int closed = 0;
        foreach (HexAgentChat chat in agentChats.Where(chat => keys.Contains(chat.Key)).ToList())
        {
            // Closing one closes what it started, those must be old and closable too
            if (!agentChats.Contains(chat) || !closable.Contains(chat.Key) || agentChats.Any(each => each.Parent == chat.Id && !keys.Contains(each.Key)))
                continue;
            CloseAgentChat(chat);
            closed++;
        }

        if (closed > 0)
        {
            KeepChats();
            agentsListDrawn = NOT_DRAWN;
            noonFleetDrawn = NOT_DRAWN;
        }

        List<HexOldChat> others = [.. chats.Where(chat => !OpenFolder(chat))];
        InBackground("delete old chats", () => HexDataTidy.DeleteChats(others), gone =>
        {
            Say(Counted("agent_data.deleted_chats", gone + closed));
            MeasureAgentData();
        }, quiet: true);
    }

    /// <summary>Delete Unused Pictures: the pasted pictures no chat mentions found, then asked about.</summary>
    private void AskDeleteUnusedImages()
    {
        string[] keep = [.. agentImages];
        DateTime now = DateTime.UtcNow;
        InBackground("find unused pictures", () => HexDataTidy.UnusedImages(HexData.AgentImages, [HexData.AgentChats, HexData.AgentHistory], keep, now), unused =>
        {
            if (unused.Count == 0)
            {
                Say(Said("agent_data.no_unused_images"));
                return;
            }

            long size = unused.Sum(file => new FileInfo(file) is { Exists: true } info ? info.Length : 0);
            string go = Said("agent_data.images_go");
            Context.Windows.Ask(Counted("agent_data.images_title", unused.Count), Said("agent_data.images_text", ("size", HexDataTidy.Bytes(size))),
                [go, Said("agent_data.cancel")], answer =>
                {
                    if (answer == go)
                        DeleteDataFiles(unused, "agent_data.deleted_images");
                }, risky: go);
        }, quiet: true);
    }

    /// <summary>Clear Temporary Files: the agents' files not written in the last minutes found, then asked about.</summary>
    private void AskClearTemp()
    {
        DateTime now = DateTime.UtcNow;
        InBackground("find temporary files", () => HexDataTidy.OldTemp([HexData.AgentTemp, HexData.OldAgentTemp], now), old =>
        {
            if (old.Count == 0)
            {
                Say(Said("agent_data.no_temp"));
                return;
            }

            long size = old.Sum(file => new FileInfo(file) is { Exists: true } info ? info.Length : 0);
            string go = Said("agent_data.temp_go");
            Context.Windows.Ask(Said("agent_data.temp_title"), Said("agent_data.temp_text", ("count", old.Count.ToString()), ("size", HexDataTidy.Bytes(size))),
                [go, Said("agent_data.cancel")], answer =>
                {
                    if (answer == go)
                        DeleteDataFiles(old, "agent_data.cleared_temp");
                }, risky: go);
        }, quiet: true);
    }

    // A text said with how many, its _one for just the one
    private string Counted(string key, int count) => count == 1 ? Said(key + "_one") : Said(key, ("count", count.ToString()));

    private void DeleteDataFiles(List<string> files, string said) =>
        InBackground("tidy agent data", () => HexDataTidy.DeleteFiles(files), gone =>
        {
            Say(Counted(said, gone));
            MeasureAgentData();
        }, quiet: true);
}
