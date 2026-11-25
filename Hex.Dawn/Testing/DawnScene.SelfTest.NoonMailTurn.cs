namespace Hex.Dawn;

// An instruction for an agent whose turn is said over while its process is still going (Done before the process
// exited, or its checkpoint being taken) is kept once in its mail and starts its next turn once, not mailed again by
// StartAgent. What the person types for such an agent is still kept as an instruction of its own.
internal sealed partial class DawnScene
{
    private void TestNoonMailTurn(HexPointerScript test)
    {
        HexExtension? provider = null;
        HexAgentChat? lead = null, scout = null;
        string told = string.Empty;
        const string SAID = "run the parser tests once";

        test.Run(() =>
        {
            string folder = Path.Combine(testFolder, "turn_fixture");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, HexExtension.FILE), """
                let extension = { name: "Turn fixture" };
                func on_start(hex) {
                    hex.agent({ id: "turn_fixture", name: "Turn fixture", run: { program: "hex-turn-fixture-missing" } });
                }
                """);
            provider = new HexExtension(folder, builtIn: false) { Enabled = true };
            extensions.Add(provider);
            extensions.Start(provider);
            lead = new HexAgentChat(++agentChatsMade, "turn_fixture");
            lead.Ask("coordinate");
            lead.Done();
            scout = new HexAgentChat(++agentChatsMade, "turn_fixture") { Parent = lead.Id, Depth = 1, Name = "Scout" };
            scout.Ask("look round");
            scout.Done();
            agentChats.Add(lead);
            agentChats.Add(scout);
            // Its turn said over, its run still there: starting, which is what AgentRunBusy answers for with no process
            agentsStarting.Add(scout);
            told = Deliver(scout, lead, "instruction", SAID, directive: true);
        });
        test.Check("noon mail turn: an instruction for an agent still going is kept once and says it's queued", () =>
            told.Contains("next turn starts on it", StringComparison.Ordinal) && scout!.Mail.Count == 1 && scout.UnreadMail().Length == 1 && scout.Entries.Count(entry => entry.Kind == HexAgentEntryKind.User) == 1
            && hub.MailOf(scout).All.Count(entry => entry.Mail.Text == SAID) == 1 && hub.MailOf(scout).All[^1].Route == HexMailRoute.Inbox);

        // What the person types meanwhile is its own instruction, not the one already kept
        test.Run(() => StartAgent(scout!, extensions.AgentRuns["turn_fixture"].Extension, extensions.AgentRuns["turn_fixture"].Run, "and the lexer ones", "and the lexer ones"));
        test.Check("noon mail turn: what the person types for it is kept as well", () =>
            scout!.Mail.Count == 2 && scout.Mail[1].From is null && scout.Mail[1].Text == "and the lexer ones" && scout.Mail[0].Text == SAID);

        // The run gone, it starts on both, each said once
        test.Run(() =>
        {
            agentsStarting.Remove(scout!);
            AfterAgentTurn(scout!);
        });
        test.Check("noon mail turn: its next turn has the instruction exactly once", () =>
        {
            string asked = scout!.Entries.Last(entry => entry.Kind == HexAgentEntryKind.User).Text;
            int first = asked.IndexOf(SAID, StringComparison.Ordinal);
            return first >= 0 && asked.IndexOf(SAID, first + 1, StringComparison.Ordinal) < 0 && scout.UnreadMail().Length == 0;
        });
        test.WaitFor(() => !scout!.Running && !agentsStarting.Contains(scout), 20.0f);

        // A message of any kind starts a chat that isn't working, a stopped one too, one turn for the messages that arrived
        // together (after the debounce), and the answer says so
        string resumedFinished = string.Empty, resumedStopped = string.Empty;
        int asksBefore = 0;
        test.Run(() =>
        {
            scout!.Done();
            asksBefore = scout.Entries.Count(entry => entry.Kind == HexAgentEntryKind.User);
            resumedFinished = Deliver(scout, lead, "progress", "first of two", directive: false);
            Deliver(scout, lead, "finding", "second of two", directive: false);
        });
        test.Check("noon mail turn: a progress message to a finished chat says it resumed it with the message", () =>
            resumedFinished.Contains("resumed it with your message", StringComparison.Ordinal));
        test.WaitFor(() => scout!.Entries.Count(entry => entry.Kind == HexAgentEntryKind.User) > asksBefore, 10.0f);
        test.Check("noon mail turn: messages that came together start one turn with both", () =>
        {
            string asked = scout!.Entries.Last(entry => entry.Kind == HexAgentEntryKind.User).Text;
            return asked.Contains("first of two", StringComparison.Ordinal) && asked.Contains("second of two", StringComparison.Ordinal)
                && scout.Entries.Count(entry => entry.Kind == HexAgentEntryKind.User) == asksBefore + 1 && scout.UnreadMail().Length == 0;
        });
        test.WaitFor(() => !scout!.Running && !agentsStarting.Contains(scout), 20.0f);
        test.Run(() =>
        {
            scout!.Done();
            scout.Stopped = true;
            asksBefore = scout.Entries.Count(entry => entry.Kind == HexAgentEntryKind.User);
            resumedStopped = Deliver(scout, lead, "answer", "wake up after the stop", directive: false);
        });
        test.WaitFor(() => scout!.Entries.Count(entry => entry.Kind == HexAgentEntryKind.User) > asksBefore, 10.0f);
        test.Check("noon mail turn: a stopped chat resumes on a message too", () =>
            resumedStopped.Contains("resumed it with your message", StringComparison.Ordinal) && !scout!.Stopped
            && scout.Entries.Last(entry => entry.Kind == HexAgentEntryKind.User).Text.Contains("wake up after the stop", StringComparison.Ordinal));
        test.WaitFor(() => !scout!.Running && !agentsStarting.Contains(scout), 20.0f);

        test.Run(() =>
        {
            agentChats.Remove(lead!);
            agentChats.Remove(scout!);
            extensions.ForgetAgents(provider!);
            HexAgentProfiles.Forget(provider!.Id);
        });
    }
}
