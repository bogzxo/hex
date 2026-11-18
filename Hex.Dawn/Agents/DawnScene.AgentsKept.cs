using System.Text.Json.Nodes;

namespace Hex.Dawn;

// The chats of the workspace kept on disk as they go (HexAgentChatsKept), a few seconds apart and once more as Hex closes,
// and read back when Hex starts on it, so closing Hex, a crash or the machine going down loses nothing of them. Another
// workspace opened has its own chats, swapped in once none of this one's agents is working.
internal sealed partial class DawnScene
{
    // How often the chats that moved are written
    private const float CHATS_KEPT_EVERY = 3.0f;

    private HexAgentChatsKept? chatsKept;
    private float chatsKeptIn = CHATS_KEPT_EVERY;

    // Where every workspace's chats are
    private static string ChatsKeptRoot => Path.Combine(TerminalFiles.UserFolder, "agent_chats");

    // The folder for the workspace open now
    private string ChatsKeptFolder => HexAgentChatsKept.FolderFor(ChatsKeptRoot, explorer.workspace.Folders.FirstOrDefault());

    /// <summary>
    /// The workspace's chats read back as Hex starts. Not in a self test, which has chats of its own, and not after a
    /// restart into a new build, whose chats the Hex before handed over as they were at that moment.
    /// </summary>
    private void BringBackChats()
    {
        if (options.SelfTest || options.Check.Length > 0)
            return;

        chatsKept = new HexAgentChatsKept(ChatsKeptFolder);
        hub.History = chatsKept.History;
        if (!ResumingFromDev)
            TakeKeptChats();
    }

    // The kept chats put in the panel, the one that was open opened
    private void TakeKeptChats()
    {
        var (chats, state) = chatsKept!.Read();
        foreach (HexAgentChat chat in chats)
        {
            if (ChatById(chat.Id) is null)
                agentChats.Add(chat);
            agentChatsMade = Math.Max(agentChatsMade, chat.Id);
        }

        agentChatsMade = Math.Max(agentChatsMade, state?["made"]?.GetValue<int>() ?? 0);
        if (state?["open"]?.GetValue<int>() is { } open && ChatById(open) is { } opened)
            agentChat = opened;
        agentsListDrawn = string.Empty;
        agentsDrawn = (-1, -1);
    }

    /// <summary>The chats that moved written every few seconds, and another workspace's swapped in once nothing works.</summary>
    private void UpdateChatsKept(float dt)
    {
        if (chatsKept is null || (chatsKeptIn -= dt) > 0.0f)
            return;
        chatsKeptIn = CHATS_KEPT_EVERY;

        KeepChats();
        if (ChatsKeptFolder is var wanted && wanted != chatsKept.Folder && !agentChats.Any(chat => chat.Running))
            SwapKeptChats(wanted);
    }

    /// <summary>The chats written now, the newest of them up to the most a workspace keeps.</summary>
    private void KeepChats()
    {
        if (chatsKept is null)
            return;

        List<HexAgentChat> over = HexAgentChatsKept.PastMost(agentChats);
        IReadOnlyList<HexAgentChat> kept = over.Count == 0 ? agentChats : [.. agentChats.Where(chat => !over.Contains(chat))];
        chatsKept.Keep(kept, new JsonObject
        {
            ["open"] = agentChat?.Id,
            ["made"] = agentChatsMade
        });
    }

    // This workspace's chats kept and put away, and the other one's brought out, their older rows left in their files
    private void SwapKeptChats(string folder)
    {
        hub.History = null;
        foreach (HexAgentChat chat in agentChats.ToList())
            agentChats.Remove(chat);
        agentChat = null;
        agentDraftContext = null;
        agentsInput.Text = string.Empty;

        chatsKept = new HexAgentChatsKept(folder);
        hub.History = chatsKept.History;
        TakeKeptChats();
    }

#if DEBUG
    // A restart into a build Hex made hands its chats over in a file of its own
    private static bool ResumingFromDev => System.Environment.GetEnvironmentVariable(DEV_RESUME) is { Length: > 0 };
#else
    private static bool ResumingFromDev => false;
#endif
}
