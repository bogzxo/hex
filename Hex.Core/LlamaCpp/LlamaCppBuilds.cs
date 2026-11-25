using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>One file of a llama.cpp release: its name, where it downloads from and its size in bytes.</summary>
internal sealed record LlamaCppAsset(string Name, string Url, long Size);

/// <summary>A llama.cpp release (bNNNNN, a build of its master), with its files.</summary>
internal sealed record LlamaCppRelease(string Tag, DateTimeOffset Published, IReadOnlyList<LlamaCppAsset> Assets);

/// <summary>
/// A build of llama.cpp for one machine: the backend it runs on (cuda, vulkan, metal, rocm or cpu), its version for CUDA
/// and ROCm, the archive with llama-server in it and, for CUDA on Windows and Linux, the archive of CUDA's own libraries
/// that goes beside it.
/// </summary>
internal sealed record LlamaCppBuild(string Backend, Version? BackendVersion, LlamaCppAsset Archive, LlamaCppAsset? Runtime);

/// <summary>
/// Which of a llama.cpp release's builds a machine wants, and asking GitHub for the newest release. The builds are told
/// apart by their names, llama-b12651-bin-win-cuda-13.5-x64.zip, llama-b12651-bin-ubuntu-vulkan-x64.tar.gz,
/// llama-b12651-bin-macos-arm64.tar.gz, and CUDA's libraries cudart-llama-bin-win-cuda-13.5-x64.zip.
/// </summary>
internal static partial class LlamaCppBuilds
{
    public const string CUDA = "cuda", VULKAN = "vulkan", METAL = "metal", ROCM = "rocm", CPU = "cpu";

    /// <summary>Where llama.cpp is released.</summary>
    public const string REPOSITORY = "ggml-org/llama.cpp";

    [GeneratedRegex(@"^llama-b\d+-bin-(?<os>win|ubuntu|macos)-(?<rest>.+?)\.(?:zip|tar\.gz)$", RegexOptions.IgnoreCase)]
    private static partial Regex Archive();

    [GeneratedRegex(@"^cudart-llama-(?:b\d+-)?bin-(?<os>win|ubuntu)-cuda-(?<version>[\d.]+)-(?<arch>x64|arm64)\.(?:zip|tar\.gz)$", RegexOptions.IgnoreCase)]
    private static partial Regex CudaRuntime();

    /// <summary>
    /// The backends a machine can run, the one it should first: CUDA on an NVIDIA card whose driver runs one of the
    /// release's CUDAs, Metal on a Mac with Apple silicon, Vulkan on any other card (AMD's, Intel's, an NVIDIA card's
    /// whose driver is too old), ROCm on AMD's as a choice, and always the processor alone.
    /// </summary>
    public static List<string> Backends(LlamaCppPlatform platform, IReadOnlyList<LlamaCppAsset> assets)
    {
        var all = new List<string>();
        foreach (string backend in new[] { METAL, CUDA, VULKAN, ROCM, CPU })
        {
            if (backend == ROCM && platform.Gpu?.Vendor != LlamaCppGpu.AMD)
                continue;
            if (backend == CUDA && platform.Gpu?.Vendor != LlamaCppGpu.NVIDIA)
                continue;
            if (backend == METAL && platform.Gpu?.Vendor != LlamaCppGpu.APPLE)
                continue;
            if (backend == VULKAN && platform.Gpu is null or { Vendor: LlamaCppGpu.APPLE })
                continue;
            if (Choose(platform, backend, assets) is not null)
                all.Add(backend);
        }
        return all;
    }

