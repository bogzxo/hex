using Horizon.UI.Components;

namespace Hex.Core;

/// <summary>
/// Where Hex keeps what it makes as it goes rather than what it's set up with: the agents' chats, the pictures pasted
/// into them, their temporary files, and caches. All of it in one folder, data next to the exe, beside settings, so
/// it's a double click away from Hex.exe, its size can be said and it can be tidied in one place. A Hex whose folder
/// can't be written (one in Program Files) keeps it in the user's local app data instead, laid out the same, and says
/// so (<see cref="Fallback"/>). HEX_HOME (or DUSK_HOME) moves it under that folder with the settings, for a test.
/// <code>
/// data/
///   agents/chats/&lt;workspace&gt;/   chat-*.json, chats.json and history/ of each workspace (HexAgentChatsKept)
///   agents/history/               the older rows of chats no workspace keeps
///   agents/images/                pictures pasted into the agents' box
///   agents/temp/                  each chat's files for its agent system (MCP config, settings), made again as it starts
///   agents/limits.tsv             the agent systems' usage limits last heard
///   cache/harness/                the harness's code index and what it saved (stats.tsv)
/// </code>
/// Every file of these was in the settings folder or %TEMP% before, <see cref="MoveOld"/> moves them over once.
/// </summary>
public static class HexData
{
    /// <summary>The name of the folder next to the exe.</summary>
    public const string FOLDER = "data";

    private static (string Root, bool Fallback)? found;

    /// <summary>The data folder, made the first time it's asked for.</summary>
    public static string Root => Where().Root;

    /// <summary>Whether the folder next to the exe couldn't be written and the user's local app data has it instead.</summary>
    public static bool Fallback => Where().Fallback;

    public static string Agents => Path.Combine(Root, "agents");
    public static string AgentChats => Path.Combine(Agents, "chats");
    public static string AgentHistory => Path.Combine(Agents, "history");
    public static string AgentImages => Path.Combine(Agents, "images");
    public static string AgentTemp => Path.Combine(Agents, "temp");
    public static string AgentLimits => Path.Combine(Agents, "limits.tsv");
    public static string Cache => Path.Combine(Root, "cache");
    public static string HarnessCache => Path.Combine(Cache, "harness");

    /// <summary>Where the agents' temporary files were before, still cleaned with the new ones.</summary>
    public static string OldAgentTemp => Path.Combine(Path.GetTempPath(), "hex-agents");

    /// <summary>What was moved over from where it was before, and what wasn't, a line each.</summary>
    public static string MovedLog => Path.Combine(Root, "moved.log");

    private static (string Root, bool Fallback) Where()
    {
        // A test's home is asked for every time, a test sets it and puts it back
        if ((Environment.GetEnvironmentVariable("HEX_HOME") ?? Environment.GetEnvironmentVariable("DUSK_HOME")) is { Length: > 0 } home)
            return (Made(Path.Combine(home, FOLDER)), false);

        return found ??= Choose(HexHost.ProgramFolder, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Writable);
    }

    /// <summary>
    /// The data folder of a Hex in <paramref name="programFolder"/>: the one next to it when that can be written, else
    /// Hex's folder in <paramref name="localData"/> (the user's local app data) with the same layout.
    /// </summary>
    internal static (string Root, bool Fallback) Choose(string programFolder, string localData, Func<string, bool> writable)
    {
        string beside = Path.Combine(programFolder, FOLDER);
        if (writable(beside))
            return (beside, false);

        bool linux = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
        return (Made(Path.Combine(localData, linux ? "hex" : "Hex", FOLDER)), true);
    }

    /// <summary>
    /// Where each of the files was before, and where it goes now. <paramref name="settings"/> is the settings folder
    /// (TerminalFiles.UserFolder) they were in. The agents' temporary files aren't moved, they're made again as a chat
    /// starts and a Hex still running from before may be using them, they're cleaned where they are instead.
    /// </summary>
    public static IReadOnlyList<(string From, string To)> OldPlaces(string settings) =>
    [
        (Path.Combine(settings, "agent_chats"), AgentChats),
        (Path.Combine(settings, "agent_history"), AgentHistory),
        (Path.Combine(settings, "agent_images"), AgentImages),
        (Path.Combine(settings, "agent_limits.tsv"), AgentLimits),
        (Path.Combine(settings, "harness"), HarnessCache)
    ];

    /// <summary>What a move of the old files did.</summary>
    public readonly record struct Moved(int Files, int Skipped, int Failed);

    /// <summary>
    /// The files of the settings folder that are the data folder's now moved over, as Hex starts. Nothing in a
    /// self test, which has a folder of its own.
    /// </summary>
    public static Moved MoveOld() => MoveOld(OldPlaces(TerminalFiles.UserFolder), MovedLog);

    /// <summary>
    /// Each old place moved to its new one, a file at a time: one whose new place has a file already is left where it
    /// was (skipped, never written over), one that won't move (in use, no room) is left too, and nothing that didn't
    /// move is deleted. The folders emptied by it go. What was moved and what wasn't is put at the end of
    /// <paramref name="log"/>, nothing is written when there was nothing to move.
    /// </summary>
    internal static Moved MoveOld(IEnumerable<(string From, string To)> places, string? log)
    {
        int files = 0, skipped = 0, failed = 0;
        var lines = new List<string>();
        foreach (var (from, to) in places)
        {
            if (SamePlace(from, to))
                continue;

            if (File.Exists(from))
            {
                MoveFile(from, to);
                continue;
            }

            if (!Directory.Exists(from))
                continue;

            List<string> inside;
            try
            {
                inside = [.. Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                lines.Add($"failed  {from}: {e.Message}");
                failed++;
                continue;
            }

            foreach (string file in inside)
                MoveFile(file, Path.Combine(to, Path.GetRelativePath(from, file)));
            RemoveEmpty(from);
        }

        if (lines.Count > 0 && log is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllLines(log, [$"# {DateTime.Now:yyyy-MM-dd HH:mm:ss} moved {files}, skipped {skipped}, failed {failed}", .. lines]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new Moved(files, skipped, failed);

        void MoveFile(string file, string into)
        {
            try
            {
                if (File.Exists(into) || Directory.Exists(into))
                {
                    lines.Add($"skipped {file}: {into} is there already");
                    skipped++;
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(into)!);
                File.Move(file, into);
                lines.Add($"moved   {file} -> {into}");
                files++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                lines.Add($"failed  {file}: {e.Message}");
                failed++;
            }
        }
    }

    // The folders under one and it left empty taken away, the deepest first, a folder with anything in it left
    private static void RemoveEmpty(string folder)
    {
        try
        {
            foreach (string inside in Directory.EnumerateDirectories(folder))
                RemoveEmpty(inside);
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool SamePlace(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), Horizon.Core.Platform.PlatformPaths.Comparison);

    // Whether a folder can be made and written into, which one in Program Files can't
    private static bool Writable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".written");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string Made(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // what's written into it says so
        }

        return folder;
    }
}
