using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The self test, extensions, Hello Dolly and the tabs of the settings extensions have.
internal sealed partial class DawnScene
{
    // The greeting extension's command as the palette has it, under whatever bogz has called it this week
    private string DollyCommand() => $"{extensions.All.First(extension => extension.Id == "hello_dolly").Name}: Another greeting";

    private void TestExtensions(HexPointerScript test)
    {
        /* Extensions. Hello Dolly comes with Hex and puts a greeting in the title bar as it starts */

        string TitleNow() => ((TitleBar)top).Title;
        test.Check("extensions: Hello Dolly is loaded and started, a greeting of its own after Hex in the title bar", () =>
            extensions.All.FirstOrDefault(extension => extension.Id == "hello_dolly") is { Enabled: true, Failed: null, Runtime: { } runtime }
            && runtime.UserScope.Lookup("welcome_greetings") is ListValue greetings && greetings.Items.Any(said => said is StringValue { Value: var text } && text == titleNote)
            && titleNote.Length > 0 && TitleNow().Contains($"Hex {HexAbout.Version} ({titleNote})", StringComparison.Ordinal));

        test.Run(() => OpenSettings(EXTENSIONS));
        test.Settle(0.3f);
        test.Run(() => SettingsPage!.Enter("disabled_extensions", new ListValue([new StringValue("hello_dolly")])));
        test.Wait();
        test.Check("extensions: switched off on its page of the settings, its greeting goes and it stays off", () =>
            titleNote.Length == 0 && !TitleNow().Contains('(') && settings.DisabledExtensions.Contains("hello_dolly")
            && extensions.All.First(extension => extension.Id == "hello_dolly") is { Enabled: false });
        test.Run(() => SettingsPage!.Enter("disabled_extensions", new ListValue([])));
        test.Wait();
        test.Check("extensions: and on again", () => !settings.DisabledExtensions.Contains("hello_dolly") && extensions.All.First(extension => extension.Id == "hello_dolly") is { Enabled: true });
        test.Run(() => CloseSettings());

        HexExtension? broken = null;
        test.Run(() =>
        {
            string folder = Path.Combine(testFolder, "broken_extension");
            Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(Path.Combine(folder, HexExtension.FILE), "let extension = { name: \"Broken\" };\nfunc on_start(hex) { hex.nothing_of_the_sort(); }\n");
            broken = new HexExtension(folder, builtIn: false) { Enabled = true };
            extensions.Start(broken);
        });
        test.Wait();
        test.Check("extensions: one that throws in on_start says why in the output and Hex carries on", () =>
            broken is { Failed: not null } && said.Count > 0 && said[^1].Error && said[^1].Text.Contains("Broken", StringComparison.Ordinal));
        test.Check("extensions: and puts a card up in the corner about it, with a way to its page", () =>
            toasts.Cards.Count > 0 && toasts.Cards[^1] is { Icon: "error", Actions.Count: 1 } card && card.Text.Contains("Broken", StringComparison.Ordinal)
            && toasts.Bounds.Width > 0 && toasts.Bounds.Max.X <= chrome.Root.Bounds.Max.X);

        Vector2 closeCard = Vector2.Zero;
        test.Run(() => closeCard = toasts.Module!.ToWorld(new Vector2(toasts.Bounds.Max.X - 12 - 7, toasts.Bounds.Max.Y - 12 - 7)));
        test.Click(() => closeCard);
        test.WaitFor(() => toasts.Cards.Count == 0, 2.0f);
        test.Check("extensions: its x takes the card away", () => toasts.Cards.Count == 0);

        test.Check("extensions: Hello Dolly's command for another greeting is in the command palette", () =>
            extensions.Commands().Any(command => command.Text == DollyCommand()));
        test.Run(() =>
        {
            titleNote = string.Empty;
            extensions.Commands().First(command => command.Text == DollyCommand()).Picked();
        });
        test.Wait();
        test.Check("extensions: and picked, there is a greeting again", () => titleNote.Length > 0 && TitleNow().Contains($"({titleNote})", StringComparison.Ordinal));

        // Quick open with a > in front is the commands, the way VS Code's is
        test.Run(QuickOpen);
        test.Wait();
        test.Run(() =>
        {
            TextBox box = Descendants(textDialog!.Body).OfType<TextBox>().First();
            box.Text = ">anothergreeting";
            box.OnChanged!(box.Text);
        });
        test.Wait();
        test.Check("quick open: > lists the commands, the extensions' among them, narrowed by what follows it", () =>
            Descendants(textDialog!.Body).OfType<ListBox>().First().Items is [var only, ..] && only == DollyCommand());
        test.Run(() => textDialog!.Close());
        test.Wait();

        // An extension hears about a file that is opened
        HexExtension? listening = null;
        test.Run(() =>
        {
            string folder = Path.Combine(testFolder, "listening_extension");
            Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(Path.Combine(folder, HexExtension.FILE), "let extension = { name: \"Listening\" };\nlet opened = [];\nfunc on_open(hex, file) { opened.push(file); hex.say(`opened {len(opened)}`); }\n");
            listening = new HexExtension(folder, builtIn: false) { Enabled = true };
            extensions.Add(listening);
            extensions.Start(listening);
            System.IO.File.WriteAllText(Path.Combine(testFolder, "selftest_heard.hor"), "let heard = 1;");
            Open(Path.Combine(testFolder, "selftest_heard.hor"));
        });
        test.Wait();
        test.Check("extensions: on_open hears about a file opened, with its path", () =>
            listening is { Failed: null, Runtime: { } runtime } && runtime.UserScope.Lookup("opened") is ListValue { Count: 1 } opened
            && opened[0] is StringValue { Value: var file } && file == Path.GetFullPath(Path.Combine(testFolder, "selftest_heard.hor")));
        test.Run(() =>
        {
            extensions.Remove(listening!);
            CloseNow();
        });
    }

