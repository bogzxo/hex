using System.Text.Json.Nodes;

using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// A bridge outliving the Hex it talks to. The client's handshake said again to the next Hex under the bridge's own id,
/// whose answer the client never sees, the calls cut off failed so they're made again, and what the client said in
/// between handed on after the handshake.
/// </summary>
public sealed class McpBridgeSessionTests
{
    private const string INITIALIZE = """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"clientInfo":{"name":"claude-code"}}}""";
    private const string INITIALIZED = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    [Fact]
    public void A_call_cut_off_is_failed_and_one_answered_is_not()
    {
        var session = new McpBridgeSession();
        session.FromClient(INITIALIZE, connected: true);
        Assert.NotNull(session.FromHex("""{"jsonrpc":"2.0","id":0,"result":{}}"""));
        session.FromClient(INITIALIZED, connected: true);
        session.FromClient("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"hex_dev_restart"}}""", connected: true);
        session.FromClient("""{"jsonrpc":"2.0","id":"x","method":"tools/call","params":{"name":"hex_read"}}""", connected: true);
        session.FromHex("""{"jsonrpc":"2.0","id":"x","result":{}}""");

        List<string> failed = session.Dropped();
        JsonObject error = Assert.Single(failed.Select(line => JsonNode.Parse(line)!.AsObject()));
        Assert.Equal(5, error["id"]!.GetValue<int>());
        Assert.Contains("make the call again", error["error"]!["message"]!.GetValue<string>());
        Assert.Empty(session.Dropped());
    }

    [Fact]
    public void An_answer_lets_go_of_its_call_however_its_id_is_written_and_a_line_that_isnt_json_of_none()
    {
        var session = new McpBridgeSession();

        // The same id escaped one way by the client and another by Hex, and a method that isn't a text, which is no call
        session.FromClient("""{"jsonrpc":"2.0","id":"a+b","method":"tools/call","params":{"name":"hex_read","arguments":{"path":"x"}}}""", connected: true);
        session.FromClient("""{"jsonrpc":"2.0","id":9,"method":{"not":"a name"}}""", connected: true);
        session.FromClient("""{"jsonrpc":"2.0","id":3,"method":"tools/list"} trailing""", connected: true);
        Assert.Equal("""{"id":"a+b","jsonrpc":"2.0","result":{"content":[{"type":"text","text":"x"}]}}""",
            session.FromHex("""{"id":"a+b","jsonrpc":"2.0","result":{"content":[{"type":"text","text":"x"}]}}"""));

        // Not JSON at all, passed on as it is and letting go of nothing
        session.FromClient("""{"jsonrpc":"2.0","id":4,"method":"tools/list"}""", connected: true);
        Assert.Equal("""{"id":4,"result":""", session.FromHex("""{"id":4,"result":"""));
        JsonObject error = Assert.Single(session.Dropped().Select(line => JsonNode.Parse(line)!.AsObject()));
        Assert.Equal(4, error["id"]!.GetValue<int>());
    }

    [Fact]
    public void The_next_hex_is_shaken_hands_with_first_and_its_answer_kept_from_the_client()
    {
        var session = new McpBridgeSession();
        session.FromClient(INITIALIZE, connected: true);
        session.FromClient(INITIALIZED, connected: true);
        session.Dropped();

        // Said while there was no Hex, held for the next, and not failed with the calls cut off
        session.FromClient("""{"jsonrpc":"2.0","id":7,"method":"tools/list"}""", connected: false);
        Assert.Empty(session.Dropped());

        List<string> replay = session.Replay();
        Assert.Equal(3, replay.Count);
        Assert.Equal(McpBridgeSession.REPLAY_ID, JsonNode.Parse(replay[0])!["id"]!.GetValue<string>());
        Assert.Equal("initialize", JsonNode.Parse(replay[0])!["method"]!.GetValue<string>());
        Assert.Equal(INITIALIZED, replay[1]);
        Assert.Contains("tools/list", replay[2]);
        Assert.Empty(session.Held());

        Assert.Null(session.FromHex("""{"jsonrpc":"2.0","id":"hex-bridge-again","result":{}}"""));
        string? answer = """{"jsonrpc":"2.0","id":7,"result":{"tools":[]}}""";
        Assert.Equal(answer, session.FromHex(answer));
        Assert.Contains("list_changed", McpBridgeSession.ToolsChanged());
    }
}
