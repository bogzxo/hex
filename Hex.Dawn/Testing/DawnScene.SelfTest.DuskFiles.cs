using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test, Dusk's files in Hex. The terminal's settings.hor, keys.hor and a plugin, in the test's own home,
// opened, known for what they are, completed, explained and checked, and the settings edited through their form.
internal sealed partial class DawnScene
{
    private void TestDuskFiles(HexPointerScript test)
    {
        string home = TerminalFiles.UserFolder;
        string settingsFile = Path.Combine(home, TerminalSettings.FILE);
        string keysFile = Path.Combine(home, TerminalFiles.KEYS);
        string pluginFile = Path.Combine(home, TerminalFiles.PLUGINS, "selftest", TerminalPlugin.FILE);
        float textScaleWas = 0.16f;
        test.Run(() => textScaleWas = terminals.Settings.TextScale);

        test.Check("dusk files: api.hor is written, with Hex's own on the object", () =>
            File.Exists(Path.Combine(home, TerminalApiDescription.FILE))
            && TerminalApiDescription.Read(File.ReadAllText(Path.Combine(home, TerminalApiDescription.FILE))) is { } api
            && api.Functions.TryGetValue("file", out var file) && file.Programs == "hex" && api.Commands.ContainsKey("split_right"));

        // settings.hor
        test.Run(() =>
        {
            terminals.SaveSettings();
            Open(settingsFile);
        });
        test.Wait();
        test.Check("dusk files: settings.hor is known as the terminal's settings", () => terminalFiles.TerminalKindOf(document) == TerminalFileKind.Settings);
        test.Run(() =>
        {
            code.Document.MoveToEnd(false);
            code.Document.Insert("\n");
            code.Document.Insert("let more = { anim");
        });
        test.Check("dusk files: a key of the settings is completed", () =>
            terminalFiles.TerminalCompletions(document, code) is { } offered && offered.Items.Any(item => item.Text == "animations"));
        test.Run(() => code.Document.Undo());
        test.Run(() => code.Document.Undo());
        test.Wait();

        test.Check("dusk files: a setting is explained under the pointer", () =>
        {
            int row = Enumerable.Range(0, code.Document.LineCount).First(line => code.Document.LineAt(line).TrimStart().StartsWith("scrollback:"));
            int column = code.Document.LineAt(row).IndexOf("scrollback", StringComparison.Ordinal) + 2;
            return codeEditing.HoverHint(code, new UITextPosition(row, column))?.Contains("How many rows") == true;
        });

        test.Run(() =>
        {
            int row = Enumerable.Range(0, code.Document.LineCount).First(line => code.Document.LineAt(line).TrimStart().StartsWith("scrollback:"));
            code.Document.MoveTo(new UITextPosition(row, 0), false);
            code.Document.Insert("    frobnicate: 3,\n");
        });
        test.Run(() => ShowDiagnostics(document));
        test.Check("dusk files: a setting that isn't one is warned of", () => code.Diagnostics.Any(problem => problem.Message.Contains("frobnicate is no setting")));
        test.Run(() => code.Document.Undo());
        test.Run(() => ShowDiagnostics(document));

        // The form, a view of the file
        test.Check("dusk files: settings.hor has the form as a view", () => ViewsFor(document).Any(kind => kind.Id == "dusk_settings.form"));
        test.Run(() => SwitchToExtensionView("dusk_settings.form"));
        test.Wait();
        test.Settle(0.2f);
        test.Check("dusk files: the form has a row a setting", () =>
            Descendants(extensionViews).OfType<FormPanel>().FirstOrDefault() is { } form && form.Fields.Count == TerminalSettings.Described.Length);
        test.Run(() => Descendants(extensionViews).OfType<FormPanel>().First().Enter("text_scale", new NumberValue(0.25f)));
        test.Wait();
        test.Check("dusk files: a value entered in the form is written into the file where it is", () =>
            documents.Find(open => open.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(settingsFile)) is { } open && open.Editor.Text.Contains("text_scale: 0.25"));
        test.Run(() => SwitchView(HexView.Code));
        test.Wait();
        test.Run(() => SaveAll());
        test.WaitFor(() => Math.Abs(terminals.Settings.TextScale - 0.25f) < 0.001f, 3.0f);
        test.Check("dusk files: saved, the terminal takes it in", () => Math.Abs(terminals.Settings.TextScale - 0.25f) < 0.001f);

        // Put back, the terminal's text was half as big again as the code's for every suite after this one
        test.Run(() =>
        {
            terminals.Settings.TextScale = textScaleWas;
            terminals.ApplySettings();
            terminals.SaveSettings();
        });
        test.Wait();
        test.Check("dusk files: and the text scale goes back to what it was", () =>
            Math.Abs(terminals.Settings.TextScale - textScaleWas) < 0.001f && terminalPanel.Tabs.All(tab => tab.Views.All(view => Math.Abs(view.TextScale - textScaleWas) < 0.001f)));

        // keys.hor
        test.Run(() =>
        {
            File.WriteAllText(keysFile, "#define dusk\nlet keys = {\n    new_tab: \"Ctrl+Shift+Nope\",\n    nothing_at_all: \"F2\"\n};\n");
            Open(keysFile);
        });
        test.Wait();
        test.Run(() => ShowDiagnostics(document));
        test.Check("dusk files: keys.hor warns of a key that isn't one and a command nobody has", () =>
            terminalFiles.TerminalKindOf(document) == TerminalFileKind.Keys
            && code.Diagnostics.Any(problem => problem.Message.Contains("isn't a key")) && code.Diagnostics.Any(problem => problem.Message.Contains("nothing_at_all")));
        test.Run(() =>
        {
            code.Document.MoveTo(new UITextPosition(2, 0), false);
            code.Document.Insert("    spl");
        });
        test.Check("dusk files: a command's name is completed in keys.hor", () =>
            terminalFiles.TerminalCompletions(document, code) is { } offered && offered.Items.Any(item => item.Text == "split_right"));
        test.Run(() => code.Document.Undo());

        // A plugin
        test.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pluginFile)!);
            File.WriteAllText(pluginFile, "#define dusk\nlet plugin = { name: \"Self test\", description: \"x\" };\nterminal.\n");
            Open(pluginFile);
        });
        test.Wait();
        test.Run(() => code.Document.MoveTo(new UITextPosition(2, 9), false));
        test.Check("dusk files: after terminal. in a plugin, what the object has", () =>
            terminalFiles.TerminalCompletions(document, code) is { } offered && offered.Items.Any(item => item.Text == "split") && offered.Items.Any(item => item.Text == "file"));
        test.Check("dusk files: and what a call takes", () => terminalFiles.TerminalSignature(document, "terminal.split", 0) is { } signature && signature.Text.Contains("\"right\" or \"down\""));
        test.Run(() =>
        {
            CloseNow();
            Directory.Delete(Path.GetDirectoryName(pluginFile)!, recursive: true);
        });
        test.Wait();
    }
}
