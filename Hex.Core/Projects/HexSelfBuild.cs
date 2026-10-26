using System.Text.RegularExpressions;

namespace Hex.Core.Projects;

/// <summary>
/// Hex building itself. Windows won't let a build write over the exe and the dlls of a program that's running, and Hex
/// building Hex into the folder it runs from is exactly that, the copy fails with MSB3027 and the files stay locked. It
/// does let a running image be renamed, so the locked files are moved aside to name.old first, the build writes new ones
/// in their place, and the .old ones are cleaned up the next time Hex starts (from the new exe, which locks only itself).
/// A build that didn't write them gets the old ones put back.
/// </summary>
internal static partial class HexSelfBuild
{
    private const string ASIDE = ".old";

    // What's moved aside, a dll, an exe or a pdb, with a number when an older .old of it is still locked too
    [GeneratedRegex(@"\.(dll|exe|pdb|so|dylib)(\.\d+)?\.old$", RegexOptions.IgnoreCase)]
    private static partial Regex MovedAside();

    /// <summary>The folder Hex runs from, the one a build mustn't write over without moving it aside.</summary>
    public static string RunningFolder => Path.GetFullPath(AppContext.BaseDirectory);

    /// <summary>
    /// The exe that's running, Hex.exe, as it was called when Hex started. Windows names a running image by where it is
    /// now, so asked first after the move it was Hex.exe.old, and the new build was never seen.
    /// </summary>
    public static string? RunningExe { get; } = Environment.ProcessPath is { } path ? Regex.Replace(path, @"(\.\d+)?\.old$", string.Empty, RegexOptions.IgnoreCase) : null;

    /// <summary>Whether a folder is the one Hex runs from.</summary>
    public static bool IsRunningFolder(string? folder) =>
        folder is { Length: > 0 } && PlatformPaths.Same(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), Path.TrimEndingDirectorySeparator(RunningFolder));

    /// <summary>
    /// The files of a folder (and the folders in it) a build can't write over, every dll, exe and pdb that won't open for
    /// writing, moved aside to name.old. The ones moved, by the names they had. Nothing is needed of this outside Windows,
    /// where a file that's in use can be written over anyway.
    /// </summary>
    public static List<string> MoveAside(string folder)
    {
        var moved = new List<string>();
        if (!OperatingSystem.IsWindows() || !Directory.Exists(folder))
            return moved;

        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(file);
            if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Locked(file))
                continue;

            try
            {
                File.Move(file, FreeAsideName(file));
                moved.Add(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // One that won't move is left for the build to say so about
            }
        }

        return moved;
    }

    /// <summary>The files a build didn't write after all put back from where they were moved aside.</summary>
    public static int PutBack(IEnumerable<string> moved)
    {
        int back = 0;
        foreach (string file in moved)
        {
            if (File.Exists(file))
                continue;

            // The newest .old of it, the one just made
            string? aside = Directory.EnumerateFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file) + "*" + ASIDE)
                .Where(candidate => MovedAside().IsMatch(candidate)).OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(candidate => candidate.Length).FirstOrDefault();
            if (aside is null)
                continue;

            try
            {
                File.Move(aside, file);
                back++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return back;
    }

    /// <summary>
    /// What a build over Hex moved aside taken away as Hex starts, off the starting thread, and again a little later for the
    /// files the Hex that restarted into this one was still closing on.
    /// </summary>
    public static void CleanUpSoon()
    {
        if (!OperatingSystem.IsWindows())
            return;

        _ = Task.Run(async () =>
        {
            CleanUp(RunningFolder);
            await Task.Delay(TimeSpan.FromSeconds(20));
            CleanUp(RunningFolder);
        });
    }

    /// <summary>What was moved aside before takes away, as Hex starts. One still in use (another Hex of before) waits for the next start.</summary>
    public static int CleanUp(string folder)
    {
        int gone = 0;
        if (!Directory.Exists(folder))
            return gone;

        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*" + ASIDE, SearchOption.AllDirectories))
            {
                if (!MovedAside().IsMatch(file))
                    continue;

                try
                {
                    File.Delete(file);
                    gone++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return gone;
    }

    // Whether a file is in use, it won't open for writing with nobody else let in
    private static bool Locked(string file)
    {
        try
        {
            using FileStream stream = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Read only is a file the build can't write either, but moving it doesn't help that
            return false;
        }
    }

    // name.old, or name.1.old when an older one is still there and still locked
    private static string FreeAsideName(string file)
    {
        string aside = file + ASIDE;
        for (int i = 1; File.Exists(aside); i++)
        {
            try
            {
                File.Delete(aside);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                aside = $"{file}.{i}{ASIDE}";
            }
        }

        return aside;
    }

    /// <summary>
    /// The folders a command line builds into, from -p:OutDir or -o when it says, or else by what the projects it builds
    /// evaluate to. Asks the SDK, so it's for off the editor's thread.
    /// </summary>
    public static async Task<List<string>> OutputFoldersOf(string command, string folder, DotnetProjects projects, CancellationToken cancel = default)
    {
        var found = new List<string>();
        foreach (DotnetCommandLine line in DotnetCommandLine.Read(command, folder))
        {
            if (line.OutputFolder is { } output)
            {
                found.Add(output);
                continue;
            }

            string? target = line.Target ?? DotnetSolutions.BuildTargetIn(folder);
            if (target is null)
                continue;

            List<string> each = DotnetSolutions.ProjectsOf(target);
            DotnetEvaluation?[] evaluated = await Task.WhenAll(each.Take(16).Select(async project =>
            {
                try
                {
                    return await projects.Evaluate(project, cancel);
                }
                catch (Exception e) when (e is InvalidDataException or IOException or System.Text.Json.JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return null;
                }
            }));
            found.AddRange(evaluated.Where(evaluation => evaluation is { OutputFolder.Length: > 0 }).Select(evaluation => evaluation!.OutputFolder));
        }

        return [.. found.Distinct(PlatformPaths.Comparer)];
    }
}
