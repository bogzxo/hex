using System.Text;
using Hex.Core;
using Xunit.Abstractions;

namespace Hex.Tests;

/// <summary>The GGUF metadata reader and the capabilities read from it, on bytes built here, and on real models when they are on this computer.</summary>
public sealed class GgufReaderTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hex-gguf-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Gguf
    {
        private readonly MemoryStream _kv = new();
        private readonly BinaryWriter _w;
        private int _count;
        public Gguf() { _w = new BinaryWriter(_kv, Encoding.UTF8, true); }

        private void Str(string s) { var b = Encoding.UTF8.GetBytes(s); _w.Write((ulong)b.Length); _w.Write(b); }
        private void Key(string key, uint type) { Str(key); _w.Write(type); _count++; }

        public Gguf Text(string key, string value) { Key(key, 8); Str(value); return this; }
        public Gguf U32(string key, uint value) { Key(key, 4); _w.Write(value); return this; }
        public Gguf U64(string key, ulong value) { Key(key, 10); _w.Write(value); return this; }
        public Gguf Strings(string key, params string[] values) { Key(key, 9); _w.Write(8u); _w.Write((ulong)values.Length); foreach (var v in values) Str(v); return this; }
        public Gguf Ints(string key, params int[] values) { Key(key, 9); _w.Write(5u); _w.Write((ulong)values.Length); foreach (var v in values) _w.Write(v); return this; }
        public Gguf Floats(string key, params float[] values) { Key(key, 9); _w.Write(6u); _w.Write((ulong)values.Length); foreach (var v in values) _w.Write(v); return this; }

        public byte[] Bytes(uint version = 3, bool magic = true)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes(magic ? "GGUF" : "GGUX"));
            w.Write(version);
            w.Write(0UL);
            w.Write((ulong)_count);
            w.Write(_kv.ToArray());
            w.Flush();
            return ms.ToArray();
        }
    }

    private string Save(string name, byte[] bytes) { string path = Path.Combine(_dir, name); File.WriteAllBytes(path, bytes); return path; }

    private static Gguf Basic() => new Gguf()
        .Text("general.architecture", "qwen35").Text("general.name", "Qwen3.5 9B").Text("general.size_label", "9B")
        .U32("general.file_type", 15).U64("general.parameter_count", 9_000_000_000)
        .Floats("qwen35.rope.scaling", 1f, 2f)
        .U32("qwen35.context_length", 262144).U32("qwen35.block_count", 32).U32("qwen35.embedding_length", 4096)
        .Ints("qwen35.attention.head_count_kv", 0, 0, 4, 0, 4).U32("qwen35.expert_count", 8);

    [Fact]
    public void A_valid_v3_header_gives_its_metadata_and_a_quant_from_the_file_type()
    {
        string path = Save("model.gguf", Basic().Strings("tokenizer.ggml.tokens", "a", "<|im_start|>", "b")
            .Text("tokenizer.chat_template", "{% for t in tools %}{{ t }}{% endfor %}<think>").Bytes());
        var info = GgufReader.Read(path);
        Assert.NotNull(info);
        Assert.Equal("qwen35", info.Architecture);
        Assert.Equal("Qwen3.5 9B", info.Name);
        Assert.Equal("9B", info.SizeLabel);
        Assert.Equal(9_000_000_000, info.Parameters);
        Assert.Equal(15, info.FileType);
        Assert.Equal("Q4_K_M", info.Quant);
        Assert.Equal(262144, info.ContextLength);
        Assert.Equal(32, info.Layers);
        Assert.Equal(4096, info.EmbeddingLength);
        Assert.Equal(4, info.HeadCountKv);
        Assert.Equal(8, info.ExpertCount);
        Assert.False(info.HasFimTokens);
        Assert.False(info.IsZeta);
        var caps = LocalModelCapsOf.From(info);
        Assert.Equal(LocalModelCaps.Chat | LocalModelCaps.Tools | LocalModelCaps.Thinking, caps);
    }

    [Fact]
    public void Version_2_is_read_too_and_other_versions_are_not()
    {
        Assert.NotNull(GgufReader.Read(Save("v2.gguf", Basic().Bytes(version: 2))));
        Assert.Null(GgufReader.Read(Save("v1.gguf", Basic().Bytes(version: 1))));
        Assert.Null(GgufReader.Read(Save("v9.gguf", Basic().Bytes(version: 9))));
    }

    [Fact]
    public void The_quant_in_the_file_name_wins_over_the_file_type_when_it_is_more_specific()
    {
        var info = GgufReader.Read(Save("Qwen3.5-9B-UD-Q4_K_XL.gguf", Basic().Bytes()));
        Assert.Equal("Q4_K_XL", info!.Quant);
        var noFileType = GgufReader.Read(Save("plain.gguf", new Gguf().Text("general.architecture", "llama").Bytes()));
        Assert.Null(noFileType!.Quant);
    }

    [Fact]
    public void Fim_tokens_in_the_tokens_array_give_Fim_and_Predict()
    {
        string path = Save("fim.gguf", Basic().Strings("tokenizer.ggml.tokens", "x", "<|fim_prefix|>", "<|fim_suffix|>", "<|fim_middle|>").Bytes());
        var info = GgufReader.Read(path)!;
        Assert.True(info.HasFimTokens);
        var caps = LocalModelCapsOf.From(info);
        Assert.True(caps.HasFlag(LocalModelCaps.Fim) && caps.HasFlag(LocalModelCaps.Predict));
        Assert.False(caps.HasFlag(LocalModelCaps.Chat));
    }

    [Fact]
    public void Zeta_with_fim_tokens_is_an_edit_model_without_the_Fim_badge_while_others_keep_Fim()
    {
        var zeta = GgufReader.Read(Save("zeta-2.1.gguf", new Gguf().Text("general.architecture", "llama").Text("general.name", "Zeta 2.1")
            .Strings("tokenizer.ggml.tokens", "x", "<fim_prefix>", "<fim_suffix>", "<fim_middle>").Bytes()))!;
        Assert.True(zeta.HasFimTokens);
        Assert.Equal(LocalModelCaps.EditModel | LocalModelCaps.Predict, LocalModelCapsOf.From(zeta));

        var mellum = GgufReader.Read(Save("mellum.gguf", new Gguf().Text("general.architecture", "llama").Text("general.name", "Mellum")
            .Text("tokenizer.chat_template", "{{ messages }}").Strings("tokenizer.ggml.tokens", "x", "<fim_prefix>", "<fim_suffix>", "<fim_middle>").Bytes()))!;
        var caps = LocalModelCapsOf.From(mellum);
        Assert.True(caps.HasFlag(LocalModelCaps.Fim) && caps.HasFlag(LocalModelCaps.Predict) && caps.HasFlag(LocalModelCaps.Chat));
        Assert.False(LocalModelCapsOf.FromOllama(["completion", "insert"], "zeta-2.1").HasFlag(LocalModelCaps.Fim));
    }

    [Theory]
    [InlineData("64x1.2B", "Mellum2.1-12B-A2.5B-Thinking-Q4_K_M", "12B-A2.5B")]
    [InlineData("64x1.2B", "Mellum", "64×1.2B MoE")]
    [InlineData("9B", "Qwen3.5-9B", "9B")]
    [InlineData(null, "plain", null)]
    [InlineData("8x7B", "Qwen3-30B-A3B-Instruct", "30B-A3B")]
    public void The_size_label_shows_total_and_active_parameters_of_a_mixture_of_experts(string? label, string name, string? expected) =>
        Assert.Equal(expected, LocalModelSize.Label(label, name));

    [Fact]
    public void Fim_token_ids_alone_give_Fim()
    {
        string path = Save("ids.gguf", Basic().U32("tokenizer.ggml.fim_pre_token_id", 7).Bytes());
        Assert.True(GgufReader.Read(path)!.HasFimTokens);
    }

    [Fact]
    public void Zeta_by_name_or_by_marker_template_is_an_edit_model_that_does_not_chat()
    {
        var byName = GgufReader.Read(Save("zeta-2.1.gguf", new Gguf().Text("general.architecture", "qwen2").Text("general.name", "Zeta 2.1")
            .Text("tokenizer.chat_template", "{{ messages }}").Bytes()))!;
        Assert.True(byName.IsZeta);
        var caps = LocalModelCapsOf.From(byName);
        Assert.Equal(LocalModelCaps.EditModel | LocalModelCaps.Predict, caps);

        var byTemplate = GgufReader.Read(Save("other.gguf", new Gguf().Text("general.architecture", "qwen2").Text("tokenizer.chat_template", "<|marker_1|> CURRENT UPDATED").Bytes()))!;
        Assert.True(byTemplate.IsZeta);
        var byTokens = GgufReader.Read(Save("tok.gguf", new Gguf().Text("general.architecture", "qwen2").Strings("tokenizer.ggml.tokens", "<|marker_3|>").Bytes()))!;
        Assert.True(byTokens.IsZeta);
    }

    [Fact]
    public void A_truncated_file_gives_what_was_read_or_null_and_never_throws()
    {
        byte[] whole = Basic().Strings("tokenizer.ggml.tokens", "a", "b", "c").Text("tokenizer.chat_template", "hello").Bytes();
        for (int cut = 0; cut < whole.Length; cut += 3)
        {
            var info = GgufReader.Read(Save("cut.gguf", whole[..cut]));
            if (cut < 24) Assert.Null(info);   // the header isn't whole
        }
        var partial = GgufReader.Read(Save("partial.gguf", whole[..(whole.Length - 20)]));
        Assert.NotNull(partial);
        Assert.Equal("qwen35", partial.Architecture);
        Assert.Equal(32, partial.Layers);
    }

    [Fact]
    public void A_wrong_magic_an_empty_file_garbage_and_a_missing_file_give_null()
    {
        Assert.Null(GgufReader.Read(Save("magic.gguf", Basic().Bytes(magic: false))));
        Assert.Null(GgufReader.Read(Save("empty.gguf", [])));
        Assert.Null(GgufReader.Read(Save("junk.gguf", Enumerable.Range(0, 500).Select(i => (byte)(i * 31)).ToArray())));
        Assert.Null(GgufReader.Read(Path.Combine(_dir, "missing.gguf")));
        Assert.Null(GgufReader.Read(""));
    }

    [Fact]
    public void Absurd_counts_and_lengths_do_not_throw_or_allocate()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write(Encoding.ASCII.GetBytes("GGUF")); w.Write(3u); w.Write(ulong.MaxValue); w.Write(ulong.MaxValue);
            w.Write(ulong.MaxValue); w.Write(Encoding.ASCII.GetBytes("general.name"));   // a key length far past the file
        }
        var info = GgufReader.Read(Save("absurd.gguf", ms.ToArray()));
        Assert.True(info is null || info.Architecture is null);
    }

    [Theory]
    [InlineData(15, "Q4_K_M")]
    [InlineData(1, "F16")]
    [InlineData(7, "Q8_0")]
    [InlineData(18, "Q6_K")]
    [InlineData(32, "BF16")]
    public void Quant_names_map_from_the_file_type(int fileType, string name) => Assert.Equal(name, GgufReader.QuantName(fileType));

    [Fact]
    public void An_unknown_file_type_has_no_quant_name() => Assert.Null(GgufReader.QuantName(9999));

    [Fact]
    public void Ollama_capabilities_map_to_the_caps()
    {
        Assert.Equal(LocalModelCaps.Chat | LocalModelCaps.Tools | LocalModelCaps.Thinking | LocalModelCaps.Vision,
            LocalModelCapsOf.FromOllama(["completion", "tools", "thinking", "vision"], "qwen3-vl"));
        Assert.Equal(LocalModelCaps.Fim | LocalModelCaps.Predict | LocalModelCaps.Chat, LocalModelCapsOf.FromOllama(["completion", "insert"], "qwen2.5-coder"));
        Assert.Equal(LocalModelCaps.EditModel | LocalModelCaps.Predict, LocalModelCapsOf.FromOllama(["completion"], "hf.co/x/Zeta-2:Q4"));
        Assert.Equal(LocalModelCaps.None, LocalModelCapsOf.FromOllama([], "x"));
    }

    private static readonly string[] RealModels =
    [
        @"D:\models\Mellum2.1-12B-A2.5B-Thinking-Q4_K_M.gguf",
        @"D:\models\Qwen3.5-9B-UD-Q4_K_XL.gguf",
        @"D:\models\zeta-2.1.i1-Q4_K_M.gguf",
    ];

    /// <summary>Only the real files that are on this computer; passes without any of them.</summary>
    [Fact]
    public void Real_models_in_D_models_are_read_quickly()
    {
        foreach (string path in RealModels)
        {
            if (!File.Exists(path)) continue;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var info = GgufReader.Read(path);
            clock.Stop();
            Assert.NotNull(info);
            var caps = LocalModelCapsOf.From(info);
            output.WriteLine($"{Path.GetFileName(path)}: {clock.ElapsedMilliseconds} ms, arch={info.Architecture}, name={info.Name}, size={info.SizeLabel}, params={info.Parameters}, " +
                $"ftype={info.FileType}, quant={info.Quant}, ctx={info.ContextLength}, layers={info.Layers}, emb={info.EmbeddingLength}, kv={info.HeadCountKv}, experts={info.ExpertCount}, " +
                $"template={(info.ChatTemplate?.Length ?? 0)} chars, fim={info.HasFimTokens}, zeta={info.IsZeta}, caps={caps}");
            Assert.NotNull(info.Architecture);
            Assert.True(clock.ElapsedMilliseconds < 1000);
            if (path.Contains("zeta", StringComparison.Ordinal)) { Assert.True(info.IsZeta); Assert.False(caps.HasFlag(LocalModelCaps.Chat)); }
        }
    }
}
