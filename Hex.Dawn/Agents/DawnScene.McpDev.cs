using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

// Hex working on itself, for a debug build run out of its own source. An agent in the Agents panel changes Hex's code,
// builds it into the folder this Hex runs from, tests it and restarts into what it built, carrying on in the new Hex
// with every chat as it was, so the next change is made with the last one already working. No harness outside Hex is
// needed for any of it. A Hex that's running can't have its files written over, but Windows lets them be renamed, so
// the build renames whatever is locked out of the way first and puts it back when the build fails. Only in a debug
// build, a release one has none of it.
internal sealed partial class DawnScene
{
#if DEBUG
    // What a file renamed out of the build's way ends with, and the variable the new Hex finds the chats it's handed in
    private const string DEV_ASIDE = ".hexold", DEV_RESUME = "HEX_DEV_RESUME";

    // How long the old Hex waits after saying it's restarting, so the agent has its answer written down before it goes
    private const float DEV_RESTART_DELAY = 2.0f;

    // The restart waiting to happen and when, set off the editor's thread by the tool
    private volatile string? devRestartWith;
    private float devRestartIn = -1.0f;
    private bool devResumed;

    // Whether a build has gone through since this Hex started, what there is to restart into. Its files can be older than
    // this Hex when nothing changed, the build copies them back as they were
    private static volatile bool devBuilt;

    // Whether the build that's running has been kept as the good one yet, once a run, before its first build
    private static volatile bool devKept;

    // How long the Hex that came up from a restart runs before it says it's whole, and where it says so
    private const float DEV_HEALTHY_AFTER = 5.0f;
    private string? devHealthy;
    private float devHealthyIn = -1.0f;
    private string? devResumeFile;

    // The log of a build that crashed coming up, for the agent that made it, when this Hex is the one put back
    private string? devCrash;

    private IEnumerable<HexMcpTool> DevTools()
    {
        if (HexSourceRoot() is null)
            return [];

        return
        [
            new(new McpTool("hex_dev_build", "Build Hex",
                "Build Hex from the source this Hex was built from, into the folder it runs from, so hex_dev_restart starts the new build. Debug builds of Hex only. The running Hex's own files are moved aside first and put back if the build fails, so a failed build leaves this Hex and its next start as they were. Says the errors, file:line, when there are any.",
                McpSchema.Empty()), null, RunAway: (_, cancel) => DevBuild(cancel)),
            new(new McpTool("hex_dev_test", "Test Hex",
                "Run Hex's unit tests (Hex.Tests), all of them or those a filter picks, and say how many passed and which failed with why. hex_selftest runs the in-editor self tests in a Hex of its own, the newest build once hex_dev_build has made one.",
                McpSchema.Object([("filter", McpSchema.String("A dotnet test filter, \"FullyQualifiedName~Agent\" for the agent tests, all of them when left out."))])),
                null, RunAway: (args, cancel) => DevTest(args.String("filter"), cancel)),
            new(new McpTool("hex_dev_restart", "Restart into the new build",
                "Restart Hex into the build hex_dev_build made. Every chat of the Agents panel goes over to the new Hex as it was, and yours goes on there in the same session with the note as its next message, so write down in it what you were doing and what's next. Your turn ends here, call nothing after it.",
                McpSchema.Object([("note", McpSchema.String("What to carry on with in the new build, complete in itself."))])),
                // Hex restarting into its own build carries on the chat that asked, so it has to know which that was
                DevRestart, TakesCaller: true)
        ];
    }

    /// <summary>The folder Hex's source is in when this Hex was built from it, the one with Hex/Hex.csproj, null otherwise.</summary>
    private static string? HexSourceRoot()
    {
        for (DirectoryInfo? at = new(Hex.Core.HexHost.ProgramFolder); at is not null; at = at.Parent)
        {
            if (File.Exists(Path.Combine(at.FullName, "Hex", "Hex.csproj")) && File.Exists(Path.Combine(at.FullName, "Hex.Tests", "Hex.Tests.csproj")))
                return at.FullName;
        }

        return null;
    }

