using System.Text.Json.Nodes;

namespace Hex.Core.Notebooks;

/// <summary>
/// A kernel that can be started, out of its kernel.json, the command line that starts it ({connection_file} in it for
/// where the ports and the key are), what it's called, its language, and whether it's interrupted by a signal (the
/// usual) or a message.
/// </summary>
internal sealed record KernelSpec(string Name, string DisplayName, string Language, IReadOnlyList<string> Argv, IReadOnlyDictionary<string, string> Env, bool InterruptByMessage, string Folder);

/// <summary>
/// Finding the kernels on this machine without asking Jupyter, which needn't be installed for a kernel to be. The
/// folders Jupyter looks in (JUPYTER_PATH, the user's, the system's, and the share/jupyter of every Python found), each
/// kernel a folder with a kernel.json. A Python that has ipykernel but never had its kernel.json written gets one made up
/// for it, and "python" at the start of a command line is the Python whose folder it was found in, as Jupyter has it.
/// </summary>
internal static class KernelSpecs
{
    /// <summary>Every kernel found, by name, the first found of a name kept. Slow-ish (it looks at the disk), off the editor's thread.</summary>
    public static List<KernelSpec> Find()
    {
        var found = new List<KernelSpec>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(KernelSpec spec)
        {
            if (names.Add(spec.Name))
                found.Add(spec);
        }

        List<string> pythons = Pythons();
        foreach (string folder in KernelFolders(pythons))
        {
            if (!Directory.Exists(folder))
                continue;

            foreach (string kernel in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Read(kernel, PythonOf(folder, pythons) ?? pythons.FirstOrDefault()) is { } spec)
                    Add(spec);
            }
        }

        // A Python with ipykernel in it and no kernel.json of its own
        foreach (string python in pythons)
        {
            if (HasIpykernel(python))
                Add(new KernelSpec("python3", "Python 3 (ipykernel)", "python", [python, "-m", "ipykernel_launcher", "-f", "{connection_file}"], new Dictionary<string, string>(), false, Path.GetDirectoryName(python)!));
        }

        return found;
    }

    /// <summary>A kernel.json read, null when there isn't one or it says nothing that can be started.</summary>
    public static KernelSpec? Read(string folder, string? python)
    {
        string file = Path.Combine(folder, "kernel.json");
        if (!File.Exists(file))
            return null;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject spec || spec["argv"] is not JsonArray argv || argv.Count == 0)
                return null;

            var line = argv.Select(part => part?.GetValue<string>() ?? string.Empty).ToList();
            if (python is not null && line[0] is "python" or "python3")
                line[0] = python;

            var env = new Dictionary<string, string>();
            if (spec["env"] is JsonObject vars)
            {
                foreach (var (variable, value) in vars)
                    env[variable] = value?.GetValue<string>() ?? string.Empty;
            }

            string name = Path.GetFileName(folder);
            return new KernelSpec(name, spec["display_name"]?.GetValue<string>() ?? name, spec["language"]?.GetValue<string>() ?? string.Empty,
                line, env, spec["interrupt_mode"]?.GetValue<string>() == "message", folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The kernel for a notebook, the one its metadata names, else the first of its language, else any.</summary>
    public static KernelSpec? For(IReadOnlyList<KernelSpec> specs, string? name, string? language) =>
        specs.FirstOrDefault(spec => string.Equals(spec.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? specs.FirstOrDefault(spec => string.Equals(spec.Language, language, StringComparison.OrdinalIgnoreCase))
        ?? specs.FirstOrDefault();

    // The folders kernels are kept in, in the order Jupyter looks
    private static IEnumerable<string> KernelFolders(List<string> pythons)
    {
        if (Environment.GetEnvironmentVariable("JUPYTER_PATH") is { Length: > 0 } extra)
        {
            foreach (string part in extra.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                yield return Path.Combine(part, "kernels");
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "jupyter", "kernels");
        else if (OperatingSystem.IsMacOS())
            yield return Path.Combine(home, "Library", "Jupyter", "kernels");
        else
            yield return Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(home, ".local", "share"), "jupyter", "kernels");

        foreach (string python in pythons)
            yield return Path.Combine(PrefixOf(python), "share", "jupyter", "kernels");

        if (OperatingSystem.IsWindows())
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "jupyter", "kernels");
        else
        {
            yield return "/usr/local/share/jupyter/kernels";
            yield return "/usr/share/jupyter/kernels";
        }
    }

    // The Python a kernel folder belongs to, the one it's the share/jupyter/kernels of
    private static string? PythonOf(string folder, List<string> pythons) =>
        pythons.FirstOrDefault(python => PlatformPaths.Same(Path.Combine(PrefixOf(python), "share", "jupyter", "kernels"), folder));

    /// <summary>Where a Python keeps its things, the folder it's in on Windows and the one over its bin elsewhere.</summary>
    public static string PrefixOf(string python)
    {
        string folder = Path.GetDirectoryName(python)!;
        if (OperatingSystem.IsWindows())
            return string.Equals(Path.GetFileName(folder), "Scripts", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(folder)! : folder;
        return Path.GetFileName(folder) == "bin" ? Path.GetDirectoryName(folder)! : folder;
    }

    // Whether ipykernel is installed into a Python, by its folder, without starting it
    private static bool HasIpykernel(string python)
    {
        string prefix = PrefixOf(python);
        if (OperatingSystem.IsWindows())
            return Directory.Exists(Path.Combine(prefix, "Lib", "site-packages", "ipykernel"));

        string lib = Path.Combine(prefix, "lib");
        return Directory.Exists(lib) && Directory.EnumerateDirectories(lib, "python3*").Any(version => Directory.Exists(Path.Combine(version, "site-packages", "ipykernel")));
    }

    /// <summary>
    /// The Pythons on this machine, the ones on the PATH first, then where python.org's installer and Homebrew put them.
    /// Windows' own python.exe that only opens the Store isn't one.
    /// </summary>
    public static List<string> Pythons()
    {
        var found = new List<string>();
        void Add(string path)
        {
            if (File.Exists(path) && !path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) && !found.Any(seen => PlatformPaths.Same(seen, path)))
                found.Add(Path.GetFullPath(path));
        }

        string[] names = OperatingSystem.IsWindows() ? ["python.exe"] : ["python3", "python"];
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in names)
            {
                try { Add(Path.Combine(folder.Trim('"'), name)); }
                catch (ArgumentException) { }
            }
        }

        if (OperatingSystem.IsWindows())
        {
            string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
            if (Directory.Exists(programs))
            {
                foreach (string version in Directory.EnumerateDirectories(programs, "Python3*").OrderDescending(StringComparer.OrdinalIgnoreCase))
                    Add(Path.Combine(version, "python.exe"));
            }
        }
        else
        {
            foreach (string path in (string[])["/opt/homebrew/bin/python3", "/usr/local/bin/python3", "/usr/bin/python3"])
                Add(path);
        }

        return found;
    }
}
