using System.Text.Json.Nodes;
using Hex.Core;
using Hex.Dawn;
using Horizon.UI.Scripting;

namespace Hex.Tests;

public class PythonServerSettingsTests
{
    [Fact]
    public void Local_environment_preserves_other_settings_and_explicit_interpreter()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-python-settings-" + Guid.NewGuid());
        string python = PythonEnvironment.ExecutableIn(Path.Combine(root, ".venv"));
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        try
        {
            File.WriteAllText(python, "fixture");
            var settings = JsonNode.Parse("{\"python\":{\"analysis\":{\"typeCheckingMode\":\"strict\"}}}")!.AsObject();
            var entry = new LanguageServers.Registration("python", "pyright-langserver", ["--stdio"], "python", [".py"], settings);
            var selected = HexLanguages.PythonServerSettings(entry, Path.Combine(root, "sample.py"), root)!;
            Assert.Equal(python, selected["python"]!["pythonPath"]!.GetValue<string>());
            Assert.Equal("strict", selected["python"]!["analysis"]!["typeCheckingMode"]!.GetValue<string>());
            Assert.Null(settings["python"]!["pythonPath"]);
            settings["python"]!["pythonPath"] = "explicit-python";
            Assert.Null(HexLanguages.PythonServerSettings(entry, Path.Combine(root, "sample.py"), root));
            Assert.Null(HexLanguages.PythonServerSettings(entry with { Command = "pylsp" }, Path.Combine(root, "sample.py"), root));
        }
        finally { Directory.Delete(root, true); }
    }
}
