using Hex.Dawn;
using Horizon.UI.Scripting;

namespace Hex.Tests;

/// <summary>What changed on the disk that a language server is told of, the build output and hidden folders left out.</summary>
public sealed class ServerFilesChangedTests
{
    [Fact]
    public void CSharpIsNotToldOfBuildOutputHiddenFoldersOrHexsDataFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-server-files");
        using LanguageServer csharp = LanguageServer.Over("csharp", "csharp", root, Stream.Null, new MemoryStream());
        using LanguageServer python = LanguageServer.Over("python", "python", root, Stream.Null, new MemoryStream());

        string source = Path.Combine(root, "hex", "Hex.Dawn", "Languages", "HexLanguages.cs");
        string scratch = Path.Combine(root, "hex", "Hex", "bin", "Debug", "net11.0", "data", "agents", "temp", "scratch", "probe.cs");
        string generated = Path.Combine(root, "hex", "Hex.Dawn", "obj", "Debug", "net11.0", "Hex.Dawn.AssemblyInfo.cs");
        string copy = Path.Combine(root, ".hexbase", "hex", "Hex.Dawn", "Hex.Dawn.csproj");
        string git = Path.Combine(root, "hex", ".git", "index");
        string script = Path.Combine(root, "tools", "bin", "run.py");
        string engine = Path.Combine(Path.GetDirectoryName(root)!, "horizon", "Horizon.UI", "UIComponent.cs");
        (string, LanguageServerFileChange)[] changes =
        [
            (source, LanguageServerFileChange.Changed), (scratch, LanguageServerFileChange.Created), (generated, LanguageServerFileChange.Changed),
            (copy, LanguageServerFileChange.Created), (git, LanguageServerFileChange.Changed), (script, LanguageServerFileChange.Changed),
            (engine, LanguageServerFileChange.Changed)
        ];

        // Beside its folder too, a solution of Hex's has the engine's projects in it
        Assert.Equal([source, engine], HexLanguages.ServerFiles(csharp, changes).Select(change => change.Path));
        // A bin folder is .NET's build output alone, a Python one may have scripts in it
        Assert.Equal([source, scratch, generated, script, engine], HexLanguages.ServerFiles(python, changes).Select(change => change.Path));
    }

    [Fact]
    public void CSharpIsNotToldOfAnotherRepositorysWorktree()
    {
        // The workspace's folder with the clones in it and the worktrees agents make beside them, hexwt-* a git worktree
        string root = Path.Combine(Path.GetTempPath(), "hex-server-worktrees-" + Guid.NewGuid().ToString("N"));
        string hex = Path.Combine(root, "hex"), engine = Path.Combine(root, "horizon"), worktree = Path.Combine(root, "hexwt-lsp"), nested = Path.Combine(hex, "hexwt-inside");
        try
        {
            Directory.CreateDirectory(Path.Combine(hex, ".git", "worktrees", "hexwt-lsp"));
            Directory.CreateDirectory(Path.Combine(engine, ".git"));
            Directory.CreateDirectory(Path.Combine(worktree, "Hex"));
            Directory.CreateDirectory(Path.Combine(nested, "Hex"));
            File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine(hex, ".git", "worktrees", "hexwt-lsp")}" + "\n");
            File.WriteAllText(Path.Combine(nested, ".git"), $"gitdir: {Path.Combine(hex, ".git", "worktrees", "hexwt-inside")}" + "\n");

            string own = Path.Combine(hex, "Hex", "Hex.csproj"), beside = Path.Combine(engine, "Horizon.UI", "Horizon.UI.csproj");
            string copy = Path.Combine(worktree, "Hex", "Hex.csproj"), copySource = Path.Combine(worktree, "Hex", "Program.cs"), inside = Path.Combine(nested, "Hex", "Hex.csproj");
            (string, LanguageServerFileChange)[] changes =
            [
                (own, LanguageServerFileChange.Changed), (beside, LanguageServerFileChange.Changed), (copy, LanguageServerFileChange.Created),
                (copySource, LanguageServerFileChange.Changed), (inside, LanguageServerFileChange.Created)
            ];

            // A server on the workspace's folder hears of the clones in it and not of the worktrees
            using LanguageServer workspace = LanguageServer.Over("csharp", "csharp", root, Stream.Null, new MemoryStream());
            Assert.Equal([own, beside], HexLanguages.ServerFiles(workspace, changes).Select(change => change.Path));
            Assert.True(HexLanguages.ReloadsServer(workspace, own));
            Assert.False(HexLanguages.ReloadsServer(workspace, copy));

            // One on Hex's own clone hears of the engine beside it, a worktree inside it is still another's
            using LanguageServer clone = LanguageServer.Over("csharp", "csharp", hex, Stream.Null, new MemoryStream());
            Assert.Equal([own, beside], HexLanguages.ServerFiles(clone, changes).Select(change => change.Path));
            Assert.False(HexLanguages.ReloadsServer(clone, inside));

            // And one started in the worktree, on a solution of its own, hears of its own projects
            using LanguageServer agents = LanguageServer.Over("csharp", "csharp", worktree, Stream.Null, new MemoryStream());
            Assert.Contains(copy, HexLanguages.ServerFiles(agents, changes).Select(change => change.Path));
            Assert.True(HexLanguages.ReloadsServer(agents, copy));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
