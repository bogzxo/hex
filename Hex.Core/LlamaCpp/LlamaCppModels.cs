namespace Hex.Core;

/// <summary>
/// A model the llama.cpp setup offers, one file of it on Hugging Face: whether it can chat (follow instructions and hold
/// a conversation, which Link Chat and edits by instruction need; an edit prediction model only rewrites code), its name,
/// the repository and file, its size and SHA-256 as Hugging Face says them, how many parameters it has and how many work
/// at once (a mixture of experts uses a few of them per token), the prompt format predictions take with it, and a line
/// about it. The one model picked does everything Hex asks of a model, a card's memory holding one well.
/// </summary>
internal sealed record LlamaCppModel(
    bool Chats,
    string Id,
    string Title,
    string Repository,
    string File,
    long Size,
    string Sha256,
    double BillionParameters,
    double BillionActive,
    string PromptFormat,
    string About,
    LocalModelCaps Caps = LocalModelCaps.None,
    string Architecture = "",
    int Context = 0,
    string Quant = "",
    int Layers = 0,
    int KvHeads = 0,
    string SizeLabel = "")
{
    /// <summary>Whether only some of its parameters work for each token, so its experts can wait in RAM and it still runs fast.</summary>
    public bool MixtureOfExperts => BillionActive < BillionParameters;

    /// <summary>Where it downloads from.</summary>
    public string Url => $"https://huggingface.co/{Repository}/resolve/main/{File}";
}

/// <summary>How a model fits a machine, best first.</summary>
internal enum LlamaCppFit
{
    /// <summary>All of it on the graphics card, as fast as the card goes.</summary>
    Gpu,

    /// <summary>A mixture of experts whose experts wait in RAM, the rest on the card: fast enough, a few active at once.</summary>
    Experts,

    /// <summary>Part on the card and part on the processor, every token through both: slow.</summary>
    Split,

    /// <summary>On the processor alone, with no card to help: slow for anything but a small model.</summary>
    Cpu,

    /// <summary>More than the machine has.</summary>
    TooBig,
}

/// <summary>
/// The models the llama.cpp setup offers, best for their job and size as of November 2026, every file checked to be on
/// Hugging Face (size and SHA-256 from its API, 2026-11-23), and how each fits a machine. What a model needs beside its
/// file (its context and the server's buffers) is counted as a little over a tenth of it, at least a GiB; what the system
/// and Hex keep for themselves as 6 GiB of the RAM.
/// <para>
/// Zed's Zeta 2.1 (May 2026, Seed-Coder 8B) is the newest open edit prediction model, rewriting the lines round the caret
/// from your recent edits rather than only filling in at it, but it can't chat. Qwen3.5 9B (February 2026) led the agent
/// benchmarks at this size in JetBrains' own comparison (SWE-bench Verified 50, Terminal-Bench 2.1 21.7, BFCL v4 58.5)
/// and fills in the middle with Qwen's tokens; JetBrains' Mellum2.1 (October 2026, 12B with 2.5B at work) is close (47,
/// 17.4, and the best tool calling, 62.3) and several times faster, a mixture of experts. Qwen2.5-Coder 1.5B is for a
/// machine without a card.
/// </para>
/// </summary>
internal static class LlamaCppModels
{
    private const long GIB = 1L << 30;

    // The prompt formats, PredictionFormats' names (the prediction side's, which Core doesn't see)
    private const string ZETA21 = "zeta21", QWEN = "qwen", INFER = "infer";

    /// <summary>What the system and Hex keep of the RAM for themselves.</summary>
    public const long RESERVED_MIB = 6 * 1024;

