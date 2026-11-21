using System.Text;

namespace Hex.Core;

/// <summary>The line ends a file uses, as the sync tells one side's convention from the other's.</summary>
internal enum HexLinkEol
{
    /// <summary>Not known, the file wasn't read (a base kept from before line ends were kept, or agreed by hash alone).</summary>
    Unknown,

    /// <summary>Text with no line end in it at all.</summary>
    None,

    /// <summary>Text whose every line ends in LF.</summary>
    Lf,

    /// <summary>Text whose every line ends in CR LF.</summary>
    Crlf,

    /// <summary>Text with more than one kind of line end, or a CR alone, which is sent exactly as it is.</summary>
    Mixed,

    /// <summary>Not text, never converted.</summary>
    Binary
}

/// <summary>
/// What a file both sides agreed on was on each side, kept per path: the hash of the bytes this side has
/// (<see cref="Local"/>) and of those the other side has (<see cref="Remote"/>), which are the same unless the two
/// differ only in line ends (a Windows checkout with core.autocrlf against a Mac's), the hash of its text with every
/// CR LF made LF (<see cref="Text"/>, null for a binary file or one not read), and each side's line ends.
/// </summary>
internal sealed record HexLinkSyncAgreed(string Local, string Remote, string? Text = null, HexLinkEol LocalEol = HexLinkEol.Unknown, HexLinkEol RemoteEol = HexLinkEol.Unknown);

internal sealed partial class HexLinkSync
{
    // A file's bytes as text when they are: its line ends, the hash its text has with CR LF made LF (the encoding in it,
    // so the same words in UTF-8 and UTF-16 are still two files), and the text and encoding to convert it with
    private readonly record struct TextInfo(HexLinkEol Eol, string? Norm, string? Text, string? Encoding);

    private static TextInfo TextOf(byte[] bytes)
    {
        if (HexLinkFiles.Decode(bytes) is not { } decoded)
            return new TextInfo(HexLinkEol.Binary, null, null, null);
        int crlf = 0, lf = 0, cr = 0;
        string text = decoded.Text;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        HexLinkEol eol = (crlf, lf, cr) switch
        {
            (0, 0, 0) => HexLinkEol.None,
            ( > 0, 0, 0) => HexLinkEol.Crlf,
            (0, > 0, 0) => HexLinkEol.Lf,
            _ => HexLinkEol.Mixed
        };
        string norm = crlf > 0 ? text.Replace("\r\n", "\n", StringComparison.Ordinal) : text;
        return new TextInfo(eol, HexLinkFiles.Hash(Encoding.UTF8.GetBytes(decoded.Encoding + "\n" + norm)), text, decoded.Encoding);
    }

    /// <summary>
    /// What a tree's manifest says of a file beside its hash, so two sides can tell files that only differ in line ends
    /// without reading them: the hash of its text with CR LF made LF and its line ends ("lf", "crlf" or "none"). Both null
    /// for a binary file or text with more than one kind of line end.
    /// </summary>
    public static (string? Text, string? Eol) TextAndEol(byte[] bytes)
    {
        TextInfo info = TextOf(bytes);
        return info.Eol switch
        {
            HexLinkEol.Lf => (info.Norm, "lf"),
            HexLinkEol.Crlf => (info.Norm, "crlf"),
            HexLinkEol.None => (info.Norm, "none"),
            _ => (null, null)
        };
    }

    /// <summary>
    /// Whether two files are the same as far as the sync's concerned: the same bytes, or both text that only differs
    /// in CR LF against LF.
    /// </summary>
    public static bool SameText(byte[] one, byte[] other) =>
        one.AsSpan().SequenceEqual(other) || (TextOf(one) is { Norm: { } a } && TextOf(other) is { Norm: { } b } && a == b);

    // A text file with one kind of line end made the receiver's, when the receiver's is known and the other kind; a
    // binary file, a mixed one or one with no line ends goes exactly as it is
    private static byte[] Converted(byte[] bytes, TextInfo info, HexLinkEol target)
    {
        if (info.Text is null || info.Encoding is null || info.Eol is not (HexLinkEol.Lf or HexLinkEol.Crlf)
            || target is not (HexLinkEol.Lf or HexLinkEol.Crlf) || target == info.Eol)
            return bytes;
        string made = target == HexLinkEol.Crlf ? info.Text.Replace("\n", "\r\n", StringComparison.Ordinal) : info.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return HexLinkFiles.Encode(made, info.Encoding) ?? bytes;
    }

    // How many agreed text files use each kind of line end on each side, for a new file to take the receiver's
    private int localLf, localCrlf, remoteLf, remoteCrlf;

    // A base set (under the gate), the counts of line ends kept with it
    private void SetBase(string path, HexLinkSyncAgreed agreed)
    {
        RemoveBase(path);
        bases[path] = agreed;
        Count(agreed, 1);
        // What the other side has, with its text and line ends when they're known (its text is this side's, they agreed)
        string? eol = agreed.Text is null ? null : EolWord(agreed.RemoteEol) is "lf" or "crlf" or "none" ? EolWord(agreed.RemoteEol) : null;
        remote[path] = new HexLinkTreeFile(path, agreed.Remote, 0, eol is null ? null : agreed.Text, eol);
        tries.Remove(path);
    }

    private void RemoveBase(string path)
    {
        if (bases.Remove(path, out HexLinkSyncAgreed? was))
            Count(was, -1);
    }

    private void Count(HexLinkSyncAgreed agreed, int by)
    {
        if (agreed.LocalEol == HexLinkEol.Lf)
            localLf += by;
        else if (agreed.LocalEol == HexLinkEol.Crlf)
            localCrlf += by;
        if (agreed.RemoteEol == HexLinkEol.Lf)
            remoteLf += by;
        else if (agreed.RemoteEol == HexLinkEol.Crlf)
            remoteCrlf += by;
    }

    // The line ends most agreed text files of the folder have on one side, unknown when it's even or nothing's known
    private HexLinkEol Most(bool here)
    {
        lock (gate)
        {
            (int lf, int crlf) = here ? (localLf, localCrlf) : (remoteLf, remoteCrlf);
            return lf > crlf ? HexLinkEol.Lf : crlf > lf ? HexLinkEol.Crlf : HexLinkEol.Unknown;
        }
    }

    // The base both sides come to when each has bytes of its own, read: the hash of each and their line ends
    private static HexLinkSyncAgreed AgreedOf(byte[] local, byte[] remote)
    {
        TextInfo here = TextOf(local), there = TextOf(remote);
        return new HexLinkSyncAgreed(HexLinkFiles.Hash(local), HexLinkFiles.Hash(remote), here.Norm, here.Eol, there.Eol);
    }

    // Both sides have the same bytes, known by their hash alone (line ends not read)
    private static HexLinkSyncAgreed Identical(string hash) => new(hash, hash);
}
