using Hex.Dawn;

namespace Hex.Tests;

public sealed class HexDebugBuiltInMembersTests
{
    [Theory]
    [InlineData("string", "System.String", "Substring")]
    [InlineData("int", "System.Int32", "CompareTo")]
    [InlineData("decimal", "System.Decimal", "CompareTo")]
    [InlineData("bool", "System.Boolean", "CompareTo")]
    [InlineData("char", "System.Char", "CompareTo")]
    [InlineData("int[]", "System.Array", "Length")]
    public void Alias_and_runtime_name_offer_the_same_instance_members(string alias, string runtime, string expected)
    {
        var members = HexDebugExpressions.BuiltInMembers(alias);
        Assert.NotNull(members);
        Assert.Contains(expected, members);
        Assert.Equal(members, HexDebugExpressions.BuiltInMembers(runtime));
        Assert.Equal(members.Count, members.Distinct().Count());
        Assert.DoesNotContain("Parse", members);
        Assert.DoesNotContain("CreateInstance", members);
    }

    [Fact]
    public void User_types_are_left_for_the_debugger_to_inspect()
    {
        Assert.Null(HexDebugExpressions.BuiltInMembers(null));
        Assert.Null(HexDebugExpressions.BuiltInMembers("Game.Player"));
        Assert.Null(HexDebugExpressions.BuiltInMembers("String"));
    }
}
