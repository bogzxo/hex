using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// The catalog of extensions Hex installs from (HexExtensionCatalog). A sync out of a folder laid out like the repository,
/// an install written over an older one, versions put in order, and the real catalog, every folder of it an extension
/// that says what it is.
/// </summary>
public class ExtensionCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"hex-catalog-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task A_sync_keeps_the_extensions_and_nothing_else()
    {
        Write("source/one/extension.hor", "let extension = { name: \"One\", version: \"1.0.0\" };");
        Write("source/one/view.hor", "#define layout");
        Write("source/README.md", "# not an extension");
        Write("source/notes/todo.txt", "a folder without an extension.hor");

        string synced = Path.Combine(root, "synced");
        await HexExtensionCatalog.Sync(Path.Combine(root, "source"), synced);

        Assert.Equal(["one"], HexExtensionCatalog.Extensions(synced).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(synced, "one", "view.hor")));
        Assert.False(File.Exists(Path.Combine(synced, "README.md")));
        Assert.False(Directory.Exists(synced + ".new"));
    }

    [Fact]
    public async Task A_sync_that_fails_leaves_the_last_one_as_it_was()
    {
        string synced = Path.Combine(root, "synced");
        Write("synced/kept/extension.hor", "let extension = { name: \"Kept\" };");

        await Assert.ThrowsAnyAsync<IOException>(() => HexExtensionCatalog.Sync("not a folder and not owner/name", synced));
        Assert.Equal(["kept"], HexExtensionCatalog.Extensions(synced).Select(Path.GetFileName));
    }

    [Fact]
    public void An_install_over_an_older_one_takes_out_what_it_doesnt_have_any_more()
    {
        Write("new/extension.hor", "let extension = { version: \"2.0.0\" };");
        Write("new/parts/added.hor", "let added = 1;");
        Write("installed/extension.hor", "let extension = { version: \"1.0.0\" };");
        Write("installed/gone.hor", "let gone = 1;");

        HexExtensionCatalog.Install(Path.Combine(root, "new"), Path.Combine(root, "installed"));

        Assert.Contains("2.0.0", File.ReadAllText(Path.Combine(root, "installed", "extension.hor")));
        Assert.True(File.Exists(Path.Combine(root, "installed", "parts", "added.hor")));
        Assert.False(File.Exists(Path.Combine(root, "installed", "gone.hor")));
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.10.0", "1.9.2", true)]
    [InlineData("v2", "1.9", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.2.0-beta", "1.1.0", true)]
    public void Versions_go_in_order(string version, string than, bool newer) =>
        Assert.Equal(newer, HexExtensionCatalog.IsNewer(version, than));

    [Theory]
    [InlineData("bogzxo/hex-extensions", true)]
    [InlineData("someone/their.extensions", true)]
    [InlineData("C:/extensions", false)]
    [InlineData("just-a-name", false)]
    public void A_source_is_a_repository_or_a_folder(string source, bool repo) =>
        Assert.Equal(repo, HexExtensionCatalog.IsRepo(source));

    [Fact]
    public void Every_extension_of_the_real_catalog_says_what_it_is()
    {
        string[] found = [.. HexExtensionCatalog.Extensions(Catalog.Folder)];
        Assert.NotEmpty(found);
        foreach (string folder in found)
        {
            string text = File.ReadAllText(Path.Combine(folder, HexExtensionCatalog.FILE));
            Assert.True(text.Contains("let extension", StringComparison.Ordinal) && text.Contains("version:", StringComparison.Ordinal),
                $"{Path.GetFileName(folder)} has to say what it is and its version, Hex decides updates by it");
        }
    }
}
