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

    protected override bool UsesScrollSpeed => true;
}
