namespace Hex.Dawn;

// A message for an agent that takes more as it works goes to its run with whatever it hadn't read before it, oldest
// first, and is read only once written. One that kept an earlier message in its inbox (it couldn't be told more then) is
// not passed over by the next, and a write that fails leaves everything unread in its mail.
internal sealed partial class DawnScene
{
    // A program that echoes each line of its stdin back with "got " in front until an empty one or the end, and one that
    // exits at once, as a cmd script on Windows and a sh script elsewhere. Started the way each platform starts a script.
    private static (string Program, string[] Arguments) SteerFixture(string folder, string name, string[] body)
    {
        if (OperatingSystem.IsWindows())
        {
            string cmd = Path.Combine(folder, name + ".cmd");
            File.WriteAllText(cmd, "@echo off\r\n" + string.Join("\r\n", body) + "\r\n");
            return (cmd, []);
        }

        string sh = Path.Combine(folder, name + ".sh");
        File.WriteAllText(sh, string.Join("\n", body) + "\n");
        return ("/bin/sh", [sh]);
    }

    private void TestNoonMailSteer(HexPointerScript test)
    {
        HexExtension? provider = null;
        HexAgentChat? lead = null, scout = null, gone = null;
        HexAgentProcess? live = null, dead = null;
        string told = string.Empty, failed = string.Empty;

        test.Run(() =>
        {
            string folder = Path.Combine(testFolder, "steer_fixture");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, HexExtension.FILE), """
                let extension = { name: "Steer fixture" };
                func on_start(hex) {
                    hex.agent({ id: "steer_fixture", name: "Steer fixture", run: { program: "hex-steer-fixture-missing", message: (text, chat) => text } });
                }
                """);
            provider = new HexExtension(folder, builtIn: false) { Enabled = true };
            extensions.Add(provider);
            extensions.Start(provider);
            lead = new HexAgentChat(++agentChatsMade, "steer_fixture");
            lead.Ask("coordinate");
            lead.Done();
            scout = new HexAgentChat(++agentChatsMade, "steer_fixture") { Parent = lead.Id, Depth = 1, Name = "Scout" };
            scout.Ask("look round");
            gone = new HexAgentChat(++agentChatsMade, "steer_fixture") { Parent = lead.Id, Depth = 1, Name = "Gone" };
            gone.Ask("look elsewhere");
            agentChats.Add(lead);
            agentChats.Add(scout);
            agentChats.Add(gone);

            var echo = OperatingSystem.IsWindows()
                ? SteerFixture(folder, "steered", [":loop", "set line=", "set /p line=", "if not defined line exit /b 0", "echo got %line%", "goto loop"])
                : SteerFixture(folder, "steered", ["while IFS= read -r line; do", "  [ -z \"$line\" ] && exit 0", "  echo \"got $line\"", "done"]);
            var quick = OperatingSystem.IsWindows()
                ? SteerFixture(folder, "quick", ["exit /b 0"])
                : SteerFixture(folder, "quick", ["exit 0"]);
            live = HexAgentProcess.Start(echo.Program, echo.Arguments, folder, "start\n", keepInput: true);
            dead = HexAgentProcess.Start(quick.Program, quick.Arguments, folder, null, keepInput: true);
        });

        // Waited for on the tick, not spun on, so the window keeps drawing
        test.WaitFor(() => dead?.Exited == true, 20.0f);

        test.Run(() =>
        {
            if (live is null || dead is null || !extensions.AgentRuns.TryGetValue("steer_fixture", out var found))
                return;

            // Kept in its inbox earlier, when it couldn't be told more, and never read
            var older = new HexAgentMail(hub.NextMailSeq(), lead!.Id, MailName(lead), "finding", "the older message", false) { To = scout!.Id };
            scout.Post(older);
            hub.MailOf(scout).Add(older, HexMailRoute.Inbox);

            // Registered only for the calls, so the tick doesn't take the lines its output makes, and taken out again
            // whatever happens in them
            var steered = new AgentRun(scout, live, found.Extension, found.Run, ChatObject(scout, found.Extension), "steered", true);
            var broken = new AgentRun(gone!, dead, found.Extension, found.Run, ChatObject(gone!, found.Extension), "quick", true);
            try
            {
                agentRuns.Add(steered);
                told = Deliver(scout, lead, "instruction", "the newer instruction", directive: true);
                agentRuns.Remove(steered);

                // Gone while its stdin is still open to Hex: the write fails and the message stays in its mail
                agentRuns.Add(broken);
                failed = Deliver(gone!, lead, "instruction", "nobody reads this yet", directive: true);
            }
            finally
            {
                agentRuns.Remove(steered);
                agentRuns.Remove(broken);
            }
        });
        test.WaitFor(() => live?.Lines.Any(line => line.Line.Contains("the newer instruction", StringComparison.Ordinal)) == true, 20.0f);
        test.Check("noon mail steer: a successful steer hands the run the older unread message first and then the new one", () =>
        {
            if (live is null || scout is null)
                return false;
            string output = string.Join('\n', live.Lines.Select(line => line.Line));
            int first = output.IndexOf("the older message", StringComparison.Ordinal);
            return first >= 0 && output.IndexOf("the newer instruction", StringComparison.Ordinal) > first
                && told.Contains("takes it in", StringComparison.Ordinal) && scout.UnreadMail().Length == 0
                && hub.MailOf(scout).All.Last(entry => entry.Mail.Text == "the newer instruction").Route == HexMailRoute.Steered;
        });
        test.Check("noon mail steer: a write that fails leaves the message unread in its inbox", () =>
            gone is not null && failed.Length > 0 && !failed.Contains("takes it in", StringComparison.Ordinal)
            && gone.UnreadMail() is [{ Text: "nobody reads this yet" }]
            && hub.MailOf(gone).All.Last(entry => entry.Mail.Text == "nobody reads this yet").Route == HexMailRoute.Inbox && !gone.Entries.Any(entry => entry.Queued));

        // Whatever the checks said, nothing of the test is left running or registered
        test.Run(() =>
        {
            live?.CloseInput();
            live?.Dispose();
            dead?.Dispose();
            agentRuns.RemoveAll(run => run.Chat == scout || run.Chat == gone);
            foreach (HexAgentChat? chat in new[] { lead, scout, gone })
            {
                if (chat is not null)
                    agentChats.Remove(chat);
            }

            if (provider is not null)
            {
                extensions.ForgetAgents(provider);
                HexAgentProfiles.Forget(provider.Id);
            }
        });
    }
}
