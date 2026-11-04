using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Hex.Installation;

public sealed record ReleaseAsset(string Version, string Name, Uri Download, long Size, string Sha256, string Label = "")
{
    /// <summary>The version the way a person reads it, the number and what the release is called (2.8.0 thickboy).</summary>
    public string Shown => Label.Length > 0 ? Label : Version.TrimStart('v', 'V');
}
public sealed record InstallProgress(string Message, float Fraction);

/// <summary>The public stable release and its exact platform asset. No credentials or remote commands.</summary>
public sealed class ReleaseClient(HttpClient? client = null)
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly HttpClient http = client ?? Shared;

    public async Task<ReleaseAsset> Latest(string rid, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/bogzxo/hex/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Hex-Installer", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(InstallText.Get("errors.no_release"));
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException(InstallText.Get("errors.rate_limit"));
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancel), rid);
    }

    public static ReleaseAsset Parse(string json, string rid)
    {
        InstallPaths.ValidateRid(rid);
        var release = JsonNode.Parse(json) ?? throw new InvalidDataException(InstallText.Get("errors.empty_release"));
        if (release["draft"]?.GetValue<bool>() != false || release["prerelease"]?.GetValue<bool>() != false)
            throw new InvalidDataException(InstallText.Get("errors.stable_only"));
        string version = release["tag_name"]?.GetValue<string>() ?? "";
        _ = VersionOf(version);
        string name = $"Hex-{rid}.zip";
        var asset = (release["assets"] as JsonArray)?.SingleOrDefault(a => a?["name"]?.GetValue<string>() == name)
            ?? throw new InvalidOperationException(InstallText.Get("errors.missing_asset", ("asset", name)));
        var url = new Uri(asset["browser_download_url"]!.GetValue<string>());
        if (url.Scheme != "https" || url.Host != "github.com" || !url.AbsolutePath.StartsWith("/bogzxo/hex/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException(InstallText.Get("errors.download_source"));
        string digest = asset["digest"]?.GetValue<string>() ?? "";
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException(InstallText.Get("errors.no_digest"));
        long size = asset["size"]!.GetValue<long>();
        if (size <= 0 || size > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException(InstallText.Get("errors.asset_size"));
        return new(version, name, url, size, digest[7..], LabelOf(release["name"]?.GetValue<string>(), version));
    }

    /// <summary>
    /// What the release calls itself after the number, out of its title (Hex 2.8.0 thickboy). A title that doesn't
    /// start with Hex and the release's own number says nothing, the number goes on its own then.
    /// </summary>
    public static string LabelOf(string? title, string tag)
    {
        string number = tag.TrimStart('v', 'V');
        string said = (title ?? "").Trim();
        if (said.StartsWith("Hex ", StringComparison.OrdinalIgnoreCase))
            said = said[4..].TrimStart();
        said = said.TrimStart('v', 'V');
        if (!said.StartsWith(number, StringComparison.Ordinal))
            return number;
        string name = said[number.Length..].Trim();
        return name.Length > 0 && name.Length <= 40 && !name.Any(char.IsControl) ? $"{number} {name}" : number;
    }

    public static Version VersionOf(string tag) => Version.TryParse(tag.TrimStart('v', 'V'), out var version)
        ? version : throw new InvalidDataException(InstallText.Get("errors.version", ("version", tag)));

    /// <summary>Verify a cached package again in the installer process, before extracting or changing the installation.</summary>
    public static async Task Verify(ReleaseAsset asset, string path, CancellationToken cancel)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        if (input.Length != asset.Size) throw new InvalidDataException(InstallText.Get("errors.verification"));
        byte[] digest = await SHA256.HashDataAsync(input, cancel);
        if (!Convert.ToHexString(digest).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(InstallText.Get("errors.verification"));
    }

    public async Task Download(ReleaseAsset asset, string destination, Action<InstallProgress>? progress, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Download);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Hex-Installer", "1.0"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancel);
        await using var output = File.Create(destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancel)) > 0)
        {
            total += read;
            if (total > asset.Size) throw new InvalidDataException(InstallText.Get("errors.download_size"));
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
            progress?.Invoke(new(InstallText.Get("status.downloading", ("version", asset.Shown), ("downloaded", (total / 1048576f).ToString("0.0")), ("total", (asset.Size / 1048576f).ToString("0.0"))), .1f + .6f * total / asset.Size));
        }
        if (total != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(InstallText.Get("errors.verification"));
    }
}
