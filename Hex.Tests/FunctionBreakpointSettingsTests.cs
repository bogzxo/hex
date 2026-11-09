using System.Text.Json.Nodes;

using Hex.Dawn;

namespace Hex.Tests;

public class FunctionBreakpointSettingsTests
{
    [Fact]
    public void FunctionSettingsRoundTripAlongsideLegacyLineBreakpoints()
    {
        var kept = JsonNode.Parse("""[{"file":"Program.cs","line":9},{"function":" old.name ","enabled":false},{"function":""},{"function":12}]""")!.AsArray();
        var read = HexDebugging.ReadFunctionBreakpoints(kept);
        Assert.False(Assert.Single(read).Value);
        Assert.Contains("old.name", read.Keys);
        read["Program.Target(int)"] = true;
        read["program.target"] = false;
        var written = new JsonArray(kept[0]!.DeepClone());
        HexDebugging.WriteFunctionBreakpoints(written, read);
        var restored = HexDebugging.ReadFunctionBreakpoints(JsonNode.Parse(written.ToJsonString())!.AsArray());
        Assert.Equal(read, restored);
        Assert.Equal(9, written[0]!["line"]!.GetValue<int>());
        Assert.True(restored["Program.Target(int)"]);
        Assert.False(restored["program.target"]);
        Assert.Empty(HexDebugging.ReadFunctionBreakpoints(new JsonArray(new JsonObject { ["file"] = "legacy.cs", ["line"] = 1 })));
    }
}
