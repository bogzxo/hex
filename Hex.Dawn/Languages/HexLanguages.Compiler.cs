using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Hex.Dawn;

// What only a compiler finds, found before anything is built. serve-d reads a D file the way D-Scanner does, what's
// written wrong and what looks off, and never which names there are or what fits where, so a string put in an int or a
// call to nothing went unsaid until a build. dmd (or ldmd2) looks over the text in the editor with -o-, nothing written,
// as the file opens, a moment after the typing stops and as it's saved, with the import folders of its dub project (dub
// describe) or the folder its module declaration says its packages start in. What it says goes under the code with the
// server's, a tenth of a second or so a file.
internal sealed partial class HexLanguages
{
    // How long after the last keystroke a file is checked, a little longer than the server waits
    private const float CHECK_DELAY = 0.8f;

    // How long a check or a dub describe may take before it's given up on (the first describe of a project may fetch)
    private static readonly TimeSpan CheckLimit = TimeSpan.FromSeconds(15), DescribeLimit = TimeSpan.FromSeconds(60);

    // The files waiting to be checked and how long until, what the check last found of each, and the one going now
    private readonly Dictionary<HexDocument, float> checkIn = [];
    private readonly Dictionary<HexDocument, IReadOnlyList<UICodeDiagnostic>> checkFound = [];
    private Task<IReadOnlyList<UICodeDiagnostic>?>? checking;
    private HexDocument? checkingOf;

    // The compiler that checks D, looked for once a run and again when the PATH may have changed, null for none
    private string? dChecker;
    private bool dCheckerLooked;

    // A dub project's import folders by its folder, with when its dub.json or dub.sdl was written
    private static readonly ConcurrentDictionary<string, (DateTime Written, string[] Imports)> dubImports = new(PlatformPaths.Comparer);

