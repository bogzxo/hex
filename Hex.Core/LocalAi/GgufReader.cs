using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>
/// Reads the header and key/value metadata of a GGUF file (versions 2 and 3, little-endian) and nothing else: the tensor
/// data is never touched, and arrays are skipped by seeking, except the tokenizer's tokens, which are scanned for the FIM
/// and Zeta marker tokens without being kept. A truncated or corrupt file gives what was read so far, or null when even the
/// header is wrong. It does not throw on bad data.
/// </summary>
internal static partial class GgufReader
{
    private const int MaxTemplateBytes = 512 * 1024;
    private const int MaxKeyBytes = 512;
    private const int MaxStringBytes = 64 * 1024;
    private const long MaxKvCount = 1_000_000;

    // GGUF value types
    private const uint TypeString = 8, TypeArray = 9;

    public static GgufInfo? Read(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var state = new State();
            var reader = new Source(stream);
            try { if (!Parse(reader, state)) return null; }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or InvalidDataException or ArgumentException or OverflowException or UnauthorizedAccessException)
            {
                // Truncated or corrupt after a valid header: keep what was read
            }
            return state.HeaderRead ? Build(path, stream.Length, state) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>general.file_type (llama.cpp's llama_ftype) as a quant name, or null when unknown.</summary>
    public static string? QuantName(int fileType) => fileType switch
    {
        0 => "F32", 1 => "F16", 2 => "Q4_0", 3 => "Q4_1", 7 => "Q8_0", 8 => "Q5_0", 9 => "Q5_1",
        10 => "Q2_K", 11 => "Q3_K_S", 12 => "Q3_K_M", 13 => "Q3_K_L", 14 => "Q4_K_S", 15 => "Q4_K_M",
        16 => "Q5_K_S", 17 => "Q5_K_M", 18 => "Q6_K", 19 => "IQ2_XXS", 20 => "IQ2_XS", 21 => "Q2_K_S",
        22 => "IQ3_XS", 23 => "IQ3_XXS", 24 => "IQ1_S", 25 => "IQ4_NL", 26 => "IQ3_S", 27 => "IQ3_M",
        28 => "IQ2_S", 29 => "IQ2_M", 30 => "IQ4_XS", 31 => "IQ1_M", 32 => "BF16", 36 => "TQ1_0", 37 => "TQ2_0",
        38 => "MXFP4_MOE",
        _ => null,
    };

    [GeneratedRegex(@"(?<![A-Za-z0-9])((?:IQ|Q|TQ)\d(?:_[A-Z0-9]+)+|BF16|F16|F32|MXFP4(?:_MOE)?)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex QuantInName();

    private sealed class State
    {
        public string? Architecture, Name, SizeLabel, ChatTemplate;
        public long? Parameters;
        public int? FileType;
        public readonly Dictionary<string, long> Numbers = new();
        public bool HeaderRead, FimTokenSeen, FimIdSeen, MarkerSeen;
    }

    private static bool Parse(Source s, State st)
    {
        if (s.U32() != 0x46554747u) return false;   // "GGUF"
        uint version = s.U32();
        if (version is not (2 or 3)) return false;
        s.U64();                                    // tensor count
        ulong kvCount = s.U64();
        if (kvCount > MaxKvCount) kvCount = MaxKvCount;
        st.HeaderRead = true;

        for (ulong i = 0; i < kvCount; i++)
        {
            string? key = s.ReadString(MaxKeyBytes);
            uint type = s.U32();
            if (key is null) { s.SkipValue(type); continue; }

            switch (key)
            {
                case "general.architecture": st.Architecture = ReadStr(s, type, MaxStringBytes); continue;
                case "general.name": st.Name = ReadStr(s, type, MaxStringBytes); continue;
                case "general.size_label": st.SizeLabel = ReadStr(s, type, MaxStringBytes); continue;
                case "tokenizer.chat_template": st.ChatTemplate = ReadStr(s, type, MaxTemplateBytes); continue;
                case "tokenizer.ggml.tokens":
                    if (type == TypeArray) ScanTokens(s, st); else s.SkipValue(type);
                    continue;
                case "tokenizer.ggml.fim_pre_token_id" or "tokenizer.ggml.fim_suf_token_id" or "tokenizer.ggml.fim_mid_token_id"
                    or "tokenizer.ggml.prefix_token_id" or "tokenizer.ggml.suffix_token_id" or "tokenizer.ggml.middle_token_id":
                    st.FimIdSeen = true; s.SkipValue(type); continue;
            }

            if (key == "general.parameter_count") { if (s.TryNumber(type, out long p)) st.Parameters = p; else s.SkipValue(type); continue; }
            if (key == "general.file_type") { if (s.TryNumber(type, out long f)) st.FileType = (int)Math.Clamp(f, int.MinValue, int.MaxValue); else s.SkipValue(type); continue; }

            if (key.EndsWith(".context_length", StringComparison.Ordinal) || key.EndsWith(".block_count", StringComparison.Ordinal)
                || key.EndsWith(".embedding_length", StringComparison.Ordinal) || key.EndsWith(".expert_count", StringComparison.Ordinal))
            {
                if (s.TryNumber(type, out long v)) st.Numbers[key] = v; else s.SkipValue(type);
            }
            else if (key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal))
            {
                if (type == TypeArray) { long max = s.MaxOfNumberArray(); if (max > 0) st.Numbers[key] = max; }
                else if (s.TryNumber(type, out long v)) st.Numbers[key] = v; else s.SkipValue(type);
            }
            else s.SkipValue(type);
        }
        return true;
    }

    private static string? ReadStr(Source s, uint type, int max)
    {
        if (type != TypeString) { s.SkipValue(type); return null; }
        return s.ReadString(max);
    }

    // Walks the tokens array without keeping it, noting FIM and Zeta marker tokens
    private static void ScanTokens(Source s, State st)
    {
        uint elementType = s.U32();
        ulong count = s.U64();
        if (elementType != TypeString) { s.SkipArrayBody(elementType, count); return; }
        for (ulong i = 0; i < count; i++)
        {
            ulong len = s.U64();
            if (len is >= 5 and <= 40)
            {
                ReadOnlySpan<byte> span = s.Peek((int)len);
                if (span[0] == (byte)'<' && span[^1] == (byte)'>')
                {
                    string token = Encoding.UTF8.GetString(span);
                    if (token.Contains("fim_prefix", StringComparison.OrdinalIgnoreCase) || token.Contains("fim-prefix", StringComparison.OrdinalIgnoreCase)
                        || token.Contains("fim_begin", StringComparison.OrdinalIgnoreCase) || token.Contains("fim_suffix", StringComparison.OrdinalIgnoreCase)
                        || token.Contains("fim_middle", StringComparison.OrdinalIgnoreCase) || token is "<PRE>" or "<SUF>" or "<MID>")
                        st.FimTokenSeen = true;
                    else if (token.StartsWith("<|marker_", StringComparison.Ordinal)) st.MarkerSeen = true;
                }
                s.Skip((long)len);
            }
            else s.Skip(checked((long)len));
        }
    }

    private static GgufInfo Build(string path, long bytes, State st)
    {
        string? arch = st.Architecture;
        int? Arch(string suffix) => arch is not null && st.Numbers.TryGetValue(arch + suffix, out long v) ? (int)Math.Clamp(v, 0, int.MaxValue) : null;

        string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
        string? quant = null;
        var match = QuantInName().Match(fileName);
        if (match.Success) quant = match.Groups[1].Value.ToUpperInvariant();
        else if (st.FileType is int ft) quant = QuantName(ft);

        string template = st.ChatTemplate ?? string.Empty;
        bool zeta = (st.Name ?? string.Empty).Contains("zeta", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("zeta", StringComparison.OrdinalIgnoreCase)
            || st.MarkerSeen
            || template.Contains("<|marker_", StringComparison.Ordinal)
            || (template.Contains("CURRENT", StringComparison.Ordinal) && template.Contains("UPDATED", StringComparison.Ordinal));

        // Only what the template says is kept, not the template (it runs to kilobytes per model, held for the whole session)
        (string? head, bool tools, bool thinking) = Digest(st.ChatTemplate);
        return new GgufInfo(path, bytes, arch, st.Name, st.SizeLabel, st.Parameters, st.FileType, quant,
            Arch(".context_length"), Arch(".block_count"), Arch(".embedding_length"), Arch(".attention.head_count_kv"), Arch(".expert_count"),
            head, st.FimTokenSeen || st.FimIdSeen, zeta, tools, thinking);
    }

    /// <summary>A chat template as it's kept: its first lines (what the model's details show), and whether it mentions tools and thinking. Null head for no template.</summary>
    internal static (string? Head, bool Tools, bool Thinking) Digest(string? template)
    {
        if (string.IsNullOrEmpty(template))
            return (null, false, false);
        bool tools = template.Contains("tools", StringComparison.OrdinalIgnoreCase) || template.Contains("tool_call", StringComparison.OrdinalIgnoreCase);
        bool thinking = template.Contains("<think>", StringComparison.Ordinal) || template.Contains("enable_thinking", StringComparison.Ordinal);
        int cut = template.Length, seen = 0;
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] == '\n' && ++seen == PREVIEW_LINES)
            {
                cut = i;
                break;
            }
        }
        return (string.Join("\n", template[..cut].Split('\n').Select(line => line.Length > PREVIEW_WIDTH ? line[..PREVIEW_WIDTH] + "…" : line)), tools, thinking);
    }

