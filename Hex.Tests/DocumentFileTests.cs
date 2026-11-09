using System.Text;
using Hex.Core;

namespace Hex.Tests;

public sealed class DocumentFileTests : IDisposable
{
    [Theory]
    [InlineData("a\nb\nc\n", "a\r\nb\nc\r\n")]
    [InlineData("a\nchanged\nc\n", "a\r\nchanged\nc\r\n")]
    [InlineData("a\ninserted\nb\nc\n", "a\r\ninserted\r\nb\nc\r\n")]
    [InlineData("a\nc\n", "a\r\nc\r\n")]
    [InlineData("a\nb\nc", "a\r\nb\nc")]
    public void Mixed_endings_follow_preserved_and_edited_lines(string edited, string expected) =>
        Assert.Equal(expected, DocumentFile.WithLineEndings(edited, "a\r\nb\nc\r\n", true, true));

    [Fact]
    public void Explicit_line_ending_conversion_overrides_preservation() =>
        Assert.Equal("a\nb\nc\n", DocumentFile.WithLineEndings("a\nb\nc\n", "a\r\nb\nc\r\n", false, true));

    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-save-test-" + Guid.NewGuid().ToString("N"));
    public DocumentFileTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    [Theory]
    [InlineData(65001, false)]
    [InlineData(65001, true)]
    [InlineData(1200, true)]
    [InlineData(1201, true)]
    [InlineData(12000, true)]
    [InlineData(12001, true)]
    public void Edited_file_and_save_as_preserve_unicode_encoding(int codePage, bool bom)
    {
        Encoding encoding = codePage == 65001 ? new UTF8Encoding(bom, true) : Encoding.GetEncoding(codePage);
        string path = Path.Combine(folder, "original.txt"), copy = Path.Combine(folder, "copy.txt");
        File.WriteAllText(path, "before\r\n", encoding);
        Encoding detected = DocumentFile.EncodingOf(path);
        const string after = "after \u03B1 \U0001F680\r\n";
        DocumentFile.Write(path, after, detected);
        DocumentFile.Write(copy, after, detected);
        byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes(after)];
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Equal(expected, File.ReadAllBytes(copy));
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public void Failed_encoding_leaves_existing_bytes_and_removes_temporary_file()
    {
        string path = Path.Combine(folder, "original.txt");
        File.WriteAllText(path, "keep these bytes");
        byte[] before = File.ReadAllBytes(path);
        Assert.Throws<EncoderFallbackException>(() => DocumentFile.Write(path, "bad \uD800", new UTF8Encoding(false, true)));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public void Failed_replace_leaves_existing_bytes_and_removes_temporary_file()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows denies replacing a file opened without delete sharing.
        string path = Path.Combine(folder, "original.txt");
        File.WriteAllText(path, "keep these bytes");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => DocumentFile.Write(path, "replacement", new UTF8Encoding(false, true)));
        Assert.Equal("keep these bytes", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(folder));
    }
}
