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
/// differ only in line endings or a UTF-8 BOM, the hash of its text with CRLF folded into LF and the BOM ignored
/// (<see cref="Text"/>, null for a binary file or one not read), and each side's line endings.
/// </summary>
internal sealed record HexLinkSyncAgreed(string Local, string Remote, string? Text = null, HexLinkEol LocalEol = HexLinkEol.Unknown, HexLinkEol RemoteEol = HexLinkEol.Unknown, bool? Executable = null);

internal sealed partial class HexLinkSync
{
    // The text hash folds CRLF into LF and ignores a UTF-8 BOM. The original encoding still controls writes.
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
        string encoding = decoded.Encoding == "utf-8-bom" ? "utf-8" : decoded.Encoding;
        return new TextInfo(eol, HexLinkFiles.Hash(Encoding.UTF8.GetBytes(encoding + "\n" + norm)), text, decoded.Encoding);
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
    /// in line endings or a UTF-8 BOM.
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
        tries.Remove(path);
        // A later watch report is the current remote state. The agreement only records what this operation handled.
        if (remoteObservedWhileWorking.Contains(path))
            return;
        string? eol = EolWord(agreed.RemoteEol);
        bool knownText = agreed.Text is not null && eol is "lf" or "crlf" or "none";
        HexLinkTreeFile? previous = remote.GetValueOrDefault(path);
        remote[path] = new HexLinkTreeFile(path, agreed.Remote, previous?.Size ?? 0,
            knownText ? agreed.Text : null, knownText ? eol : null, agreed.Executable ?? previous?.Executable);
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
        string hash = HexLinkFiles.Hash(local);
        if (here.Norm is not null) HexLinkMerge.RememberBase(hash, local);
        return new HexLinkSyncAgreed(hash, HexLinkFiles.Hash(remote), here.Norm, here.Eol, there.Eol);
    }

    // Both sides have the same bytes, known by their hash alone (line ends not read)
    private static HexLinkSyncAgreed Identical(string hash) => new(hash, hash);
}
