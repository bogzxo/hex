namespace Hex.Core.Projects;

/// <summary>
/// A task's command line that's a dotnet run of one project and nothing else, what F5 debugs instead of running it in a
/// terminal. The project (--project, or the one in the folder it runs in), the configuration (-c), the launch profile
/// (--launch-profile, or none with --no-launch-profile), framework/runtime selection, explicit environment overrides (-e), and the program's own arguments after --. A run of the examples
/// picked beside the Run button ran in a terminal and went straight past every breakpoint.
/// </summary>
/// <param name="Arguments">The program's arguments, null when the command gives none and the launch profile's are used.</param>
internal sealed record DotnetRunLine(string Project, string? Configuration, string? Profile, bool NoProfile, IReadOnlyList<string>? Arguments)
{
    /// <summary>Explicit -e/--environment values, applied after launch-profile settings and kept literal.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public string? Framework { get; init; }
    public DotnetRuntimeOptions RuntimeOptions { get; init; } = new();

    public IReadOnlyDictionary<string, string> RuntimeEnvironment(DotnetLaunchProfile? profile)
    {
        var values = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (!NoProfile && profile is not null)
            foreach (var (name, value) in profile.RuntimeEnvironment()) values[name] = value;
        foreach (var (name, value) in Environment) values[name] = value;
        return values;
    }

    /// <summary>
    /// What a command line run in a folder says to dotnet run, null for one that isn't a dotnet run of a .csproj alone (a
    /// chain of commands among them, or a folder with no project or more than one).
    /// </summary>
    public static DotnetRunLine? Of(string command, string folder)
    {
        List<string> words = DotnetCommandLine.Words(command);
        if (words.Count < 2 || !Path.GetFileNameWithoutExtension(words[0]).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || !words[1].Equals("run", StringComparison.OrdinalIgnoreCase) || words.Any(word => word is "&&" or ";" or "||" or "|"))
            return null;

        string? project = null, configuration = null, profile = null, framework = null;
        string? runtime = null, architecture = null, os = null;
        bool? selfContained = null;
        bool noProfile = false;
        List<string>? arguments = null;
        var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (int i = 2; i < words.Count; i++)
        {
            string word = words[i];
            string? value = i + 1 < words.Count ? words[i + 1] : null;
            if (word == "--")
            {
                arguments = [.. words.Skip(i + 1)];
                break;
            }

            if (word == "--no-launch-profile")
            {
                noProfile = true;
                continue;
            }

            string? assignment = AttachedEnvironment(word);
            if (word is "-e" or "--environment")
            {
                if (value is null || value.StartsWith('-')) return null;
                assignment = value;
                i++;
            }
            if (assignment is not null)
            {
                int equals = assignment.IndexOf('=');
                string name = equals >= 0 ? assignment[..equals] : assignment;
                string text = equals >= 0 ? assignment[(equals + 1)..] : string.Empty;
                if (name.Length == 0 || name.Contains('\0') || text.Contains('\0')) return null;
                environment[name] = text;
                continue;
            }

            string? selectedFramework = AttachedFramework(word);
            if (word is "-f" or "--framework")
            {
                if (value is null) return null;
                selectedFramework = value;
                i++;
            }
            if (selectedFramework is not null)
            {
                if (selectedFramework.Length == 0 || !char.IsLetterOrDigit(selectedFramework[0]) || selectedFramework.Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '-' or '+')))
                    return null;
                framework = selectedFramework;
                continue;
            }

            var runtimeOption = RuntimeOption(word);
            if (runtimeOption.Option is {} option)
            {
                string? selected = runtimeOption.Value;
                if (option is "self-contained" or "no-self-contained" && selfContained is not null) return null;
                if (option == "no-self-contained")
                {
                    if (selected is not null) return null;
                    selfContained = false;
                    continue;
                }
                if (option == "self-contained")
                {
                    if (selected is null && value is not null && !value.StartsWith('-')) { selected = value; i++; }
                    if (selected is null) selfContained = true;
                    else if (bool.TryParse(selected, out bool enabled)) selfContained = enabled;
                    else return null;
                    continue;
                }
                if (selected is null) { selected = value; i++; }
                if (string.IsNullOrEmpty(selected) || !char.IsLetterOrDigit(selected[0])
                    || selected.Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '-' or '+' or '_'))) return null;
                if (option == "runtime") runtime = selected;
                else if (option == "arch") architecture = selected;
                else os = selected;
                continue;
            }

            // An option with a value has it in the next word, which isn't looked at again
            if (value is null)
                continue;
            switch (word)
            {
                case "--project" or "-p":
                    project = value;
                    break;
                case "-c" or "--configuration":
                    configuration = value;
                    break;
                case "--launch-profile" or "-lp":
                    profile = value;
                    break;
                case "-v" or "--verbosity":
                    break;
                default:
                    continue;
            }

            i++;
        }

        string? whole = Whole(project, folder);
        if (runtime is not null && (architecture is not null || os is not null)) return null;
        return whole is not null && whole.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(whole)
            ? new DotnetRunLine(whole, configuration, profile, noProfile, arguments) { Environment = environment, Framework = framework,
                RuntimeOptions = new(runtime, architecture, os, selfContained) }
            : null;
    }

    private static (string? Option, string? Value) RuntimeOption(string word)
    {
        foreach (var (spelling, option) in ((string, string)[])[("-r", "runtime"), ("--runtime", "runtime"), ("-a", "arch"), ("--arch", "arch"),
            ("--os", "os"), ("--sc", "self-contained"), ("--self-contained", "self-contained"), ("--no-self-contained", "no-self-contained")])
        {
            if (word == spelling) return (option, null);
            if (word.Length > spelling.Length && word.StartsWith(spelling, StringComparison.Ordinal) && word[spelling.Length] is '=' or ':')
                return (option, word[(spelling.Length + 1)..]);
        }
        return (null, null);
    }

    private static string? AttachedEnvironment(string word)
    {
        foreach (string option in (string[])["--environment", "-e"])
            if (word.Length > option.Length && word.StartsWith(option, StringComparison.Ordinal) && word[option.Length] is '=' or ':')
                return word[(option.Length + 1)..];
        return null;
    }

    private static string? AttachedFramework(string word)
    {
        foreach (string option in (string[])["--framework", "-f"])
            if (word.Length > option.Length && word.StartsWith(option, StringComparison.Ordinal) && word[option.Length] is '=' or ':')
                return word[(option.Length + 1)..];
        return null;
    }

    // The project named, made whole from the folder, a folder's own project when it names a folder or nothing
    private static string? Whole(string? project, string folder)
    {
        try
        {
            string path = Path.GetFullPath(Path.Combine(folder, (project ?? ".").Trim('"')));
            if (!Directory.Exists(path))
                return path;

            string[] found = Directory.GetFiles(path, "*.csproj");
            return found.Length == 1 ? found[0] : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
