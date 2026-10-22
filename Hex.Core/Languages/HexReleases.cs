using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>One release of something on GitHub, its tag, its name, what it says changed (markdown) and its page.</summary>
internal sealed record HexRelease(string Tag, string Name, string Notes, string Page, DateTimeOffset Published);

/// <summary>
/// Asking GitHub what has been released of something (api.github.com, without signing in, which allows sixty questions
/// an hour from one address, a check a day of a handful of servers is nowhere near it). Drafts and prereleases are left
/// out, a release is what somebody would install. Every call is for the background, and throws when GitHub can't be
/// reached or says no.
/// </summary>
internal static class HexReleases
{
    private static readonly HttpClient http = MakeClient();

    private static HttpClient MakeClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(15) };

        // GitHub turns away a request that doesn't say who it is from
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Hex", version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>The newest releases of a repository (owner/name), newest first, up to so many.</summary>
    public static async Task<List<HexRelease>> Recent(string repo, int count = 20, CancellationToken cancel = default)
    {
        using HttpResponseMessage answered = await http.GetAsync($"repos/{repo}/releases?per_page={Math.Clamp(count, 1, 100)}", cancel).ConfigureAwait(false);
        if (!answered.IsSuccessStatusCode)
            throw new HttpRequestException(Refusal(answered));

        var releases = new List<HexRelease>();
        if (JsonNode.Parse(await answered.Content.ReadAsStringAsync(cancel).ConfigureAwait(false)) is JsonArray all)
        {
            foreach (JsonNode? node in all)
            {
                if (node is JsonObject release && Of(release) is { } read)
                    releases.Add(read);
            }
        }

        return [.. releases.OrderByDescending(release => release.Published)];
    }

    /// <summary>The newest release of a repository, null when it has none.</summary>
    public static async Task<HexRelease?> Latest(string repo, CancellationToken cancel = default)
    {
        using HttpResponseMessage answered = await http.GetAsync($"repos/{repo}/releases/latest", cancel).ConfigureAwait(false);
        if (answered.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        if (!answered.IsSuccessStatusCode)
            throw new HttpRequestException(Refusal(answered));

        return JsonNode.Parse(await answered.Content.ReadAsStringAsync(cancel).ConfigureAwait(false)) is JsonObject release ? Of(release) : null;
    }

    // Downloads go to wherever GitHub sends them, not its API, without the API's answers asked for and for longer
    private static readonly HttpClient downloads = MakeDownloads();

    private static HttpClient MakeDownloads()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Hex", version));
        return client;
    }

    /// <summary>A file downloaded to a path, how far along it is said as it goes (from 0 to 1, when its size is known).</summary>
    public static async Task Download(string url, string to, Action<float>? progress = null, CancellationToken cancel = default)
    {
        using HttpResponseMessage answered = await downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        if (!answered.IsSuccessStatusCode)
            throw new HttpRequestException($"the download said {(int)answered.StatusCode} {answered.ReasonPhrase}");

        long? size = answered.Content.Headers.ContentLength;
        await using Stream from = await answered.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using FileStream into = File.Create(to);
        byte[] buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await from.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            await into.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            done += read;
            if (size is > 0)
                progress?.Invoke(done / (float)size.Value);
        }
    }

    // Why GitHub said no, the hour's questions running out being the one worth saying in words
    private static string Refusal(HttpResponseMessage answered) =>
        answered.StatusCode == System.Net.HttpStatusCode.Forbidden && answered.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0"
            ? "GitHub has had all the questions it takes from here this hour, try again later"
            : $"GitHub said {(int)answered.StatusCode} {answered.ReasonPhrase}";

    private static HexRelease? Of(JsonObject release)
    {
        if (release["draft"]?.GetValue<bool>() == true || release["prerelease"]?.GetValue<bool>() == true)
            return null;
        if (release["tag_name"]?.GetValue<string>() is not { Length: > 0 } tag)
            return null;

        string name = release["name"]?.GetValue<string>() is { Length: > 0 } named ? named : tag;
        string notes = release["body"]?.GetValue<string>() ?? string.Empty;
        string page = release["html_url"]?.GetValue<string>() ?? string.Empty;
        DateTimeOffset published = DateTimeOffset.TryParse(release["published_at"]?.GetValue<string>(), out var at) ? at : DateTimeOffset.MinValue;
        return new HexRelease(tag, name, notes.Replace("\r\n", "\n"), page, published);
    }

    /// <summary>
    /// What changed, as a page of markdown, from the releases given (newest first) that are newer than a version, all
    /// of them when it is null. The newest one alone when none is newer, there is still something to read.
    /// </summary>
    public static string Changelog(IReadOnlyList<HexRelease> releases, string? since, Func<string, string> literal)
    {
        var newer = releases.Where(release => since is null || HexServerCatalog.IsNewer(release.Tag, since)).ToList();
        if (newer.Count == 0 && releases.Count > 0)
            newer.Add(releases[0]);

        var page = new StringBuilder();
        foreach (HexRelease release in newer)
        {
            string when = release.Published == DateTimeOffset.MinValue ? string.Empty : $" · {release.Published:yyyy-MM-dd}";
            page.Append("## ").Append(literal(release.Name)).Append(when).Append("\n\n");
            page.Append(release.Notes.Trim().Length > 0 ? literal(release.Notes.Trim()) : "_It says nothing about what changed._").Append("\n\n");
        }

        return page.Length > 0 ? page.ToString().TrimEnd() : "_No releases._";
    }
}
