using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Hex.Core;

/// <summary>What a model can do for prediction (docs/next-edit-plan.md, 4.1).</summary>
internal enum PredictionModelKind
{
    /// <summary>Rewrites a region of code from the recent edits and can't chat: Zeta 2, 2.1.</summary>
    EditModel,

    /// <summary>Fills in the middle at the caret and nothing else: Qwen2.5-Coder base, StarCoder, Codestral.</summary>
    Fim,

    /// <summary>Follows instructions, no fill in the middle.</summary>
    Instruct,

    /// <summary>Follows instructions and fills in the middle: Qwen3.5, Mellum.</summary>
    FimAndInstruct,
}

/// <summary>
/// Which <see cref="PredictionModelKind"/> a model is: the setting edit_predictions_kind when it names one, otherwise the
/// model's name, overridden by what the server says of the model (Ollama's /api/show, llama-server's /props and
/// /tokenize) once that has been asked, remembered per server and model. A model whose name says Zeta is an edit model
/// whatever the server says: its template is a chat one but it can't chat.
/// </summary>
internal static class PredictionModels
{
    public const string AUTO = "auto";
    public const string EDIT_MODEL = "edit_model";
    public const string FIM = "fim";
    public const string INSTRUCT = "instruct";
    public const string FIM_INSTRUCT = "fim_instruct";

    /// <summary>Every setting value, auto first, in the order the settings list them.</summary>
    public static IReadOnlyList<string> Names { get; } = [AUTO, EDIT_MODEL, FIM, INSTRUCT, FIM_INSTRUCT];

    // What the servers said, by the server's URL and the model; only conclusive answers are kept
    private static readonly ConcurrentDictionary<(string Url, string Model), PredictionModelKind> probed = new();

    /// <summary>The kind a setting value names, null for auto or a value that isn't one.</summary>
    public static PredictionModelKind? Named(string name) => name.Trim().ToLowerInvariant() switch
    {
        EDIT_MODEL => PredictionModelKind.EditModel,
        FIM => PredictionModelKind.Fim,
        INSTRUCT => PredictionModelKind.Instruct,
        FIM_INSTRUCT => PredictionModelKind.FimAndInstruct,
        _ => null,
    };

    /// <summary>The setting value for a kind.</summary>
    public static string NameOf(PredictionModelKind kind) => kind switch
    {
        PredictionModelKind.EditModel => EDIT_MODEL,
        PredictionModelKind.Fim => FIM,
        PredictionModelKind.Instruct => INSTRUCT,
        _ => FIM_INSTRUCT,
    };

    /// <summary>
    /// What the model's name says. Zeta is an edit model. Code model families (coder, starcoder, codellama, deepseek,
    /// codestral, codegemma) fill in the middle, and also follow instructions when the name says instruct or it. Qwen3,
    /// Qwen3.5 and Mellum do both. Other names with instruct, chat or it follow instructions only. A name that says nothing is
    /// taken to fill in the middle, which is what Hex always did with it.
    /// </summary>
    public static PredictionModelKind FromName(string model)
    {
        string name = model.ToLowerInvariant();
        string joined = name.Replace("-", "").Replace("_", "").Replace(".", "");
        if (joined.Contains("zeta", StringComparison.Ordinal))
            return PredictionModelKind.EditModel;

        bool instruct = joined.Contains("instruct", StringComparison.Ordinal) || joined.Contains("chat", StringComparison.Ordinal) || HasWord(name, "it");
        bool code = IsCodeFamily(model);
        if (joined.Contains("mellum", StringComparison.Ordinal) || joined.Contains("qwen3", StringComparison.Ordinal))
            return PredictionModelKind.FimAndInstruct;
        if (code)
            return instruct ? PredictionModelKind.FimAndInstruct : PredictionModelKind.Fim;
        return instruct ? PredictionModelKind.Instruct : PredictionModelKind.Fim;
    }

    // Whether the name is one of the code model families, which fill in the middle (coder covers starcoder and deepseek-coder)
    private static bool IsCodeFamily(string model)
    {
        string joined = model.ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(".", "");
        return joined.Contains("coder", StringComparison.Ordinal) || joined.Contains("codellama", StringComparison.Ordinal)
            || joined.Contains("deepseek", StringComparison.Ordinal) || joined.Contains("codestral", StringComparison.Ordinal)
            || joined.Contains("codegemma", StringComparison.Ordinal);
    }

    // Whether the name has the word between anything that isn't a letter or digit: gemma-3-it, not "fit" or "item"
    private static bool HasWord(string name, string word)
    {
        foreach (string part in name.Split(['-', '_', '.', ':', '/', ' '], StringSplitOptions.RemoveEmptyEntries))
            if (part == word)
                return true;
        return false;
    }

    /// <summary>
    /// The kind the setting, the name and what a server said (null for nothing said) make. The setting wins; then an edit
    /// model by its name; then what the server said, except that a code model by its name is taken to fill in the middle
    /// only when the server says it does both, base models shipping a chat template; then the name.
    /// </summary>
    public static PredictionModelKind Decide(string setting, string model, PredictionModelKind? said)
    {
        if (Named(setting) is { } chosen)
            return chosen;
        PredictionModelKind byName = FromName(model);
        if (byName == PredictionModelKind.EditModel || said is not { } server)
            return byName;
        return byName == PredictionModelKind.Fim && server == PredictionModelKind.FimAndInstruct && IsCodeFamily(model) ? PredictionModelKind.Fim : server;
    }

