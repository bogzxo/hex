using Horizon.Core.Platform;
using System.Security.Cryptography;
using System.Text;

namespace Hex.Core;

internal static class PythonEnvironment
{
    internal static async Task<string> Create(string python, string root, Action<string> output, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        string folder = Path.Combine(Path.GetFullPath(root), ".venv");
        if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("The .venv path already exists. Select its interpreter or choose another project folder.");
        // Claim the destination without merging into a directory another process just created.
        string reservation = Path.Combine(Path.GetFullPath(root), ".hex-venv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reservation);
        try { Directory.Move(reservation, folder); }
        finally { if (Directory.Exists(reservation)) Directory.Delete(reservation); }
        try
        {
            var created = await CMakeProject.Run(python, root, ["-m", "venv", folder], cancel);
            output(created.Output);
            if (created.ExitCode != 0) throw new IOException("Python could not create the virtual environment. See Output for details.");
            string interpreter = ExecutableIn(folder);
            var pip = await CMakeProject.Run(interpreter, root, ["-m", "pip", "--version"], cancel);
            output(pip.Output);
            if (pip.ExitCode != 0) throw new IOException("The virtual environment was created, but pip is unavailable. See Output for details.");
            cancel.ThrowIfCancellationRequested();
            return interpreter;
        }
        catch
        {
            if (Directory.Exists(folder))
            {
                string preserved = folder + ".failed-" + Guid.NewGuid().ToString("N");
                try
                {
                    Directory.Move(folder, preserved);
                    output($"Incomplete environment preserved at {preserved}. You can retry creating .venv.");
                }
                catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException)
                {
                    output($"Could not preserve the incomplete environment separately: {recovery.Message}. Files remain at {folder}.");
                }
            }
            throw;
        }
    }

    internal static IReadOnlyDictionary<string, string> TerminalEnvironment(string interpreter, IReadOnlyDictionary<string, string>? inherited)
    {
        var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (inherited is not null)
            foreach (var pair in inherited) environment[pair.Key] = pair.Value;
        string scripts = Path.GetDirectoryName(Path.GetFullPath(interpreter))!;
        string? home = Path.GetDirectoryName(scripts);
        environment["PATH"] = scripts + Path.PathSeparator + (environment.GetValueOrDefault("PATH") ?? Horizon.Core.Processes.ProcessEnvironment.Path());
        environment["VIRTUAL_ENV"] = home is not null && File.Exists(Path.Combine(home, "pyvenv.cfg")) ? home : string.Empty;
        environment["PYTHONHOME"] = string.Empty;
        return environment;
    }

    private static string SelectionFile(string root)
    {
        string path = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hex", "Python",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + ".txt");
    }

    internal static void Select(string root, string? interpreter)
    {
        string file = SelectionFile(root);
        if (interpreter is null) { File.Delete(file); return; }
        interpreter = Path.GetFullPath(interpreter, root);
        if (!File.Exists(interpreter)) throw new FileNotFoundException("Python interpreter was not found.", interpreter);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temporary = file + "." + Guid.NewGuid() + ".tmp";
        try { File.WriteAllText(temporary, interpreter); File.Move(temporary, file, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string? Selected(string root)
    {
        try
        {
            string file = SelectionFile(root);
            if (!File.Exists(file)) return null;
            string path = File.ReadAllText(file).Trim();
            return Path.IsPathFullyQualified(path) && File.Exists(path) ? path : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static string ExecutableIn(string folder) => Path.Combine(folder,
        OperatingSystem.IsWindows() ? "Scripts" : "bin", OperatingSystem.IsWindows() ? "python.exe" : "python");

    public static string? FindLocal(string from, string root)
    {
        if (PlatformPaths.IsUnder(from, root) && Selected(root) is { } chosen) return chosen;
        for (string? folder = Path.GetFullPath(from); folder is not null && PlatformPaths.IsUnder(folder, root); folder = Path.GetDirectoryName(folder))
        {
            if (Selected(folder) is { } selected) return selected;
            foreach (string name in new[] { ".venv", "venv" })
                if (ExecutableIn(Path.Combine(folder, name)) is { } candidate && File.Exists(candidate)) return candidate;
            if (PlatformPaths.Same(folder, root)) break;
        }
        return null;
    }
}
