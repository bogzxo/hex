namespace Hex.Core;

/// <summary>What a GGUF file's metadata says about the model, read without loading its tensors.</summary>
internal sealed record GgufInfo(
    string Path, long Bytes,
    string? Architecture,        // general.architecture
    string? Name,                // general.name
    string? SizeLabel,           // general.size_label, e.g. "9B", "12B-A2.5B"
    long? Parameters,            // general.parameter_count when present
    int? FileType,               // general.file_type, mapped to a quant name by GgufReader.QuantName
    string? Quant,               // e.g. "Q4_K_M", from file_type or the file name
    int? ContextLength,          // <arch>.context_length
    int? Layers,                 // <arch>.block_count
    int? EmbeddingLength,        // <arch>.embedding_length
    int? HeadCountKv,            // <arch>.attention.head_count_kv (for a KV-cache estimate)
    int? ExpertCount,            // <arch>.expert_count (MoE)
    string? ChatTemplate,        // tokenizer.chat_template's first lines only (GgufReader.Digest); null when the file has no template
    bool HasFimTokens,           // fim prefix/suffix/middle token ids or tokens present
    bool IsZeta,                 // name, or a template/tokens recognisable as Zeta edit models
    bool TemplateTools = false,  // the template mentions tools
    bool TemplateThinking = false); // the template mentions thinking
