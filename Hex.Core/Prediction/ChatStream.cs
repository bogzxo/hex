namespace Hex.Core;

/// <summary>A piece of a chat model's streamed answer: its text, and whether it's what the model thinks rather than its answer.</summary>
internal readonly record struct ChatPiece(string Text, bool Thinking);

/// <summary>A chat model asked with a conversation, its answer streamed back a piece at a time as it's written.</summary>
internal interface IChatStreamer
{
    /// <summary>
    /// The answer as it comes, thinking pieces marked (the server's reasoning field, or &lt;think&gt; blocks in the text).
    /// Throws <see cref="PredictionException"/> for what went wrong; a cancel stays a cancel.
    /// </summary>
    IAsyncEnumerable<ChatPiece> Stream(IReadOnlyList<ChatTurn> conversation, CancellationToken cancel);
}

/// <summary>The chat streamer for a kind of server.</summary>
internal static class ChatStreams
{
    /// <summary>
    /// Ollama's chat (provider ollama, the server's URL) or OpenAI's (provider openai, any URL OpenAiChat.Url takes, Hex's
    /// llama-server among them), asking the model named, with the tuning and the most tokens given. Null for another
    /// provider or a URL that isn't one.
    /// </summary>
    public static IChatStreamer? Make(string provider, string url, string model, string? key, PredictionTuning tuning, int maxTokens) =>
        provider switch
        {
            PredictionProviders.LINKAI => LinkAiModelId.Is(model) ? (IChatStreamer)new LinkChatStreamer(model, tuning, maxTokens) : null,
            PredictionProviders.OLLAMA => OllamaChat.Url(url) is { } chat
                ? new OllamaChat(PredictionProviders.Shared, chat, model) { Tuning = tuning, MaxTokens = maxTokens }
                : null,
            PredictionProviders.OPENAI or PredictionProviders.LLAMACPP => OpenAiChat.Url(url) is { } completions
                ? new OpenAiChat(PredictionProviders.Shared, completions, model, string.IsNullOrWhiteSpace(key) ? null : key)
                    { Tuning = tuning, MaxTokens = maxTokens }
                : null,
            _ => null,
        };
}

/// <summary>
/// A streamed answer split into what the model thinks and what it answers: text between &lt;think&gt; and &lt;/think&gt;
/// comes out as thinking pieces, the tags dropped, even split across pieces. Text that could be the start of a tag is
/// held until the next piece says whether it is one. One per answer.
/// </summary>
internal sealed class ThinkSplitter
{
    private const string OPEN = "<think>", CLOSE = "</think>";

    private string held = string.Empty;
    private bool inside, afterThought;

    /// <summary>Whether any thinking has been seen, for the tests and the status.</summary>
    public bool Thought { get; private set; }

    /// <summary>Whether the answer is in the middle of thinking now.</summary>
    public bool Thinking => inside;

    /// <summary>
    /// The pieces a piece of the stream makes, in order, added to <paramref name="pieces"/> (one list kept for the whole
    /// answer and cleared by the caller); none while only a possible tag has come.
    /// </summary>
    public void Feed(string piece, List<ChatPiece> pieces)
    {
        // The usual piece: no tag begun, none held, nothing that could begin one
        if (held.Length == 0 && piece.IndexOf('<') < 0)
        {
            Add(pieces, piece);
            return;
        }

        string text = held.Length == 0 ? piece : held + piece;
        held = string.Empty;
        int at = 0;
        while (at < text.Length)
        {
            string tag = inside ? CLOSE : OPEN;
            int found = text.IndexOf(tag, at, StringComparison.OrdinalIgnoreCase);
            if (found >= 0)
            {
                Add(pieces, text[at..found]);
                at = found + tag.Length;
                inside = !inside;
                Thought = true;
                if (!inside)
                    afterThought = true;
                continue;
            }

            // No whole tag: the end that could begin one is held, the rest comes out as it is
            int keep = Partial(text, at, tag);
            Add(pieces, keep == 0 && at == 0 ? text : text[at..^keep]);
            held = keep == 0 ? string.Empty : text[^keep..];
            break;
        }
    }

    /// <summary>What was held at the end of the answer: text that turned out not to be a tag, added to the pieces.</summary>
    public void End(List<ChatPiece> pieces)
    {
        Add(pieces, held);
        held = string.Empty;
    }

    // A piece of the text as thinking or answer; the new lines a model puts after its thinking aren't the answer
    private void Add(List<ChatPiece> pieces, string text)
    {
        if (!inside && afterThought)
        {
            text = text.TrimStart('\n', '\r');
            if (text.Length > 0)
                afterThought = false;
        }
        if (text.Length > 0)
            pieces.Add(new ChatPiece(text, inside));
    }

    // How many characters at the end of the text from start could be the start of the tag
    private static int Partial(string text, int start, string tag)
    {
        int most = Math.Min(tag.Length - 1, text.Length - start);
        for (int length = most; length > 0; length--)
            if (string.Compare(text, text.Length - length, tag, 0, length, StringComparison.OrdinalIgnoreCase) == 0)
                return length;
        return 0;
    }
}
