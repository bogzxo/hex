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

        // The row of the ones switched off says what they're called, a toggle each, and gives the right one's folder to the settings
        string Dolly() => extensions.All.First(extension => extension.Id == "hello_dolly").Name;
        test.Run(() => OpenSettings(EXTENSIONS));
        test.Settle(0.3f);
        test.Check("extensions: the row of the ones switched off says their names, not their folders", () =>
            SettingsPage!.Settings.First(setting => setting.Key == "disabled_extensions").Choices is { } listed
            && listed.Length == extensions.All.Count && listed.Contains(Dolly()) && (Dolly() == "hello_dolly" || !listed.Contains("hello_dolly")));
        test.Run(() => SettingsPage!.Enter("disabled_extensions", new ListValue([new StringValue(Dolly())])));
        test.Wait();
        test.Check("extensions: switched off on its page of the settings, its greeting goes and it stays off", () =>
            titleNote.Length == 0 && !TitleNow().Contains('(') && settings.DisabledExtensions.Contains("hello_dolly")
            && extensions.All.First(extension => extension.Id == "hello_dolly") is { Enabled: false }
            && SettingsPage!.IsModified?.Invoke("disabled_extensions") == true);
        test.Run(() => SettingsPage!.Enter("disabled_extensions", new ListValue([])));
        test.Wait();
        test.Check("extensions: and on again", () => !settings.DisabledExtensions.Contains("hello_dolly") && extensions.All.First(extension => extension.Id == "hello_dolly") is { Enabled: true });
        test.Run(() => CloseSettings());

        TestExtensionSettingRows(test);

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

    private void TestExtensionSettingRows(HexPointerScript test)
    {
        /* The settings an extension describes are rows of the settings window like the rest, its help under the name, a
           reset while it's changed, and found by the search. An extension of the test's own with one of each kind, which
           writes down what it's told */

        const string LOUDNESS = "extension_told_extension_loudness", GAME = "extension_told_extension_game";
        HexExtension? told = null;
        test.Run(() =>
        {
            string folder = Path.Combine(testFolder, "told_extension");
            Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(Path.Combine(folder, HexExtension.FILE), """
                let extension = {
                    name: "Told",
                    settings: {
                        loudness: { title: "Told loudness", about: "How loud the told extension shouts.", kind: "number", default: 3 },
                        game: { title: "Told game", about: "The exe the told extension starts.", kind: "path", default: "" },
                        shout: { title: "Told shout", about: "Whether it shouts at all.", kind: "flag", default: false },
                        style: { title: "Told style", about: "The way it says things.", kind: "choice", choices: ["calm", "sharp"],
                            labels: ["Calm", "Sharp as a tack"], help: ["Quiet and slow.", "Short and quick."], default: "calm" },
                        volume: { title: "Told volume", about: "How far up it goes.", kind: "number", least: 0, most: 10, step: 1, slider: true, default: 5 }
                    }
                };
                let heard = [];
                func on_setting(hex, name, value) { heard.push(name); heard.push(value); }
                """);
            told = new HexExtension(folder, builtIn: false) { Enabled = true };
            extensions.Add(told);
            extensions.Start(told);
            OpenSettings(EXTENSION_SECTION + "Told");
        });
        test.Settle(0.3f);

        // Shown when it and everything it's in are, the search hides a row and a section that hasn't one left hides itself
        bool Shown(UIComponent component)
        {
            for (UIComponent? at = component; at is not null && at != SettingsPage; at = at.Parent)
            {
                if (!at.Visible)
                    return false;
            }

            return true;
        }

        // What a row says without the accent a search lights the words it found in
        bool LabelShown(string text) => Descendants(SettingsPage!).OfType<Label>().Any(label => label.Text.Replace("[color:accent]", "").Replace("[/color]", "") == text && Shown(label));
        bool Heard(string name, IRuntimeValue value) => told is { Failed: null, Runtime: { } runtime } && runtime.UserScope.Lookup("heard") is ListValue { Count: >= 2 } heard
            && heard[^2] is StringValue { Value: var said } && said == name && SettingValues.Same(heard[^1], value);

        test.Run(() => SettingsPage!.Find("told loudn"));
        test.Wait();
        test.Check("extensions: an extension's setting is a row of the settings like any other, found by the search", () =>
            SettingsPage!.Settings.FirstOrDefault(setting => setting.Key == LOUDNESS) is { Kind: UIFormKind.Number, Group: EXTENSION_SECTION + "Told", Help: "How loud the told extension shouts." }
            && SettingsPage.RowsShown == 1 && LabelShown("[b]Told loudness[/b]") && LabelShown("How loud the told extension shouts."));

        test.Run(() =>
        {
            SettingsPage!.Find(string.Empty);
            SettingsPage.Enter(LOUDNESS, new NumberValue(7));
        });
        test.Wait();
        test.Check("extensions: changed on its row, it's kept, the extension is told and the row has its reset", () =>
            extensions.SettingOf(told!, "loudness") is NumberValue { Value: 7 } && settings.ExtensionSettings["told_extension"].ContainsKey("loudness")
            && Heard("loudness", new NumberValue(7)) && SettingsPage!.IsModified?.Invoke(LOUDNESS) == true);

        test.Run(() => SettingsPage!.Reset(LOUDNESS));
        test.Wait();
        test.Check("extensions: its reset puts the extension's own default back and tells it", () =>
            extensions.SettingOf(told!, "loudness") is NumberValue { Value: 3 } && Heard("loudness", new NumberValue(3))
            && SettingsPage!.IsModified?.Invoke(LOUDNESS) == false);

        // A choice is a dropdown of its own labels and lines of help, one that isn't among them is left out, and a number
        // with ends is kept between them
        const string STYLE = "extension_told_extension_style", VOLUME = "extension_told_extension_volume";
        test.Run(() =>
        {
            var source = new ExtensionSettingsSource(this);
            source.Set(STYLE, new StringValue("sharp"));
            source.Set(STYLE, new StringValue("shouty"));
            source.Set(VOLUME, new NumberValue(99));
        });
        test.Wait();
        test.Check("extension settings: choice kind", () =>
            SettingsPage!.Settings.FirstOrDefault(setting => setting.Key == STYLE) is { Kind: UIFormKind.Choice, Choices: ["calm", "sharp"], ChoiceLabels: ["Calm", "Sharp as a tack"] } style
            && style.HelpOf("sharp") == "Short and quick." && extensions.SettingOf(told!, "style") is StringValue { Value: "sharp" }
            && SettingsPage.Settings.FirstOrDefault(setting => setting.Key == VOLUME) is { Kind: UIFormKind.Number, Least: 0.0f, Most: 10.0f, Slider: true }
            && extensions.SettingOf(told!, "volume") is NumberValue { Value: 10.0f });

        // A path has Choose... beside its box, Windows' dialog in a run of Hex and a box to type it into while it tests itself
        string picked = Path.GetFullPath(Path.Combine(testFolder, "told_extension", "told_game.exe"));
        test.Run(() =>
        {
            // Searched for, its row is the only one with a Choose... showing, the picture behind the code has one too
            System.IO.File.WriteAllText(picked, string.Empty);
            SettingsPage!.Find("told game");
            Descendants(SettingsPage).OfType<Button>().First(button => button.Label == Said("extension_settings.choose") && Shown(button)).OnPressed!();
        });
        test.Wait();
        test.Check("extensions: a path's Choose... asks which file", () => textDialog is { IsOpen: true });
        test.Run(() => (textDialog is { IsOpen: true } asked ? Descendants(asked.Body).OfType<TextBox>().FirstOrDefault() : null)?.OnSubmitted?.Invoke(picked));
        test.Wait();
        test.Check("extensions: and fills its row with it", () =>
            extensions.SettingOf(told!, "game") is StringValue { Value: var game } && game == picked && Heard("game", new StringValue(picked))
            && Descendants(SettingsPage!).OfType<TextBox>().Any(box => box.Text == picked) && SettingsPage!.IsModified?.Invoke(GAME) == true);

        // Dawn's size, on the same page, has its reset while it isn't the one the window says
        float sizeWas = 1.0f;
        test.Run(() =>
        {
            sizeWas = compositor.Scale;
            SettingsPage!.Find(string.Empty);
            SettingsPage.Enter("size", new StringValue(DescribeScale(UsualScale == 2.0f ? 1.5f : 2.0f)));
        });
        test.Wait();
        test.Check("settings: Dawn's size other than the window's has a reset", () => compositor.Scale != UsualScale && SettingsPage!.IsModified?.Invoke("size") == true);
        test.Run(() => SettingsPage!.Reset("size"));
        test.Wait();
        test.Check("settings: which puts the window's back", () => compositor.Scale == UsualScale && SettingsPage!.IsModified?.Invoke("size") == false);

        test.Run(() =>
        {
            SetScale(sizeWas, remember: false);
            CloseSettings();
            extensions.Remove(told!);
            settings.ExtensionSettings.Remove("told_extension");
            SaveSettings();
        });
        test.Wait();
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
