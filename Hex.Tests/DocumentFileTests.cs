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

    [Theory]
    [InlineData("a\r\nb\nc\r\nd\n", "a\nb\nnew\nc\nd\n", "a\r\nb\nnew\nc\r\nd\n")]
    [InlineData("a\r\nb\nc\r\nd\n", "a\nb\nc\nd\nlast\n", "a\r\nb\nc\r\nd\nlast\n")]
    [InlineData("a\r\nb\nc\r\nd\n", "first\na\nb\nc\nd\n", "first\r\na\r\nb\nc\r\nd\n")]
    [InlineData("a\r\nb\r\nc\r\n", "a\nX\nY\nc\n", "a\r\nX\r\nY\r\nc\r\n")]
    [InlineData("a\nb\nc\n", "a\nX\nY\nc\n", "a\nX\nY\nc\n")]
    [InlineData("a\r\nb\nc\n", "a\nb\nx\ny\nc\n", "a\r\nb\nx\ny\nc\n")]
    public void Inserted_lines_take_a_neighbours_ending_and_uniform_files_stay_uniform(string original, string edited, string expected) =>
        Assert.Equal(expected, DocumentFile.WithLineEndings(edited, original, original.Contains("\r\n"), original.Contains("\r\n")));

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

    private const int CannotRemoveReplacedFile = unchecked((int)0x80070497);
    private const int SharingViolation = unchecked((int)0x80070020);

    [Fact]
    public void A_transient_windows_replacement_refusal_retries_the_same_staged_bytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = Path.Combine(folder, "original.txt");
        var encoding = new UTF8Encoding(true, true);
        File.WriteAllText(path, "before", encoding);
        DateTime creationTime = new(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, creationTime);
        int attempts = 0, checks = 0;
        string? staged = null;
        bool saved = DocumentFile.Write(path, "my draft", encoding, () =>
        {
            checks++;
            return File.ReadAllText(path) == "before";
        }, (source, target, backup) =>
        {
            if (++attempts == 1)
            {
                staged = source;
                throw new IOException("Simulated Windows replacement refusal.", CannotRemoveReplacedFile);
            }

            Assert.Equal(staged, source);
            File.Replace(source, target, backup);
        });

        Assert.True(saved);
        Assert.Equal(2, attempts);
        Assert.Equal(1, checks);
        byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes("my draft")];
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Equal(creationTime, File.GetCreationTimeUtc(path));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Theory]
    [InlineData(CannotRemoveReplacedFile, 2, 1)]
    [InlineData(SharingViolation, 1, 0)]
    public void A_persistent_windows_replacement_error_reaches_the_caller_and_leaves_the_original(int error, int tries, int rechecks)
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = Path.Combine(folder, "original.txt");
        File.WriteAllText(path, "before");
        int attempts = 0, checks = 0;
        const string draft = "my unsaved draft";

        IOException failure = Assert.Throws<IOException>(() =>
            DocumentFile.Write(path, draft, new UTF8Encoding(false), () =>
            {
                checks++;
                return File.ReadAllText(path) == "before";
            }, (source, _, _) =>
            {
                attempts++;
                Assert.Equal(draft, File.ReadAllText(source));
                throw new IOException("Simulated Windows replacement refusal.", error);
            }));

        Assert.Equal(error, failure.HResult);
        Assert.Equal(tries, attempts);
        Assert.Equal(rechecks, checks);
        Assert.Equal("before", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public void A_changed_destination_vetoes_the_retry()
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = Path.Combine(folder, "original.txt");
        File.WriteAllText(path, "before");
        int attempts = 0, checks = 0;
        bool saved = DocumentFile.Write(path, "my draft", new UTF8Encoding(false), () =>
        {
            checks++;
            return File.ReadAllText(path) == "before";
        }, (_, target, _) =>
        {
            Assert.Equal(1, ++attempts);
            File.WriteAllText(target, "externally changed");
            throw new IOException("Simulated Windows replacement refusal.", CannotRemoveReplacedFile);
        });

        Assert.False(saved);
        Assert.Equal(1, attempts);
        Assert.Equal(1, checks);
        Assert.Equal("externally changed", File.ReadAllText(path));
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
