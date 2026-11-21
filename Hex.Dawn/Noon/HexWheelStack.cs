namespace Hex.Dawn;

/// <summary>
/// A stack that passes a turn of the wheel over itself on to a scroll it holds, for margins round a column that should
/// scroll the column as though the pointer were over it (Agentic's middle, its chat centred to a reading width on a wide
/// window). Without it the wheel did nothing anywhere but over the column.
/// </summary>
internal sealed class HexWheelStack : StackPanel
{
    /// <summary>The scroll the wheel goes to, asked each time since it can be moved elsewhere.</summary>
    public Func<ScrollPanel?>? Wheeled { get; init; }

    protected override bool OnScroll(float delta) => Wheeled?.Invoke() is { Visible: true } scroll && scroll.Wheel(delta);

    // A drag or the middle button's autoscroll over the margins moves the column with it to the fraction of a pixel, as
    // over the column. Left to the stack's own, it went by whole notches of the wheel and jumped at slow speeds
    protected override bool OnPan(System.Numerics.Vector2 delta) => Wheeled?.Invoke() is { Visible: true } scroll && scroll.Pan(delta);

    protected override bool UsesScrollSpeed => true;
}
