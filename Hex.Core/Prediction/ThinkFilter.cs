namespace Hex.Core;

/// <summary>
/// What a thinking model streams with its thinking taken out: everything between &lt;think&gt; and &lt;/think&gt; is
/// dropped, the tags too, even split across pieces. A model whose server keeps its thinking apart (Ollama's thinking
/// field) never writes the tags and goes through as it is. Text that could be the start of a tag is held until the next
/// piece says whether it is one; what is held at the end is given by <see cref="End"/>. One per answer. It is
/// <see cref="ThinkSplitter"/> with the thinking pieces left out, so the two always split alike.
/// </summary>
internal sealed class ThinkFilter
{
    private readonly ThinkSplitter splitter = new();
    private readonly List<ChatPiece> pieces = new(2);

    /// <summary>Whether any thinking has been dropped, for the tests and the status.</summary>
    public bool Thought => splitter.Thought;

    /// <summary>Whether the answer is in the middle of thinking now.</summary>
    public bool Thinking => splitter.Thinking;

    /// <summary>What of a piece is the answer, empty while the model thinks or while a tag may be starting.</summary>
    public string Feed(string piece)
    {
        pieces.Clear();
        splitter.Feed(piece, pieces);
        return Answer();
    }

    /// <summary>What was held at the end of the answer: text that turned out not to be a tag, nothing while thinking.</summary>
    public string End()
    {
        pieces.Clear();
        splitter.End(pieces);
        return Answer();
    }

    // The answer pieces joined, the one there usually is as it is
    private string Answer()
    {
        string? one = null;
        System.Text.StringBuilder? joined = null;
        foreach (ChatPiece piece in pieces)
        {
            if (piece.Thinking)
                continue;
            if (one is null)
                one = piece.Text;
            else
                (joined ??= new System.Text.StringBuilder(one)).Append(piece.Text);
        }
        return joined?.ToString() ?? one ?? string.Empty;
    }
}
