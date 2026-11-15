using System.Diagnostics;
using System.Text;

using Hex.Core.Git;

namespace Hex.Core.Projects;

/// <summary>What New Project was asked to make, the kind, the name, the folder it goes in and the extras.</summary>
/// <param name="Folder">The project's own folder, the place picked with the name on the end.</param>
/// <param name="Optional">Whether the step the kind can leave out is run (Python's venv).</param>
/// <param name="Git">Whether the folder is made a repository once it's all there.</param>
/// <param name="Contents">
/// Its files and commands worked out already, which an extension's project has to be on the editor's thread. Null works
/// them out as it's made, fine for Hex's own.
/// </param>
internal sealed record ProjectRequest(ProjectTemplate Template, string Name, string Folder, bool Optional, bool Git, ProjectContents? Contents = null);

/// <summary>
/// Making a project, its folder, its files, the commands of its tools and git init. Blocks the whole way, so it's for the
/// background. Every step is said as it starts, and the first that goes wrong stops the rest and is what it hands back.
/// </summary>
internal static class ProjectCreation
{
    // dotnet new restores the project as well, which is the network on a cold cache, so it gets a while
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(5);

    /// <summary>Whether a folder will take a new project, there and empty or not there at all.</summary>
    public static bool FolderFree(string folder) =>
        !File.Exists(folder) && (!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any());

    /// <summary>Makes a project, what went wrong handed back and null when nothing did.</summary>
    public static string? Make(ProjectRequest request, Action<string> say, CancellationToken cancel = default)
    {
        string folder = request.Folder;
        if (request.Contents is null && request.Template.Source is not null)
            return "an extension's project has to be worked out before it's made";
        if (!FolderFree(folder))
            return $"{folder} is there already and has things in it";

        try
        {
            Directory.CreateDirectory(folder);
            ProjectContents contents = request.Contents ?? ProjectTemplates.Contents(request.Template, request.Name, request.Optional);
            string inside = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            // The files first, the SDK's templates write round a .gitignore happily and a venv wants its pyproject there
            foreach (var (relative, text) in contents.Files)
            {
                string path = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));

                // An extension's file goes in the project's folder or nowhere, ../ doesn't take one out of it
                if (!path.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
                    return $"{relative} is outside the project's folder";
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            foreach (string[] command in contents.Commands)
            {
                if (command.Length == 0)
                    continue;
                cancel.ThrowIfCancellationRequested();
                say(string.Join(' ', command));
                var (exit, said) = Run(folder, command, cancel);
                if (exit != 0)
                    return $"{string.Join(' ', command)} ended with {exit}. {LastLine(said)}".TrimEnd();
            }

            if (request.Git)
            {
                say("git init");
                if (GitRepository.Init(folder) is { } why)
                    return $"git init didn't work. {why}";
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return e.Message;
        }
    }

    // A command run in the folder to its end, dotnet the way Hex always runs it and the rest found on the PATH the way
    // a language server is (npm's .cmd shims and all)
    private static (int Exit, string Said) Run(string folder, string[] command, CancellationToken cancel)
    {
        if (command[0] == "dotnet")
            return DotnetCli.Run(folder, command[1..], Patience, cancel).GetAwaiter().GetResult();

        ProcessStartInfo info = Horizon.UI.Scripting.LanguageServerProgram.StartInfo(command[0], command[1..]);
        info.WorkingDirectory = folder;
        using Process process = Process.Start(info) ?? throw new InvalidOperationException($"{command[0]} didn't start");
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancel), errors = process.StandardError.ReadToEndAsync(cancel);
        if (!process.WaitForExit(Patience))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, $"{command[0]} took longer than {Patience.TotalMinutes} minutes");
        }

        return (process.ExitCode, output.Result + errors.Result);
    }

    // The last thing a tool said, which is usually the error and not the banner
    private static string LastLine(string said) =>
        said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;

    /// <summary>
    /// Whether the program a step needs is on the machine, dotnet for the .NET ones and python for a venv. Looks on the
    /// PATH, so it's cheap enough for a click but not for every tick.
    /// </summary>
    public static bool Has(string program) => program.Length == 0 || Horizon.UI.Scripting.LanguageServerProgram.Locate(program) is not null;
}
