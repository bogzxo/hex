using System.Text.Json;

using Hex.Core;

namespace Hex.Tests;

/// <summary>The pure parts behind the Local AI window's second round of fixes: long names, the preset search, router aliases and states.</summary>
public sealed class LocalAiWindowFixesTests
{
    [Theory]
    [InlineData("Qwen3.5 9B", 20, "Qwen3.5 9B")]
    [InlineData("Qwen3.5-Coder-30B-A3B-Instruct", 12, "Qwen3.5-Cod…")]
    [InlineData("abcdef", 1, "…")]
    [InlineData("abcdef", 0, "")]
    public void A_long_name_is_cut_with_an_ellipsis(string text, int most, string expected)
    {
        string cut = LocalAiWindowState.Ellipsize(text, most);
        Assert.Equal(expected, cut);
        Assert.True(cut.Length <= Math.Max(most, 0));
    }

    [Fact]
    public void A_search_finds_the_preset_fields_by_their_help()
    {
        LocalAiRowDef[] rows = [new("temperature", LocalAiPage.Presets), new("glossary_temperature", LocalAiPage.Glossary), new("context", LocalAiPage.Presets)];
        (string, string, string) Texts(string key) => key switch
        {
            "temperature" => ("Creativity (temperature)", "Lower is more predictable.", "Temperature controls how willing the model is."),
            "glossary_temperature" => ("Temperature", "Randomness.", "..."),
            _ => ("How much it can read", "Tokens.", "A larger context."),
        };
        IReadOnlyList<LocalAiRowDef> found = LocalAiWindowState.Filter("temperature", rows, Texts);
        Assert.Equal(["temperature", "glossary_temperature"], found.Select(row => row.Key));
        // From the Overview the search goes to the Presets page, which comes before the Glossary
        Assert.Equal(LocalAiPage.Presets, LocalAiWindowState.JumpTo(found, LocalAiPage.Overview));
    }

    [Fact]
    public void A_router_alias_is_shown_by_its_display_name()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["local"] = "Qwen3.5 9B" };
        Assert.Equal("Qwen3.5 9B", LocalAiWindowState.DisplayNameOf("local", names));
        Assert.Equal("Qwen3.5 9B", LocalAiWindowState.DisplayNameOf("LOCAL", names));
        Assert.Equal("other", LocalAiWindowState.DisplayNameOf("other", names));
    }

    [Fact]
    public void Every_router_model_is_listed_with_its_state_and_file()
    {
        const string answer = """
            {"data":[
              {"id":"local","status":{"value":"loaded","args":["llama-server","--model","D:\\models\\Qwen3.5-9B-Q4.gguf","--port","1"]}},
              {"id":"chat","status":{"value":"unloaded","args":["-m","/m/other.gguf"]}},
              {"id":"plain"}
            ]}
            """;
        using JsonDocument json = JsonDocument.Parse(answer);
        List<LlamaCppRouterModel> models = LlamaCppServer.RouterModels(json.RootElement);
        Assert.Equal([new("local", "loaded", "Qwen3.5-9B-Q4.gguf"), new("chat", "unloaded", "other.gguf"), new("plain", "loaded", null)], models);
        Assert.Equal(["local", "plain"], LlamaCppServer.Loaded(json.RootElement));
    }
}
