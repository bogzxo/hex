using System.Text.Json.Nodes;
using Hex.Core;
using Hex.Dawn;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed class HexLinkSymbolTests
{
    [Fact]
    public async Task Go_to_symbol_disappears_when_View_is_revoked_and_the_host_denies_cached_calls()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        await pair.PeerA.ListTools();
        Assert.True(DawnScene.LinkHasRemoteSymbols(pair.PeerA));

        pair.BOnA = HexLinkRights.None;
        pair.LinkA.RightsMoved(DeviceB.Id);
        Assert.True(Eventually(() => pair.PeerA.Granted == HexLinkRights.None));
        Assert.False(DawnScene.LinkHasRemoteSymbols(pair.PeerA));

        var denied = await Assert.ThrowsAsync<HexLinkException>(() =>
            pair.PeerA.Call("hex_symbols", new JsonObject { ["query"] = "Navigation" }));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, denied.Code);
        Assert.Equal("view", (denied.Detail as JsonObject)?["needs"]?.GetValue<string>());
        Assert.Empty(pair.A.LinkTools.Called);
        Assert.Contains(pair.A.Audited, entry =>
            entry.What == "refused" && entry.Who.Id == DeviceB.Id && entry.Detail == "tool hex_symbols, it needs view");
    }

    [Fact]
    public void Structured_locations_disambiguate_identical_index_root_labels_and_declaration_names()
    {
        using var host = new LinkServices(DeviceA);
        string first = Directory.CreateDirectory(Path.Combine(host.Root, "first", "src")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(host.Root, "second", "src")).FullName;
        File.WriteAllText(Path.Combine(first, "Navigation.cs"), "// first\npublic class Navigation { }\n");
        File.WriteAllText(Path.Combine(second, "Navigation.cs"), "// second\n\npublic class Navigation { }\n");
        var index = new HexCodeIndex(() => [first, second]) { StaleAfter = 0 };
        HexLinkFolder[] folders = [new("src", first), new("src-2", second)];

        HexAnswer answer = DawnScene.LinkSymbolLocations(index.Current(), folders, new JsonObject { ["query"] = "Navigation" });

        Assert.False(answer.Failed);
        var rows = Assert.IsType<JsonArray>(JsonNode.Parse(answer.Text));
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row?["wire"]?.GetValue<string>() == "src/Navigation.cs"
            && row["line"]?.GetValue<int>() == 1);
        Assert.Contains(rows, row => row?["wire"]?.GetValue<string>() == "src-2/Navigation.cs"
            && row["line"]?.GetValue<int>() == 2);
    }

    [Fact]
    public void A_synced_symbol_uses_the_bound_root_even_if_current_labels_have_been_reassigned()
    {
        using var host = new LinkServices(DeviceA);
        string original = Directory.CreateDirectory(Path.Combine(host.Root, "first", "src")).FullName;
        string other = Directory.CreateDirectory(Path.Combine(host.Root, "second", "src")).FullName;
        string originalFile = Path.Combine(original, "Navigation.cs");
        File.WriteAllText(originalFile, "public class Original { }");
        File.WriteAllText(Path.Combine(other, "Navigation.cs"), "public class Other { }");
        HexLinkFolder syncBinding = new("src", original);
        HexLinkFolder[] current = [new("src", other), new("src-2", original)];

        string? resolved = DawnScene.LinkSyncedLocalFile(syncBinding, current, "src/Navigation.cs");
        Assert.NotNull(resolved);
        Assert.Equal("public class Original { }", File.ReadAllText(resolved!));
        Assert.Null(DawnScene.LinkSyncedLocalFile(syncBinding, [current[0]], "src/Navigation.cs"));
        Assert.Null(DawnScene.LinkSyncedLocalFile(syncBinding, current, "src-2/Navigation.cs"));
    }
}
