namespace Hex.Core;

/// <summary>
/// Fill in the middle, the way code models are trained to complete at the caret: the text before it and after it
/// between the family's tokens, and the model writes what goes between. The answer is inserted at the caret. The
/// families differ in their tokens and in whether the text after the caret goes first.
/// </summary>
internal sealed class FimFormat : IPredictionFormat
{
    // About 1500 tokens before the caret and 500 after, in bytes
    private const int BEFORE_BYTES = 1500 * PredictionTokens.BYTES;
    private const int AFTER_BYTES = 500 * PredictionTokens.BYTES;

    // The most tokens of an answer that finishes the caret's line
    private const int MOST_IN_LINE = 64;

    // The family's tokens round the text before the caret, the text after it and the answer, and their order
    private readonly string prefix, suffix, middle;
    private readonly bool suffixFirst;

    // Where the family's answers end: its end tokens and its own markers
    private readonly string[] stops;

    private FimFormat(string name, string prefix, string suffix, string middle, bool suffixFirst, string[] ends)
    {
        Name = name;
        this.prefix = prefix;
        this.suffix = suffix;
        this.middle = middle;
        this.suffixFirst = suffixFirst;
        stops = [.. ends, .. new[] { prefix.Trim(), suffix.Trim(), middle.Trim() }.Where(token => token.Length > 0)];
    }

    public string Name { get; }

    /// <summary>Qwen2.5-Coder's tokens, the commonest, and what a model of no known family is asked with.</summary>
    public static readonly FimFormat Qwen = new(PredictionFormats.QWEN, "<|fim_prefix|>", "<|fim_suffix|>", "<|fim_middle|>", false,
        ["<|endoftext|>", "<|file_sep|>", "<|repo_name|>", "<|im_end|>"]);

    /// <summary>CodeGemma, Qwen's tokens with its own file separator and ends.</summary>
    public static readonly FimFormat CodeGemma = new(PredictionFormats.CODE_GEMMA, "<|fim_prefix|>", "<|fim_suffix|>", "<|fim_middle|>", false,
        ["<|file_separator|>", "<eos>", "<end_of_turn>"]);

    /// <summary>StarCoder's tokens, without the bars.</summary>
    public static readonly FimFormat StarCoder = new(PredictionFormats.STAR_CODER, "<fim_prefix>", "<fim_suffix>", "<fim_middle>", false,
        ["<|endoftext|>", "<file_sep>"]);

    /// <summary>Code Llama's, with the spaces it was trained with.</summary>
    public static readonly FimFormat CodeLlama = new(PredictionFormats.CODE_LLAMA, "<PRE> ", " <SUF>", " <MID>", false,
        [" <EOT>", "<EOT>"]);

    /// <summary>DeepSeek Coder's, full-width bars and all.</summary>
    public static readonly FimFormat DeepSeekCoder = new(PredictionFormats.DEEPSEEK_CODER, "<｜fim▁begin｜>", "<｜fim▁hole｜>", "<｜fim▁end｜>", false,
        ["<｜end▁of▁sentence｜>", "<|EOT|>"]);

    /// <summary>Codestral's, the text after the caret first and nothing after the text before it.</summary>
    public static readonly FimFormat Codestral = new(PredictionFormats.CODESTRAL, "[PREFIX]", "[SUFFIX]", "", true,
        ["</s>", "[MIDDLE]"]);

    public PredictionPrompt? Build(PredictionInput input, int maxTokens)
    {
        string text = input.Text;
        int caret = input.Caret;
        if (caret < 0 || caret > text.Length || HasTokens(text))
            return null;

        // Inside a word nothing is asked, except where the word is the old name of a rename being followed: the word is
        // taken out, and what the model writes in its place has to be the new name
        int anchor = caret, tail = caret;
        string? expect = null;
        if (InWord(text, caret))
        {
            if (input.Rename is not { } rename || !WordAt(text, caret, out anchor, out tail) || text[anchor..tail] != rename.OldName
                || rename.NewName.Length == 0)
                return null;
            expect = rename.NewName;
        }

        int lineEnd = text.IndexOf('\n', tail);
        if (lineEnd < 0)
            lineEnd = text.Length;
        bool midLine = !text.AsSpan(tail, lineEnd - tail).IsWhiteSpace();

        int start = Before(text, anchor), end = After(text, tail, lineEnd);
        string before = text[start..anchor], after = text[tail..end];
        if (input.Notebook is { } notebook)
        {
            if (start == 0 && notebook.Before.Length > 0 && !HasTokens(notebook.Before))
            {
                before = notebook.Before + "\n\n" + before;
                before = before[Before(before, before.Length)..];
            }
            if (end == text.Length && notebook.After.Length > 0 && !HasTokens(notebook.After))
            {
                after += "\n\n" + notebook.After;
                after = after[..After(after, 0, lineEnd - tail)];
            }
        }

        // The recent edits as comments before the path's, where the language has comments
        string history = Marker(input.Language) is { } marker
            ? PredictionEventText.Comments(input.Events, input.Rename, input.Path, marker, PredictionEventText.FIM_TOKENS)
            : "";
        if (HasTokens(history))
            history = "";
        before = history + Comment(input.Language, input.Path) + before;
        string written = suffixFirst ? suffix + after + prefix + before + middle : prefix + before + suffix + after + middle;

        // In the middle of a line only the rest of the line is wanted
        string[] stop = midLine ? ["\n", .. stops] : stops;
        int most = Math.Max(1, midLine ? Math.Min(maxTokens, MOST_IN_LINE) : maxTokens);
        if (expect is not null)
            most = Math.Max(1, Math.Min(most, MOST_IN_LINE / 4));
        var request = new PredictionWireRequest(written, null, most, stop, Name);
        return expect is null
            ? new PredictionPrompt(request, caret, caret, "", caret)
            : new PredictionPrompt(request, anchor, tail, text[anchor..tail], anchor, expect);
    }

