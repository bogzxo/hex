using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// The self test, what an extension can ask (a menu, a question, a name), keys on its commands, on_change, and an
// extension read again when its file changes, without Hex being started again.
internal sealed partial class HexScene
{
    private const string ASKING_EXTENSION = """
        let extension = { name: "Asking" };
        let answers = [];
        let changed = [];
        let picked = [];
        func on_start(hex) {
            hex.command("Ask me", func(hex) {
                hex.ask({ title: "Well?", text: "go on then", buttons: ["Yes", "No"] }, func(hex, said) { answers.push(said); });
            }, "Ctrl+Alt+J");
            hex.command("Name it", func(hex) {
                hex.prompt({ title: "Called?", value: "first", placeholder: "a name" }, func(hex, said) { answers.push(said); });
            });
            hex.command("Menu", func(hex) {
                hex.menu([
                    { label: "Pick me", icon: "check", on_pressed: func(hex) { picked.push("me"); } },
                    "-",
                    { label: "More", items: [{ label: "Deeper", on_pressed: func(hex) { picked.push("deeper"); } }] },
                    { label: "Not now", enabled: false }
                ]);
            });
        }
        func on_change(hex, file) { changed.push(file); }
        """;

    private void TestExtensionPrompts(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "asking_extension");
        HexExtension? asking = null;
        IRuntimeValue? Their(string name) => asking?.Runtime?.UserScope.Lookup(name);
        void Command(string name) => extensions.Commands().First(command => command.Text.StartsWith($"Asking: {name}", StringComparison.Ordinal)).Picked();

        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, HexExtension.FILE), ASKING_EXTENSION);
            asking = new HexExtension(folder, builtIn: false) { Enabled = true };
            extensions.Add(asking);
            extensions.Start(asking);
        });

        // Watched once the file it was made with is written and done with, or that's a change to it
        test.Settle(0.5f);
        test.Run(extensions.WatchFolders);
        test.Wait();
        test.Check("extensions: a command on a key says the key in the palette, and the key reads as control, alt and J", () =>
            asking is { Failed: null } && extensions.Commands().Any(command => command.Text == "Asking: Ask me   (Ctrl+Alt+J)")
            && UIKeys.Read("Ctrl+Alt+J") == (Key.J, true, false, true) && UIKeys.Read("Shift+F6") == (Key.F6, false, true, false) && UIKeys.Read("Ctrl+Nope") is null);

        // hex.ask, the dialog with its buttons, the one pressed handed to the function
        test.Run(() => Command("Ask me"));
        test.Wait();
        test.Check("extensions: hex.ask puts a dialog up with the buttons it was given", () =>
            textDialog is { IsOpen: true } asked && asked.Buttons.Count == 2 && asked.Buttons[1].Label == "No");

        // It pops up, its buttons only where they'll be once it has, and the click takes it down
        test.Settle(0.35f);
        System.Numerics.Vector2 no = default;
        test.Run(() => no = chrome.ToWorld(textDialog!.Buttons[1].Bounds.Center));
        test.Click(() => no);
        test.Wait();
        test.Check("extensions: and the button pressed is handed to its function", () =>
            Their("answers") is ListValue { Count: 1 } answers && answers[0] is StringValue { Value: "No" } && textDialog is not { IsOpen: true });

        // hex.prompt, a line of text asked for, what was typed handed over
        test.Run(() => Command("Name it"));
        test.Wait();
        test.Run(() =>
        {
            TextBox box = Descendants(textDialog!.Body).OfType<TextBox>().First();
            box.Text = "second";
            box.OnSubmitted!(box.Text);
        });
        test.Wait();
        test.Check("extensions: hex.prompt asks for a line of text, starting with what it said, and hands over what was typed", () =>
            Their("answers") is ListValue { Count: 2 } answers && answers[1] is StringValue { Value: "second" });

        // hex.menu at the pointer, an item picked, one inside a menu of its own, one that can't be
        test.Run(() => Command("Menu"));
        test.Wait();
        test.Check("extensions: hex.menu puts a menu up at the pointer, its separator and its menu inside and the item that can't be picked greyed", () =>
            contextMenu?.Showing is { } shown && shown.Items.Count == 4 && shown.Items[1].IsSeparator && shown.Items[2].Submenu is { Items.Count: 1 }
            && shown.Items[3].IsEnabled?.Invoke() == false);
        PickFromMenu("Pick me");
        test.Wait();
        test.Run(() => Command("Menu"));
        test.Wait();
        PickFromMenu("Deeper");
        test.Wait();
        test.Check("extensions: and what's picked out of it, out of the menu inside too, calls its on_pressed", () =>
            Their("picked") is ListValue { Count: 2 } picked && picked[0] is StringValue { Value: "me" } && picked[1] is StringValue { Value: "deeper" });

        // on_change, once typing stops, not as a file is opened
        string typed = Path.Combine(testFolder, "selftest_changed.hor");
        test.Run(() =>
        {
            File.WriteAllText(typed, "let changing = 1;\n");
            Open(typed);
        });
        test.Settle(0.5f);
        test.Check("extensions: on_change isn't told about a file just opened", () => Their("changed") is ListValue { Count: 0 });
        test.Run(() =>
        {
            ShowCode();
            document.Editor.Document.MoveTo(new UITextPosition(0, 0));
            document.Editor.Document.Insert("// ");
        });
        test.Settle(0.5f);
        test.Check("extensions: on_change hears about a file a moment after it was typed into, with its path", () =>
            Their("changed") is ListValue { Count: 1 } changed && changed[0] is StringValue { Value: var path } && path == Path.GetFullPath(typed));

        // Read again without a restart when its file changes, its commands those of the new one
        test.Run(() => File.WriteAllText(Path.Combine(folder, HexExtension.FILE),
            ASKING_EXTENSION.Replace("\"Ask me\"", "\"Ask me again\"", StringComparison.Ordinal)));
        test.WaitFor(() => extensions.Commands().Any(command => command.Text.StartsWith("Asking: Ask me again", StringComparison.Ordinal)), 4.0f);
        test.Check("extensions: changing an extension's file reads it again, its commands the new ones and the old one gone", () =>
            asking is { Failed: null } && extensions.Commands().Any(command => command.Text == "Asking: Ask me again   (Ctrl+Alt+J)")
            && !extensions.Commands().Any(command => command.Text.StartsWith("Asking: Ask me   ", StringComparison.Ordinal))
            && Their("answers") is ListValue { Count: 0 });

        test.Run(() =>
        {
            if (extensions.extensionWatchers.Remove(asking!, out FileSystemWatcher? watcher))
                watcher.Dispose();
            extensions.Remove(asking!);
            CloseNow();
        });
        test.Wait();
    }
}
