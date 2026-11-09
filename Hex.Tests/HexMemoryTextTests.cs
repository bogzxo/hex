using Hex.Dawn;
using Horizon.UI.Scripting;

namespace Hex.Tests;

public class HexMemoryTextTests
{
    [Fact]
    public void BytesRemainHexadecimalAndPrintableAsciiWithUnreadableBytesDistinct()
    {
        string text = HexMemoryText.Rows(new DebugMemory("0x1000", [72, 101, 120, 0, 255], 3), 8);
        Assert.Contains("0x00001000  48 65 78 00 FF ?? ?? ??", text);
        Assert.Contains("| Hex..???", text);
        Assert.DoesNotContain("00 00", text);
    }

    [Fact]
    public void AddressRowsHandleDecimalAndUnsignedHighAddresses()
    {
        Assert.StartsWith("0x00001000", HexMemoryText.Rows(new DebugMemory("4096", [1], 0), 1));
        string text = HexMemoryText.Rows(new DebugMemory("0xFFFFFFFFFFFFFFFF", new byte[17], 0), 17);
        Assert.StartsWith("0xffffffffffffffff", text);
        Assert.Contains("0x1000000000000000f", text);
    }

    [Fact]
    public void ShortReadsNeverPadMissingMemoryWithZeroes()
    {
        string text = HexMemoryText.Rows(new DebugMemory("0x1000", [1, 2], 0), 256);
        Assert.Contains("01 02", text);
        Assert.DoesNotContain("00", text[(text.IndexOf("  ", StringComparison.Ordinal) + 2)..]);
        Assert.Equal(string.Empty, HexMemoryText.Rows(new DebugMemory("0x0", [], 0), 256));
    }

    [Theory]
    [InlineData("0x00000123 \"Hex\"", "0x00000123")]
    [InlineData("0XFFFF", "0XFFFF")]
    [InlineData("4096", "4096")]
    [InlineData("0x1+2", null)]
    [InlineData("bytes", null)]
    public void NativePointerResultsYieldOnlyTheirAddress(string value, string? expected) => Assert.Equal(expected, HexMemoryText.Address(value));
}