    /// <summary>The release's build of a backend for the machine, null when the release has none it can run.</summary>
    public static LlamaCppBuild? Choose(LlamaCppPlatform platform, string backend, IReadOnlyList<LlamaCppAsset> assets)
    {
        string os = platform.Os switch { LlamaCppPlatform.WINDOWS => "win", LlamaCppPlatform.MACOS => "macos", _ => "ubuntu" };
        var builds = new List<(LlamaCppAsset Asset, string Backend, Version? Version)>();
        foreach (LlamaCppAsset asset in assets)
        {
            Match match = Archive().Match(asset.Name);
            if (!match.Success || !match.Groups["os"].Value.Equals(os, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Read(match.Groups["os"].Value.ToLowerInvariant(), match.Groups["rest"].Value.ToLowerInvariant()) is { } read && read.Arch == platform.Arch)
                builds.Add((asset, read.Backend, read.Version));
        }

        var fitting = builds.Where(build => build.Backend == backend).ToList();
        if (backend == CUDA)
        {
            // The newest CUDA the driver runs; a driver that doesn't say gets the oldest, the likeliest to run
            Version? driver = platform.Gpu?.Cuda;
            fitting = driver is null
                ? [.. fitting.OrderBy(build => build.Version).Take(1)]
                : [.. fitting.Where(build => build.Version is { } v && new Version(v.Major, v.Minor) <= new Version(driver.Major, driver.Minor))
                    .OrderByDescending(build => build.Version).Take(1)];
        }
        else
            fitting = [.. fitting.OrderByDescending(build => build.Version).Take(1)];

        if (fitting.Count == 0)
            return null;
        var (archive, chosen, version) = fitting[0];
        LlamaCppAsset? runtime = null;
        if (chosen == CUDA && os != "macos")
        {
            runtime = assets.FirstOrDefault(asset => CudaRuntime().Match(asset.Name) is { Success: true } m
                && m.Groups["os"].Value.Equals(os, StringComparison.OrdinalIgnoreCase) && m.Groups["arch"].Value == platform.Arch
                && Version.TryParse(m.Groups["version"].Value, out Version? v) && v == version);
        }
        return new LlamaCppBuild(chosen, version, archive, runtime);
    }

    // What an archive's name says after the system: its backend, its version and the processor, null for a build that
    // isn't one of these (SYCL, OpenVINO, Adreno, Snapdragon, s390x)
    private static (string Backend, Version? Version, string Arch)? Read(string os, string rest)
    {
        string[] parts = rest.Split('-');
        string arch = parts[^1];
        if (arch is not ("x64" or "arm64"))
            return null;
        string[] middle = parts[..^1];
        Version? version = middle.Length > 1 && Version.TryParse(middle[1], out Version? v) ? v : null;
        return middle switch
        {
            [] => (os == "macos" && arch == "arm64" ? METAL : CPU, null, arch),
            ["cpu"] => (CPU, null, arch),
            ["vulkan"] => (VULKAN, null, arch),
            ["cuda", _] when version is not null => (CUDA, version, arch),
            ["rocm", _] when version is not null => (ROCM, version, arch),
            _ => null,
        };
    }

    private static readonly HttpClient github = MakeClient();

    private static HttpClient MakeClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(20) };
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Hex", version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// The newest release of llama.cpp with builds in it. Its builds are all marked prerelease and its "latest" can be a
    /// tag with none, so the newest few are asked for and the first bNNNNN with files is it.
    /// </summary>
    public static async Task<LlamaCppRelease> Latest(CancellationToken cancel)
    {
        using HttpResponseMessage answered = await github.GetAsync($"repos/{REPOSITORY}/releases?per_page=10", cancel).ConfigureAwait(false);
        if (!answered.IsSuccessStatusCode)
            throw new HttpRequestException(answered.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "GitHub has had all the questions it takes from here this hour, try again later"
                : $"GitHub said {(int)answered.StatusCode} {answered.ReasonPhrase}");
        return Newest(await answered.Content.ReadAsStringAsync(cancel).ConfigureAwait(false))
            ?? throw new HttpRequestException("GitHub has no llama.cpp build with files in its newest releases");
    }

    /// <summary>The newest bNNNNN release with files in GitHub's list of releases, null when there is none.</summary>
    public static LlamaCppRelease? Newest(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray releases)
            return null;
        LlamaCppRelease? newest = null;
        foreach (JsonObject release in releases.OfType<JsonObject>())
        {
            if (release["tag_name"]?.GetValue<string>() is not { } tag || !Tag().IsMatch(tag) || release["draft"]?.GetValue<bool>() == true)
                continue;
            var assets = (release["assets"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(asset => new LlamaCppAsset(asset["name"]?.GetValue<string>() ?? "", asset["browser_download_url"]?.GetValue<string>() ?? "",
                    asset["size"]?.GetValue<long>() ?? 0))
                .Where(asset => asset.Name.Length > 0 && asset.Url.StartsWith("https://", StringComparison.Ordinal)).ToList();
            if (assets.Count == 0)
                continue;
            DateTimeOffset published = DateTimeOffset.TryParse(release["published_at"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : DateTimeOffset.MinValue;
            if (newest is null || BuildNumber(tag) > BuildNumber(newest.Tag))
                newest = new LlamaCppRelease(tag, published, assets);
        }
        return newest;
    }

    [GeneratedRegex(@"^b\d+$")]
    private static partial Regex Tag();

    /// <summary>The build number of a tag, b12651 is 12651, 0 for one that isn't.</summary>
    public static int BuildNumber(string tag) => tag.Length > 1 && int.TryParse(tag.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : 0;
}