    private static readonly Regex ModuleDeclaration = new(@"^\s*module\s+([\w.]+)\s*;", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Whether a file is one the compiler looks over.</summary>
    private static bool Checked(HexDocument of) => of.Path is { } path && Path.GetExtension(path).ToLowerInvariant() is ".d" or ".di";

    /// <summary>A file is checked so long from now, opened, typed into or saved.</summary>
    private void CheckSoon(HexDocument of, float delay = CHECK_DELAY)
    {
        if (Checked(of) && DChecker() is not null)
            checkIn[of] = delay;
    }

    /// <summary>
    /// The other D files open checked again a little after the one typed into, they read its unsaved text and what they
    /// said about it may be old.
    /// </summary>
    private void CheckOthersSoon(HexDocument changed)
    {
        if (!Checked(changed))
            return;

        foreach (HexDocument other in context.Documents.All)
        {
            if (other != changed && Checked(other) && !checkIn.ContainsKey(other))
                CheckSoon(other, CHECK_DELAY * 2.0f);
        }
    }

    /// <summary>
    /// What the compiler said about a file, nothing while the server says something's written wrong in it. dmd stops at
    /// what doesn't parse and says it again its own way a line or two on, one stray box. was five rows.
    /// </summary>
    private IReadOnlyList<UICodeDiagnostic>? CheckDiagnosticsOf(HexDocument of)
    {
        if (!checkFound.TryGetValue(of, out IReadOnlyList<UICodeDiagnostic>? found) || found.Count == 0)
            return null;

        return lintFound.GetValueOrDefault(of) is { } server && server.Any(diagnostic => !diagnostic.Warning) ? null : found;
    }

    /// <summary>dmd, or ldmd2 (LDC with dmd's flags), the first on the PATH, null for neither.</summary>
    internal string? DChecker()
    {
        if (!dCheckerLooked)
        {
            dCheckerLooked = true;
            PlatformPaths.RefreshPath();
            dChecker = ((string[])["dmd", "ldmd2"]).FirstOrDefault(LanguageServers.IsAvailable);
        }

        return dChecker;
    }

    /// <summary>Every tick, the check that's due started (one at a time), and the one that finished taken in.</summary>
    private void UpdateChecks(float dt)
    {
        if (checking is { IsCompleted: true } done)
        {
            if (checkingOf is { } of && context.Documents.All.Contains(of) && done.Result is { } found)
            {
                checkFound[of] = found;
                context.Documents.DiagnosticsChanged(of);
            }

            (checking, checkingOf) = (null, null);
        }

        if (checking is not null || checkIn.Count == 0)
            return;

        foreach (HexDocument due in checkIn.Keys.ToArray())
        {
            if ((checkIn[due] -= dt) > 0.0f)
                continue;

            checkIn.Remove(due);
            if (DChecker() is not { } compiler || due.Path is not { } path)
                continue;

            string text = due.Editor.Text;
            IReadOnlyList<string> roots = context.Workspace.Folders;

            // The other D files open with changes, which are what the compiler is to read for them, not their files
            List<(string Path, string Text)> unsaved = [.. context.Documents.All
                .Where(open => open != due && Checked(open) && (open.Modified || open.CodeEdited))
                .Select(open => (open.Path!, open.Editor.Text))];
            checkingOf = due;
            checking = Task.Run(() => CheckD(compiler, path, text, roots, unsaved));
            return;
        }
    }

    // How many C# projects Check All Files builds at most, a folder of the engine has dozens
    private const int MOST_PROJECTS = 8;

    /// <summary>
    /// The code files of some folders checked, off the editor's thread. Every D file looked over by the compiler the way
    /// one that's open is, and every C# project built (dotnet build, what Visual Studio's Build does) with what it says
    /// read off it. How many files and projects, and what's wrong in them.
    /// </summary>
    internal static (int Files, List<HexResult> Problems) CheckCode(IReadOnlyList<string> roots, string? dChecker)
    {
        int files = 0;
        var found = new List<HexResult>();
        foreach (string root in roots)
        {
            if (dChecker is not null)
            {
                foreach (string file in HexSearch.FilesIn(root, "*.d"))
                {
                    files++;
                    string text;
                    try
                    {
                        text = File.ReadAllText(file).ReplaceLineEndings("\n");
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (UICodeDiagnostic wrong in CheckD(dChecker, file, text, roots) ?? [])
                        found.Add(new HexResult(file, wrong.Line, wrong.Column, wrong.Message, Warning: wrong.Warning));
                }
            }

            foreach (string project in HexSearch.FilesIn(root, "*.csproj").Take(MOST_PROJECTS))
            {
                files++;
                found.AddRange(BuildProblems(project));
            }
        }

        return (files, [.. found.Distinct()]);
    }

    // A C# project built, what the build said is wrong in it
    private static List<HexResult> BuildProblems(string project)
    {
        var found = new List<HexResult>();
        string folder = Path.GetDirectoryName(project)!;
        try
        {
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in (string[])["build", project, "-nologo", "-v", "q", "-clp:NoSummary"])
                start.ArgumentList.Add(argument);

            using Process process = Process.Start(start) ?? throw new InvalidOperationException();
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
            {
                process.Kill(entireProcessTree: true);
                return found;
            }

            var reader = new TerminalProblemReader(TerminalProblemMatcher.Shipped, problem =>
            {
                string file = Path.IsPathRooted(problem.File) ? problem.File : Path.Combine(folder, problem.File);
                found.Add(new HexResult(Path.GetFullPath(file), Math.Max(0, problem.Line - 1), Math.Max(0, problem.Column - 1), DawnScene.CodeAfter(problem.Message), Warning: problem.Warning));
            });
            reader.Feed((output.Result + "\n" + errors.Result).ReplaceLineEndings("\n") + "\n");
        }
        catch (Exception)
        {
            // No dotnet, nothing built
        }

        return found;
    }

    /// <summary>A file let go of, what was found of it goes.</summary>
    private void ForgetCheck(HexDocument of)
    {
        checkIn.Remove(of);
        checkFound.Remove(of);
    }

    /// <summary>
    /// The text of a D file looked over by a compiler, what it's wrong about as diagnostics of that file, null when the
    /// compiler couldn't be asked. Errors in the modules it imports are theirs, not said here.
    /// </summary>
    internal static IReadOnlyList<UICodeDiagnostic>? CheckD(string compiler, string path, string text, IReadOnlyList<string> roots, IReadOnlyList<(string Path, string Text)>? unsaved = null)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory;
        List<string> imports = [.. ImportFoldersOf(path, text, roots)];
        string? overlay = Overlay(imports, unsaved ?? []);
        try
        {
            return CheckD(compiler, path, text, folder, overlay is null ? imports : [overlay, .. imports]);
        }
        finally
        {
            if (overlay is not null)
            {
                try { Directory.Delete(overlay, recursive: true); }
                catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// The files open with changes written to a folder of their own the way they sit under their import folders, for
    /// the compiler to find before the files. It read them off the disk, and a rename across files that wasn't saved
    /// yet had it say the new name didn't exist. Null for none.
    /// </summary>
    private static string? Overlay(IReadOnlyList<string> imports, IReadOnlyList<(string Path, string Text)> unsaved)
    {
        string? overlay = null;
        foreach (var (file, changed) in unsaved)
        {
            if (imports.FirstOrDefault(folder => PlatformPaths.IsUnder(file, folder)) is not { } under)
                continue;

            overlay ??= Path.Combine(Path.GetTempPath(), "hex-dmd-" + Guid.NewGuid().ToString("N")[..12]);
            string to = Path.Combine(overlay, Path.GetRelativePath(under, file));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.WriteAllText(to, changed);
            }
            catch (Exception)
            {
                // That one's read off the disk
            }
        }

        return overlay;
    }

    // The check itself, with the import folders it's given
    private static IReadOnlyList<UICodeDiagnostic>? CheckD(string compiler, string path, string text, string folder, IReadOnlyList<string> imports)
    {
        var start = new ProcessStartInfo(compiler)
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Nothing written, every column said, every error said, then where the imports are and the text from stdin
        foreach (string argument in (string[])["-o-", "-vcolumns", "-verrors=0", "-wi"])
            start.ArgumentList.Add(argument);
        foreach (string import in imports)
            start.ArgumentList.Add("-I" + import);
        start.ArgumentList.Add("-");

        string said;
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException();
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(text);
            process.StandardInput.Close();
            if (!process.WaitForExit(CheckLimit))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            said = output.Result + "\n" + errors.Result;
        }
        catch (Exception)
        {
            return null;
        }

        return DiagnosticsIn(said, text, Path.GetFileName(path));
    }

    /// <summary>What a compiler said about a file read from stdin (__stdin.d) as diagnostics of it, with what said it after each.</summary>
    internal static List<UICodeDiagnostic> DiagnosticsIn(string said, string text, string fileName)
    {
        var found = new List<UICodeDiagnostic>();
        var reader = new TerminalProblemReader([.. TerminalProblemMatcher.Shipped.Where(matcher => matcher.Name == "d")], problem =>
        {
            string file = Path.GetFileName(problem.File);
            if (file != "__stdin.d" && !file.Equals(fileName, PlatformPaths.Comparison))
                return;

            int line = Math.Max(0, problem.Line - 1), column = Math.Max(0, problem.Column - 1);
            found.Add(new UICodeDiagnostic(line, column, DawnScene.WordLength(text, line, column), problem.Message + " (dmd)", problem.Warning));
        });
        reader.Feed(said.ReplaceLineEndings("\n") + "\n\n\n");
        return found;
    }

    /// <summary>
    /// Where a D file's imports are looked for. Its dub project's folders, as dub describes them (its dependencies' with
    /// them), or else the folder its packages start in (source for module geometry.shapes in source/geometry), with the
    /// project's or the workspace's source or src folder, and the workspace's folder it's in.
    /// </summary>
    internal static IEnumerable<string> ImportFoldersOf(string path, string text, IReadOnlyList<string> roots)
    {
        string full = Path.GetFullPath(path);
        var folders = new List<string>();
        void Add(string? folder)
        {
            if (folder is not null && Directory.Exists(folder) && !folders.Any(known => PlatformPaths.Same(known, folder)))
                folders.Add(folder);
        }

        if (DubProjectOf(full) is { } project)
        {
            foreach (string described in DubImports(project))
                Add(described);
        }

        // The folder the module's packages start in, as many folders up as its name has dots
        string? top = Path.GetDirectoryName(full);
        if (ModuleDeclaration.Match(text) is { Success: true } declared)
        {
            int depth = declared.Groups[1].Value.Count(character => character == '.');
            for (int up = 0; up < depth && top is not null; up++)
                top = Path.GetDirectoryName(top);
        }

        Add(top);
        Add(Path.GetDirectoryName(full));
        if (roots.Where(root => PlatformPaths.IsUnder(full, root)).OrderByDescending(root => root.Length).FirstOrDefault() is { } root)
        {
            Add(Path.Combine(root, "source"));
            Add(Path.Combine(root, "src"));
            Add(root);
        }

        return folders;
    }

    /// <summary>The folder of the dub project a file is in, the nearest one over it with a dub.json or a dub.sdl, null for none.</summary>
    internal static string? DubProjectOf(string file)
    {
        for (string? folder = Path.GetDirectoryName(file); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (File.Exists(Path.Combine(folder, "dub.json")) || File.Exists(Path.Combine(folder, "dub.sdl")))
                return folder;
        }

        return null;
    }

    /// <summary>
    /// A dub project's import folders, its own and its dependencies', asked of dub once and again when its dub.json or
    /// dub.sdl is written. Its source and src folders when dub can't be asked.
    /// </summary>
    private static string[] DubImports(string project)
    {
        string recipe = File.Exists(Path.Combine(project, "dub.json")) ? Path.Combine(project, "dub.json") : Path.Combine(project, "dub.sdl");
        DateTime written = File.GetLastWriteTimeUtc(recipe);
        if (dubImports.TryGetValue(project, out var known) && known.Written == written)
            return known.Imports;

        string[] imports = [Path.Combine(project, "source"), Path.Combine(project, "src")];
        try
        {
            var start = new ProcessStartInfo("dub") { WorkingDirectory = project, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in (string[])["describe", "--import-paths"])
                start.ArgumentList.Add(argument);

            using Process process = Process.Start(start) ?? throw new InvalidOperationException();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (process.WaitForExit(DescribeLimit) && process.ExitCode == 0)
            {
                string[] described = [.. output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(Path.IsPathRooted)];
                if (described.Length > 0)
                    imports = described;
            }
            else if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // No dub, the usual folders
        }

        dubImports[project] = (written, imports);
        return imports;
    }
}
