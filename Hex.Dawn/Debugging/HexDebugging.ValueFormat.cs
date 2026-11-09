namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private bool hexadecimalLocals;

    private void ToggleHexadecimalLocals()
    {
        if (!DebugStopped || debugging?.Can("supportsValueFormattingOptions") != true) return;
        hexadecimalLocals = !hexadecimalLocals;
        variables.Reset();
        context.Panels.RefreshSide(HexSide.Debug);
    }
}
