namespace Hex.Dawn;

// What the editor does every tick is a list with names (HexTicks). The order some of it has to be in is checked here,
// so a step moved to where it reads better and breaks something is caught by more than somebody noticing.
internal sealed partial class DawnScene
{
    private void TestTicks(HexPointerScript test)
    {
        string[] names = [.. TickNames];
        int At(string name) => Array.IndexOf(names, name);

        test.Check("ticks: every step has a name of its own", () => names.Length > 30 && names.Distinct(StringComparer.Ordinal).Count() == names.Length);
        test.Check("ticks: the parts are among them by the names they give", () =>
            At("work.languages") >= 0 && At("work.git") >= 0 && At("work.tasks") >= 0 && At("work.properties") >= 0 && At("late.debugger") >= 0);

        // What finished in the background is taken in before anything that shows it. A folder's changes are read before
        // the languages look for where they are. The languages have what their servers said before Git and the tasks go
        test.Check("ticks: finished work first, the folders before the languages, the languages before Git and the tasks", () =>
            At("work.finished") == 0 && At("work.folders") < At("work.languages")
            && At("work.languages") < At("work.git") && At("work.git") < At("work.tasks"));

        // The panel is fitted last of its stretch, after whatever goes in it had its turn
        test.Check("ticks: the debugger before its bar, and the panel after what's in it", () =>
            At("late.debugger") < At("late.debug_bar") && At("panel.terminal") < At("panel.panel") && At("panel.panel") == names.Length - 1);
    }
}