    public static readonly IReadOnlyList<LlamaCppModel> All =
    [
        new(false, "zeta-2.1", "Zeta 2.1", "mradermacher/zeta-2.1-i1-GGUF", "zeta-2.1.i1-Q4_K_M.gguf",
            5_071_410_336, "216ddb5722a49f9ff49b21a050c4db3c801ef6f34e4bd1702d5db43e3325b076", 8.25, 8.25, ZETA21,
            "The best open edit prediction model (Zed, May 2026): from your recent edits it rewrites the lines round the caret, not just fills in at it. Predictions only, it can't chat.",
            LocalModelCaps.Predict | LocalModelCaps.EditModel, "llama", 32768, "Q4_K_M", 32, 8, "8.3B"),
        new(true, "qwen3.5-9b", "Qwen3.5 9B", "unsloth/Qwen3.5-9B-GGUF", "Qwen3.5-9B-UD-Q4_K_XL.gguf",
            5_966_095_584, "6f5d30666c2d8ae16a306e616d95341dcf3cc46810df84d7e6f5a7d1e4c1b293", 9.65, 9.65, QWEN,
            "The strongest agent at this size (SWE-bench Verified 50): chat, edits by instruction and fill-in-the-middle predictions. Thinks before it answers.",
            LocalModelCaps.Chat | LocalModelCaps.Tools | LocalModelCaps.Thinking | LocalModelCaps.Predict | LocalModelCaps.Fim, "qwen35", 262144, "Q4_K_XL", 8, 4, "9B"),   // 8 of its 32 layers are full attention (the rest keep a small fixed state), the only ones with a KV cache
        new(true, "mellum2.1", "Mellum2.1 12B-A2.5B", "JetBrains/Mellum2.1-12B-A2.5B-Thinking-GGUF", "Mellum2.1-12B-A2.5B-Thinking-Q4_K_M.gguf",
            8_071_295_264, "ecc4d5b8107fc219e23c494b2d107552137637b6c6002ee713e0ba8af7fa2755", 12.15, 2.5, INFER,
            "JetBrains' coding agent (October 2026), a mixture of experts with 2.5B at work per token: several times faster than Qwen3.5 9B, the best at calling tools, close behind on agent work.",
            LocalModelCaps.Chat | LocalModelCaps.Tools | LocalModelCaps.Thinking | LocalModelCaps.Predict | LocalModelCaps.Fim, "mellum", 131072, "Q4_K_M", 28, 4),
        new(true, "qwen2.5-coder-1.5b", "Qwen2.5-Coder 1.5B", "Qwen/Qwen2.5-Coder-1.5B-Instruct-GGUF", "qwen2.5-coder-1.5b-instruct-q8_0.gguf",
            1_894_532_160, "507de59046601282ba768a9789900e6ccf60ed93ddf346730b7c68eb0715bc47", 1.5, 1.5, QWEN,
            "Small and quick, fills in at the caret, for a machine without a graphics card. Chats, but simply.",
            LocalModelCaps.Chat | LocalModelCaps.Tools | LocalModelCaps.Predict | LocalModelCaps.Fim, "qwen2", 32768, "Q8_0", 28, 2),
    ];

    /// <summary>The models that can follow instructions, best first: what edits by instruction use when the code model can't.</summary>
    public static readonly IReadOnlyList<LlamaCppModel> EditModels =
        [.. new[] { "mellum2.1", "qwen3.5-9b", "qwen2.5-coder-1.5b" }.Select(id => All.First(model => model.Id == id && model.Chats))];

    /// <summary>The catalog's model with this id, null for none.</summary>
    public static LlamaCppModel? Find(string id) => All.FirstOrDefault(model => model.Id == id);

    /// <summary>The catalog's model whose file a path is, null for a file of one's own.</summary>
    public static LlamaCppModel? OfFile(string path) =>
        path.Length == 0 ? null : All.FirstOrDefault(model => path.EndsWith(model.File, StringComparison.OrdinalIgnoreCase));

    /// <summary>What a model needs besides its file, its context and the server's buffers.</summary>
    public static long Overhead(long size) => Math.Max(GIB, size / 9);

    /// <summary>How a model of this size fits a machine.</summary>
    public static LlamaCppFit Fit(LlamaCppModel model, LlamaCppPlatform platform) => Fit(model.Size, model.MixtureOfExperts, platform);

    /// <summary>How a file of this size, a mixture of experts or not, fits a machine.</summary>
    public static LlamaCppFit Fit(long size, bool experts, LlamaCppPlatform platform)
    {
        long need = size + Overhead(size);
        long vram = platform.VramMiB * 1024 * 1024;
        long ram = Math.Max(0, platform.MemoryMiB - RESERVED_MIB) * 1024 * 1024;
        bool unified = platform.Gpu?.Vendor == LlamaCppGpu.APPLE;

        if (vram > 0 && need <= vram)
            return LlamaCppFit.Gpu;
        if (unified)
            return need <= ram ? LlamaCppFit.Split : LlamaCppFit.TooBig;
        if (need > vram + ram)
            return LlamaCppFit.TooBig;
        if (vram == 0)
            return LlamaCppFit.Cpu;
        // The experts go to RAM, the rest (attention, the shared weights, the context) has to fit on the card
        return experts && size / 10 + Overhead(size) <= vram ? LlamaCppFit.Experts : LlamaCppFit.Split;
    }

    /// <summary>
    /// The model to offer first: the first in the catalog's order (best first) that runs on the card, else the smallest.
    /// A machine with no card is offered the smallest, the one a processor keeps up with.
    /// </summary>
    public static LlamaCppModel Recommended(LlamaCppPlatform platform)
    {
        if (platform.VramMiB == 0)
            return All.MinBy(model => model.Size)!;
        return All.FirstOrDefault(model => Fit(model, platform) is LlamaCppFit.Gpu or LlamaCppFit.Experts)
            ?? All.MinBy(model => model.Size)!;
    }
}
