using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test, files that are open changed on the disk by something else. Read again when Hex has nothing of its own in
// them, asked about on a card when it has, and a save that would write over the disk's asking first. Then what isn't
// saved kept in a backup and brought back after Hex stopped without asking.
internal sealed partial class HexScene
{
    private void TestDiskChanges(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "changed_outside.hor");
        HexDocument? opened = null;

        test.Run(() =>
        {
            File.WriteAllText(file, "let numbers = { one: 1 };\n");
            Open(file);
            opened = document;
        });
        test.Wait();
        test.Check("disk: a file opened knows what the disk said", () => opened is { DiskText: "let numbers = { one: 1 };\n" } && code.Text.Contains("one: 1", StringComparison.Ordinal));

        test.Run(() => File.WriteAllText(file, "let numbers = { one: 1, two: 2 };\n"));
        test.WaitFor(() => code.Text.Contains("two: 2", StringComparison.Ordinal), 3.0f);
        test.Check("disk: written by something else with nothing changed here, it's read again, still saved", () =>
            document == opened && code.Text.Contains("two: 2", StringComparison.Ordinal) && !HasChanges(opened!) && !opened!.Modified);

        test.Run(() => code.Document.Undo());
        test.Wait();
        test.Check("disk: and Ctrl+Z has what it was", () => code.Text.Contains("one: 1 }", StringComparison.Ordinal) && HasChanges(opened!));

        // There's a change here now (the undo), so the next write asks
        test.Run(() => File.WriteAllText(file, "let numbers = { three: 3 };\n"));
        test.WaitFor(() => DiskCard() is not null, 3.0f);
        test.Check("disk: with changes here as well it asks on a card, and leaves the text alone", () =>
            DiskCard() is { Actions.Count: 3 } && code.Text.Contains("one: 1 }", StringComparison.Ordinal));

        test.Run(() => DiskCard()!.Actions[0].Pressed());
        test.Wait();
        test.Check("disk: take the disk's makes it the disk's, saved, and the card goes", () =>
            code.Text.Contains("three: 3", StringComparison.Ordinal) && !HasChanges(opened!) && DiskCard() is null);

        // A save over a file written since it was read asks, whether or not the watcher has said anything yet
        test.Run(() =>
        {
            code.Document.Replace("let numbers = { mine: 4 };\n");
            File.WriteAllText(file, "let numbers = { theirs: 5 };\n");
            Save();
        });
        test.Wait();
        test.Check("disk: saving over what was written there since asks first, and writes nothing yet", () =>
            chrome.Dialog is { IsOpen: true } && File.ReadAllText(file).Contains("theirs: 5", StringComparison.Ordinal));

        test.Run(() =>
        {
            chrome.Dialog?.Close();
            foreach (UINotification card in toasts.Cards)
                card.Close();
            if (documents.Contains(opened!))
            {
                Show(opened!);
                CloseNow();
            }
        });
        test.Wait();
    }

    private void TestBackups(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "kept_safe.hor");
        HexDocument? opened = null;

        test.Run(() =>
        {
            File.WriteAllText(file, "let kept = 1;\n");
            Open(file);
            opened = document;
            code.Document.Replace("let kept = 2;\n");
        });
        test.WaitFor(() => Backups.All().Any(found => found.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(file)), 4.0f);
        test.Check("backups: a file with changes is kept in a backup a couple of seconds on, its file left alone", () =>
            Backups.All().Any(found => found.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(file) && found.Text == "let kept = 2;\n")
            && File.ReadAllText(file) == "let kept = 1;\n");

        // Closed and the changes thrown away, the backup goes with them
        test.Run(() =>
        {
            Show(opened!);
            CloseNow();
        });
        test.WaitFor(() => !Backups.All().Any(found => found.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(file)), 2.0f);
        test.Check("backups: and closed without saving, it goes", () =>
            !Backups.All().Any(found => found.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(file)));

        // As if Hex had stopped with it unsaved, a backup left behind for a file that isn't open
        test.Run(() => Backups.Keep(file, "kept_safe", "let kept = 3;\n"));
        test.WaitFor(() => Backups.All().Any(found => found.Text == "let kept = 3;\n"), 2.0f);
        // Whatever else of the self test was left unsaved comes back with it, the kept file is put on screen after
        test.Run(() =>
        {
            RestoreBackups();
            if (documents.Find(open => open.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(file)) is { } restored)
                Show(restored);
        });
        test.Wait();
        test.Check("backups: brought back as Hex starts, the file opened with what wasn't saved, said on a card", () =>
            document.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(file) && code.Text == "let kept = 3;\n"
            && HasChanges(document) && toasts.Cards.Any(card => card.Text.Contains("brought back", StringComparison.Ordinal)));
        test.Run(() => code.Document.Undo());
        test.Wait();
        test.Check("backups: and Ctrl+Z goes back to its file", () => code.Text == "let kept = 1;\n" && !HasChanges(document));

        test.Run(() =>
        {
            foreach (UINotification card in toasts.Cards)
                card.Close();
            CloseNow();
        });
        test.Wait();
    }

    private UINotification? DiskCard() => toasts.Cards.FirstOrDefault(card => !card.Closed && card.Text.Contains("changed on the disk", StringComparison.Ordinal));
}
