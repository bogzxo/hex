using System.Diagnostics;
using System.Text.RegularExpressions;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

/// <summary>
/// One server of language_servers.hor, what it is started with and what the settings say about it, how it is installed
/// and updated and where its releases are.
/// </summary>
/// <param name="Name">Its key in the file, csharp, python, which the settings keep its place and versions by.</param>
/// <param name="Command">The program, on the PATH or a full path, before the settings say where it is.</param>
/// <param name="About">A sentence on what it is, empty for nothing.</param>
/// <param name="Install">The command line that installs it, empty for one that is downloaded by hand.</param>
/// <param name="Update">The command line that updates it, the install one when the file doesn't say.</param>
/// <param name="Repo">Its repository on GitHub, owner/name, empty for none.</param>
/// <param name="VersionArguments">What makes it say its version, null for a server with no way to.</param>
/// <param name="Reload">
/// The files that, made, changed or taken away in the workspace, have the server started again ("*.csproj", "*.sln"),
/// for a server that reads its projects once as it starts and never again. Empty for none.
/// </param>
/// <param name="Settings">What the server is told to be, by its sections, null for nothing (see LanguageServer.Settings).</param>
/// <param name="Needs">
/// The files a server won't run without somewhere in its folder ("*.csproj", "*.sln"), by name or *.extension. Where
/// there's none it isn't started, csharp-ls stops the moment it starts on a lone file. Empty for a server that runs anywhere.
/// </param>
internal sealed record HexServerEntry(
    string Name,
    string Command,
    string[] Arguments,
    string LanguageId,
    string[] Extensions,
    string About,
    string Install,
    string Update,
    string Repo,
    string[]? VersionArguments,
    string[]? Reload = null,
    System.Text.Json.Nodes.JsonObject? Settings = null,
    string[]? Needs = null)
{
    /// <summary>Whether a file is one whose change has the server started again, by its name ("*.csproj" or a whole name).</summary>
    public bool ReloadsFor(string path) => Matches(Reload, path);

    /// <summary>Whether a file is one of those the server won't run without, see <see cref="Needs"/>.</summary>
    public bool NeedsFor(string path) => Matches(Needs, path);

    // Whether a file's name is one of some patterns, "*.csproj" or a whole name
    private static bool Matches(string[]? patterns, string path)
    {
        if (patterns is not { Length: > 0 })
            return false;

        string name = System.IO.Path.GetFileName(path);
        foreach (string pattern in patterns)
        {
            if (pattern.StartsWith("*.", StringComparison.Ordinal) ? name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) : name.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Where it says the releases are, on GitHub, empty without a repository.</summary>
    public string ReleasesPage => Repo.Length > 0 ? $"https://github.com/{Repo}/releases" : string.Empty;
}

/// <summary>Reading language_servers.hor, and finding out what is on this machine and which version of it.</summary>
internal static partial class HexServerCatalog
{
    /// <summary>The servers a file of them names, in its order. Throws for a file that makes no sense.</summary>
    public static List<HexServerEntry> Read(string text)
    {
        HIDLRuntime runtime = new() { Output = null };
        var (success, message) = runtime.Evaluate(text);
        if (!success || runtime.UserScope.Lookup("servers") is not ObjectValue servers)
            throw new Exception(success ? "it has to declare an object called 'servers'" : message);

        var entries = new List<HexServerEntry>();
        foreach (var (name, value) in servers.Properties)
        {
            if (value is not ObjectValue server || server.Properties.GetValueOrDefault("command") is not StringValue command)
                continue;

            string install = Text(server, "install");
            string update = Text(server, "update");
            entries.Add(new HexServerEntry(
                name,
                command.Value,
                Texts(server, "args") ?? [],
                Text(server, "language") is { Length: > 0 } language ? language : name,
                Texts(server, "extensions") ?? [],
                Text(server, "about"),
                install,
                update.Length > 0 ? update : install,
                Text(server, "repo"),
                Texts(server, "version"),
                Texts(server, "reload"),
                server.Properties.GetValueOrDefault("settings") is ObjectValue settings ? Json(settings) as System.Text.Json.Nodes.JsonObject : null,
                Texts(server, "needs")));
        }

        return entries;
    }

    /// <summary>A value of HIDL as the JSON a language server is told it in, an object, a list, a text, a number or a flag.</summary>
    public static System.Text.Json.Nodes.JsonNode? Json(IRuntimeValue value) => value switch
    {
        ObjectValue entries => new System.Text.Json.Nodes.JsonObject(entries.Properties.Select(pair =>
            new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(pair.Key, Json(pair.Value)))),
        ListValue items => new System.Text.Json.Nodes.JsonArray([.. items.Items.Select(Json)]),
        StringValue text => System.Text.Json.Nodes.JsonValue.Create(text.Value),
        NumberValue number => System.Text.Json.Nodes.JsonValue.Create(number.Value),
        BooleanValue flag => System.Text.Json.Nodes.JsonValue.Create(flag.Value),
        _ => null
    };

    // A text, or one a system ({ windows: ..., macos: ..., linux: ... }), this one's, empty for none
    private static string Text(ObjectValue of, string key) => of.Properties.GetValueOrDefault(key) switch
    {
        StringValue text => text.Value,
        ObjectValue bySystem => bySystem.Properties.GetValueOrDefault(SystemName) is StringValue mine ? mine.Value : string.Empty,
        _ => string.Empty
    };

    /// <summary>What language_servers.hor calls the system Hex is running on, windows, macos or linux.</summary>
    public static string SystemName => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    private static string[]? Texts(ObjectValue of, string key) =>
        of.Properties.GetValueOrDefault(key) is ListValue list ? [.. list.Items.OfType<StringValue>().Select(item => item.Value)] : null;

    /// <summary>
    /// Where a command is, the file itself for a path, otherwise the first match on the PATH (with PATHEXT's endings on
    /// Windows). Null for nowhere.
    /// </summary>
    public static string? Locate(string command)
    {
        if (command.Length == 0)
            return null;

        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(command) ? Path.GetFullPath(command) : null;

        string[] endings = OperatingSystem.IsWindows()
            ? ["", .. (System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)]
            : [""];

        foreach (string folder in (System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ending in endings)
            {
                try
                {
                    string candidate = Path.Combine(folder.Trim('"'), command + ending);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    break;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The version a program says it is, the first thing in what it prints that looks like one (0.13.0, 19.1.2,
    /// 2024-12-18), null when it says nothing like it or takes more than a few seconds. Blocks, for the background.
    /// </summary>
    public static string? VersionOf(string program, string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
                info.ArgumentList.Add(argument);

            using Process? process = Process.Start(info);
            if (process is null)
                return null;

            // Both read at once, a program that fills one pipe while the other is read stalls
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }

            return FindVersion(output.Result) ?? FindVersion(errors.Result);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The first thing in some text that looks like a version, null for none.</summary>
    public static string? FindVersion(string text) => VersionPattern().Match(text) is { Success: true } found ? found.Value : null;

    /// <summary>
    /// Whether one version is newer than another, by their numbers in order (v0.20.0 against 0.13.0, 2025-01-02 against
    /// 2024-12-18), what isn't a number left out. False when either has no numbers.
    /// </summary>
    public static bool IsNewer(string version, string than)
    {
        long[] a = Numbers(version), b = Numbers(than);
        if (a.Length == 0 || b.Length == 0)
            return false;

        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            long x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y)
                return x > y;
        }

        return false;
    }

    /// <summary>Whether two versions are the same one, v0.20.0 and 0.20 being.</summary>
    public static bool Same(string a, string b) => !IsNewer(a, b) && !IsNewer(b, a) && Numbers(a).Length > 0 && Numbers(b).Length > 0;

    private static long[] Numbers(string version) =>
        [.. NumberPattern().Matches(version).Select(match => long.TryParse(match.Value, out long number) ? number : 0)];

    [GeneratedRegex(@"\d+(?:[.\-]\d+)+")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();
}