    private const int PREVIEW_LINES = 4, PREVIEW_WIDTH = 110;

    /// <summary>A small buffered forward reader over a file that can seek past what it does not need.</summary>
    private sealed class Source(FileStream stream)
    {
        private readonly byte[] _buffer = new byte[1 << 16];
        private int _pos, _len;
        private long _base;                 // file offset of _buffer[0]
        private readonly long _length = stream.Length;

        private long Position => _base + _pos;

        private void Need(int n)
        {
            if (n > _buffer.Length) throw new InvalidDataException("value too large");
            if (_len - _pos >= n) return;
            int keep = _len - _pos;
            Buffer.BlockCopy(_buffer, _pos, _buffer, 0, keep);
            _base += _pos; _pos = 0; _len = keep;
            while (_len < n)
            {
                int got = stream.Read(_buffer, _len, _buffer.Length - _len);
                if (got <= 0) throw new EndOfStreamException();
                _len += got;
            }
        }

        public uint U32() { Need(4); uint v = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_pos)); _pos += 4; return v; }
        public ulong U64() { Need(8); ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(_pos)); _pos += 8; return v; }

        /// <summary>The next n bytes, not consumed. n must fit the buffer.</summary>
        public ReadOnlySpan<byte> Peek(int n) { Need(n); return _buffer.AsSpan(_pos, n); }

        public void Skip(long n)
        {
            if (n < 0 || Position + n > _length) throw new EndOfStreamException();
            if (n <= _len - _pos) { _pos += (int)n; return; }
            long target = Position + n;
            stream.Seek(target, SeekOrigin.Begin);
            _base = target; _pos = 0; _len = 0;
        }

        public string? ReadString(int max)
        {
            ulong len = U64();
            if (len > (ulong)(_length - Position)) throw new EndOfStreamException();
            if (len > (ulong)max) { Skip((long)len); return null; }
            int n = (int)len;
            Need(n);
            string text = Encoding.UTF8.GetString(_buffer, _pos, n);
            _pos += n;
            return text;
        }

        public bool TryNumber(uint type, out long value)
        {
            value = 0;
            switch (type)
            {
                case 0: Need(1); value = _buffer[_pos]; _pos += 1; return true;
                case 1: Need(1); value = (sbyte)_buffer[_pos]; _pos += 1; return true;
                case 2: Need(2); value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_pos)); _pos += 2; return true;
                case 3: Need(2); value = BinaryPrimitives.ReadInt16LittleEndian(_buffer.AsSpan(_pos)); _pos += 2; return true;
                case 4: Need(4); value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_pos)); _pos += 4; return true;
                case 5: Need(4); value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_pos)); _pos += 4; return true;
                case 10: { ulong v = U64(); value = v > long.MaxValue ? long.MaxValue : (long)v; return true; }
                case 11: Need(8); value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.AsSpan(_pos)); _pos += 8; return true;
                default: return false;
            }
        }

        public static int FixedSize(uint type) => type switch
        {
            0 or 1 or 7 => 1,
            2 or 3 => 2,
            4 or 5 or 6 => 4,
            10 or 11 or 12 => 8,
            _ => 0,
        };

        public void SkipValue(uint type)
        {
            int size = FixedSize(type);
            if (size > 0) { Skip(size); return; }
            if (type == TypeString) { ulong len = U64(); Skip(checked((long)len)); return; }
            if (type == TypeArray) { uint element = U32(); SkipArrayBody(element, U64()); return; }
            throw new InvalidDataException("unknown value type");
        }

        public void SkipArrayBody(uint element, ulong count)
        {
            int size = FixedSize(element);
            if (size > 0)
            {
                if (count > (ulong)(_length / size)) throw new EndOfStreamException();
                Skip(checked((long)count * size));
                return;
            }
            if (count > (ulong)_length) throw new EndOfStreamException();
            for (ulong i = 0; i < count; i++) SkipValue(element);
        }

        /// <summary>Consumes an array (its element type and length first) and gives its largest integer, 0 when not integers.</summary>
        public long MaxOfNumberArray()
        {
            uint element = U32();
            ulong count = U64();
            if (FixedSize(element) == 0 || element is 6 or 7 or 12 || count > (ulong)_length) { SkipArrayBody(element, count); return 0; }
            long max = 0;
            for (ulong i = 0; i < count; i++) { TryNumber(element, out long v); if (v > max) max = v; }
            return max;
        }
    }
}
