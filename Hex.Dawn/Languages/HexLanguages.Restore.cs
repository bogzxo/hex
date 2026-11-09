using System.Diagnostics;

using Horizon.Core.Threading;

namespace Hex.Dawn;

// A .NET folder opened that was never restored (a project of its with no obj/project.assets.json) has its packages
// restored in the background, quietly, the way Visual Studio restores a solution as it opens it. csharp-ls loaded it
// without them, every type out of a package was an error (DotTiled could not be found, 80 times in one file), a build
// restored them and the errors stayed, as it never loads a project again by itself. The restore writes the assets and
// the server is started again for them (the reload of language_servers.hor), so does a build that restores. And
// Restart Language Server in the palette starts the current file's server again by hand.
internal sealed partial class HexLanguages
{
    private const string RESTORE_WORK = "restore ";

    // What a restore writes next to each project, which the servers that load projects start again on
    internal const string ASSETS = "project.assets.json";

    // The folders already looked at for a restore this session, each once
    private readonly HashSet<string> restoreLooked = new(PlatformPaths.Comparer);

    /// <summary>Every folder of the workspace that has a solution or a project nobody restored has it restored, each folder once.</summary>
    internal void RestoreWhereNeeded()
    {
        if (context.Options.SelfTest || !ServerEntries.Any(RestoresFor))
            return;

        foreach (string folder in context.Workspace.Folders)
        {
            string root = Path.GetFullPath(folder);
            if (!restoreLooked.Add(root))
                continue;

            // What the folder has is read off the editor's thread, a solution of fifty projects is fifty folders looked in
            context.Work.InBackground(context.Strings.Said("languages.restore_looking"), () => Unrestored(root), target =>
            {
                if (target is not null)
                    Restore(root, target);
            }, quiet: true);
        }
    }

    // Whether a server is one that needs a project's packages restored to know its types, it's started again on the assets
    private static bool RestoresFor(HexServerEntry entry) => entry.ReloadsFor(ASSETS);

    /// <summary>
    /// What to restore in a folder, its solution when a project of that has no assets, or its own project when there's
    /// no solution and that has none. Null when everything has them, or there's nothing .NET here.
    /// </summary>
    internal static string? Unrestored(string folder)
    {
        string? solution = LanguageServers.SolutionIn(folder);
        if (solution is not null)
            return ProjectsOf(solution).Any(project => !Restored(project)) ? solution : null;

        try
        {
            string[] projects = [.. Directory.EnumerateFiles(folder, "*.csproj").Concat(Directory.EnumerateFiles(folder, "*.fsproj"))];
            return projects.Length == 1 && !Restored(projects[0]) ? projects[0] : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether a project has been restored, its assets in its obj folder. One that isn't there at all doesn't count.</summary>
    internal static bool Restored(string project) =>
        !File.Exists(project) || File.Exists(Path.Combine(Path.GetDirectoryName(project) ?? string.Empty, "obj", ASSETS));

    /// <summary>The projects a .sln or .slnx names, where they are, the folders of it (solution folders) left out.</summary>
    internal static List<string> ProjectsOf(string solution)
    {
        var found = new List<string>();
        string folder = Path.GetDirectoryName(Path.GetFullPath(solution)) ?? string.Empty;
        try
        {
            foreach (string line in File.ReadLines(solution))
            {
                // Project("{...}") = "Name", "Folder\Name.csproj", "{...}" in a .sln, <Project Path="Folder/Name.csproj" /> in a .slnx
                if (ProjectLine.Match(line) is { Success: true } named)
                {
                    string path = named.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    if (path.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
                        found.Add(Path.GetFullPath(Path.Combine(folder, path)));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return found;
    }

    private static readonly System.Text.RegularExpressions.Regex ProjectLine = new(@"^\s*(?:Project\(""[^""]*""\)\s*=\s*""[^""]*""\s*,\s*""(?<path>[^""]+)""|<Project\s+Path=""(?<path>[^""]+)"")", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// dotnet restore of a solution or a project, in the background with no card (the status bar's count of what's going
    /// has it, and stops it), what it said in the output. Once it's done the servers that run there start again.
    /// </summary>
    private void Restore(string root, string target)
    {
        string name = Path.GetFileName(target);
        context.Notices.Note($"> dotnet restore {name}");
        context.Work.Start(RESTORE_WORK + root, context.Strings.Said("languages.restoring", ("name", name)), async work =>
        {
            Horizon.Core.Processes.ProcessEnvironment.WaitForLoginShell();
            var info = new ProcessStartInfo("dotnet")
            {
                ArgumentList = { "restore", target, "--verbosity", "quiet" },
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };

            using Process process = Process.Start(info) ?? throw new InvalidOperationException("dotnet couldn't be started");
            process.StandardInput.Close();
            process.OutputDataReceived += (_, said) => { if (said.Data is { Length: > 0 } line) work.Write(line, show: false); };
            process.ErrorDataReceived += (_, said) => { if (said.Data is { Length: > 0 } line) work.Write(line, show: false); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(work.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                throw;
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException(context.Strings.Said("languages.restore_failed", ("name", name), ("code", process.ExitCode.ToString())));
        }, item =>
        {
            foreach (string said in item.Output)
                context.Notices.Note(said, error: item.State == WorkState.Failed);

            if (item.State == WorkState.Failed)
            {
                context.Notices.Say(item.Error ?? context.Strings.Said("languages.restore_failed", ("name", name), ("code", "?")), error: true);
                return;
            }

            if (item.State != WorkState.Succeeded)
                return;

            context.Notices.Say(context.Strings.Said("languages.restored", ("name", name)));

            // A watcher can miss the assets in a flood of writes, the servers that run here go again either way
            foreach (HexServerEntry entry in serverEntries.Where(RestoresFor))
            {
                if (LanguageServers.Running.Any(server => server.Name == entry.Name && PlatformPaths.IsUnder(server.RootDirectory, root)))
                    reloadIn[entry] = RELOAD_DELAY;
            }
        }, seen: false, cancellable: true);
    }

    /// <summary>
    /// Restart Language Server, the server of the file on screen stopped and started again with every open file of its,
    /// what it said of them forgotten until it says it again. For a server that lost track of its projects, the way
    /// Visual Studio's Unload and Reload Project is.
    /// </summary>
    private void RestartCurrentServer()
    {
        // The server the file is in, or the one it would be in when that one fell over or never started
        HexDocument current = context.Documents.Current;
        HexServerEntry? entry = LinterOf(current) is { } name ? ServerEntry(name)
            : current.Path is { } path && LanguageServers.RegistrationFor(ServerPathFor(current, path)) is { } registered ? ServerEntry(registered.Name) : null;
        if (entry is null)
        {
            context.Notices.Say(context.Strings.Said("languages.no_server_to_restart"), error: true);
            return;
        }

        reloadIn.Remove(entry);
        context.Notices.Say(context.Strings.Said("languages.restarting", ("command", Path.GetFileNameWithoutExtension(CommandOf(entry)))));
        if (linters.TryGetValue(current, out LanguageServer? server)) RestartStoppedServer(server);
        else RestartServer(entry);
    }
}
