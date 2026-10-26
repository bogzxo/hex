using System.Collections.Concurrent;
using System.Diagnostics;

namespace Hex.Core.Projects;

/// <summary>
/// The MSBuild projects Hex has looked at, each evaluated by the SDK off the editor's thread and kept until something
/// that could change what it says changes, the project file, a Directory.Build.props or .targets over it (or
/// Directory.Packages.props), or a file made, renamed or taken away in its folder. A file's project is the nearest SDK
/// style project over it, the way a glob of the SDK takes in every file under a project's folder.
/// </summary>
internal sealed partial class DotnetProjects : IDisposable
{
    /// <summary>The one Hex uses.</summary>
    public static DotnetProjects Shared { get; } = new();

    // The files over a project that change what it evaluates to without being it
    private static readonly string[] Imported = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

    private sealed class Kept
    {
        public DotnetEvaluation? Evaluation;
        public string Stamp = string.Empty;
        public int Generation;
        public int EvaluatedAt = -1;
        public Task<DotnetEvaluation>? Going;
        public FileSystemWatcher? Watcher;
    }

    private readonly ConcurrentDictionary<string, Kept> kept = new(PlatformPaths.Comparer);

    /// <summary>A project's folder had a file made, renamed or taken away, or the project was evaluated again, by its path.</summary>
    public event Action<string>? Changed;