    private static async Task<McpToolResult> DevBuild(CancellationToken cancel)
    {
        string root = HexSourceRoot()!;

        // The build that's running works, it's kept beside it before anything is built over it, for the guard to put back
        // if what's built doesn't come up
        if (!devKept)
        {
            Hex.Core.HexDevBuilds.Mirror(Hex.Core.HexHost.ProgramFolder, Hex.Core.HexDevBuilds.GoodFolder(Hex.Core.HexHost.ProgramFolder));
            devKept = true;
        }

        List<(string From, string Aside)> moved = MoveLockedAside(Hex.Core.HexHost.ProgramFolder);
        var watch = Stopwatch.StartNew();
        (int code, string output) = await DevRun("dotnet", ["build", Path.Combine(root, "Hex", "Hex.csproj"), "-c", "Debug", "-nologo", "-v", "q", "-clp:NoSummary"], root, cancel);
        string[] errors = [.. output.Split('\n').Select(line => line.Trim()).Where(line => line.Contains(": error ", StringComparison.Ordinal)).Distinct()];
        int warnings = output.Split('\n').Count(line => line.Contains(": warning ", StringComparison.Ordinal));

        if (code != 0 || errors.Length > 0)
        {
            PutBack(moved);
            var said = new StringBuilder($"The build failed in {watch.Elapsed.TotalSeconds:0}s, this Hex and its files are as they were.\n");
            foreach (string error in errors.Take(40))
                said.Append(ShortPath(error, root)).Append('\n');
            if (errors.Length == 0)
                said.Append(output.Length > 4000 ? output[^4000..] : output);
            return McpToolResult.Error(said.ToString().TrimEnd());
        }

        // The extensions this Hex has installed brought up to their source, so a change to Claude Code's or Codex's comes
        // with the build as Hex's own code does
        int synced = 0;
        string installed = Path.Combine(Hex.Core.HexHost.ProgramFolder, "extensions");
        if (Directory.Exists(installed))
        {
            foreach (string extension in Directory.EnumerateDirectories(installed))
            {
                string from = Path.Combine(root, "hex-extensions", Path.GetFileName(extension));
                if (!Directory.Exists(from))
                    continue;
                foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                {
                    string to = Path.Combine(extension, Path.GetRelativePath(from, file));
                    if (File.Exists(to) && File.ReadAllBytes(to).AsSpan().SequenceEqual(File.ReadAllBytes(file)))
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(file, to, overwrite: true);
                    synced++;
                }
            }
        }

        devBuilt = true;
        return McpToolResult.Text($"Built in {watch.Elapsed.TotalSeconds:0}s, {warnings} warnings, {moved.Count} files of the running Hex moved aside, {synced} extension files brought up to their source. hex_dev_test and hex_selftest test it, hex_dev_restart starts it.");
    }

