using Hex.Dawn;
using Horizon.UI.Components;

namespace Hex.Tests;

public sealed class HexBreakpointTrackingTests
{
    private sealed class Fixture
    {
        internal readonly UITextDocument Document = new() { Text = "zero\none\ntwo\nthree\nfour\nfive" };
        internal readonly HexBreakpointTracking Tracking = new();
        internal Dictionary<int, HexBreakpoint> Visible = Enumerable.Range(1, 4)
            .ToDictionary(line => line, line => new HexBreakpoint(Condition: "value == " + line, Enabled: line % 2 == 0));
        internal Fixture() => Document.LineChanged += change => Visible = Tracking.Apply(Visible, change);
        internal void DeleteBlock()
        {
            Document.Select(new(1, 0), new(4, 0));
            Document.Delete();
        }
    }

    [Fact]
    public void Undo_restores_every_collapsed_breakpoint_and_its_distinct_settings()
    {
        var fixture = new Fixture();
        var expected = fixture.Visible.ToArray();
        fixture.DeleteBlock();
        Assert.Equal(1, Assert.Single(fixture.Visible).Key);
        fixture.Document.Undo();
        Assert.Equal(expected, fixture.Visible.OrderBy(pair => pair.Key).ToArray());
        fixture.Document.Redo();
        Assert.Equal(1, Assert.Single(fixture.Visible).Key);
        fixture.Document.Undo();
        Assert.Equal(expected, fixture.Visible.OrderBy(pair => pair.Key).ToArray());
    }

    [Fact]
    public void Removing_a_collapsed_breakpoint_prevents_undo_from_resurrecting_it()
    {
        var fixture = new Fixture();
        fixture.DeleteBlock();
        fixture.Visible.Clear();
        fixture.Tracking.Synchronize(fixture.Visible);
        fixture.Document.Undo();
        Assert.Empty(fixture.Visible);
        fixture.Document.Redo();
        Assert.Empty(fixture.Visible);
    }

    [Fact]
    public void Manual_setting_changes_survive_text_undo_without_overwriting_hidden_breakpoints()
    {
        var fixture = new Fixture();
        var hidden = fixture.Visible[2];
        fixture.DeleteBlock();
        var changed = new HexBreakpoint(LogMessage: "changed while collapsed");
        fixture.Visible[1] = changed;
        fixture.Tracking.Synchronize(fixture.Visible);
        fixture.Document.Undo();
        Assert.Equal(changed, fixture.Visible[1]);
        Assert.Equal(hidden, fixture.Visible[2]);
        Assert.Equal(4, fixture.Visible.Count);
    }

    [Fact]
    public void A_breakpoint_added_after_deletion_follows_undo_and_redo_without_inheriting_old_identity()
    {
        var fixture = new Fixture();
        fixture.DeleteBlock();
        fixture.Visible.Clear();
        fixture.Tracking.Synchronize(fixture.Visible);
        var added = new HexBreakpoint(LogMessage: "new breakpoint");
        fixture.Visible[2] = added;
        fixture.Document.Undo();
        Assert.Equal(new KeyValuePair<int, HexBreakpoint>(5, added), Assert.Single(fixture.Visible));
        fixture.Document.Redo();
        Assert.Equal(new KeyValuePair<int, HexBreakpoint>(2, added), Assert.Single(fixture.Visible));
    }
}