    public PredictionParsed Parse(PredictionPrompt prompt, string output, bool complete)
    {
        string answer = output.Contains('\r') ? output.Replace("\r\n", "\n") : output;
        bool cut = false;

        // The family's own tokens end it, whatever the server did with the stop sequences
        foreach (string stop in stops)
        {
            int at = answer.IndexOf(stop, StringComparison.Ordinal);
            if (at >= 0)
            {
                answer = answer[..at];
                cut = true;
            }
        }

        if (prompt.Expect is { } expect)
            return Renamed(prompt, expect, answer, cut || complete);

        var (before, after) = Sides(prompt.Request.Prompt);
        int restEnd = after.IndexOf('\n');
        string rest = restEnd < 0 ? after : after[..restEnd];

        if (!rest.AsSpan().IsWhiteSpace())
        {
            int newline = answer.IndexOf('\n');
            if (newline >= 0)
            {
                answer = answer[..newline];
                cut = true;
            }
        }
        else
        {
            int indent = Indent(before.AsSpan(before.LastIndexOf('\n') + 1));
            if (Block(answer, indent, complete || cut) is { } end)
            {
                answer = answer[..end];
                cut = true;
            }
        }

        if (!cut && !complete)
            return PredictionParsed.Unsettled;

        string insertion = Unrepeated(answer.TrimEnd(), rest);

        // The caret after only the line's indentation: an answer that writes it again doesn't double it
        string lineBefore = before[(before.LastIndexOf('\n') + 1)..];
        if (lineBefore.Length > 0 && lineBefore.AsSpan().IsWhiteSpace() && insertion.StartsWith(lineBefore, StringComparison.Ordinal))
            insertion = insertion[lineBefore.Length..];
        if (insertion.AsSpan().IsWhiteSpace())
            return PredictionParsed.Nothing;
        return new PredictionParsed([new PredictionEdit(prompt.Caret, prompt.Caret, insertion)], prompt.Caret + insertion.Length, true);
    }

    // A rename's answer: its first word has to be the new name, and then it replaces the old word where the caret was
    private static PredictionParsed Renamed(PredictionPrompt prompt, string expect, string answer, bool ended)
    {
        int length = 0;
        while (length < answer.Length && Word(answer[length]))
            length++;
        if (!ended && length == answer.Length)
            return PredictionParsed.Unsettled;
        if (answer[..length] != expect)
            return PredictionParsed.Nothing;
        return new PredictionParsed([new PredictionEdit(prompt.RegionStart, prompt.RegionEnd, expect)], prompt.RegionStart + expect.Length, true);
    }

    // The word the offset is inside, where it starts and ends
    private static bool WordAt(string text, int at, out int start, out int end)
    {
        start = at;
        end = at;
        while (start > 0 && Word(text[start - 1]))
            start--;
        while (end < text.Length && Word(text[end]))
            end++;
        return end > start;
    }

    /// <summary>The answer with what it repeats of the text after the caret on its line taken off its end.</summary>
    internal static string Unrepeated(string insertion, string rest)
    {
        rest = rest.TrimEnd();
        for (int length = Math.Min(insertion.Length, rest.Length); length > 0; length--)
        {
            if (insertion.AsSpan().EndsWith(rest.AsSpan(0, length), StringComparison.Ordinal))
                return insertion[..^length];
        }
        return insertion;
    }

