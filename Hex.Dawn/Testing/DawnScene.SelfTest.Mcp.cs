using System.Text.Json.Nodes;

namespace Hex.Dawn;

// The self test of the MCP server. It's put up in this Hex, a client comes in at its pipe's door with the token from its
// file the way Hex --mcp does, and calls the tools the way an agent would, a screenshot, the list of what's on screen,
// a click on the File menu, typing into a file it opened, a command, the problems of a file that doesn't parse, waiting,
// and each time what the editor really did is checked, not only what the tool said.
internal sealed partial class DawnScene
{
    private void TestMcp(HexPointerScript test)
    {
        string mcpFile = Path.Combine(testFolder, "selftest_mcp.hor"), mcpBroken = Path.Combine(testFolder, "selftest_mcp_broken.hor");
        HexMcpTestClient? client = null;
        McpListing? listing = null;
        Task<JsonObject>? answer = null;
        string? refused = null;

        // A call made and waited for, the answer in answer for the checks after it
        void Ask(string tool, JsonObject? arguments = null, float timeout = 15.0f)
        {
            test.Run(() => answer = client?.Call(tool, arguments));
            test.WaitFor(() => answer?.IsCompleted != false, timeout);
        }

        string Answered() => HexMcpTestClient.TextOf(answer);

        test.Run(() =>
        {
            // A dialog an earlier part left up takes every click, the File menu's too, which is what it should do
            chrome.Dialog?.Close();
            File.WriteAllText(mcpFile, "let speed = 3;\nlet names = {\n    first: \"one\"\n};\n");
            File.WriteAllText(mcpBroken, "let = 3;\n");
            StartMcp();
            listing = McpDiscovery.Of(System.Environment.ProcessId);
        });
        test.Check("mcp: the server is up, its file in the settings folder says its pipe and a token", () =>
            listing is { Token.Length: 64 } && listing.Pipe == McpDiscovery.PipeOf(System.Environment.ProcessId) && File.Exists(Path.Combine(McpDiscovery.Folder, $"{listing.Pid}.json")));

        // The wrong token isn't let in, the right one is, off the editor's thread as a client would be
        Task<HexMcpTestClient?>? connecting = null;
        test.Run(() => connecting = Task.Run(() =>
        {
            using (HexMcpTestClient.Connect(listing!, "not the token", out refused)) { }
            return HexMcpTestClient.Connect(listing!, listing!.Token, out _);
        }));
        test.WaitFor(() => connecting?.IsCompleted == true, 10.0f);
        test.Run(() => client = connecting!.IsCompletedSuccessfully ? connecting.Result : null);
        test.Check("mcp: a client with the wrong token is turned away and one with the right token gets in", () => refused is not null && client is not null);

        test.Run(() => answer = client?.Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-03-26",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "selftest", ["version"] = "1" }
        }));
        test.WaitFor(() => answer?.IsCompleted != false, 10.0f);
        test.Run(() => client?.Notify("notifications/initialized"));
        test.WaitFor(() => McpClients == 1 && mcpItem.IsVisible?.Invoke() == true, 3.0f);
        test.Check("mcp: initialize settles on the older version the client asked for, and the status bar says an agent is in", () =>
            answer is { IsCompletedSuccessfully: true } && answer.Result["result"]?["protocolVersion"]?.GetValue<string>() == "2025-03-26"
            && answer.Result["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "hex" && McpClients == 1 && mcpItem.IsVisible?.Invoke() == true
            && mcpItem.Text.Contains(Hex.Shell.HexText.Get("mcp.status"), StringComparison.Ordinal));

        test.Run(() => answer = client?.Request("tools/list"));
        test.WaitFor(() => answer?.IsCompleted != false, 10.0f);
        test.Check("mcp: tools/list has every tool, each with an object schema", () =>
            answer is { IsCompletedSuccessfully: true } && answer.Result["result"]?["tools"] is JsonArray tools && tools.Count == McpTools.Tools.Count
            && tools.All(tool => tool?["inputSchema"]?["type"]?.GetValue<string>() == "object" && tool["name"]!.GetValue<string>().StartsWith("hex_", StringComparison.Ordinal)));

        Ask("hex_screenshot", new JsonObject { ["scale"] = 0.25 });
        test.Check("mcp: a screenshot is a PNG of the window, a quarter of its size", () =>
            answer is { IsCompletedSuccessfully: true } && answer.Result["result"]?["content"] is JsonArray content
            && content.FirstOrDefault(item => item?["type"]?.GetValue<string>() == "image") is { } image
            && Convert.FromBase64String(image["data"]!.GetValue<string>()) is var png && png.Length > 1000 && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G'
            && Answered().Contains($"{Engine.WindowManager.ViewportSize.X:0}x{Engine.WindowManager.ViewportSize.Y:0}", StringComparison.Ordinal));

        Ask("hex_ui_tree", new JsonObject { ["interactive"] = true });
        test.Check("mcp: the ui tree lists the menu bar's titles and the status bar's items with ids", () =>
            Answered() is var tree && tree.Contains("MenuTitle \"File\"", StringComparison.Ordinal) && tree.Contains(" StatusItem ", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.IsMatch(tree, @"\bc\d+ "));

        // A click on the File title opens its menu, a real click of the agent's pointer, and Escape shuts it
        Ask("hex_click", new JsonObject { ["text"] = "File" });
        test.Run(() =>
        {
            if (menu.OpenMenu?.Title != "File")
                Console.WriteLine($"[Hex self-test]   the click said {Answered()}");
        });
        test.Check("mcp: clicking File by its text opens the File menu", () => menu.OpenMenu?.Title == "File" && !HexMcpTestClient.Failed(answer));
        Ask("hex_key", new JsonObject { ["keys"] = "Escape" });
        test.Check("mcp: Escape shuts it again", () => menu.OpenMenu is null);

        Ask("hex_open", new JsonObject { ["path"] = mcpFile, ["line"] = 4, ["column"] = 3 });
        test.Check("mcp: open puts the file up at the line asked for", () =>
            document.Path is { } open && PlatformPaths.Same(open, mcpFile) && code.Document.Caret == new UITextPosition(3, 2) && !HexMcpTestClient.Failed(answer));

        Ask("hex_type", new JsonObject { ["text"] = "\nlet typed = 7;" });
        test.Check("mcp: typing goes into the code at the caret, as if typed", () => code.Text.Contains("};\nlet typed = 7;", StringComparison.Ordinal) && document.Modified);

        Ask("hex_editor", new JsonObject { ["start_line"] = 5, ["end_line"] = 5 });
        test.Check("mcp: the editor tool reads the line back with the caret", () => Answered().Contains("let typed = 7;", StringComparison.Ordinal) && Answered().Contains("Ln 5, Col 15", StringComparison.Ordinal));

        Ask("hex_outline");
        test.Check("mcp: the outline has what the file declares, the typed line too", () => Answered().Contains("speed", StringComparison.Ordinal) && Answered().Contains("typed", StringComparison.Ordinal));

        // A shortcut through the engine's keyboard, saving the file
        Ask("hex_key", new JsonObject { ["keys"] = "Ctrl+S" });
        test.WaitFor(() => !document.Modified, 3.0f);
        test.Check("mcp: Ctrl+S pressed by the agent saves the file", () => !document.Modified && File.ReadAllText(mcpFile).Contains("let typed = 7;", StringComparison.Ordinal));

        Ask("hex_run_command", new JsonObject { ["name"] = "View: Problems" });
        test.Check("mcp: a command run by its name does what it does, the Problems panel up", () => bottom.Shown && bottom.Selected == PROBLEMS_PAGE && !HexMcpTestClient.Failed(answer));

        Ask("hex_open", new JsonObject { ["path"] = mcpBroken });
        Ask("hex_wait_for", new JsonObject { ["condition"] = "problems", ["value"] = "selftest_mcp_broken.hor", ["timeout"] = 8.0 }, 15.0f);
        test.Check("mcp: waiting for a file's problems comes back once they're there", () => !HexMcpTestClient.Failed(answer));
        Ask("hex_problems", new JsonObject { ["path"] = mcpBroken });
        test.Check("mcp: and the problems tool lists it with its line", () => Answered().Contains("selftest_mcp_broken.hor:1:", StringComparison.Ordinal) && Answered().Contains(" error ", StringComparison.Ordinal));

        Ask("hex_wait_for", new JsonObject { ["condition"] = "text", ["value"] = "selftest_mcp.hor", ["timeout"] = 3.0 });
        test.Check("mcp: waiting for text on screen comes back at once when it's there (a tab)", () => !HexMcpTestClient.Failed(answer));
        Ask("hex_wait_for", new JsonObject { ["condition"] = "text", ["value"] = "nothing says this anywhere 7f3a", ["timeout"] = 0.3 });
        test.Check("mcp: and fails after its timeout when it never comes", () => HexMcpTestClient.Failed(answer) && Answered().Contains("wasn't true", StringComparison.Ordinal));

        Ask("hex_key", new JsonObject { ["keys"] = "Ctrl+Shift+P" });
        test.Check("mcp: Ctrl+Shift+P from the agent puts the command palette up", () => chrome.Dialog is { IsOpen: true });
        Ask("hex_key", new JsonObject { ["keys"] = "Escape" });
        test.WaitFor(() => chrome.Dialog is not { IsOpen: true }, 2.0f);
        test.Check("mcp: and Escape takes it away", () => chrome.Dialog is not { IsOpen: true });

        Ask("hex_click", new JsonObject { ["id"] = "c99999999" });
        test.Check("mcp: an id that isn't on screen is the tool failing, said so", () => HexMcpTestClient.Failed(answer) && Answered().Contains("isn't on screen", StringComparison.Ordinal));
        test.Run(() => answer = client?.Call("hex_open", new JsonObject { ["nonsense"] = 1 }));
        test.WaitFor(() => answer?.IsCompleted != false, 5.0f);
        test.Check("mcp: arguments a tool doesn't take come back as its failure, with what it does take", () =>
            HexMcpTestClient.Failed(answer) && Answered().Contains("'path' is required", StringComparison.Ordinal));

        // Hung up on, the status bar's item goes, and the server goes with its file when it's switched off
        test.Run(() => client?.Dispose());
        test.WaitFor(() => McpClients == 0 && mcpItem.IsVisible?.Invoke() == false, 3.0f);
        test.Check("mcp: a client hanging up takes the status bar's item with it", () => McpClients == 0 && mcpItem.IsVisible?.Invoke() == false);
        test.Run(() =>
        {
            StopMcp();
            foreach (HexDocument open in documents.Where(open => open.Path is { } path && (PlatformPaths.Same(path, mcpFile) || PlatformPaths.Same(path, mcpBroken))).ToList())
                CloseDocument(open);
        });
        test.Wait();
        test.Check("mcp: switched off, its file is gone from the settings folder", () => McpDiscovery.Of(System.Environment.ProcessId) is null);
    }
}
