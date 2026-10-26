using System.Diagnostics;

using Horizon.Core.Threading;

namespace Hex.Dawn;

// The workspace's own Push all (its tasks.hor, next to push-all.ps1 over the hex repository) run against repositories
// made for it in the test's folder. Bare ones stand in for GitHub, HIDL, the engine with HIDL as a submodule and hex
// with the engine as one, cloned into a workspace of the same shape with a commit of HIDL's and one of hex's not pushed
// yet, and Fighter2D not there at all. The dry run says what it would do and changes nothing, then the real one pushes
// HIDL, moves the engine onto it and pushes that, and moves hex onto the engine and pushes that, all into the bare ones.
// Left out when the tests don't run inside such a workspace.
internal sealed partial class DawnScene
{
    private void TestPushAll(HexPointerScript test)
    {
        string? over = FindUpFolder(AppContext.BaseDirectory, "push-all.ps1");
        if (over is null || !File.Exists(Path.Combine(over, HexTasks.TASKS_FILE)))
        {
            test.Run(() => Console.WriteLine("[Hex self-test] push all left out, there's no workspace tasks.hor over Hex"));
            return;
        }

        // Short names, the objects of a repository pushed to are a long way down a folder for the test that's long already
        string folder = Path.Combine(testFolder, "pa"), origins = Path.Combine(folder, "o"), workspace = Path.Combine(folder, "w");
        string dryKey = string.Empty, pushKey = string.Empty;
        Dictionary<string, string> before = [];

        test.Run(() =>
        {
            // The submodules are cloned from folders here, which git only does when it's told it may, and Git for
            // Windows is told it may go past the old limit of a path's length
            System.Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "2");
            System.Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow");
            System.Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
            System.Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_1", "core.longpaths");
            System.Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_1", "true");

            MakePushAllRepositories(folder, origins, workspace);
            File.Copy(Path.Combine(over, HexTasks.TASKS_FILE), Path.Combine(workspace, HexTasks.TASKS_FILE), overwrite: true);
            before = OriginHeads(origins);

            HexAutomation dry = PushAllAutomation(workspace, "push_all_dry_run");
            dryKey = HexTasks.AutomationKey(dry);
            tasks.RunAutomation(dry);
        });
        test.WaitFor(() => work.Items.Any(item => item.Key == dryKey && !item.IsActive), 60.0f);
        test.Check("push all: the dry run ran to its end", () => work.Items.FirstOrDefault(item => item.Key == dryKey) is { State: WorkState.Succeeded });
        test.Check("push all: it says what it would push and move, and leaves Fighter2D out", () =>
            work.Items.First(item => item.Key == dryKey).Output is var said
            && said.Contains("  would push 1 commit(s) of horizon/Horizon.HIDL to origin/main")
            && said.Any(line => line.StartsWith("  would move horizon/Horizon.HIDL from ", StringComparison.Ordinal))
            && said.Contains("  horizon is on GitHub already")
            && said.Any(line => line.StartsWith("  hex/horizon is at ", StringComparison.Ordinal))
            && said.Contains("  would push 1 commit(s) of hex to origin/main")
            && said.Contains("  Fighter2D isn't here, it's left out")
            && said.Contains("Dry run done, nothing was changed."));
        test.Check("push all: and nothing was pushed", () => OriginHeads(origins).SequenceEqual(before));

        // What the dry run said, in the test's output for whoever reads it
        test.Run(() =>
        {
            foreach (string line in work.Items.FirstOrDefault(item => item.Key == dryKey)?.Output ?? [])
                Console.WriteLine($"[Hex self-test]   | {line}");
        });

        // The real one, into the bare repositories of the test's folder and nowhere else
        test.Run(() =>
        {
            HexAutomation push = PushAllAutomation(workspace, "push_all");
            pushKey = HexTasks.AutomationKey(push);
            tasks.RunAutomation(push);
        });
        test.WaitFor(() => work.Items.Any(item => item.Key == pushKey && !item.IsActive), 120.0f);
        test.Check("push all: the push ran to its end", () => work.Items.FirstOrDefault(item => item.Key == pushKey) is { State: WorkState.Succeeded });
        test.Check("push all: HIDL's commit is in its origin", () => PushGit(origins, "--git-dir=hidl.git", "log", "-1", "--format=%s", "main") == "HIDL change");
        test.Check("push all: the engine moved onto it, committed and pushed", () =>
            PushGit(origins, "--git-dir=engine.git", "log", "-1", "--format=%s", "main") == "HIDL at HIDL change"
            && PushGit(origins, "--git-dir=engine.git", "rev-parse", "main:Horizon.HIDL") == PushGit(origins, "--git-dir=hidl.git", "rev-parse", "main"));
        test.Check("push all: hex moved onto the engine, committed and pushed with its own commit", () =>
            PushGit(origins, "--git-dir=hex.git", "log", "-2", "--format=%s", "main") == "The engine submodule at HIDL at HIDL change\nhex change"
            && PushGit(origins, "--git-dir=hex.git", "rev-parse", "main:horizon") == PushGit(origins, "--git-dir=engine.git", "rev-parse", "main"));