    private void TestExtensionTabs(HexPointerScript test)
    {
        /* The sections extensions have in the settings window. The move editor has a layout of its own there, which keeps
           what it's told through hex.set_setting */

        UIScriptedLayout? MoveSettings() => settingsMade.FirstOrDefault(made => made.Owner.Name == "Move editor");
        HexExtension MoveEditor() => extensions.All.First(extension => extension.Name == "Move editor");
        test.Run(() => OpenSettings(EXTENSION_SECTION + "Move editor"));
        test.Wait();
        test.Check("settings: a section of the window for every extension with something to set, the move editor's its own layout", () =>
            SettingsPage is not null && MoveSettings() is { } page && page.Made.TryGet<TextBox>("game_path", out _));
        test.Check("settings: its own layout, filled in from the game a move file was last opened in", () =>
            extensions.SettingOf(MoveEditor(), "last_game") is StringValue { Value: var root } && Path.GetFullPath(root) == Path.GetFullPath(game)
            && MoveSettings()!.Made.Get<Dropdown>("opponent").Options.Contains("tester")
            && MoveSettings()!.Made.Get<Label>("found").Text.StartsWith("There is no", StringComparison.Ordinal));

        test.Run(() =>
        {
            SettingsPage!.GoTo(EXTENSION_SECTION + "Move editor");
            TypeInto(MoveSettings()!.Made.Get<TextBox>("game_path"), "bin/fighter.exe");
        });
        test.Wait();
        test.Check("settings: what is typed into it is kept by Hex, a relative path from the game's folder", () =>
            extensions.SettingOf(MoveEditor(), "game") is StringValue { Value: "bin/fighter.exe" } && settings.ExtensionSettings["move_editor"].ContainsKey("game")
            && MoveSettings()!.Made.Get<Label>("found").Text.Contains("bin/fighter.exe", StringComparison.Ordinal));

        test.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(game, "bin"));
            System.IO.File.WriteAllText(Path.Combine(game, "bin", "fighter.exe"), string.Empty);
            HexExtension extension = MoveEditor();
            extensions.SetSetting(extension, "game", new StringValue("bin/fighter.exe"), tell: true);
            TypeInto(MoveSettings()!.Made.Get<TextBox>("game_path"), "");
            TypeInto(MoveSettings()!.Made.Get<TextBox>("game_path"), "bin/fighter.exe");
        });
        test.Wait();
        test.Check("settings: and it says when the exe is there", () =>
            MoveSettings()!.Made.Get<Label>("found").Text.StartsWith("Fight! starts", StringComparison.Ordinal));

        // Look asks for the exe, Windows' dialog in a run of Hex and a box to type it into while it tests itself
        test.Run(() => MoveSettings()!.Made.Get<TextBox>("game_path").PressButton());
        test.Wait();
        test.Check("settings: Look asks where the exe is", () => textDialog is { IsOpen: true });
        test.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(game, "build"));
            System.IO.File.WriteAllText(Path.Combine(game, "build", "Fighter2D.exe"), string.Empty);
            Descendants(textDialog!.Body).OfType<TextBox>().First().OnSubmitted!(Path.Combine(game, "build", "Fighter2D.exe"));
        });
        test.Wait();
        test.Check("settings: and what was picked inside the game is kept from the game's folder", () =>
            extensions.SettingOf(MoveEditor(), "game") is StringValue { Value: "build/Fighter2D.exe" }
            && MoveSettings()!.Made.Get<TextBox>("game_path").Text == "build/Fighter2D.exe"
            && MoveSettings()!.Made.Get<Label>("found").Text.StartsWith("Fight! starts", StringComparison.Ordinal));

        test.Run(() =>
        {
            settings.ExtensionSettings.Remove("move_editor");
            CloseSettings();
        });
        test.Wait();
        test.Check("settings: and the sections go with the window", () => settingsMade.Count == 0 && extensions.SettingOf(MoveEditor(), "game") is StringValue { Value: "" });

        // A setting written and read back the way the settings file keeps it, a path with its backslashes among them
        test.Check("settings: the settings of extensions are kept in the extensions section of the settings file and read back as they were", () =>
        {
            string file = Path.Combine(testFolder, "selftest_settings.hor");
            var kept = new HexSettings(file);
            kept.ExtensionSettings["hitboxes"] = new Dictionary<string, IRuntimeValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["game"] = new StringValue(@"C:\Games\Fighter2D\Fighter2D.exe"),
                ["dummy"] = new StringValue("block")
            };
            kept.ExtensionSettings["other"] = new Dictionary<string, IRuntimeValue>(StringComparer.OrdinalIgnoreCase) { ["loud"] = Values.Bool(true), ["volume"] = new NumberValue(0.5f) };
            if (kept.Save() is not null)
                return false;

            var read = new HexSettings(file);
            return read.ExtensionSettings["hitboxes"]["game"] is StringValue { Value: @"C:\Games\Fighter2D\Fighter2D.exe" }
                && read.ExtensionSettings["hitboxes"]["dummy"] is StringValue { Value: "block" }
                && read.ExtensionSettings["other"]["loud"] is BooleanValue { Value: true } && read.ExtensionSettings["other"]["volume"] is NumberValue { Value: 0.5f };
        });
    }
}