    /// <summary>How long an evaluation may take before it's given up on, a first run of the SDK on a cold machine is slow.</summary>
    public TimeSpan Patience { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The project a file belongs to, the nearest SDK style project file over it (its own folder first), null outside
    /// one. A folder is asked about the same way, from itself up.
    /// </summary>
    public static string? ProjectOf(string path)
    {
        string full = Path.GetFullPath(path);
        for (string? folder = Directory.Exists(full) ? full : Path.GetDirectoryName(full); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            try
            {
                foreach (string project in Directory.EnumerateFiles(folder, "*.*proj").Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (IsSdkProject(project))
                        return project;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A folder that can't be read has no project as far as this goes
            }
        }

        return null;
    }

    /// <summary>Whether a project file is SDK style, an Sdk on its Project, an Sdk element or the SDK's props imported by hand.</summary>
    public static bool IsSdkProject(string project)
    {
        if (Path.GetExtension(project) is not { Length: > 4 } extension || !extension.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            string text = File.ReadAllText(project);
            return ProjectXml.Read(text) is { Name: "Project" } root
                && (root.Attribute("Sdk") is not null || root.Child("Sdk") is not null || text.Contains("Sdk=\"", StringComparison.Ordinal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>What a project evaluated to the last time, when nothing it depends on has changed since, null otherwise.</summary>
    public DotnetEvaluation? Known(string project)
    {
        if (!kept.TryGetValue(Path.GetFullPath(project), out Kept? known) || known.Evaluation is null)
            return null;
        return known.EvaluatedAt == known.Generation && known.Stamp == StampOf(project) ? known.Evaluation : null;
    }

    /// <summary>
    /// What a project evaluates to, what was known when it's still good, the SDK asked otherwise (once at a time, asked
    /// twice the second waits for the first).
    /// </summary>
    public Task<DotnetEvaluation> Evaluate(string project, CancellationToken cancel = default)
    {
        string full = Path.GetFullPath(project);
        if (Known(full) is { } known)
            return Task.FromResult(known);

        Kept entry = kept.GetOrAdd(full, _ => new Kept());
        lock (entry)
        {
            if (entry.Going is { IsCompleted: false } going)
                return going;

            Watch(full, entry);
            int generation = entry.Generation;
            string stamp = StampOf(full);
            return entry.Going = Task.Run(async () =>
            {
                DotnetEvaluation evaluation = await RunEvaluation(full, cancel);
                lock (entry)
                {
                    entry.Evaluation = evaluation;
                    entry.Stamp = stamp;
                    entry.EvaluatedAt = generation;
                }

                Changed?.Invoke(full);
                return evaluation;
            }, cancel);
        }
    }

    /// <summary>What was known of a project forgotten, the next ask evaluates it again (after Hex wrote to it).</summary>
    public void Forget(string project)
    {
        if (kept.TryGetValue(Path.GetFullPath(project), out Kept? known))
        {
            lock (known)
                known.Generation++;
        }
    }

    /// <summary>The SDK asked, the items a Build Action can be and the properties Hex wants, then the project's own kinds too.</summary>
    private async Task<DotnetEvaluation> RunEvaluation(string project, CancellationToken cancel)
    {
        string[] types = [.. DotnetEvaluation.CommonItemTypes, "Page", "Resource", "ApplicationDefinition", "AvailableItemName"];
        DotnetEvaluation evaluation = DotnetEvaluation.Parse(project, await Msbuild(project, types, cancel));

        // A kind the project offers that wasn't asked about (an Avalonia project's) asked as well, once
        string[] more = [.. evaluation.ItemTypes.Where(type => !types.Contains(type, StringComparer.OrdinalIgnoreCase))];
        return more.Length == 0 ? evaluation : DotnetEvaluation.Parse(project, await Msbuild(project, [.. types, .. more], cancel));
    }

    private async Task<string> Msbuild(string project, string[] items, CancellationToken cancel)
    {
        var arguments = new List<string> { "msbuild", project, "-nologo" };
        arguments.AddRange(items.Select(item => "-getItem:" + item));
        arguments.AddRange(DotnetEvaluation.Asked.Select(property => "-getProperty:" + property));
        var (exit, said) = await DotnetCli.Run(Path.GetDirectoryName(project)!, arguments, Patience, cancel);
        if (exit != 0 && !said.TrimStart().StartsWith('{'))
            throw new InvalidDataException(said.Trim());
        return said;
    }

    /// <summary>What the project depends on as it is on the disk now, its file and the imports over it, their times.</summary>
    internal static string StampOf(string project)
    {
        var stamp = new System.Text.StringBuilder();
        stamp.Append(File.GetLastWriteTimeUtc(project).Ticks);
        for (string? folder = Path.GetDirectoryName(Path.GetFullPath(project)); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            foreach (string name in Imported)
            {
                string file = Path.Combine(folder, name);
                if (File.Exists(file))
                    stamp.Append('|').Append(file).Append('@').Append(File.GetLastWriteTimeUtc(file).Ticks);
            }
        }

        return stamp.ToString();
    }

    // A watch on the project's folder, files made, renamed or taken away (outside bin and obj) change what its globs take in
    private void Watch(string project, Kept entry)
    {
        if (entry.Watcher is not null)
            return;

        string folder = Path.GetDirectoryName(project)!;
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
            };
            // When the watch began, macOS says a file was made a while after it was, a project written just before it was
            // evaluated threw away the evaluation straight away
            DateTime since = DateTime.UtcNow;
            void Moved(string path, bool made)
            {
                string inside = Path.GetRelativePath(folder, path).Replace('\\', '/');
                if (inside.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) || inside.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) || inside is "bin" or "obj")
                    return;
                if (made && File.Exists(path) && File.GetLastWriteTimeUtc(path) < since)
                    return;

                lock (entry)
                    entry.Generation++;
                Changed?.Invoke(project);
            }

            watcher.Created += (_, e) => Moved(e.FullPath, made: true);
            watcher.Deleted += (_, e) => Moved(e.FullPath, made: false);
            watcher.Renamed += (_, e) => Moved(e.FullPath, made: false);
            watcher.EnableRaisingEvents = true;
            entry.Watcher = watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException)
        {
            // Without a watch a file made in the folder is seen the next time the project is written or Hex starts
        }
    }

    public void Dispose()
    {
        foreach (Kept entry in kept.Values)
            entry.Watcher?.Dispose();
        kept.Clear();
    }
}

/// <summary>The dotnet command run as a child, what it wrote and how it ended, without what launched Hex leaking into it.</summary>
internal static class DotnetCli
{
    // What `dotnet run` or Visual Studio sets for the MSBuild it is, which sends a child's SDK to the wrong place
    private static readonly string[] Inherited = ["MSBuildExtensionsPath", "MSBUILD_EXE_PATH", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly", "MSBUILDNOINPROCNODE"];

    public static async Task<(int Exit, string Said)> Run(string folder, IEnumerable<string> arguments, TimeSpan patience, CancellationToken cancel = default)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (string name in Inherited)
            start.Environment.Remove(name);
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        // Nothing left running behind it. MSBuild's reused nodes, its server and the compiler's server outlive dotnet with
        // its output pipe still open, so reading the output to its end never ended, Hex.Tests hung on Linux and macOS
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["UseSharedCompilation"] = "false";

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("dotnet didn't start");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancel);
        Task<string> errors = process.StandardError.ReadToEndAsync(cancel);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(patience);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        return (process.ExitCode, await output + await errors);
    }
}
