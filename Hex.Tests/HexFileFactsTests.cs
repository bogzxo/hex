using System.Text;

using Hex.Core;

namespace Hex.Tests;

/// <summary>What the properties panel says of a file off its bytes, its encoding, line endings, indentation and a picture's size.</summary>
public sealed class HexFileFactsTests
{
    [Fact]
    public void TextSaysItsEncodingLineEndingsAndIndentation()
    {
        HexTextFacts plain = HexFileFacts.Text(Encoding.ASCII.GetBytes("class A\n{\n    int x;\n    void B()\n    {\n        x++;\n    }\n}\n"));
        Assert.Equal(new HexTextFacts("UTF-8 (ASCII)", "LF", "4 spaces", 9), plain);

        byte[] bom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("a\r\n\tb\r\n\tcé\r\n")];
        Assert.Equal(new HexTextFacts("UTF-8 with BOM", "CRLF", "Tabs", 4), HexFileFacts.Text(bom));

        Assert.Equal("Mixed, mostly LF", HexFileFacts.Text(Encoding.ASCII.GetBytes("a\nb\nc\r\n")).LineEndings);
        Assert.Equal("2 spaces", HexFileFacts.Text(Encoding.ASCII.GetBytes("a:\n  b:\n    c: 1\n  d: 2\n")).Indentation);
        Assert.Equal("Windows-1252", HexFileFacts.Text([0x61, 0xE9, 0x62]).Encoding);
        Assert.Equal("UTF-16 LE", HexFileFacts.Text([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("hi")]).Encoding);
    }

    [Fact]
    public void APicturesSizeIsReadFromItsHeader()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 2, 128, 0, 0, 1, 224];
        Assert.Equal(new HexImageFacts(640, 480, "PNG"), HexFileFacts.Image(png));

        byte[] gif = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 32, 0, 16, 0];
        Assert.Equal(new HexImageFacts(32, 16, "GIF"), HexFileFacts.Image(gif));

        // A JPEG's frame after an APP0 marker
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 11, 8, 0, 100, 0, 200, 3, 0, 0, 0];
        Assert.Equal(new HexImageFacts(200, 100, "JPEG"), HexFileFacts.Image(jpeg));

        Assert.Null(HexFileFacts.Image("not a picture"u8));
    }

    [Fact]
    public void AFileWithANulInItIsntText()
    {
        string folder = Directory.CreateTempSubdirectory("hex-facts-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "a.txt"), "plain\n");
            File.WriteAllBytes(Path.Combine(folder, "a.bin"), [0x4D, 0x5A, 0x00, 0x01]);
            File.WriteAllBytes(Path.Combine(folder, "w.txt"), [0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0]);
            Assert.True(HexFileFacts.LooksLikeText(Path.Combine(folder, "a.txt")));
            Assert.False(HexFileFacts.LooksLikeText(Path.Combine(folder, "a.bin")));
            Assert.True(HexFileFacts.LooksLikeText(Path.Combine(folder, "w.txt")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void SizesAreSaidTheWayPeopleSayThem()
    {
        Assert.Equal("1 byte", HexFileFacts.Size(1));
        Assert.Equal("812 bytes", HexFileFacts.Size(812));
        Assert.Equal("14.2 KB", HexFileFacts.Size(14540));
        Assert.Equal("3 MB (3,145,728 bytes)", HexFileFacts.SizeExactly(3 * 1024 * 1024));
    }
}
