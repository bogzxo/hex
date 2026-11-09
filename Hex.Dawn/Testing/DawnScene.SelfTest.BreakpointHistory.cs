using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestBreakpointHistory(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "breakpoint-history.cs");
        const string source = "int first = 1;\nint answer = 42;\nSystem.Console.WriteLine(answer);\n";
        var settings = new HexBreakpoint(Condition: "first == 1", Enabled: false);
        void Reset()
        {
            code.Text = source;
            if (debugger.breakpoints.TryGetValue(file, out var marks)) marks.Clear();
            debugger.Settle(file, 1, settings);
        }
        bool At(int line) => code.Breakpoints is { Count: 1 } marks && marks.Contains(line) && debugger.SettingsOf(file, line) == settings;
        test.Run(() => { File.WriteAllText(file, source); Open(file); Reset(); });
        test.Check("breakpoint history: conditional disabled breakpoint is on the original statement", () => At(1));
        test.Run(() => { code.Document.MoveTo(new UITextPosition(1, 0)); code.Document.Insert("// before answer\n"); });
        test.Check("breakpoint history: pasted code carries the breakpoint and its settings", () => At(2));
        test.Run(() => code.Document.Undo());
        test.Check("breakpoint history: undo paste restores the original line", () => code.Text == source && At(1));
        test.Run(() => code.Document.Redo());
        test.Check("breakpoint history: redo paste follows the original statement", () => At(2));
        test.Run(() => { Reset(); code.Document.MoveTo(new UITextPosition(0, 0)); code.Document.Insert("\n"); });
        test.Check("breakpoint history: inserting a line above shifts the breakpoint", () => At(2));
        test.Run(() => code.Document.Undo());
        test.Check("breakpoint history: undo line insertion shifts the breakpoint back", () => At(1));
        test.Run(() => code.Document.Redo());
        test.Check("breakpoint history: redo line insertion reapplies its shift", () => At(2));
        test.Run(() =>
        {
            Reset();
            code.Document.Select(new UITextPosition(0, 0), new UITextPosition(1, 0));
            code.Document.Delete();
        });
        test.Check("breakpoint history: deleting the preceding line retains the breakpoint", () => At(0));
        test.Run(() => code.Document.Undo());
        test.Check("breakpoint history: undo deletion restores the breakpoint", () => code.Text == source && At(1));
        test.Run(() => { Reset(); code.Document.MoveTo(new UITextPosition(0, 0)); code.Document.CopyLines(1); });
        test.Check("breakpoint history: copying a preceding line shifts its breakpoint", () => At(2));
        test.Run(() => code.Document.Undo());
        test.Check("breakpoint history: undo copied lines restores the breakpoint", () => At(1));
        test.Run(() =>
        {
            Reset();
            code.Document.MoveTo(new UITextPosition(1, 0));
            code.Document.Insert("// one\n// two\n");
            code.Document.Undo();
            code.Document.Insert("// replacement\n");
        });
        test.Check("breakpoint history: editing after undo starts a fresh line-movement history", () => At(2));
        test.Run(() => code.Document.Redo());
        test.Check("breakpoint history: abandoned redo cannot shift breakpoints", () => At(2));
        var neighbor = new HexBreakpoint(LogMessage: "trace first");
        var third = new HexBreakpoint(Condition: "answer == 42");
        bool Pair(bool swapped) => code.Breakpoints is { Count: 2 }
            && debugger.SettingsOf(file, swapped ? 0 : 1) == settings
            && debugger.SettingsOf(file, swapped ? 1 : 0) == neighbor;
        test.Run(() =>
        {
            Reset();
            debugger.Settle(file, 0, neighbor);
            code.Document.MoveTo(new UITextPosition(1, 0));
            code.Document.MoveLines(-1);
        });
        test.Check("breakpoint reorder: swapping two marked lines swaps their distinct settings", () => Pair(true));
        test.Run(() => code.Document.Undo());
        test.Check("breakpoint reorder: undo restores both marked statements", () => Pair(false) && code.Text == source);
        test.Run(() => code.Document.Redo());
        test.Check("breakpoint reorder: redo swaps both settings again", () => Pair(true));
        bool Block(bool moved) => code.Breakpoints is { Count: 3 }
            && debugger.SettingsOf(file, moved ? 1 : 0) == neighbor
            && debugger.SettingsOf(file, moved ? 2 : 1) == settings
            && debugger.SettingsOf(file, moved ? 0 : 2) == third;
        test.Run(() =>
        {
            Reset();
            debugger.Settle(file, 0, neighbor);
            debugger.Settle(file, 2, third);
            code.Document.Select(new UITextPosition(0, 0), new UITextPosition(2, 0));
            code.Document.MoveLines(1);
        });
        test.Check("breakpoint reorder: moving a selected block also relocates its neighbor", () => Block(true) && code.Document.LineAt(0).StartsWith("System.Console"));
        test.Run(() => code.Document.Undo());
        test.Check("breakpoint reorder: undo a block move restores all three breakpoint settings", () => Block(false) && code.Text == source);
        test.Run(() => { code.Document.Redo(); code.Document.MoveLines(-1); });
        test.Check("breakpoint reorder: moving the block back up preserves every setting", () => Block(false) && code.Text == source);
        test.Run(() =>
        {
            code.Document.Select(new UITextPosition(0, 0), new UITextPosition(2, 0));
            code.Document.Delete();
        });
        test.Check("breakpoint recovery: deleting marked lines displays one collapsed breakpoint", () => code.Breakpoints is { Count: 1 } collapsed && collapsed.Contains(0));
        test.Run(() => code.Document.Undo());
        test.Check("breakpoint recovery: undo restores all collapsed conditions and logpoints", () => Block(false) && code.Text == source);
        test.Run(() => code.Document.Redo());
        var changed = neighbor with { Enabled = false, LogMessage = "changed while collapsed" };
        test.Run(() => { debugger.Settle(file, 0, changed); code.Document.Undo(); });
        test.Check("breakpoint recovery: manual settings survive undo while other identities return", () => code.Breakpoints is { Count: 3 }
            && debugger.SettingsOf(file, 0) == changed && debugger.SettingsOf(file, 1) == settings && debugger.SettingsOf(file, 2) == third);
        test.Run(() => { code.Document.Redo(); debugger.ToggleBreakpoint(file, 0); code.Document.Undo(); });
        test.Check("breakpoint recovery: removing the collapsed marker prevents resurrection on undo", () => code.Breakpoints is { Count: 0 });
        test.Run(() => { debugger.breakpoints.Clear(); CloseNow(); });
    }
}
