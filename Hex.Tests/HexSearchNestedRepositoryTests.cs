using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// A repository cloned into the workspace's own (the engine and Hex inside the game's) is walked whatever the outer
/// .gitignore says of it, the way git and ripgrep take it, and its own .gitignore still counts inside it.
/// </summary>
public sealed class HexSearchNestedRepositoryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("hex-nested").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private void Write(string relative, string text = "x")
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void A_repository_inside_is_walked_though_the_outer_gitignore_leaves_it_out()
    {
        Write(".gitignore", "engine/\nloose/\n");
        Write("top.cs");
        Directory.CreateDirectory(Path.Combine(root, "engine", ".git"));
        Write("engine/.gitignore", "bin/\n");
        Write("engine/Code.cs");
        Write("engine/bin/Built.cs");
        Write("loose/Gone.cs");

        var found = HexSearch.FilesIn(root, "*.cs", excludes: HexExcludes.Usual)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).ToList();

        Assert.Contains("top.cs", found);
        Assert.Contains("engine/Code.cs", found);
        Assert.DoesNotContain("engine/bin/Built.cs", found);
        Assert.DoesNotContain("loose/Gone.cs", found);
    }
}
