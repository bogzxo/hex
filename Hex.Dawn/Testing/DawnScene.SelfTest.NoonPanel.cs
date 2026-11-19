using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The side bar's Agents view and Agentic are one view of the one chat. A chat picked in the side bar is the one in
// Agentic, one picked in Agentic's fleet is the one in the side bar after, and a question answered in Agentic is
// answered in the side bar too, no longer wanting looking at.
internal sealed partial class DawnScene
{
    private void TestNoonPanel(HexPointerScript test)
    {
        HexAgentChat? asking = null, other = null;
        string? told = null;

        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            asking = new HexAgentChat(++agentChatsMade, "noon_agent");
            asking.Ask("ask me something");
            asking.Question("Which way?", ["Left", "Right"], 1, 300, answer => told = answer);
            other = new HexAgentChat(++agentChatsMade, "noon_agent");
            other.Ask("another chat");
            other.Say("the other one");
            other.Done();
            agentChats.Add(asking);
            agentChats.Add(other);
            ShowAgentsPanel();
            OpenAgentChat(asking);
        });
        test.Settle(0.4f);
        test.Check("noon panel: a question waiting in the side bar has its answers to press", () =>
            Descendants(agentsScroll).OfType<Button>().Any(button => button.Label.StartsWith("Right", StringComparison.Ordinal)) && asking!.Pending.Any());

        test.Run(() =>
        {
            ShowFace(HexFace.Noon);
            noonWait = 0;
        });
        test.Settle(0.4f);
        test.Check("noon panel: the chat picked in the side bar is the one in Agentic", () => agentChat == asking && agentsView.Parent == noonCenter);
        test.Run(() => Descendants(agentsScroll).OfType<Button>().First(button => button.Label == "Left").OnPressed!());
        test.Settle(0.4f);
        test.Check("noon panel: answered in Agentic, the agent is told and nothing waits any more", () => told == "Left" && !asking!.Pending.Any());

        // Picked in the fleet, by its card's press
        test.Run(() =>
        {
            int at = ChatsInOrder().FindIndex(each => each.Chat == other);
            Descendants(noonFleetRows.Children[at]).OfType<Button>().First().OnPressed!();
        });
        test.Settle(0.3f);
        test.Check("noon panel: a chat picked in the fleet is the one in the middle", () => agentChat == other);
        test.Run(() => ShowFace(HexFace.Dawn));
        test.Settle(0.4f);
        test.Check("noon panel: and the one in the side bar back in Dawn", () =>
            agentChat == other && agentsView.Parent == left && Descendants(agentsScroll).OfType<Label>().Any(label => label.Text.Contains("the other one", StringComparison.Ordinal)));
        test.Run(() => OpenAgentChat(asking!));
        test.Settle(0.4f);
        test.Check("noon panel: the question answered in Agentic says its answer in the side bar", () =>
            Descendants(agentsScroll).OfType<Label>().Any(label => label.Text.Contains(Said("agents_panel.question_answered", ("answer", "Left")), StringComparison.Ordinal)));

        // Details' files: long shared folders and the same name in two folders, each told apart by its name and nearest folder
        string[] touched =
        [
            "src/Hex.Dawn/Testing/DawnScene.SelfTest.NoonPanel.cs", "src/Hex.Dawn/Testing/DawnScene.SelfTest.NoonLooks.cs", "src/Hex.Dawn/Testing/DawnScene.SelfTest.NoonKeys.cs",
            "src/Hex.Dawn/Noon/Readme.md", "src/Hex.Dawn/Agents/Readme.md", "a/very/long/shared/prefix/that/goes/on/and/on/for/ages/Program.cs", "docs/Program.cs"
        ];
        Button[] buttons = [];
        test.Run(() =>
        {
            for (int at = 0; at < touched.Length; at++)
            {
                asking!.Tool("file-" + at, "Read", $"{{\"file_path\":\"{touched[at]}\"}}");
                asking.ToolDone("file-" + at, true, "Done");
            }

            ShowFace(HexFace.Noon);
            OpenAgentChat(asking!);
            noonContextDrawn = CONTEXT_NOT_DRAWN;
            noonWait = 0;
        });
        test.Settle(0.6f);
        test.Run(() => buttons = [.. Descendants(noonContextRows).OfType<Button>().Where(button => touched.Any(file => button.Tooltip is { } tip && tip.EndsWith(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)))]);
        test.Check("noon files: every touched file keeps its whole name, none cut to a shared prefix", () =>
            buttons.Length == touched.Length && touched.All(file => buttons.Count(button => button.Label.StartsWith(Path.GetFileName(file), StringComparison.Ordinal)) >= 1));
        test.Check("noon files: the same name in two folders shows the folder that tells them apart, and the rows are all different", () =>
            buttons.Select(button => button.Label).Distinct().Count() == touched.Length
            && buttons.Count(button => button.Label.StartsWith("Readme.md", StringComparison.Ordinal) && (button.Label.Contains("Noon") || button.Label.Contains("Agents"))) == 2
            && buttons.Count(button => button.Label.StartsWith("Program.cs", StringComparison.Ordinal) && (button.Label.Contains("docs") || button.Label.Contains("ages"))) == 2);
        // A row's text as the button measures it, its icon and label at its scale and the room its style leaves, against its width
        bool Fits(Button row) => Skin is { } skin
            && skin.Font.Measure($"$({row.Icon}) {row.Label}", row.LabelScale).X
                + (skin.TryGetRegion(row.Style, out var art) ? art.ContentOr(skin.ButtonPadding) : skin.ButtonPadding).Total.X <= row.Bounds.Width + 0.5f;
        test.Check("noon files: the rows are plain text, the name then a dot then the folder, no markup, and each fits its row", () =>
            buttons.All(button => !button.Label.Contains('[') && !button.Label.Contains("color") && button.Bounds.Width > 0 && Fits(button))
            && buttons.Count(button => button.Label.Contains(FILE_DOT, StringComparison.Ordinal)) >= 4);
        test.Check("noon files: the tooltip is the full path and pressing a row goes to the file's Dawn", () =>
            buttons.All(button => button.Tooltip is { Length: > 0 } tip && touched.Any(file => tip.Replace('\\', '/').EndsWith(file, StringComparison.OrdinalIgnoreCase)) && button.OnPressed is not null));

        // Real files in two folders that differ only far up, the shared tail too long for the row: the ones that differ stay
        string fixture = Path.Combine(testFolder, "noon-files-" + Guid.NewGuid().ToString("N"));
        string[] twins = [Path.Combine(fixture, "a", "x", "SharedLongFolderNameThatIsVeryLong", "Program.cs"), Path.Combine(fixture, "b", "x", "SharedLongFolderNameThatIsVeryLong", "Program.cs")];
        test.Check("noon files: the folders that tell same-named files apart, the one that differs alone when they don't fit, the name cut only when it must", () =>
        {
            // Every character 8 pixels wide, so the room is what's asked
            float Wide(string text) => text.Length * 8.0f;
            string[] twin = ["a/x/SharedLongFolderNameThatIsVeryLong/Program.cs", "b/x/SharedLongFolderNameThatIsVeryLong/Program.cs"];
            string[] one = ["src/Noon/Readme.md", "src/Agents/Readme.md", "src/DawnScene.SelfTest.NoonPanel.cs"];
            string tail = Path.Combine("a", "x", "SharedLongFolderNameThatIsVeryLong");
            // Room for it all: the folders up to the one that differs, and the nearest folder only where nothing shares the name
            bool all = FileRowParts(twin[0], twin, Wide, 700) == ("Program.cs", tail)
                && FileRowParts(one[2], one, Wide, 700) == ("DawnScene.SelfTest.NoonPanel.cs", "src")
                && FileRowParts(one[0], one, Wide, 700) == ("Readme.md", "Noon");
            // Room for the name and one folder: the one that differs, not b\...\...VeryLong
            bool alone = FileRowParts(twin[0], twin, Wide, 150) == ("Program.cs", "a") && FileRowParts(twin[1], twin, Wide, 150) == ("Program.cs", "b");
            // No room for a folder of a name that's alone, and the name whole when it fits the row on its own
            bool bare = FileRowParts(one[2], one, Wide, 31 * 8) == ("DawnScene.SelfTest.NoonPanel.cs", string.Empty);
            // The name itself too long: cut in the middle, its end and extension kept, in the room
            var (cut, kept) = FileRowParts(one[2], one, Wide, 20 * 8);
            // One very long folder that differs: it's cut and the name stays whole, the two together in the room
            string[] deep = [new string('f', 40) + "/x/Program.cs", new string('g', 40) + "/x/Program.cs"];
            var (farName, farWhere) = FileRowParts(deep[0], deep, Wide, 24 * 8);
            bool folderCut = farName == "Program.cs" && farWhere.Length is >= 6 and < 40 && farWhere.Contains("...", StringComparison.Ordinal)
                && Wide(farName + FILE_DOT + farWhere) <= 24 * 8;
            return all && alone && folderCut && bare && cut.Length < "DawnScene.SelfTest.NoonPanel.cs".Length && cut.EndsWith("Panel.cs", StringComparison.Ordinal) && cut.Contains("...", StringComparison.Ordinal)
                && Wide(kept.Length > 0 ? cut + FILE_DOT + kept : cut) <= 20 * 8;
        });
        test.Run(() =>
        {
            foreach (string twin in twins)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(twin)!);
                File.WriteAllText(twin, "// noon files fixture\n");
            }

            for (int at = 0; at < twins.Length; at++)
            {
                asking!.Tool("twin-" + at, "Read", new System.Text.Json.Nodes.JsonObject { ["file_path"] = twins[at] }.ToJsonString());
                asking.ToolDone("twin-" + at, true, "Done");
            }

            noonContextDrawn = CONTEXT_NOT_DRAWN;
            noonWait = 0;
        });
        test.Settle(0.6f);
        test.Run(() => Shot("noon-files"));
        Button? twinB = null;
        test.Run(() => twinB = Descendants(noonContextRows).OfType<Button>().FirstOrDefault(button => button.Tooltip == twins[1]));
        test.Check("noon files: the twins' rows are both there, tell a from b and carry their full paths", () =>
            twinB is not null && Descendants(noonContextRows).OfType<Button>().Any(button => button.Tooltip == twins[0]) && twinB.Label.Contains("Program.cs") && twinB.Label != Descendants(noonContextRows).OfType<Button>().First(button => button.Tooltip == twins[0]).Label);
        test.Run(() => twinB!.OnPressed!());
        test.WaitFor(() => Face == HexFace.Dawn && document.Path is { } opened && string.Equals(Path.GetFullPath(opened), Path.GetFullPath(twins[1]), StringComparison.OrdinalIgnoreCase), 10);
        test.Check("noon files: pressing the b row goes to Dawn with that real file open, not its twin", () =>
            Face == HexFace.Dawn && document.Path is { } opened && string.Equals(Path.GetFullPath(opened), Path.GetFullPath(twins[1]), StringComparison.OrdinalIgnoreCase));

        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            try { Directory.Delete(fixture, true); } catch (IOException) { }
            CloseAgentChat(asking!);
            CloseAgentChat(other!);
        });
        test.Settle(0.2f);
    }
}