    /// <summary>The kind of the model the options describe, from what has been probed so far. Allocates nothing once probed.</summary>
    public static PredictionModelKind KindOf(PredictionOptions options) =>
        Decide(options.Kind, options.Model, probed.TryGetValue((options.ApiUrl, options.Model), out PredictionModelKind said) ? said : null);

    /// <summary>What a server said of the model, remembered; for the tests and the probe.</summary>
    public static void Remember(string url, string model, PredictionModelKind kind) => probed[(url, model)] = kind;

    /// <summary>Forgets every probe.</summary>
    public static void Forget() => probed.Clear();

    /// <summary>
    /// Ollama's /api/show for a model: a template with .Suffix or the capability insert fills in the middle; a template that
    /// takes messages (.Messages, .System or a chat markup like &lt;|im_start|&gt;) follows instructions. Null when it says
    /// neither, or isn't what /api/show answers.
    /// </summary>
    public static PredictionModelKind? ParseOllamaShow(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string template = root.TryGetProperty("template", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            bool fim = template.Contains(".Suffix", StringComparison.Ordinal);
            if (root.TryGetProperty("capabilities", out JsonElement capabilities) && capabilities.ValueKind == JsonValueKind.Array)
                fim |= capabilities.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "insert");

            bool chat = template.Contains(".Messages", StringComparison.Ordinal) || template.Contains(".System", StringComparison.Ordinal)
                || template.Contains("<|im_start|>", StringComparison.Ordinal) || template.Contains("[INST]", StringComparison.Ordinal)
                || template.Contains("<start_of_turn>", StringComparison.Ordinal);
            return Combine(fim, chat);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>llama-server's /props: whether it has a chat template (a string that isn't empty).</summary>
    public static bool? ParseLlamaProps(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("chat_template", out JsonElement template))
                return null;
            return template.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(template.GetString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>llama-server's /tokenize of one special token written out: whether the vocabulary has it as a single token.</summary>
    public static bool ParseTokenize(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("tokens", out JsonElement tokens)
                && tokens.ValueKind == JsonValueKind.Array && tokens.GetArrayLength() == 1;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The kind a server's answers make: fill in the middle and chat, either, or null when it has neither.</summary>
    public static PredictionModelKind? Combine(bool fim, bool chat) =>
        fim ? (chat ? PredictionModelKind.FimAndInstruct : PredictionModelKind.Fim) : (chat ? PredictionModelKind.Instruct : null);

    // The fill in the middle tokens a vocabulary may have, one of each family's prefix
    private static readonly string[] fimTokens = ["<|fim_prefix|>", "<fim_prefix>", "[PREFIX]", "<PRE>", "<｜fim▁begin｜>"];

    /// <summary>
    /// Asks the server what the model is, once per server and model, and remembers a conclusive answer: Ollama's /api/show,
    /// llama-server's /props and /tokenize (the provider llama.cpp, or openai at a llama-server). Never throws, a server
    /// that can't be reached or doesn't know leaves the name to say. Not asked for Hex Link, the device has its own.
    /// </summary>
    public static async Task Probe(HttpClient http, PredictionOptions options, CancellationToken cancel)
    {
        string provider = options.Provider.Trim().ToLowerInvariant();
        if (provider is not (PredictionProviders.OLLAMA or PredictionProviders.OPENAI or PredictionProviders.LLAMACPP) || options.Model.Length == 0
            || probed.ContainsKey((options.ApiUrl, options.Model)))
            return;

        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            PredictionModelKind? kind = null;
            if (provider == PredictionProviders.OLLAMA && PredictionProviders.OllamaUrl(options.ApiUrl) is { } generate)
            {
                Uri show = new UriBuilder(generate) { Path = generate.AbsolutePath[..^"generate".Length] + "show" }.Uri;
                string? answer = await Post(http, show, writer => writer.WriteString("model", options.Model), wait.Token).ConfigureAwait(false);
                kind = answer is null ? null : ParseOllamaShow(answer);
            }
            else if (PredictionProviders.OpenAiUrl(options.ApiUrl) is { } completions)
            {
                // /props and /tokenize sit at the server's root, not under /v1
                var root = new UriBuilder(completions) { Path = "/", Query = "" }.Uri;
                string? props = await Get(http, new Uri(root, "props"), wait.Token).ConfigureAwait(false);
                if (props is not null && ParseLlamaProps(props) is { } chat)
                {
                    bool fim = false;
                    foreach (string token in fimTokens)
                    {
                        string? tokens = await Post(http, new Uri(root, "tokenize"), writer =>
                        {
                            writer.WriteString("content", token);
                            writer.WriteBoolean("parse_special", true);
                        }, wait.Token).ConfigureAwait(false);
                        if (tokens is not null && ParseTokenize(tokens))
                        {
                            fim = true;
                            break;
                        }
                    }
                    kind = Combine(fim, chat);
                }
            }

            if (kind is { } found)
                probed[(options.ApiUrl, options.Model)] = found;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            // The name says
        }
    }

    private static async Task<string?> Get(HttpClient http, Uri url, CancellationToken cancel)
    {
        using HttpResponseMessage response = await http.GetAsync(url, cancel).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false) : null;
    }

    private static async Task<string?> Post(HttpClient http, Uri url, Action<Utf8JsonWriter> body, CancellationToken cancel)
    {
        using HttpContent content = PredictionHttp.Json(writer =>
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        });
        using HttpResponseMessage response = await http.PostAsync(url, content, cancel).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false) : null;
    }
}
