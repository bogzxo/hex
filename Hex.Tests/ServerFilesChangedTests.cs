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
}
