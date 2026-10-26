using System.Numerics;

using Horizon.Content;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// The self test, the texts of a layout. F12 and Ctrl+F12 from what's selected to its declaration and to its key in the
// game's language, F12 on a key in the code, and a double click on the canvas editing a text, a key's in the language's
// file and anything else's in the layout, each a step to undo.
internal sealed partial class DawnScene
{
    // A game of its own in a folder of its own, a layout next to its languages the way a game has them. Away from the
    // test's folder, a localisation folder next to that would have every other layout of the test reading its keys
    private static string TextsGame => Path.Combine(Path.GetTempPath(), "horizon-hex-selftest-texts", "ui");

    private const string TEXTS_LAYOUT = "// texts, laid out with Hex.\n"
        + "// Load it with UILayout.Load, every component is there under the name it has here.\n#define layout\n\n"
        + "let play_button = compositor.button({\n    pos: vec(0, 220),\n    size: vec(240, 60),\n    label: \"@menu.play\"\n});\n\n"
        + "let fresh_button = compositor.button({\n    pos: vec(-420, 220),\n    size: vec(240, 60),\n    label: \"@menu.fresh\"\n});\n\n"
        + "let plain_label = compositor.label({\n    pos: vec(0, 100),\n    size: vec(240, 40),\n    text: \"Plain words\"\n});\n\n"
        + "let pages = compositor.tabs({\n    pos: vec(0, -160),\n    size: vec(520, 240),\n    tabs: [\"@menu.first_tab\", \"Two\"]\n});\n\n"
        + "let page_one = compositor.stack({ parent: pages });\n\n"
        + "let page_two = compositor.stack({ parent: pages });\n";

    private const string TEXTS_LANGUAGE = "// The test game's texts, the comments stay where they are\n"
        + "let strings = {\n"
        + "    menu: {\n"
        + "        // the big one\n"
        + "        play: \"Play\", // green\n"
        + "        first_tab: \"First\"\n"
        + "    }\n"
        + "};\n";

