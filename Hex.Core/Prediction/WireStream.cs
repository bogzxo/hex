using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Hex.Core;

/// <summary>
/// A streamed answer read a line at a time as bytes in one pooled buffer: no string per line, no decoder. A line's bytes
/// are good until the next call. Server-sent events and lines of JSON both come through it. One per answer; disposing it
/// gives the buffer back and closes the body.
/// </summary>
internal sealed class WireLines(Stream body) : IDisposable
{
    // The most one line may be: a tool call's arguments are the longest thing a server puts on one
    private const int MOST_LINE = 16 * 1024 * 1024;

    private byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
    private int start, end, scanned;
    private bool finished;

    /// <summary>The line last read, without its line ending.</summary>
    public ReadOnlyMemory<byte> Current { get; private set; }

    /// <summary>A line that is already in the buffer, false when the next one needs more of the body.</summary>
    public bool TryNext()
    {
        int at = buffer.AsSpan(scanned, end - scanned).IndexOf((byte)'\n');
        if (at < 0)
        {
            scanned = end;
            return false;
        }

        int stop = scanned + at;
        Current = Trim(start, stop);
        start = scanned = stop + 1;
        return true;
    }

    /// <summary>Reads more of the body until a line is whole, the last one without an ending included; false at the end.</summary>
    public async ValueTask<bool> More(CancellationToken cancel)
    {
        while (true)
        {
            if (TryNext())
                return true;
            if (finished)
            {
                if (start >= end)
                    return false;
                Current = Trim(start, end);
                start = scanned = end;
                return true;
            }

            if (start > 0)
            {
                // The line begun is moved to the front; what was given out is no longer wanted
                Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                scanned -= start;
                end -= start;
                start = 0;
            }
            if (end == buffer.Length)
            {
                if (buffer.Length >= MOST_LINE)
                    throw new PredictionException(PredictionStatus.BadAnswer, "The server sent a line that was far too long.");
                byte[] larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                Buffer.BlockCopy(buffer, 0, larger, 0, end);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = larger;
            }

            int read = await body.ReadAsync(buffer.AsMemory(end), cancel).ConfigureAwait(false);
            if (read == 0)
                finished = true;
            else
                end += read;
        }
    }

    public void Dispose()
    {
        byte[] rented = buffer;
        buffer = [];
        start = end = scanned = 0;
        if (rented.Length > 0)
            ArrayPool<byte>.Shared.Return(rented);
        body.Dispose();
    }

    private ReadOnlyMemory<byte> Trim(int from, int to)
    {
        if (to > from && buffer[to - 1] == (byte)'\r')
            to--;
        return buffer.AsMemory(from, to - from);
    }

    /// <summary>
    /// The data of a server-sent events line, trimmed; false for a blank line, a comment or any other field, and for a
    /// data line with nothing in it.
    /// </summary>
    public static bool Data(ReadOnlyMemory<byte> line, out ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = line.Span;
        if (!span.StartsWith("data:"u8))
        {
            data = default;
            return false;
        }

        int from = 5, to = span.Length;
        while (from < to && span[from] is (byte)' ' or (byte)'\t')
            from++;
        while (to > from && span[to - 1] is (byte)' ' or (byte)'\t')
            to--;
        data = line[from..to];
        return to > from;
    }

    /// <summary>Whether data is the event that ends a server-sent events answer.</summary>
    public static bool IsDone(ReadOnlyMemory<byte> data) => data.Span.SequenceEqual("[DONE]"u8);
}

/// <summary>What the answers of the servers have in common, as the JSON of one piece of them says.</summary>
internal enum WireShape
{
    /// <summary>OpenAI's completions: choices[0].text.</summary>
    OpenAiText,

    /// <summary>OpenAI's chat: choices[0].delta.content and reasoning_content.</summary>
    OpenAiChat,

    /// <summary>Ollama's generate: response, done.</summary>
    OllamaGenerate,

    /// <summary>Ollama's chat: message.content and thinking, done.</summary>
    OllamaChat,
}

/// <summary>
/// One piece of a streamed answer, read straight from its UTF-8 JSON with no document built: the text, what the model
/// thought, whether the answer ends here, and the error the server put in it. Only the strings that are wanted are made.
/// </summary>
internal struct WireChunk
{
    /// <summary>The answer's text in this piece, null when it has none.</summary>
    public string? Text;