    private static async Task<McpToolResult> DevTest(string? filter, CancellationToken cancel)
    {
        string root = HexSourceRoot()!;
        List<string> arguments = ["test", Path.Combine(root, "Hex.Tests", "Hex.Tests.csproj"), "-nologo", "-v", "q"];
        if (!string.IsNullOrWhiteSpace(filter))
            arguments.AddRange(["--filter", filter]);

        (int code, string output) = await DevRun("dotnet", arguments, root, cancel);
        string[] lines = output.Split('\n');
        string? total = lines.LastOrDefault(line => line.Contains("Passed!", StringComparison.Ordinal) || line.Contains("Failed!", StringComparison.Ordinal))?.Trim();
        var said = new StringBuilder(total ?? (code == 0 ? "The tests passed." : "The tests didn't run."));
        said.Append('\n');

        // Each failing test and the line under it that says why
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("Failed ", StringComparison.Ordinal))
            {
                said.Append(lines[i].Trim()).Append('\n');
                for (int j = i + 1; j < Math.Min(lines.Length, i + 6); j++)
                {
                    if (lines[j].Contains("Error Message", StringComparison.Ordinal) || lines[j].Contains("Assert", StringComparison.Ordinal))
                        said.Append("  ").Append(lines[j].Trim()).Append('\n');
                }
            }
            else if (lines[i].Contains(": error ", StringComparison.Ordinal))
                said.Append(ShortPath(lines[i].Trim(), root)).Append('\n');
        }

        string text = said.ToString().TrimEnd();
        return code == 0 ? McpToolResult.Text(text) : McpToolResult.Error(text);
    }

    private IEnumerable<McpToolResult?> DevRestart(JsonObject args)
    {
        string exe = Path.Combine(Hex.Core.HexHost.ProgramFolder, OperatingSystem.IsWindows() ? "Hex.exe" : "Hex");
        if (!devBuilt || !File.Exists(exe))
        {
            yield return McpToolResult.Error("There's no new build to restart into, hex_dev_build makes one.");
            yield break;
        }

        // This call is the one thing still going in the chat that asked, it's done as far as the new Hex is concerned
        if (CallerOf(args) is { } caller && ChatById(caller) is { } asking
            && asking.Entries.LastOrDefault(entry => entry.Kind == HexAgentEntryKind.Tool && entry.State == HexAgentToolState.Running && (entry.ToolName ?? string.Empty).EndsWith("hex_dev_restart", StringComparison.Ordinal)) is { ToolId: { } id })
            asking.ToolDone(id, true, "Restarted into the new build.");

        // Every chat written down for the new Hex, and the one that asked carried on there with its note
        var resume = new JsonObject
        {
            ["face"] = Face.ToString(),
            ["open"] = agentChat?.Id,
            ["made"] = agentChatsMade,
            ["caller"] = CallerOf(args),
            ["note"] = args.String("note") ?? string.Empty,
            ["chats"] = new JsonArray([.. agentChats.Select(chat => (JsonNode)HexAgentChatStore.Write(chat))])
        };
        string file = Path.Combine(Path.GetTempPath(), $"hex-dev-resume-{System.Environment.ProcessId}.json");
        File.WriteAllText(file, resume.ToJsonString());
        devRestartWith = file;
        yield return McpToolResult.Text("Restarting into the new build. Your chat goes on there in this session with your note, end your turn now.");
    }

    /// <summary>The restart once its moment comes, the new build started on the same folder with the chats, and this Hex closed.</summary>
    private void UpdateDevRestart(float dt)
    {
        if (!devResumed)
        {
            devResumed = true;
            ResumeFromDev();
            CleanAside(Hex.Core.HexHost.ProgramFolder);
        }

        // Up from a restart with its chats back and a few seconds run, the guard is told it came up whole, and the chats
        // it was handed are let go
        if (devHealthy is { } healthy && (devHealthyIn -= dt) <= 0.0f)
        {
            devHealthy = null;
            try
            {
                File.WriteAllText(healthy, System.Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (devResumeFile is { } handed)
                    File.Delete(handed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        if (devRestartWith is not { } file)
            return;

        if (devRestartIn < 0.0f)
            devRestartIn = DEV_RESTART_DELAY;
        if ((devRestartIn -= dt) > 0.0f)
            return;

        devRestartWith = null;
        foreach (HexAgentChat chat in agentChats.Where(chat => chat.Running).ToList())
            StopAgent(chat);

        // The new build started by a guard running from the good one, which puts the good one back and starts it again if
        // the new one doesn't come up. Without a good one kept, the new build is started as it is
        string bin = Path.TrimEndingDirectorySeparator(Hex.Core.HexHost.ProgramFolder);
        string good = Hex.Core.HexDevBuilds.GoodFolder(bin);
        string guard = Path.Combine(good, OperatingSystem.IsWindows() ? "Hex.exe" : "Hex");
        bool guarded = File.Exists(guard);
        var start = new ProcessStartInfo(guarded ? guard : Path.Combine(bin, OperatingSystem.IsWindows() ? "Hex.exe" : "Hex"))
        {
            UseShellExecute = false,
            WorkingDirectory = guarded ? good : bin,
            CreateNoWindow = guarded
        };
        if (guarded)
        {
            foreach (string argument in (string[])["--dev-guard", "--bin", bin, "--good", good, "--resume", file])
                start.ArgumentList.Add(argument);
        }
        else
        {
            start.ArgumentList.Add("--window");
            start.Environment[DEV_RESUME] = file;
        }

        if (mcpHost is not null)
            start.ArgumentList.Add("--mcp");
        if (explorer.workspace.Folders.FirstOrDefault() is { } folder)
        {
            start.ArgumentList.Add("--folder");
            start.ArgumentList.Add(folder);
        }
        Process.Start(start);

        // Closed as asked to, nothing to confirm, what isn't saved kept as backups for the new Hex to bring back
        closeConfirmed = true;
        Engine.WindowManager.Close();
    }

    /// <summary>The chats a Hex that restarted into this build handed over, put back, and the one that asked carried on.</summary>
    private void ResumeFromDev()
    {
        if (System.Environment.GetEnvironmentVariable(DEV_RESUME) is not { Length: > 0 } file || !File.Exists(file))
            return;
        System.Environment.SetEnvironmentVariable(DEV_RESUME, null);

        // The chats are let go once this build says it's whole, the good one needs them again if it doesn't. A build put
        // back after one crashed is told so, and where the crash is written down
        devResumeFile = file;
        devCrash = System.Environment.GetEnvironmentVariable("HEX_DEV_CRASH") is { Length: > 0 } crash ? crash : null;
        if (System.Environment.GetEnvironmentVariable("HEX_DEV_HEALTHY") is { Length: > 0 } healthy)
        {
            devHealthy = healthy;
            devHealthyIn = DEV_HEALTHY_AFTER;
        }
        else if (devCrash is not null)
            devHealthy = null;

        // Taken, and gone from what this Hex hands down, or the next build and every agent started from here inherited
        // them, and a build that came up whole was told it had crashed
        System.Environment.SetEnvironmentVariable("HEX_DEV_CRASH", null);
        System.Environment.SetEnvironmentVariable("HEX_DEV_HEALTHY", null);

        JsonObject? resume;
        try
        {
            resume = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            if (devHealthy is null)
                File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Say($"the chats of the last build couldn't be read, {e.Message}", error: true);
            return;
        }

        // With No AI on there's nowhere for the chats to go back to and no agent to carry on, the build is up and that's all
        if (resume is null || HexAi.Off)
            return;

        foreach (JsonNode? said in resume["chats"] as JsonArray ?? [])
        {
            if (HexAgentChatStore.Read(said) is { } chat && ChatById(chat.Id) is null)
                agentChats.Add(chat);
        }

        agentChatsMade = Math.Max(agentChatsMade, resume["made"]?.GetValue<int>() ?? 0);
        if (resume["open"]?.GetValue<int>() is { } open && ChatById(open) is { } opened)
            agentChat = opened;
        if (Enum.TryParse(resume["face"]?.GetValue<string>(), out Hex.Shell.HexFace face))
            ShowFace(face);

        // The agent that restarted carries on, in the session it was in, once its extension is up
        if (resume["caller"]?.GetValue<int>() is { } caller && ChatById(caller) is { } going)
            devContinue = (going, resume["note"]?.GetValue<string>() ?? string.Empty, 30.0f);
    }

    // The end of what a crashed build printed, its own lines rather than the engine's chatter, and every line of a crash
    private static string DevCrashTail(string log)
    {
        try
        {
            // The guard still has it open, writing what this Hex prints after it, so it's read beside that writer
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string[] lines = [.. reader.ReadToEnd().Split('\n').Select(line => line.TrimEnd('\r')).Where(line => !line.StartsWith("[Info]", StringComparison.Ordinal))];
            return string.Join('\n', lines.TakeLast(60));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"(the log couldn't be read, {e.Message})";
        }
    }

    // The chat to carry on once its agent system is there, what it's told and how long it's waited for
    private (HexAgentChat Chat, string Note, float Wait)? devContinue;

    /// <summary>The agent that restarted Hex started again in the new build, as soon as its extension has said how.</summary>
    private void UpdateDevContinue(float dt)
    {
        if (devContinue is not { } going)
            return;

        if (extensions.AgentRuns.TryGetValue(going.Chat.Agent, out var run))
        {
            devContinue = null;
            string told = devCrash is { } crash
                ? $"[The build you made crashed as Hex started it, so Hex put back the build before it and started that again, you're in it now, in the same session. Everything the new build printed is in {crash}, its end is below. Find why it crashed and fix it, then hex_dev_build and hex_dev_restart again.]\n\n```\n{DevCrashTail(crash)}\n```"
                : $"[Hex restarted into the build you made, {HexAbout.Label}. You're in the same session, carry on.]";
            told += going.Note.Length > 0 ? $"\n\n{(devCrash is null ? string.Empty : "What you meant to carry on with in it\n")}{going.Note}" : string.Empty;
            StartAgent(going.Chat, run.Extension, run.Run, told, Said("dev.continued"));
            return;
        }

        devContinue = (going.Chat, going.Note, going.Wait - dt);
        if (devContinue.Value.Wait <= 0.0f)
        {
            devContinue = null;
            going.Chat.Error(Said("dev.no_agent", ("agent", going.Chat.Agent)));
        }
    }

    /// <summary>
    /// Every file under a folder that's in use moved aside out of a build's way, a running program's own files can't be
    /// written over but can be renamed. Says what was moved where, to be put back.
    /// </summary>
    internal static List<(string From, string Aside)> MoveLockedAside(string folder)
    {
        var moved = new List<(string, string)>();
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(DEV_ASIDE, StringComparison.OrdinalIgnoreCase) || !IsLocked(file))
                continue;

            string aside = file + "." + System.Environment.ProcessId + DEV_ASIDE;
            try
            {
                File.Move(file, aside, overwrite: true);
                moved.Add((file, aside));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        return moved;
    }

    /// <summary>What was moved aside put back, whatever a failed build wrote in its place taken away.</summary>
    internal static void PutBack(List<(string From, string Aside)> moved)
    {
        foreach (var (from, aside) in moved)
        {
            try
            {
                File.Move(aside, from, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The files an earlier Hex moved aside, gone once nothing has them open.</summary>
    internal static void CleanAside(string folder)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*" + DEV_ASIDE, SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsLocked(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A program run to its end off the editor's thread, what it printed and its exit code
    private static async Task<(int Code, string Output)> DevRun(string program, IEnumerable<string> arguments, string folder, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(program) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancel);
        Task<string> error = process.StandardError.ReadToEndAsync(cancel);
        try
        {
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, (await output) + (await error));
    }

    // A compiler's line with the source folder taken off its paths
    private static string ShortPath(string line, string root) =>
        line.Replace(root + Path.DirectorySeparatorChar, string.Empty, StringComparison.OrdinalIgnoreCase);
#else
    // A release build has none of it
    private static IEnumerable<HexMcpTool> DevTools() => [];

    private static void UpdateDevRestart(float dt) { }

    private static void UpdateDevContinue(float dt) { }
#endif
}
