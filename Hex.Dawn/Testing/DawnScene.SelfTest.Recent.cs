using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of the little things (HexScene.Conveniences). The command picked last over the rest of the palette and
// the file opened last over the rest of quick open, a closed file brought back where its caret was, a save tidying the
// ends of the lines, a file saving itself once typing stops, and the glass as thick as its sliders say.
internal sealed partial class HexScene
{
    private void TestRecentAndTidy(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "conveniences.txt");
        bool trims = settings.TrimWhitespace, ends = settings.FinalNewline, wraps = settings.WrapCode;
        ListBox Chooser() => Descendants(textDialog!.Body).OfType<ListBox>().First();

        // The palette, nothing picked yet and no lead, then Word Wrap picked and it's the one over the rest
        test.Run(() =>
        {
            chrome.Dialog?.Close();
            RecentCommands.Clear();
            CommandPalette();
        });
        test.Wait();
        test.Check("recent: the palette has no lead before anything was picked from it", () => textDialog is { IsOpen: true } && Chooser().Lead == 0);
        test.Run(() =>
        {
            ListBox list = Chooser();
            int wrap = Array.FindIndex(list.Items, item => item.Contains("Word Wrap", StringComparison.Ordinal));
            list.Choose(wrap);
            list.OnActivated!(list.Items[wrap]);
        });
        test.Wait();
        test.Check("recent: what was picked is remembered, and it ran", () => RecentCommands.Items.Count == 1 && RecentCommands.Items[0].Contains("Word Wrap") && settings.WrapCode != wraps);
        test.Run(CommandPalette);
        test.Wait();
        test.Check("recent: and it's over the rest the next time, a lead of one, not in the rest again", () =>
            Chooser() is { Lead: 1 } list && list.Items[0].Contains("Word Wrap") && list.Items.Count(item => item == list.Items[0]) == 1);
        test.Run(() =>
        {
            chrome.Dialog?.Close();
            if (settings.WrapCode != wraps) ToggleWrap();
        });

        // Quick open, the file opened last over the rest (it lists the .hor files of the workspace)
        string data = string.Empty;
        test.Run(() =>
        {
            // In the first folder of the workspace, whatever the parts before left open, or quick open hasn't got it
            data = Path.Combine(WorkspaceRoots().FirstOrDefault() ?? testFolder, "recent_data.hor");
            File.WriteAllText(data, "let opened = 1;\n");
            Open(data);
            QuickOpen();
        });
        test.Wait();
        test.Check("recent: quick open has the file opened last over the rest", () => Chooser() is { Lead: > 0 } list && list.Items[0] == Relative(data));
        test.Run(() =>
        {
            chrome.Dialog?.Close();
            CloseDocument(document);
            File.Delete(data);
            File.WriteAllText(file, "one  \ntwo\t");
            Open(file);
        });

        // A save tidies the ends of the lines and the end of the file
        test.Run(() =>
        {
            settings.TrimWhitespace = settings.FinalNewline = true;
            Save();
        });
        test.Wait();
        test.Check("recent: a save trims the ends of the lines and ends the file with a newline", () => File.ReadAllText(file) == "one\ntwo\n" && code.Document.Text == "one\ntwo\n");

        // Closed and brought back where the caret was
        test.Run(() =>
        {
            code.Document.MoveTo(new Horizon.UI.Components.UITextPosition(1, 2));
            CloseDocument(document);
        });
        test.Wait();
        test.Check("recent: a file closed is gone", () => !documents.Any(open => open.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(file)));
        test.Run(ReopenClosed);
        test.Wait();
        test.Check("recent: and Ctrl+Shift+T brings it back with the caret where it was", () =>
            document.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(file) && code.Document.Caret == new Horizon.UI.Components.UITextPosition(1, 2));

        // Saved by itself once typing stops
        test.Run(() =>
        {
            settings.AutoSave = true;
            settings.AutoSaveDelay = 0.3f;
            code.Focus();
            code.Document.MoveToEnd(false);
            code.Document.Insert("three");
        });
        test.WaitFor(() => File.ReadAllText(file).EndsWith("three", StringComparison.Ordinal), 3.0f);
        test.Check("recent: a file saves itself a moment after typing stops", () => File.ReadAllText(file) == "one\ntwo\nthree");
        test.Run(() => settings.AutoSave = false);

        // Ctrl+N, a scratchpad, a numbered file of text in the scratch folder, open and empty
        string scratch = Path.Combine(testFolder, "scratch");
        test.Run(() =>
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
            NewScratchpad();
        });
        test.Wait();
        test.Check("recent: Ctrl+N makes a scratchpad, an empty file of text in the scratch folder, open", () =>
            document is { IsText: true, Path: { } path } && Path.GetFullPath(path) == Path.GetFullPath(Path.Combine(scratch, "scratch 1.txt")) && code.Document.Text.Length == 0);
        test.Run(NewScratchpad);
        test.Wait();
        test.Check("recent: and the next one is the next number", () => document.Path is { } path && Path.GetFileName(path) == "scratch 2.txt");
        test.Run(() =>
        {
            CloseDocument(document);
            CloseDocument(document);
        });
        test.Wait();

        // The glass as thick as the sliders
        test.Run(() =>
        {
            settings.GlassBlur = 8.0f;
            settings.GlassRim = 0.6f;
            settings.Effects = "subtle";
            settings.EffectGlass = true;
            ApplyEffects();
        });
        test.Check("recent: the glass is as thick as its sliders, the sides worked out of them", () =>
            compositor.PopupGlass is { Blur: 8.0f, Rim: 0.6f } && left.Glass is { } side && side.Blur > 8.0f && side.Rim < 0.6f && side.Bend == 0.0f);

        test.Run(() =>
        {
            settings.GlassBlur = HexGlass.BLUR;
            settings.GlassRim = HexGlass.RIM;
            settings.Effects = "none";
            settings.TrimWhitespace = trims;
            settings.FinalNewline = ends;
            ApplyEffects();
            CloseDocument(document);
        });
        test.Wait();
    }
}
