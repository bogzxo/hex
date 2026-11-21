using System.Text;

namespace Hex.Core;

/// <summary>Tokens estimated the way Zed does, three bytes of UTF-8 each. Nothing in Hex tokenises.</summary>
internal static class PredictionTokens
{
    public const int BYTES = 3;

    /// <summary>The tokens a text is taken to be.</summary>
    public static int Of(string text) => Encoding.UTF8.GetByteCount(text) / BYTES;

    /// <summary>The tokens so many bytes are taken to be.</summary>
    public static int Of(int bytes) => bytes / BYTES;

    /// <summary>The bytes of a text in UTF-8.</summary>
    public static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>The bytes of part of a text in UTF-8.</summary>
    public static int Bytes(string text, int start, int length) => Encoding.UTF8.GetByteCount(text.AsSpan(start, length));
}