    // Where a block of lines written at the end of a line is finished: a blank line after at least one line, or a line
    // indented less than the caret's line. Only whole lines count until the answer has ended.
    private static int? Block(string answer, int indent, bool ended)
    {
        int first = answer.IndexOf('\n');
        if (first < 0)
            return null;

        bool written = !answer.AsSpan(0, first).IsWhiteSpace();
        for (int lineStart = first + 1; lineStart < answer.Length;)
        {
            int lineEnd = answer.IndexOf('\n', lineStart);
            if (lineEnd < 0 && !ended)
                return null;
            if (lineEnd < 0)
                lineEnd = answer.Length;

            ReadOnlySpan<char> line = answer.AsSpan(lineStart, lineEnd - lineStart);
            if (line.IsWhiteSpace())
            {
                if (written)
                    return lineStart - 1;
            }
            else
            {
                if (Indent(line) < indent)
                    return lineStart - 1;
                written = true;
            }
            lineStart = lineEnd + 1;
        }
        return null;
    }

    // The text before the caret and after it, read back out of a prompt this format built
    private (string Before, string After) Sides(string written)
    {
        if (suffixFirst)
        {
            int at = written.IndexOf(prefix, suffix.Length, StringComparison.Ordinal);
            return at < 0 ? ("", "") : (written[(at + prefix.Length)..^middle.Length], written[suffix.Length..at]);
        }

        int split = written.IndexOf(suffix, prefix.Length, StringComparison.Ordinal);
        return split < 0 ? ("", "") : (written[prefix.Length..split], written[(split + suffix.Length)..^middle.Length]);
    }

    // Whether the text holds one of the family's tokens, which would make its prompt ambiguous
    private bool HasTokens(string text) =>
        text.Contains(prefix.Trim(), StringComparison.Ordinal)
        || text.Contains(suffix.Trim(), StringComparison.Ordinal)
        || middle.Length > 0 && text.Contains(middle.Trim(), StringComparison.Ordinal);

    private static int Indent(ReadOnlySpan<char> line)
    {
        int width = 0;
        foreach (char each in line)
        {
            if (each == ' ')
                width++;
            else if (each == '\t')
                width += 4;
            else
                break;
        }
        return width;
    }

    // Whether the caret is inside a word, where a fill-in-the-middle model only guesses
    private static bool InWord(string text, int caret) =>
        caret > 0 && caret < text.Length && Word(text[caret - 1]) && Word(text[caret]);

    private static bool Word(char each) => char.IsLetterOrDigit(each) || each == '_';

    // Where the text before the caret starts: about 1500 tokens back, at the start of a line
    private static int Before(string text, int caret)
    {
        int start = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        int bytes = PredictionTokens.Bytes(text, start, caret - start);
        while (start > 0)
        {
            int lineStart = start < 2 ? 0 : text.LastIndexOf('\n', start - 2) + 1;
            bytes += PredictionTokens.Bytes(text, lineStart, start - lineStart);
            if (bytes > BEFORE_BYTES)
                break;
            start = lineStart;
        }
        return start;
    }

    // Where the text after the caret ends: about 500 tokens on, at the end of a line, never before the caret's line ends
    private static int After(string text, int caret, int lineEnd)
    {
        int end = lineEnd;
        int bytes = PredictionTokens.Bytes(text, caret, lineEnd - caret);
        while (end < text.Length)
        {
            int next = text.IndexOf('\n', end + 1);
            if (next < 0)
                next = text.Length;
            bytes += PredictionTokens.Bytes(text, end, next - end);
            if (bytes > AFTER_BYTES)
                break;
            end = next;
        }
        return end;
    }

    // The file's path as a comment line, where the language has line comments
    private static string Comment(string language, string path) =>
        Marker(language) is { } marker ? marker + " " + path.Replace('\\', '/') + "\n" : "";

    // The language's line comment marker, null where it has none
    private static string? Marker(string language) =>
        language.ToLowerInvariant() switch
        {
            "csharp" or "c" or "cpp" or "objective-c" or "objective-cpp" or "java" or "javascript" or "javascriptreact" or "typescript"
                or "typescriptreact" or "go" or "rust" or "swift" or "kotlin" or "dart" or "scala" or "php" or "fsharp" or "hidl" or "hlsl"
                or "glsl" or "zig" or "d" or "jsonc" or "groovy" => "//",
            "python" or "shellscript" or "bash" or "sh" or "ruby" or "yaml" or "toml" or "cmake" or "powershell" or "perl" or "r"
                or "dockerfile" or "makefile" or "elixir" or "julia" or "nim" => "#",
            "lua" or "sql" or "haskell" or "elm" => "--",
            _ => null,
        };
}