        test.Run(() =>
        {
            foreach (string name in (string[])["GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0", "GIT_CONFIG_KEY_1", "GIT_CONFIG_VALUE_1"])
                System.Environment.SetEnvironmentVariable(name, null);
        });
    }

    private HexAutomation PushAllAutomation(string workspace, string name) =>
        tasks.AutomationsOf(Path.Combine(workspace, HexTasks.TASKS_FILE), workspace).Single(automation => automation.Name == name);

    /// <summary>
    /// The bare repositories that stand in for GitHub, and the workspace cloned out of them with a commit of HIDL's and one
    /// of hex's that aren't pushed. HIDL is on its branch the way it's worked in, the submodules of hex are as cloned.
    /// </summary>
    private static void MakePushAllRepositories(string folder, string origins, string workspace)
    {
        if (Directory.Exists(folder))
            DeleteTree(folder);
        Directory.CreateDirectory(origins);
        Directory.CreateDirectory(workspace);
        string seeds = Path.Combine(folder, "s");

        string Origin(string name) => Path.Combine(origins, name + ".git").Replace('\\', '/');
        foreach (string name in (string[])["hidl", "engine", "hex"])
            PushGit(origins, "init", "-q", "--bare", "-b", "main", name + ".git");

        // Each seeded with a first commit, the engine with HIDL in it and hex with the engine
        Seed(seeds, "hidl", Origin("hidl"), null, null);
        Seed(seeds, "engine", Origin("engine"), Origin("hidl"), "Horizon.HIDL");
        Seed(seeds, "hex", Origin("hex"), Origin("engine"), "horizon");

        PushGit(workspace, "-c", "protocol.file.allow=always", "clone", "-q", "--recurse-submodules", Origin("engine"), "horizon");
        PushGit(workspace, "-c", "protocol.file.allow=always", "clone", "-q", "--recurse-submodules", Origin("hex"), "hex");
        foreach (string repo in (string[])["horizon", "horizon/Horizon.HIDL", "hex"])
        {
            PushGit(Path.Combine(workspace, repo), "config", "user.name", "Hex Test");
            PushGit(Path.Combine(workspace, repo), "config", "user.email", "hex-test@example.com");
        }

        string hidl = Path.Combine(workspace, "horizon", "Horizon.HIDL");
        PushGit(hidl, "checkout", "-q", "main");
        File.WriteAllText(Path.Combine(hidl, "change.txt"), "a change of HIDL's\n");
        PushGit(hidl, "add", "change.txt");
        PushGit(hidl, "commit", "-q", "-m", "HIDL change");

        string hex = Path.Combine(workspace, "hex");
        File.WriteAllText(Path.Combine(hex, "change.txt"), "a change of hex's\n");
        PushGit(hex, "add", "change.txt");
        PushGit(hex, "commit", "-q", "-m", "hex change");
    }

    // A repository given its first commit and pushed to its origin, with a submodule of another when it says one
    private static void Seed(string seeds, string name, string origin, string? submodule, string? path)
    {
        string seed = Path.Combine(seeds, name);
        Directory.CreateDirectory(seed);
        PushGit(seed, "init", "-q", "-b", "main");
        PushGit(seed, "config", "user.name", "Hex Test");
        PushGit(seed, "config", "user.email", "hex-test@example.com");
        File.WriteAllText(Path.Combine(seed, "README.md"), $"{name}\n");
        PushGit(seed, "add", "README.md");
        if (submodule is not null && path is not null)
            PushGit(seed, "-c", "protocol.file.allow=always", "submodule", "add", "-q", submodule, path);
        PushGit(seed, "commit", "-q", "-m", $"{name} starts");
        PushGit(seed, "push", "-q", origin, "main");
    }

    // Where each bare repository's main is
    private static Dictionary<string, string> OriginHeads(string origins) =>
        new[] { "hidl", "engine", "hex" }.ToDictionary(name => name, name => PushGit(origins, $"--git-dir={name}.git", "rev-parse", "main"));

    /// <summary>A git command run in a folder and waited for, what it wrote trimmed, thrown with what it said when it failed.</summary>
    private static string PushGit(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} in {folder} failed, {error.Result}");
        return output.Trim().ReplaceLineEndings("\n");
    }

    // A folder of repositories taken away, git makes its objects read only
    private static void DeleteTree(string folder)
    {
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, recursive: true);
    }

    // The nearest folder at or over one with something of a name in it, null for none
    private static string? FindUpFolder(string from, string name)
    {
        for (string? at = from; at is not null; at = Path.GetDirectoryName(at))
        {
            if (File.Exists(Path.Combine(at, name)) || Directory.Exists(Path.Combine(at, name)))
                return at;
        }

        return null;
    }
}
