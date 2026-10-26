using System.Numerics;

using Hex.Core.Git;

namespace Hex.Dawn;

// The self test, merge conflicts and diffs. A repository made for it with a real merge that stopped on conflicts in a
// C# file and a .hor, each opened from Merge Changes and settled with a click on a lens, staged from the card after.
// Then the Accept commands greyed out in a file with none, the words a diff changed, and a diff of broken C# that says
// nothing is wrong. Skipped where there's no git to make one with.
internal sealed partial class DawnScene
{
    private void TestConflicts(HexPointerScript test)
    {
        if (!GitProcess.Installed)
        {
            test.Check("conflicts: there's no git on this machine, so nothing of it is tested", () => true);
            return;
        }

        string repo = Path.Combine(testFolder, "conflicts_repo");
        string ports = Path.Combine(repo, "Ports.cs");
        string values = Path.Combine(repo, "values.hor");
        string server = Path.Combine(repo, "server.txt");
        string broken = Path.Combine(repo, "Broken.cs");
        bool merged = true;

        test.Run(() =>
        {
            DeleteGitFolder(repo);
            Directory.CreateDirectory(repo);
            File.WriteAllText(ports, "class Ports\n{\n    int port = 8080;\n}\n");
            File.WriteAllText(values, "values: {\n    size: 1\n}\n");
            File.WriteAllText(server, "port 8080\nhost here\n");
            File.WriteAllText(broken, "class Broken\n{\n}\n");
            Git(repo, "init", "-q", "-b", "main");
            Git(repo, "config", "user.name", "Hex self test");
            Git(repo, "config", "user.email", "selftest@example.com");
            Git(repo, "add", ".");
            Git(repo, "commit", "-q", "-m", "First go");

            // Both sides change the same lines, so the merge stops with a conflict in each
            Git(repo, "switch", "-q", "-c", "other");
            File.WriteAllText(ports, "class Ports\n{\n    int port = 9090;\n}\n");
            File.WriteAllText(values, "values: {\n    size: 2\n}\n");
            Git(repo, "commit", "-q", "-am", "Theirs");
            Git(repo, "switch", "-q", "main");
            File.WriteAllText(ports, "class Ports\n{\n    int port = 7070;\n}\n");
            File.WriteAllText(values, "values: {\n    size: 3\n}\n");
            Git(repo, "commit", "-q", "-am", "Ours");
            merged = GitProcess.Run(repo, ["merge", "-q", "other"]).Ok;

            // And files changed that the merge never touched, for the diffs
            File.WriteAllText(server, "port 9090\nhost here\n");
            File.WriteAllText(broken, "class Broken\n{\n    void Open() {\n{\n");

            explorer.AddFolder(repo);
            ShowSide(HexSide.SourceControl);
        });

        test.Check("conflicts: the merge stopped with conflicts to settle", () => !merged);

        IEnumerable<UITreeItem> SideRows(IEnumerable<UITreeItem> rows) => rows.SelectMany(row => new[] { row }.Concat(SideRows(row.Children ?? [])));
        UITreeItem? Merging(string path) => SideRows(sideTree.Items).FirstOrDefault(row =>
            row.Tooltip is { } tip && PlatformPaths.Same(tip, path) && row.Parent?.Text.StartsWith("Merge Changes", StringComparison.Ordinal) == true);
        bool On(string path) => document.Path is { } open && PlatformPaths.Same(open, path);
        Vector2 Lens(int action) => conflicts.Count > 0 && code.Module is { } module ? module.ToWorld(code.LensBounds(conflicts[0].Start, action).Center) : last;
        UINotification? SettledCard() => toasts.Cards.LastOrDefault(card => !card.Closed && card.Text.Contains("is settled", StringComparison.Ordinal));

        // Merge Changes, a click opens the file itself at its first conflict, no diff of it
        test.WaitFor(() => Merging(ports) is not null && Merging(values) is not null, 5.0f);
        test.Click(TreeRow(() => Merging(ports)));
        test.WaitFor(() => On(ports) && conflicts.Count == 1, 4.0f);
        test.Check("conflicts: a click under Merge Changes opens the real file at its conflict, no diff", () =>
            On(ports) && document.DiffOf is null && conflicts.Count == 1 && code.Document.Caret.Line == conflicts[0].Start);
        test.Check("conflicts: and the Accept commands can be picked there", () => acceptCommands.Count == 3 && acceptCommands.All(item => item.IsEnabled?.Invoke() == true));

        // Accept Incoming painted after the <<<<<<< line, clicked, one Ctrl+Z to have the block back
        test.WaitFor(() => conflicts.Count == 1 && code.LensBounds(conflicts[0].Start, 1).Width > 0.0f, 3.0f);
        test.Check("conflicts: the first marker has the three lenses after it", () =>
            code.Lenses is [{ Actions.Length: 3 } lens] && lens.Line == conflicts[0].Start && lens.Actions[1] == Said("conflicts.lens_incoming"));

        // The pointer resting on it underlines it, for a look with HEX_SELFTEST_SHOTS set
        test.Hover(() => Lens(1));
        test.Settle(0.3f);
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "conflict_lenses.png"));
        });
        test.Settle(0.3f);
        test.Click(() => Lens(1));
        test.Wait();
        test.Check("conflicts: a click on Accept Incoming keeps theirs, the markers gone", () =>
            code.Text.Replace("\r\n", "\n") == "class Ports\n{\n    int port = 9090;\n}\n" && conflicts.Count == 0 && code.Lenses.Count == 0);
        test.Run(() => code.Document.Undo());
        test.Run(() => UpdateConflicts(now: true));
        test.Check("conflicts: one Ctrl+Z has the whole block back", () => code.Text.Contains("<<<<<<<", StringComparison.Ordinal) && conflicts.Count == 1);

        // Settled again, the last of the file, the card offers to stage it
        test.WaitFor(() => conflicts.Count == 1 && code.LensBounds(conflicts[0].Start, 1).Width > 0.0f, 3.0f);
        test.Click(() => Lens(1));
        test.WaitFor(() => SettledCard() is not null, 3.0f);
        test.Check("conflicts: the last block settled puts up a card with Stage on it", () =>
            SettledCard() is { Actions.Count: 1 } card && card.Actions[0].Label == Said("conflicts.stage"));
        test.Run(() => SettledCard()?.Actions[0].Pressed());
        test.WaitFor(() => git.StateOf(ports).Change != GitChange.Conflicted && !GitProcess.Run(repo, ["diff", "--name-only", "--diff-filter=U"]).Output.Contains("Ports.cs", StringComparison.Ordinal), 6.0f);
        test.Check("conflicts: and Stage saves the file and stages it, git hears it's settled", () =>
            File.ReadAllText(ports).Replace("\r\n", "\n") == "class Ports\n{\n    int port = 9090;\n}\n"
            && !GitProcess.Run(repo, ["diff", "--name-only", "--diff-filter=U"]).Output.Contains("Ports.cs", StringComparison.Ordinal)
            && git.StateOf(ports).Change != GitChange.Conflicted);

        // The same in a file of HIDL, where the lens is too now
        test.WaitFor(() => Merging(values) is not null, 4.0f);
        test.Click(TreeRow(() => Merging(values)));
        test.WaitFor(() => On(values) && conflicts.Count == 1 && code.LensBounds(conflicts[0].Start, 0).Width > 0.0f, 4.0f);
        test.Check("conflicts: a .hor has its lenses as well, and no note where they are", () =>
            On(values) && ReferenceEquals(code.Highlighter, CodeLanguages.Hidl) && code.Lenses.Count == 1 && !code.InlineHints.Any(hint => hint.Line == conflicts[0].Start));
        test.Click(() => Lens(0));
        test.Wait();
        test.Check("conflicts: Accept Current there keeps ours", () => code.Text.Replace("\r\n", "\n") == "values: {\n    size: 3\n}\n" && conflicts.Count == 0);

        // A file with none, the commands greyed out and their key doing nothing
        string before = string.Empty;
        test.Run(() =>
        {
            settledCard?.Close();
            Open(server);
            UpdateConflicts(now: true);
            before = code.Text;
        });
        test.Check("conflicts: the Accept commands are greyed out in a file with none", () =>
            On(server) && acceptCommands.All(item => item.IsEnabled?.Invoke() == false));
        test.Run(() => acceptCommands[0].OnPicked?.Invoke());
        test.Check("conflicts: and Alt+1 there does nothing", () => code.Text == before && !messageItem.Text.Contains(Said("conflicts.none"), StringComparison.Ordinal));

        // A diff of port 8080 to port 9090 lights the two numbers, not the line
        test.Run(OpenChangesHere);
        test.WaitFor(() => document.DiffOf is not null, 4.0f);
        test.Settle(0.3f);
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "diff_words.png"));
        });
        test.Settle(0.3f);
        test.Check("diff: only the words that changed are lit inside the changed lines", () =>
            document.DiffOf is { } of && PlatformPaths.Same(of, server) && code.LineHighlights.Count == 2
            && code.RangeHighlights.Count == 2
            && code.RangeHighlights.Any(range => range is { Line: 0, Column: 5, Length: 4 } && code.Document.LineAt(0).Substring(5, 4) == "8080")
            && code.RangeHighlights.Any(range => range is { Line: 1, Column: 5, Length: 4 } && code.Document.LineAt(1).Substring(5, 4) == "9090"));
        test.Run(() =>
        {
            if (document.DiffOf is not null)
                CloseNow();
        });

        // A diff of C# with braces that don't close says nothing's wrong, in the status bar or in Problems
        test.Run(() =>
        {
            Open(broken);
            OpenChangesHere();
        });
        test.WaitFor(() => document.DiffOf is { } of && PlatformPaths.Same(of, broken), 4.0f);
        test.Settle(1.0f);
        test.Run(GatherProblems);
        test.Check("diff: a diff of broken C# says nothing is wrong with it", () =>
            document.DiffOf is not null && !code.CheckSyntax && code.SyntaxError is null && code.Diagnostics.Count == 0
            && messageItem.Color is null && !problemList.Any(problem => problem.Path == document.Name));

        test.Run(() =>
        {
            settledCard?.Close();
            foreach (HexDocument open in documents.ToList())
            {
                if ((open.Path is { } path && PlatformPaths.IsUnder(path, repo)) || (open.DiffOf is { } of && PlatformPaths.IsUnder(of, repo)))
                {
                    Show(open);
                    CloseNow();
                }
            }
            explorer.RemoveFolder(repo);
        });
        test.Wait();
        test.Run(() => DeleteGitFolder(repo));
    }
}
