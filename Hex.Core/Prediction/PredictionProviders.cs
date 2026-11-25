using System.Net;

namespace Hex.Core;

/// <summary>
/// The provider the settings describe, and the one HTTP client every provider sends with: connections kept alive and
/// renewed every five minutes, three seconds to connect, and no overall timeout, each request has its own deadline.
/// </summary>
internal static class PredictionProviders
{
    public const string OLLAMA = "ollama";
    public const string OPENAI = "openai";
    public const string LINK = "link";

    /// <summary>A chat model on a linked device (Serve AI, docs/hex-link-plan.md, 5.7): the model is <see cref="LinkAiModelId"/>'s id and the URL isn't used.</summary>
    public const string LINKAI = "linkai";

    /// <summary>A llama-server Hex runs itself, asked through its OpenAI compatible completions.</summary>
    public const string LLAMACPP = HexSettings.LLAMACPP;

    /// <summary>The client every provider sends with.</summary>
    public static HttpClient Shared { get; } = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(3),
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// The provider the options describe, or null when they don't describe one: a server needs a URL and a model,
    /// Hex Link nothing (the device attached has both).
    /// </summary>
    public static IPredictionProvider? Make(PredictionOptions options)
    {
        string provider = options.Provider.Trim().ToLowerInvariant();
        if (provider == LINK)
            return new LinkProvider();

        string model = options.Model.Trim();
        if (model.Length == 0)
            return null;

        return provider switch
        {
            OLLAMA when OllamaUrl(options.ApiUrl) is { } url => new OllamaProvider(Shared, url, model) { Tuning = options.Tuning.Clamped(), SystemPrompt = options.SystemPrompt },
            OPENAI when OpenAiUrl(options.ApiUrl) is { } url => new OpenAiProvider(Shared, url, model, string.IsNullOrWhiteSpace(options.ApiKey) ? null : options.ApiKey.Trim())
            {
                Tuning = options.Tuning.Clamped()
            },
            // Hex's own llama-server wants no key, and nothing on this machine is sent one
            LLAMACPP when OpenAiUrl(options.ApiUrl) is { } url => new OpenAiProvider(Shared, url, model, null) { Tuning = options.Tuning.Clamped(), CachePrompt = true, Slot = options.Slot },
            _ => null,
        };
    }

    /// <summary>
    /// The address of Ollama's generate API from the server's URL the user gave: http://localhost:11434 becomes
    /// http://localhost:11434/api/generate, a URL already ending /api/generate stays as it is. Null when it isn't a
    /// http or https URL.
    /// </summary>
    public static Uri? OllamaUrl(string written)
    {
        if (Parse(written) is not { } url)
            return null;
        string path = url.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase))
            return With(url, path);
        return With(url, path + "/api/generate");
    }

    /// <summary>
    /// The address of an OpenAI completions API from the URL the user gave: one ending /completions stays as it is, one
    /// ending /v1 has /completions added, a server's bare URL has /v1/completions. Null when it isn't a http or https URL.
    /// </summary>
    public static Uri? OpenAiUrl(string written)
    {
        if (Parse(written) is not { } url)
            return null;
        string path = url.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/completions", StringComparison.OrdinalIgnoreCase))
            return With(url, path);
        if (path.Length == 0)
            return With(url, "/v1/completions");
        return With(url, path + "/completions");
    }

    /// <summary>Whether a URL is this machine: localhost, 127.0.0.0/8 or ::1. Anything else sends the code away.</summary>
    public static bool IsLocal(Uri url)
    {
        if (url.IsLoopback)
            return true;
        string host = url.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);
    }

    // A http or https URL out of what was written, a bare host:port taken as http
    private static Uri? Parse(string written)
    {
        string text = written.Trim();
        if (text.Length == 0)
            return null;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out Uri? url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            ? url
            : null;
    }

    // The URL with another path, its query kept
    private static Uri With(Uri url, string path) => new UriBuilder(url) { Path = path }.Uri;
}
