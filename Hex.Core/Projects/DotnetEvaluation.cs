using System.Text.Json;

namespace Hex.Core.Projects;

/// <summary>An item of an evaluated project, its kind, how the project wrote it, the file it is and its metadata.</summary>
internal sealed record DotnetItem(string Type, string Identity, string FullPath, IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>Its Copy to Output Directory as the project says it, Never when it says nothing.</summary>
    public string CopyToOutput => DotnetEvaluation.Normalise(Metadata.GetValueOrDefault(ProjectFileEdits.COPY_TO_OUTPUT));

    /// <summary>The file the item was made in, the project, a Directory.Build.props or one of the SDK's.</summary>
    public string DefinedIn => Metadata.TryGetValue("DefiningProjectFullPath", out string? file) ? file : string.Empty;
}

/// <summary>
/// What the SDK itself says a project is, `dotnet msbuild -getItem -getProperty` read back. Globs, the SDK's defaults,
/// Directory.Build.props and the project's own items all count, the same way a build sees them, which no reading of the
/// .csproj by hand gets right.
/// </summary>
internal sealed class DotnetEvaluation
{
    /// <summary>What Copy to Output Directory says, Visual Studio's Do not copy, Copy always and Copy if newer.</summary>
    public const string NEVER = "Never", ALWAYS = "Always", IF_NEWER = "PreserveNewest";

    /// <summary>The item kinds a Build Action can be in every project, before the ones a project adds of its own.</summary>
    public static readonly string[] CommonItemTypes = ["None", "Compile", "Content", "EmbeddedResource", "AdditionalFiles"];

    /// <summary>The properties asked of every project.</summary>
    public static readonly string[] Asked = ["OutDir", "TargetPath", "AssemblyName", "OutputType", "TargetFramework", "TargetFrameworks", "UsingMicrosoftNETSdk"];

    public string Project { get; init; } = string.Empty;
    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DotnetItem> Items { get; } = [];

    /// <summary>The item kinds the project offers as a Build Action (its AvailableItemName), the common ones first.</summary>
    public List<string> ItemTypes { get; } = [];

    public string Folder => Path.GetDirectoryName(Project) ?? string.Empty;

    /// <summary>The folder a build puts the program in, made whole, empty when the project doesn't say (several frameworks).</summary>
    public string OutputFolder => Properties.TryGetValue("OutDir", out string? outDir) && outDir.Length > 0 ? Path.GetFullPath(Path.Combine(Folder, outDir)) : string.Empty;

    public string TargetPath => Properties.GetValueOrDefault("TargetPath") ?? string.Empty;

    /// <summary>Whether the project makes a program to run rather than a library.</summary>
    public bool IsProgram => Properties.GetValueOrDefault("OutputType") is { } type && (type.Equals("Exe", StringComparison.OrdinalIgnoreCase) || type.Equals("WinExe", StringComparison.OrdinalIgnoreCase));

    /// <summary>The items a file is in, of every kind that was asked.</summary>
    public IEnumerable<DotnetItem> ItemsOf(string file)
    {
        string full = Path.GetFullPath(file);
        return Items.Where(item => PlatformPaths.Same(item.FullPath, full));
    }

    /// <summary>The Build Action of a file, its first item of a kind that's a Build Action, null when it's in none.</summary>
    public DotnetItem? BuildItemOf(string file) =>
        ItemsOf(file).Where(item => !item.Type.Equals("AvailableItemName", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Type.Equals("None", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();

    /// <summary>
    /// What dotnet msbuild wrote, read. It writes the JSON alone when it worked and its errors as text otherwise, which is
    /// thrown with what it said.
    /// </summary>
    public static DotnetEvaluation Parse(string project, string said)
    {
        int start = said.IndexOf('{');
        if (start < 0)
            throw new InvalidDataException(said.Trim() is { Length: > 0 } why ? why : "dotnet msbuild said nothing");

        var evaluation = new DotnetEvaluation { Project = Path.GetFullPath(project) };
        using JsonDocument json = JsonDocument.Parse(said[start..]);
        if (json.RootElement.TryGetProperty("Properties", out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
                evaluation.Properties[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        var offered = new List<string>();
        if (json.RootElement.TryGetProperty("Items", out JsonElement items))
        {
            foreach (JsonProperty kind in items.EnumerateObject())
            {
                foreach (JsonElement item in kind.Value.EnumerateArray())
                {
                    var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonProperty each in item.EnumerateObject())
                        metadata[each.Name] = each.Value.ValueKind == JsonValueKind.String ? each.Value.GetString() ?? string.Empty : each.Value.ToString();

                    string identity = metadata.GetValueOrDefault("Identity") ?? string.Empty;
                    if (kind.Name.Equals("AvailableItemName", StringComparison.OrdinalIgnoreCase))
                    {
                        offered.Add(identity);
                        continue;
                    }

                    string full = metadata.GetValueOrDefault("FullPath") is { Length: > 0 } path ? path : Path.GetFullPath(Path.Combine(evaluation.Folder, identity));
                    evaluation.Items.Add(new DotnetItem(kind.Name, identity, full, metadata));
                }
            }
        }

        // The common ones in Visual Studio's order, then whatever else the project offers (Page, Resource, AvaloniaXaml)
        evaluation.ItemTypes.AddRange(CommonItemTypes);
        foreach (string type in offered.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!evaluation.ItemTypes.Contains(type, StringComparer.OrdinalIgnoreCase) && !type.StartsWith('_'))
                evaluation.ItemTypes.Add(type);
        }

        return evaluation;
    }

    /// <summary>Copy to Output Directory as one of its three, whatever case and blanks a project wrote it in.</summary>
    public static string Normalise(string? copy) => copy?.Trim() switch
    {
        null or "" => NEVER,
        var said when said.Equals(ALWAYS, StringComparison.OrdinalIgnoreCase) => ALWAYS,
        var said when said.Equals(IF_NEWER, StringComparison.OrdinalIgnoreCase) => IF_NEWER,
        var said when said.Equals("IfDifferent", StringComparison.OrdinalIgnoreCase) => "IfDifferent",
        _ => NEVER
    };
}
