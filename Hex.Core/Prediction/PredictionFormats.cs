namespace Hex.Core;

/// <summary>The prompt formats by the names the settings use, and the one a model's name suggests.</summary>
internal static class PredictionFormats
{
    public const string INFER = "infer";
    public const string ZETA2 = "zeta2";
    public const string ZETA21 = "zeta21";
    public const string QWEN = "qwen";
    public const string CODE_LLAMA = "code_llama";
    public const string STAR_CODER = "star_coder";
    public const string DEEPSEEK_CODER = "deepseek_coder";
    public const string CODE_GEMMA = "code_gemma";
    public const string CODESTRAL = "codestral";

    private static readonly Zeta2Format zeta2 = new(), zeta21 = new(multiRegion: true);

    /// <summary>Every format's name, infer first, in the order the settings list them.</summary>
    public static IReadOnlyList<string> Names { get; } = [INFER, ZETA21, ZETA2, QWEN, CODE_LLAMA, STAR_CODER, DEEPSEEK_CODER, CODE_GEMMA, CODESTRAL];

    /// <summary>The format of that name, null for infer or a name that isn't one.</summary>
    public static IPredictionFormat? Named(string name) => name.Trim().ToLowerInvariant() switch
    {
        ZETA21 => zeta21,
        ZETA2 => zeta2,
        QWEN => FimFormat.Qwen,
        CODE_LLAMA => FimFormat.CodeLlama,
        STAR_CODER => FimFormat.StarCoder,
        DEEPSEEK_CODER => FimFormat.DeepSeekCoder,
        CODE_GEMMA => FimFormat.CodeGemma,
        CODESTRAL => FimFormat.Codestral,
        _ => null,
    };

    /// <summary>The format a model's name suggests, Qwen's when it suggests none, those tokens being the commonest.</summary>
    public static IPredictionFormat Infer(string model)
    {
        string name = model.ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(".", "");
        // Zeta 2.1's chunked regions before Zeta 2's one region, its name holding Zeta 2's
        if (name.Contains("zeta21", StringComparison.Ordinal))
            return zeta21;
        if (name.Contains("zeta2", StringComparison.Ordinal))
            return zeta2;
        if (name.Contains("codegemma", StringComparison.Ordinal))
            return FimFormat.CodeGemma;
        if (name.Contains("codellama", StringComparison.Ordinal))
            return FimFormat.CodeLlama;
        if (name.Contains("starcoder", StringComparison.Ordinal))
            return FimFormat.StarCoder;
        if (name.Contains("deepseek", StringComparison.Ordinal))
            return FimFormat.DeepSeekCoder;
        if (name.Contains("codestral", StringComparison.Ordinal))
            return FimFormat.Codestral;
        return FimFormat.Qwen;
    }

    /// <summary>The format the options say, or the one their model suggests when they say infer.</summary>
    public static IPredictionFormat For(PredictionOptions options) => Named(options.Format) ?? Infer(options.Model);
}