    /// <summary>What the model thought in this piece, null when nothing.</summary>
    public string? Thought;

    /// <summary>The answer ends with this piece: a finish reason, or done.</summary>
    public bool Finished;

    /// <summary>The server's own error message, null when there is none.</summary>
    public string? Error;

    /// <summary>Reads a piece, or throws a bad answer when it isn't JSON.</summary>
    public static WireChunk Read(ReadOnlySpan<byte> json, WireShape shape, Uri api)
    {
        var chunk = new WireChunk();
        try
        {
            var reader = new Utf8JsonReader(json);
            if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                chunk.Top(ref reader, json, shape);
            else
                reader.Skip();
            return chunk;
        }
        catch (JsonException)
        {
            throw new PredictionException(PredictionStatus.BadAnswer, $"{PredictionHttp.Host(api)} answered with something that isn't JSON.");
        }
    }

    private void Top(ref Utf8JsonReader reader, ReadOnlySpan<byte> json, WireShape shape)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("error"u8))
            {
                reader.Read();
                ReadError(ref reader, json);
            }
            else if (reader.ValueTextEquals("choices"u8) && shape is WireShape.OpenAiText or WireShape.OpenAiChat)
            {
                reader.Read();
                Choices(ref reader, shape);
            }
            else if (reader.ValueTextEquals("message"u8) && shape == WireShape.OllamaChat)
            {
                reader.Read();
                Fields(ref reader, "content"u8, "thinking"u8);
            }
            else if (reader.ValueTextEquals("response"u8) && shape == WireShape.OllamaGenerate)
            {
                reader.Read();
                Text = Str(ref reader);
            }
            else if (reader.ValueTextEquals("done"u8) && shape is WireShape.OllamaGenerate or WireShape.OllamaChat)
            {
                reader.Read();
                Finished = reader.TokenType == JsonTokenType.True;
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
    }

    private void Choices(ref Utf8JsonReader reader, WireShape shape)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
            return;
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("text"u8) && shape == WireShape.OpenAiText)
                {
                    reader.Read();
                    Text = Str(ref reader);
                }
                else if (reader.ValueTextEquals("delta"u8) && shape == WireShape.OpenAiChat)
                {
                    reader.Read();
                    Fields(ref reader, "content"u8, "reasoning_content"u8);
                }
                else if (reader.ValueTextEquals("finish_reason"u8))
                {
                    reader.Read();
                    Finished = reader.TokenType == JsonTokenType.String;
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }
        else
            reader.Skip();

        // Only the first choice is read
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            reader.Skip();
    }

    // The object whose content and thinking fields are the text and the thought
    private void Fields(ref Utf8JsonReader reader, ReadOnlySpan<byte> content, ReadOnlySpan<byte> thinking)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals(content))
            {
                reader.Read();
                Text = Str(ref reader);
            }
            else if (reader.ValueTextEquals(thinking))
            {
                reader.Read();
                Thought = Str(ref reader);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
    }

    // A string value as a string, anything else skipped as no text
    private static string? Str(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString();
        reader.Skip();
        return null;
    }

    // Ollama's {"error": "..."} or OpenAI's {"error": {"message": "..."}}, anything else as it was written, null as none
    private void ReadError(ref Utf8JsonReader reader, ReadOnlySpan<byte> json)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                Error = reader.GetString() ?? "";
                break;
            case JsonTokenType.Null:
                break;
            case JsonTokenType.StartObject:
                int from = (int)reader.TokenStartIndex;
                string? message = null;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    bool isMessage = reader.ValueTextEquals("message"u8);
                    reader.Read();
                    if (isMessage && reader.TokenType == JsonTokenType.String)
                        message = reader.GetString() ?? "";
                    else
                        reader.Skip();
                }
                Error = message ?? Encoding.UTF8.GetString(json[from..(int)reader.BytesConsumed]);
                break;
            default:
                int begin = (int)reader.TokenStartIndex;
                reader.Skip();
                Error = Encoding.UTF8.GetString(json[begin..(int)reader.BytesConsumed]);
                break;
        }
    }
}
