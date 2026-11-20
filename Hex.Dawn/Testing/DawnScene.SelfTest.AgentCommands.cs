using System.Numerics;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    // What's typed in the agents' box, the caret at its end as typing leaves it, which is where the slash list looks
    private void TypeInAgentBox(string text)
    {
        agentsInput.Text = text;
        agentsInput.Document.MoveTo(new Horizon.UI.Components.UITextPosition(0, text.Length));
    }

    private void TestAgentCommands(HexPointerScript test)
    {
        HexExtension? provider = null;
        ObjectValue run = default;
        string? picked = agentPicked;
        HexAgentChat? previous = agentChat;
        IRuntimeValue Catalog(string description) => new ListValue([.. Enumerable.Range(0, 32).Select(i =>
            HexJson.Parse($"{{\"name\":\"task-{i:00}\",\"about\":\"{description} {i:00}\",\"hint\":\"[path]\"}}")!)]);
        test.Run(() =>
        {
            string folder = Path.Combine(testFolder, "slash_fixture");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, HexExtension.FILE), """
                let extension = { name: "Slash fixture" };
                func on_start(hex) {
                    hex.agent({ id: "slash_fixture", name: "Slash fixture", run: { program: "hex-command-fixture-missing", commands: [] } });
                }
                """);
            provider = new HexExtension(folder, builtIn: false) { Enabled = true };
            extensions.Add(provider);
            extensions.Start(provider);
            run = extensions.AgentRuns["slash_fixture"].Run;
            run.Properties["commands"] = Catalog("Description");
            NewAgentChat();
            agentPicked = "slash_fixture";
            ShowAgentsPanel();
            TypeInAgentBox("/task");
            agentsInput.Focus();
        });
        test.Settle(0.4f);
        test.Check("commands: every match is retained and overflow has a scrollbar", () =>
            agentsSlashRows.Count == 32 && agentsSlash is { MaxOffset: > 0 } && !agentsSlash.TrackBounds.IsEmpty);
        test.Check("commands: descriptions have their own visible labels", () =>
            agentsSlashRows[0].Button.Children.OfType<Label>().Any(each => each.Text.Contains("Description 00")));
        test.Run(() => Shot("agent-command-menu"));
        test.Settle(0.2f);
        test.Run(() => agentsSlash!.Wheel(-2));
        test.Settle(0.5f);
        test.Check("commands: wheel scrolls the command list while preserving the draft", () => agentsSlash!.Offset > 0 && agentsInput.Text == "/task");
        test.Drag(() => chrome.ToWorld(agentsSlash!.ThumbBounds.Center),
            () => chrome.ToWorld(new Vector2(agentsSlash!.TrackBounds.Center.X, agentsSlash.TrackBounds.Min.Y + 4)));
        test.Settle(0.2f);
        test.Check("commands: dragging the scrollbar reaches the later commands", () => agentsSlash!.Offset > agentsSlash.MaxOffset / 2);
        test.Run(() => { agentsSlash!.Offset = 0; agentsInput.Focus(); });
        for (int i = 0; i < 12; i++) PressKeys(test, "Down");
        test.Settle(0.2f);
        test.Check("commands: arrows select beyond the eighth row and reveal the selection", () =>
            agentsSlashSelected == 12 && agentsSlash!.Offset > 0 && agentsSlashRows[12].Button.Selected);
        PressKeys(test, "Tab");
        test.Settle(0.2f);
        test.Check("commands: Tab inserts the selected command and keeps focus", () => agentsInput.Text == "/task-12 " && compositor.Focus == agentsInput && agentsSlash is null);
        test.Run(() => { TypeInAgentBox("/task"); agentsInput.Focus(); });
        test.Settle(0.2f);
        test.Run(() => run.Properties["commands"] = Catalog("Updated description"));
        test.Settle(0.2f);
        test.Check("commands: late metadata refreshes without another keystroke", () =>
            agentsSlashRows[0].Button.Children.OfType<Label>().Any(each => each.Text.Contains("Updated description")));
        PressKeys(test, "Escape");
        test.Settle(0.2f);
        test.Check("commands: Escape dismisses the menu without erasing the draft", () => agentsSlash is null && agentsInput.Text == "/task");
        test.Run(() => { TypeInAgentBox("/task-31"); agentsInput.Focus(); });
        test.Settle(0.2f);
        PressKeys(test, "Enter");
        test.Settle(0.2f);
        // Written out in full, Enter sends it rather than completing it to itself (TakeFirstSlash); the fixture's program
        // isn't there, so nothing runs and the draft stays
        test.Check("commands: Enter on a command written out in full doesn't complete it to itself, nothing run without its program", () =>
            agentsInput.Text == "/task-31" && agentRuns.Count == 0);
        test.Run(() => { agentsInput.Text = "/help"; SendToAgent(); });
        test.Settle(0.2f);
        test.Check("commands: help executes locally and lists the last command", () =>
            agentChat is { Running: false } && agentChat.Entries.Any(each => each.Text.Contains("/task-31")) && agentRuns.Count == 0);
        test.Run(() =>
        {
            Shot("agent-commands");
            if (agentChat is { } made) agentChats.Remove(made);
            agentChat = previous;
            agentPicked = picked;
            agentsInput.Text = string.Empty;
            extensions.ForgetAgents(provider!);
            HexAgentProfiles.Forget(provider!.Id);
        });
    }
}
