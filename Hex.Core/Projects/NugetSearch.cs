using System.Text.Json;

namespace Hex.Core.Projects;

/// <summary>A package nuget.org found, its id, its newest version, what it says it is, how often it's been taken and every version it has, newest first.</summary>
internal sealed record NugetPackage(string Id, string Version, string Description, long Downloads, IReadOnlyList<string> Versions);

/// <summary>
/// nuget.org's search, what Manage NuGet Packages lists. The search service is found once through the feed's service
/// index the way NuGet itself finds it, every search after goes straight to it. Never on the editor's thread, the
/// chooser runs it off it with the last search cancelled for every key typed.
/// </summary>
internal static class NugetSearch
{
    private const string SERVICE_INDEX = "https://api.nuget.org/v3/index.json";

    // How many packages a search lists, a chooser shows no more than that anyway
    private const int TAKE = 25;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Where the feed searches, asked of its service index the first time and kept after
    private static string? searchService;

    /// <summary>The packages that fit what was typed, the best first, stable versions only.</summary>
    public static async Task<List<NugetPackage>> Search(string query, CancellationToken cancel = default)
    {
        string service = searchService ??= await SearchService(cancel);
        string url = $"{service}?q={Uri.EscapeDataString(query.Trim())}&take={TAKE}&prerelease=false&semVerLevel=2.0.0";
        return Parse(await Http.GetStringAsync(url, cancel));
    }

    /// <summary>The package of an id as nuget.org has it, null when it hasn't.</summary>
    public static async Task<NugetPackage?> Find(string id, CancellationToken cancel = default) =>
        (await Search("packageid:" + id, cancel)).Find(package => package.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // The search service the feed's service index names, the first of the kind
    private static async Task<string> SearchService(CancellationToken cancel)
    {
        using JsonDocument index = JsonDocument.Parse(await Http.GetStringAsync(SERVICE_INDEX, cancel));
        foreach (JsonElement resource in index.RootElement.GetProperty("resources").EnumerateArray())
        {
            if (resource.TryGetProperty("@type", out JsonElement type) && type.GetString() is { } kind && kind.StartsWith("SearchQueryService", StringComparison.Ordinal)
                && resource.TryGetProperty("@id", out JsonElement id) && id.GetString() is { Length: > 0 } url)
                return url;
        }

        throw new InvalidDataException("nuget.org's service index names no search");
    }

    /// <summary>What a search answered, read into packages.</summary>
    internal static List<NugetPackage> Parse(string json)
    {
        var found = new List<NugetPackage>();
        using JsonDocument read = JsonDocument.Parse(json);
        if (!read.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            return found;

        foreach (JsonElement package in data.EnumerateArray())
        {
            if (Text(package, "id") is not { Length: > 0 } id)
                continue;

            var versions = new List<string>();
            if (package.TryGetProperty("versions", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement version in listed.EnumerateArray())
                {
                    if (Text(version, "version") is { Length: > 0 } said)
                        versions.Add(said);
                }
            }

            // The feed lists them oldest first
            versions.Reverse();
            string newest = Text(package, "version") ?? versions.FirstOrDefault() ?? string.Empty;
            long downloads = package.TryGetProperty("totalDownloads", out JsonElement total) && total.TryGetInt64(out long count) ? count : 0;
            found.Add(new NugetPackage(id, newest, Text(package, "description") ?? string.Empty, downloads, versions.Count > 0 ? versions : [newest]));
        }

        return found;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A count of downloads said short, 1.2M, 35K.</summary>
    public static string Short(long downloads) => downloads switch
    {
        >= 1_000_000_000 => FormattableString.Invariant($"{downloads / 1_000_000_000.0:0.#}B"),
        >= 1_000_000 => FormattableString.Invariant($"{downloads / 1_000_000.0:0.#}M"),
        >= 1_000 => FormattableString.Invariant($"{downloads / 1_000.0:0.#}K"),
        _ => downloads.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}
