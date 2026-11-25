using System.Text.Json;

using Hex.Core;

namespace Hex.Tests;

public sealed class LlamaCppRouterAliasTests
{
    private const string ROUTER = """
        {"data":[
          {"id":"local","status":{"value":"loaded","args":["llama-server","--model","C:\\models\\zeta-2.1.gguf","-c","8192"]}},
          {"id":"mellum2.1","status":{"value":"unloaded","args":["llama-server","-m","/m/mellum.gguf"]}},
          {"id":"plain"}
        ]}
        """;

    [Fact]
    public void A_router_that_calls_the_same_file_by_another_name_is_used_under_its_name()
    {
        using JsonDocument json = JsonDocument.Parse(ROUTER);
        Assert.Equal("local", LlamaCppServer.AliasOfFile(json.RootElement, "D:/other/folder/ZETA-2.1.gguf"));
        Assert.Equal("mellum2.1", LlamaCppServer.AliasOfFile(json.RootElement, "/x/mellum.gguf"));
    }

    [Fact]
    public void A_router_without_the_file_gives_no_alias()
    {
        using JsonDocument json = JsonDocument.Parse(ROUTER);
        Assert.Null(LlamaCppServer.AliasOfFile(json.RootElement, "/x/qwen.gguf"));
        using JsonDocument empty = JsonDocument.Parse("{}");
        Assert.Null(LlamaCppServer.AliasOfFile(empty.RootElement, "/x/qwen.gguf"));
    }
}
