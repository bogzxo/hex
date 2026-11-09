using System.Globalization;
using System.Numerics;
using System.Text;

namespace Hex.Dawn;

internal static class HexMemoryText
{
    public static string? Address(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            int end = 2;
            while (end < text.Length && Uri.IsHexDigit(text[end])) end++;
            if (end > 2 && (end == text.Length || char.IsWhiteSpace(text[end]))) return text[..end];
        }
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? text : null;
    }

    public static string Rows(DebugMemory block, int requested)
    {
        int length = Math.Min(requested, block.Data.Length + (int)Math.Min(block.UnreadableBytes, requested));
        string raw = block.Address;
        bool hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        bool numeric = BigInteger.TryParse(hex ? "0" + raw[2..] : raw, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer, CultureInfo.InvariantCulture, out var address) && address >= 0;
        var text = new StringBuilder();
        for (int start = 0; start < length; start += 16)
        {
            string value = (address + start).ToString("x", CultureInfo.InvariantCulture).TrimStart('0');
            text.Append(numeric ? "0x" + (value.Length > 0 ? value : "0").PadLeft(hex ? Math.Max(8, raw.Length - 2) : 8, '0') : raw + "+" + start.ToString(CultureInfo.InvariantCulture));
            text.Append("  ");
            for (int index = start; index < start + 16; index++)
                text.Append(index >= length ? "  " : index >= block.Data.Length ? "??" : block.Data[index].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
            text.Append(" | ");
            for (int index = start; index < Math.Min(start + 16, length); index++)
                text.Append(index >= block.Data.Length ? '?' : block.Data[index] is >= 32 and <= 126 ? (char)block.Data[index] : '.');
            text.Append('\n');
        }
        return text.ToString();
    }
}
