using Hex.Dawn;

using Horizon.UI.Scripting;

namespace Hex.Tests;

/// <summary>
/// The Length netcoredbg can't find on an array or a string worked out from what the array or the string comes to, and
/// the process id a test host waiting for its debugger says it has.
/// </summary>
public sealed class HexDebugEvaluationTests
{
    [Theory]
    [InlineData("string[]", "{string[0]}", 0)]
    [InlineData("int[]", "{int[42]}", 42)]
    [InlineData("int[,]", "{int[2, 3]}", 6)]
    [InlineData("int[][]", "{int[3][]}", 3)]
    [InlineData("string", "\"--checks\"", 8)]
    [InlineData("string", "\"a\\\"b\\\\c\\n\"", 6)]
    [InlineData("string", "\"\"", 0)]
    public void ArraysAndStringsSayHowLongTheyAre(string type, string value, long length) =>
        Assert.Equal(length, HexDebugExpressions.LengthIn(new DebugVariable("x", value, type, 0)));

    [Theory]
    [InlineData("string", "null")]
    [InlineData("string[]", "null")]
    [InlineData("Box", "{Box}")]
    [InlineData("int", "3")]
    public void NullsAndTheRestDont(string type, string value) =>
        Assert.Null(HexDebugExpressions.LengthIn(new DebugVariable("x", value, type, 0)));

    [Fact]
    public void TheTestHostSaysItsProcessId()
    {
        Assert.Equal(1234, HexDebugging.TestHostIn("Process Id: 1234, Name: testhost"));
        Assert.Equal(98, HexDebugging.TestHostIn("  Process Id:98, Name: testhost.x86"));
        Assert.Null(HexDebugging.TestHostIn("Host debugging is enabled. Please attach debugger to testhost process to continue."));
    }
}
