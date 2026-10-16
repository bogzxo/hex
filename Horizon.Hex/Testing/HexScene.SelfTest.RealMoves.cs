using Horizon.UI.Components;

namespace Horizon.Hex;

// The self test, the move editor on Fighter2D's own move files rather than the pretend game, when the game is next to Hex
// the way the workspace has them (bogzxo/horizon/Fighter2D, or wherever HEX_FIGHT_GAME says). Its data and its fighters'
// art are copied into the test's folder first, so nothing of the real game is touched, then every move file is opened in
// turn, has to come up on the move editor with moves in its list, and leave nothing in the problems that isn't a warning.
// A move file the game reads and the editor chokes on is found here, not by whoever opens it. Without the game next to
// Hex there's nothing to open, and the run says it left this out rather than passing a check it never made.
internal sealed partial class HexScene
{
    private static string? FindFighter2D()
    {
        if (Environment.GetEnvironmentVariable("HEX_FIGHT_GAME") is { Length: > 0 } told)
            return Directory.Exists(Path.Combine(told, "Assets", "data")) ? told : null;

        for (string? folder = AppContext.BaseDirectory; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            string next = Path.Combine(folder, "Fighter2D");
            if (System.IO.File.Exists(Path.Combine(next, "Fighter2D.csproj")) && System.IO.File.Exists(Path.Combine(next, "Assets", "data", "characters.hor")))
                return next;
        }

        return null;
    }

    private static bool IsOf(HexProblem problem, string path) => problem.Path is { Length: > 0 } of && Path.GetFullPath(of) == path;

    private static void CopyFolder(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string into = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(into)!);
            System.IO.File.Copy(file, into, overwrite: true);
        }
    }

    private void TestTheGamesMoves(HexPointerScript test)
    {
        if (FindFighter2D() is not { } real)
        {
            Console.WriteLine("[Hex self-test] moves of Fighter2D: there's no Fighter2D next to Hex, left out");
            return;
        }

        // The names are known as the script is made, the copy is made as it runs
        string copy = Path.Combine(testFolder, "fighter2d");
        string[] names = [.. Directory.EnumerateFiles(Path.Combine(real, "Assets", "data"), "*_moves.hor").Select(Path.GetFileName).OfType<string>().Order()];

        test.Run(() =>
        {
            CopyFolder(Path.Combine(real, "Assets", "data"), Path.Combine(copy, "Assets", "data"));
            CopyFolder(Path.Combine(real, "Assets", "sprites", "characters"), Path.Combine(copy, "Assets", "sprites", "characters"));
        });

        foreach (string name in names)
        {
            string path = Path.GetFullPath(Path.Combine(copy, "Assets", "data", name));
            bool Here() => document.Path is { } at && Path.GetFullPath(at) == path;

            test.Run(() => Open(path));
            test.WaitFor(() => Here() && ShownView is { } view && view.Made.Get<ListBox>("moves").Items.Length > 0, 5.0f);

            // What's wrong is worked out a moment after the file is read, give it that
            test.Settle(0.5f);
            test.Check($"moves of Fighter2D: {name} opens on the move editor with its moves, and nothing the game wouldn't load", () =>
                Here() && ShownView is { } view && view.Owner.Name == "Move editor" && view.Made.Get<ListBox>("moves").Items.Length > 0
                && !problemList.Any(problem => !problem.Warning && IsOf(problem, path)));

            test.Run(() =>
            {
                foreach (string problem in problemList.Where(problem => !problem.Warning && IsOf(problem, path)).Select(problem => problem.Text))
                    Console.WriteLine($"[Hex self-test]   {name}: {problem}");
                if (Here()) CloseDocument(document);
            });
        }
    }
}