    private void TestLayoutTexts(HexPointerScript test)
    {
        string layoutFile = Path.Combine(TextsGame, "layouts", "texts.hor");
        string languageFile = Path.Combine(TextsGame, "localisation", "en-gb.hor");
        Func<UIComponent?> Named(string name) => () => document.Find(name);
        TabPanel? Pages() => document.Find("pages") as TabPanel;
        string Language() => File.ReadAllText(languageFile);
        string CaretLine() => code.Document.LineAt(code.Document.Caret.Line);

        // Two clicks in the same place, quicker than anybody's double click
        void DoubleClick(Func<Vector2> at)
        {
            test.Click(at);
            test.Click(at);
            test.Wait();
        }

        test.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(layoutFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(languageFile)!);
            File.WriteAllText(layoutFile, TEXTS_LAYOUT);
            File.WriteAllText(languageFile, TEXTS_LANGUAGE);
            languagesNear.Clear();
            Open(layoutFile);
        });
        test.Settle(0.3f);
        test.Check("texts: a layout's keys read from the language next to it", () =>
            document.Path == layoutFile && LayoutTools && Localisation.Get("menu.play") == "Play" && document.Find("play_button") is Button);

        /* Going to where something comes from */

        test.Check("texts: F12 and Ctrl+F12 are the designer's keys, and Enter", () =>
            commands.Keys.Has(Key.F12, false, false, false) && commands.Keys.Has(Key.F12, true, false, false) && commands.Keys.Has(Key.Enter, false, false, false));

        test.Click(At(Named("play_button")));
        test.Wait();
        test.Check("texts: and the palette has them with something selected", () =>
            AllCommands().Any(command => command.Text.EndsWith("Go to Declaration   (F12)", StringComparison.Ordinal))
            && AllCommands().Any(command => command.Text.EndsWith("Go to Text in Language File   (Ctrl+F12)", StringComparison.Ordinal))
            && AllCommands().Any(command => command.Text.EndsWith("Edit Text   (Enter)", StringComparison.Ordinal)));
        Pick("Layout", () => "Go to Declaration");
        test.Wait();
        test.Check("texts: Go to Declaration (F12) is the code with the caret on the let of what's selected", () =>
            document.Path == layoutFile && document.View == HexView.Code && CaretLine().StartsWith("let play_button", StringComparison.Ordinal));

        // F12 on a key in the code goes to it in the language
        test.Run(() =>
        {
            int line = code.Text.Split('\n').ToList().FindIndex(each => each.Contains("@menu.play", StringComparison.Ordinal));
            code.GoTo(line, code.Document.LineAt(line).IndexOf("@menu", StringComparison.Ordinal) + 3);
            codeEditing.GoToDefinition();
        });
        test.Wait();
        test.Check("texts: F12 on a key in the code opens the language at it", () =>
            document.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(languageFile) && CaretLine().TrimStart().StartsWith("play:", StringComparison.Ordinal));

        test.Run(() =>
        {
            Open(layoutFile);
            SwitchView(HexView.Design);
            designer.Select(document.Find("pages"));
        });
        test.Wait();
        test.Run(() => AllCommands().First(command => command.Text.EndsWith("Go to Text in Language File   (Ctrl+F12)", StringComparison.Ordinal)).Picked());
        test.Wait();
        test.Check("texts: Go to Text (Ctrl+F12) from the palette opens the language at the key of the open tab", () =>
            document.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(languageFile) && CaretLine().TrimStart().StartsWith("first_tab:", StringComparison.Ordinal));

        test.Run(() =>
        {
            Open(layoutFile);
            designer.Select(document.Find("plain_label"));
            designer.GoToSelectedText();
        });
        test.Wait();
        test.Check("texts: a text written in the layout goes to its declaration instead", () =>
            document.View == HexView.Code && CaretLine().StartsWith("let plain_label", StringComparison.Ordinal));
        test.Run(() => SwitchView(HexView.Design));
        test.Wait();

        test.RightClick(At(Named("play_button")));
        test.Wait();
        test.Check("texts: the menu of a component goes to its declaration and its text, and edits it", () =>
            contextMenu?.Showing is { } shown
            && shown.Items.Any(item => item.Label == Said("designer_selection.its_code") && item.Shortcut == "F12")
            && shown.Items.Any(item => item.Label == Said("designer_texts.its_text") && item.Shortcut == "Ctrl+F12" && item.IsVisible?.Invoke() != false)
            && shown.Items.Any(item => item.Label == Said("designer_texts.edit_text_short") && item.IsVisible?.Invoke() != false));
        PickFromMenu(Said("designer_texts.edit_text_short"));
        test.Wait();
        test.Check("texts: Edit text puts a box over it, saying what the key says, the key behind it", () =>
            designer.textEditor is { IsFocused: true, Text: "Play", Placeholder: "@menu.play" } && designer.textEditing?.Component == document.Find("play_button"));
        test.Run(() =>
        {
            TypeInto(designer.textEditor!, "Nope");
            designer.CancelTextEdit();
        });
        test.Wait();
        test.Check("texts: Escape leaves it as it was", () => designer.textEditor is null && Language() == TEXTS_LANGUAGE);

        /* Double clicking a text */

        DoubleClick(At(Named("play_button")));
        test.Settle(0.2f);
        test.Run(() => Shot("texts-editing-key"));
        test.Check("texts: a double click on a button edits its label over it", () =>
            designer.textEditor is { IsFocused: true, Text: "Play" } && document.Selected == document.Find("play_button"));
        test.Run(() =>
        {
            TypeInto(designer.textEditor!, "Start \"now\"");
            designer.textEditor!.Submit();
        });
        test.Wait();
        test.Run(() => Shot("texts-written"));
        test.Check("texts: Enter writes a key's text into the language, quotes and comments and all, and the layout keeps the key", () =>
            designer.textEditor is null && Language() == TEXTS_LANGUAGE.Replace("play: \"Play\"", "play: \"Start \\\"now\\\"\"")
            && Localisation.Get("menu.play") == "Start \"now\"" && Code().Contains("label: \"@menu.play\""));
        test.Run(Undo);
        test.Wait();
        test.Check("texts: Ctrl+Z puts the language back as it was", () => Language() == TEXTS_LANGUAGE && Localisation.Get("menu.play") == "Play");
        test.Run(Redo);
        test.Wait();
        test.Check("texts: and Ctrl+Y writes it again", () => Localisation.Get("menu.play") == "Start \"now\"");

        DoubleClick(At(Named("fresh_button")));
        test.Check("texts: a key the language hasn't got is edited from nothing", () => designer.textEditor is { Text: "", Placeholder: "@menu.fresh" });
        test.Run(() =>
        {
            TypeInto(designer.textEditor!, "Brand new");
            designer.textEditor!.Submit();
        });
        test.Wait();
        test.Check("texts: and goes into its section of the language", () =>
            Localisation.Get("menu.fresh") == "Brand new" && Language().Contains("fresh: \"Brand new\"") && Language().Contains("// the big one"));

        DoubleClick(At(Named("plain_label")));
        test.Check("texts: a double click on a label edits its text", () => designer.textEditor is { Text: "Plain words" });
        test.Run(() => TypeInto(designer.textEditor!, "Other words"));
        test.Click(Part("canvas", 0.04f, 0.5f));
        test.Wait();
        test.Check("texts: a click away keeps it, a text that isn't a key changed in the layout and its code", () =>
            designer.textEditor is null && document.Find("plain_label") is Label { Text: "Other words" } && Code().Contains("text: \"Other words\""));
        test.Settle(HexDesigner.SETTLE_TIME + 0.2f);
        test.Run(Undo);
        test.Wait();
        test.Check("texts: and Ctrl+Z takes it back like any other change of the layout", () => document.Find("plain_label") is Label { Text: "Plain words" });

        DoubleClick(() => document.Module.ToWorld(Pages()!.TabBounds(1).Center));
        test.Run(() => Shot("texts-editing-tab"));
        test.Check("texts: a double click on a tab edits that tab's name", () =>
            designer.textEditor is { Text: "Two" } && designer.textEditing is { Property: "tabs", Item: 1 } && Pages()!.Selected == 1);
        test.Run(() =>
        {
            TypeInto(designer.textEditor!, "Second");
            designer.textEditor!.Submit();
        });
        test.Wait();
        test.Check("texts: and only that one", () => Pages() is { Tabs: ["@menu.first_tab", "Second"] } && Code().Contains("tabs: [\"@menu.first_tab\", \"Second\"]"));

        DoubleClick(Part("canvas", 0.04f, 0.5f));
        test.Check("texts: a double click on nothing with a text edits nothing", () => designer.textEditor is null);

        test.Run(() =>
        {
            designer.Select(document.Find("plain_label"));
            designer.EditSelectedText();
        });
        test.Wait();
        test.Check("texts: Enter on what's selected edits its text too", () => designer.textEditor is { IsFocused: true, Text: "Plain words" });
        test.Run(designer.CancelTextEdit);

        // The language open in Hex with changes that aren't saved isn't written under them
        string before = string.Empty;
        test.Run(() =>
        {
            before = Language();
            Open(languageFile);
            Rewrite(document, document.Editor.Text + "\n// typed and not saved\n");
            Open(layoutFile);
            designer.WriteKeyText("menu.play", "Under it");
        });
        test.Wait();
        test.Check("texts: a language open with changes that aren't saved is left alone", () => Language() == before && Localisation.Get("menu.play") != "Under it");

        // Put away without asking, the next parts start from what they always did
        test.Run(() =>
        {
            Open(languageFile);
            CloseNow();
            Open(layoutFile);
            CloseNow();
        });
        test.Wait();
    }
}
