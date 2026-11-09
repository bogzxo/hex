using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestMemoryView(HexPointerScript test, string language)
    {
        bool cpp = language == "cpp", python = language == "python";
        string root = Path.Combine(testFolder, "memory " + language), file = Path.Combine(root, cpp ? "main.cpp" : python ? "main.py" : "Program.cs");
        string notes = Path.Combine(root, "notes.txt");
        string source = cpp ? "#include <cstdio>\nint main() {\n unsigned char bytes[600] = {0x48,0x65,0x78,0,0xff};\n bytes[256] = 0x5a;\n volatile int marker = 1;\n bytes[0] = 0x42;\n std::printf(\"memory %d\\n\", bytes[0]);\n marker += 1;\n std::printf(\"done %d\\n\", marker);\n}\n"
            : python ? "marker = 1\nprint(marker)\nmarker += 1\nprint(marker)\n" : "int marker = 1;\nSystem.Console.WriteLine(marker);\nmarker += 1;\nSystem.Console.WriteLine(marker);\n";
        bool Has(string key) => PaletteChoices().Any(choice => choice.Text == Said(key));
        void Command(string key) => PaletteChoices().FirstOrDefault(choice => choice.Text == Said(key))?.Picked?.Invoke();
        UITreeItem? Value() => sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.variables"))?.Children?
            .FirstOrDefault(row => row.Text.StartsWith(cpp ? "bytes = " : "marker = ", StringComparison.Ordinal) && row.Tag is SideTag { DebugCurrent: {} current } && current());
        void Menu() { if (Value() is {} row) sideTree.OnItemContextMenu?.Invoke(row, System.Numerics.Vector2.Zero); }
        Action? MemoryAction()
        {
            Menu();
            var action = contextMenu?.Showing?.Items.FirstOrDefault(item => item.Label == Said("debugging.memory_view"))?.OnPicked;
            contextMenu?.Close();
            return action;
        }
        void Ask(string input)
        {
            Command("debugging.memory");
            if (textDialog is { IsOpen: true } dialog && Descendants(dialog.Body).OfType<TextBox>().FirstOrDefault() is {} box)
            {
                box.Text = input;
                box.OnSubmitted?.Invoke(input);
            }
        }
        bool InMemory() => HexDebugging.IsMemoryPath(document.Path) && code.ReadOnly && Has("debugging.memory_refresh");
        bool FirstBytes(string bytes) => InMemory() && code.Document.LineCount > 2 && code.Document.LineAt(2).Contains("  " + bytes, StringComparison.Ordinal);
        string cache = string.Empty, previous = string.Empty;
        Action? stale = null;
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            File.WriteAllText(notes, "Native memory notes\n");
            explorer.OpenFolder(root);
            Open(file);
            debugger.breakpoints.Clear();
            debugger.ToggleBreakpoint(file, cpp ? 5 : 1);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped && Value() is not null, 60);
        test.Check("Memory: " + language + " debugger pauses with a native variable", () => debugger.DebugStopped && Value() is not null);
        test.Check("Memory: " + language + " command follows adapter capability", () => Has("debugging.memory") == cpp);
        test.Run(() => stale = MemoryAction());
        test.Check("Memory: " + language + " variable action follows adapter capability", () => (stale is not null) == cpp);
        if (cpp)
        {
            test.Check("Memory: native locals retain their memory reference", () => Value()?.Tag is SideTag { DebugValue: { MemoryReference: not null } });
            test.Run(() => { stale?.Invoke(); Open(notes); });
            test.Settle(0.5f);
            test.Check("Memory: changing documents retires a pending view", () => document.Path == notes);
            test.Run(() => { Open(file); MemoryAction()?.Invoke(); });
            test.WaitFor(() => FirstBytes("48 65 78 00 FF"), 15);
            test.Check("Memory: native array storage opens as hexadecimal and ASCII", () => FirstBytes("48 65 78 00 FF") && code.Text.Contains("| Hex..", StringComparison.Ordinal));
            test.Run(() =>
            {
                cache = document.Path!;
                previous = code.Text;
                typeof(CodeEditor).GetMethod("OnTextInput", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(code, ['x']);
                SaveFormatted();
            });
            test.Check("Memory: snapshots cannot edit source, save changes, or create source breakpoints", () => code.Text == previous && !document.Modified && File.ReadAllText(cache) == previous
                && File.ReadAllText(file) == source && code.OnGutterPressed is null && debugger.breakpoints.Keys.All(path => !HexDebugging.IsMemoryPath(path)) && !Has("debugging.toggle_breakpoint"));
            test.Run(() => Command("debugging.memory_next"));
            test.WaitFor(() => FirstBytes("5A 00"), 10);
            test.Check("Memory: next page reads the native byte offset", () => FirstBytes("5A 00"));
            test.Run(() => Command("debugging.memory_previous"));
            test.WaitFor(() => FirstBytes("48 65 78 00 FF"), 10);
            test.Check("Memory: previous page returns to the same address", () => FirstBytes("48 65 78 00 FF"));
            test.Run(() => Command("debugging.memory_refresh"));
            test.WaitFor(() => FirstBytes("48 65 78 00 FF"), 10);
            test.Check("Memory: refresh preserves the selected bytes and source", () => FirstBytes("48 65 78 00 FF") && File.ReadAllText(file) == source);
            test.Run(debugger.StepOver);
            test.WaitFor(() => debugger.DebugStopped && FirstBytes("42 65 78 00 FF"), 15);
            test.Check("Memory: source stepping refreshes changed bytes in the same view", () => FirstBytes("42 65 78 00 FF") && document.Path == cache
                && documents.Count(open => open.Path == cache) == 1 && code.ExecutionLine < 0);
            test.Run(() => Command("debugging.show_current_frame"));
            test.Check("Memory: Show Current Frame returns to source", () => document.Path == file && code.ExecutionLine == 6 && !code.ReadOnly);
            test.Run(() => Ask("&bytes[0] + 256"));
            test.WaitFor(() => FirstBytes("5A 00"), 10);
            test.Check("Memory: address expressions use the native pointer evaluator", () => FirstBytes("5A 00"));
            string address = string.Empty;
            test.Run(() => { address = code.Document.LineAt(2).Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]; Ask(address); });
            test.WaitFor(() => InMemory() && code.Document.LineAt(0).StartsWith(address, StringComparison.Ordinal), 10);
            test.Check("Memory: raw native addresses read the same bytes", () => FirstBytes("5A 00") && code.Document.LineAt(0).StartsWith(address, StringComparison.Ordinal));
            test.Run(() => Ask("0x0"));
            test.WaitFor(() => FirstBytes("?? ??"), 10);
            test.Check("Memory: unreadable regions display unknown bytes rather than zeroes", () => FirstBytes("?? ??") && code.Text.Contains(Said("debugging.memory_unreadable", ("count", "256")), StringComparison.Ordinal));
            test.Run(() => { previous = code.Text; Ask("hex_no_such_memory_expression"); });
            test.WaitFor(() => InMemory() && said.Any(entry => entry.Text.StartsWith(Said("debugging.memory_failed", ("why", string.Empty)).Trim(), StringComparison.Ordinal)), 10);
            test.Check("Memory: evaluator errors preserve the last snapshot", () => InMemory() && code.Text == previous);
            test.Run(() =>
            {
                CloseNow();
                Open(cache);
            });
            test.Check("Memory: reopening the cache stays read-only", () => document.Path == cache && code.ReadOnly && !document.Modified);
            test.Run(() =>
            {
                Command("debugging.show_current_frame");
                stale = MemoryAction();
                Command("debugging.memory");
                debugger.StepOver();
            });
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 7, 10);
            test.Run(() =>
            {
                if (textDialog is { IsOpen: true } dialog) Descendants(dialog.Body).OfType<TextBox>().FirstOrDefault()?.OnSubmitted?.Invoke("bytes");
                stale?.Invoke();
            });
            test.Settle(0.5f);
            test.Check("Memory: old prompts and variable menus cannot read a later stop", () => document.Path == file && code.ExecutionLine == 7);
            string otherCache = Path.Combine(HexDebugging.MemoryFolder, Guid.NewGuid().ToString("N"), "Older.txt");
            test.Run(() => Ask("bytes"));
            test.WaitFor(() => FirstBytes("42 65 78 00 FF"), 10);
            test.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(otherCache)!);
                File.WriteAllText(otherCache, "An older memory snapshot\n");
                Open(otherCache);
                debugger.StepOver();
            });
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 8, 10);
            test.Check("Memory: an unrelated cached snapshot cannot capture active-session stepping", () => document.Path == file && code.ExecutionLine == 8
                && File.ReadAllText(otherCache) == "An older memory snapshot\n" && documents.FirstOrDefault(open => open.Path == otherCache)?.Editor.ReadOnly == true);
            test.Run(() => { Ask("bytes"); debugger.StopDebugging(); Open(notes); });
            test.WaitFor(() => !debugger.Debugging, 10);
            test.Settle(0.5f);
            test.Check("Memory: session end retires reads without reopening a view", () => document.Path == notes && !Has("debugging.memory") && !Has("debugging.memory_refresh")
                && documents.Where(open => HexDebugging.IsMemoryPath(open.Path)).All(open => open.Editor.ReadOnly && open.Editor.ExecutionLine < 0));
        }
        test.Check("Memory: " + language + " source remains unchanged", () => File.ReadAllText(file) == source);
        test.Run(() => { if (debugger.Debugging) debugger.StopDebugging(); debugger.breakpoints.Clear(); });
        test.WaitFor(() => !debugger.Debugging, 10);
    }
}
