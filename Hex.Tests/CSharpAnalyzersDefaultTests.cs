using System.Text.Json.Nodes;

using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

/// <summary>C#'s analyzers off by default now, a true kept from when that was the default moved once, a choice made since kept.</summary>
public sealed class CSharpAnalyzersDefaultTests
{
    [Fact]
    public void TheCatalogLeavesCSharpsAnalyzersOff()
    {
        string file = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Hex.Dawn", "Assets", "hex", "language_servers.hor"));
        var csharp = HexServerCatalog.Read(File.ReadAllText(file))
            .Single(entry => entry.Name == "csharp");
        Assert.False(csharp.Settings!["csharp"]!["analyzersEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void AKeptTrueIsMovedToTheNewDefaultOnceAndAChoiceAfterStays()
    {
        var settings = new HexSettings(null);
        settings.ServerSettings["csharp"] = (JsonObject)JsonNode.Parse("""{"csharp":{"analyzersEnabled":true}}""")!;
        settings.ServerSettings["html"] = (JsonObject)JsonNode.Parse("""{"html":{"format":{"wrapLineLength":100}}}""")!;

        Assert.True(HexLanguages.MoveOldAnalyzersDefault(settings));
        Assert.False(settings.ServerSettings.ContainsKey("csharp"));
        Assert.True(settings.ServerSettings.ContainsKey("html"));
        Assert.True(settings.CSharpAnalyzersMoved);

        // Switched on afterwards, it stays on
        settings.ServerSettings["csharp"] = (JsonObject)JsonNode.Parse("""{"csharp":{"analyzersEnabled":true}}""")!;
        Assert.False(HexLanguages.MoveOldAnalyzersDefault(settings));
        Assert.True(settings.ServerSettings["csharp"]["csharp"]!["analyzersEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void AFalseTheUserChoseAndTheRestOfTheSectionStay()
    {
        var settings = new HexSettings(null);
        settings.ServerSettings["csharp"] = (JsonObject)JsonNode.Parse("""{"csharp":{"analyzersEnabled":false,"solution":"Hex.sln"}}""")!;
        Assert.True(HexLanguages.MoveOldAnalyzersDefault(settings));
        Assert.False(settings.ServerSettings["csharp"]["csharp"]!["analyzersEnabled"]!.GetValue<bool>());

        settings = new HexSettings(null);
        settings.ServerSettings["csharp"] = (JsonObject)JsonNode.Parse("""{"csharp":{"analyzersEnabled":true,"solution":"Hex.sln"}}""")!;
        HexLanguages.MoveOldAnalyzersDefault(settings);
        Assert.Equal("""{"csharp":{"solution":"Hex.sln"}}""", settings.ServerSettings["csharp"].ToJsonString());
    }
}
