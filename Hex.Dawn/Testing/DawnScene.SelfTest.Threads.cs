namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDebugThreads(HexPointerScript test) => TestDebugThreads(test, "csharp");

    private void TestDebugThreads(HexPointerScript test, string language)
    {
        string root = Path.Combine(testFolder, "debug-threads-" + language), file = Path.Combine(root, language == "python" ? "main.py" : language == "cpp" ? "main.cpp" : "Program.cs");
        int line = language == "python" ? 14 : language == "cpp" ? 15 : 16;
        int main = 0, worker = 0;
        UITreeItem? previousFrame = null;
        UITextPosition caretAfterStep = default;
        UITreeItem? Threads() => sideTree.Items.FirstOrDefault(section => section.Text == "Threads");
        bool Has(string value) => sideTree.Items.Any(section => section.Text == "Variables" && section.Children?.Any(row => row.Text == value) == true);
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, language == "python" ? """
                import threading, time
                ready = threading.Event()
                release = threading.Event()
                def Worker():
                    workerValue = 7
                    ready.set()
                    while not release.is_set():
                        time.sleep(0.01)
                    print(workerValue)
                threading.current_thread().name = 'Main fixture'
                worker = threading.Thread(target=Worker, name='Worker fixture')
                worker.start()
                ready.wait()
                mainValue = 42
                print(mainValue)
                release.set()
                worker.join()
                """ : language == "cpp" ? """
                #include <thread>
                #include <atomic>
                #include <cstdio>
                #include <chrono>
                std::atomic<bool> ready(false), release(false);
                void Worker() {
                  int workerValue = 7;
                  ready = true;
                  while (!release.load()) std::this_thread::sleep_for(std::chrono::milliseconds(10));
                  std::printf("%d\n", workerValue);
                }
                int main() {
                  std::thread worker(Worker);
                  while (!ready.load()) std::this_thread::sleep_for(std::chrono::milliseconds(10));
                  int mainValue = 42;
                  std::printf("%d\n", mainValue);
                  release = true;
                  worker.join();
                }
                """ : """
                using System;
                using System.Threading;
                class Program {
                  static ManualResetEventSlim ready = new(false), release = new(false);
                  static void Worker() {
                    int workerValue = 7;
                    ready.Set();
                    while (!release.IsSet) Thread.Sleep(10);
                    Console.WriteLine(workerValue);
                  }
                  static void Main() {
                    Thread.CurrentThread.Name = "Main fixture";
                    var worker = new Thread(Worker) { Name = "Worker fixture" };
                    worker.Start();
                    ready.Wait();
                    int mainValue = 42;
                    Console.WriteLine(mainValue);
                    release.Set();
                    worker.Join();
                  }
                }
                """);
            explorer.OpenFolder(root);
            Open(file);
            debugger.ToggleBreakpoint(file, line);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped && Has("mainValue = 42"), 45f);
        test.WaitFor(() => Threads()?.Children is { Count: >= 2 }, 10f);
        test.Run(() =>
        {
            if (language == "cpp" && (!Has("mainValue = 42") || Threads()?.Children is not { Count: >= 2 }))
            {
                Console.WriteLine($"[Thread probe] active={debugger.Debugging} stopped={debugger.DebugStopped} line={document.Editor.ExecutionLine}");
                foreach (var frame in debugger.stoppedFrames) Console.WriteLine($"[Thread probe] {frame.Name} {frame.File}:{frame.Line}");
                foreach (var entry in said.TakeLast(8)) Console.WriteLine("[Thread probe] " + entry.Text);
                foreach (var row in Threads()?.Children ?? []) Console.WriteLine("[Thread probe] thread " + row.Text + " " + row.Detail);
            }
        });
        test.Check("Threads: native threads are listed", () => Threads()?.Children is { Count: >= 2 });
        test.Run(() =>
        {
            if (Threads()?.Children is { Count: >= 2 } rows && rows.Any(row => row.Icon == "debug-stackframe-focused"))
            {
                main = int.Parse(rows.First(row => row.Icon == "debug-stackframe-focused").Detail!);
                var row = language == "cpp" ? rows.First(row => int.Parse(row.Detail!) != main) : rows.First(row => row.Text.Contains("Worker fixture", StringComparison.Ordinal));
                worker = int.Parse(row.Detail!);
                (row.Tag as SideTag)?.Pressed?.Invoke();
            }
        });
        test.WaitFor(() => Has("workerValue = 7"), 10f);
        test.Check("Threads: selecting worker shows its stack and locals", () => Has("workerValue = 7") && debugger.stoppedFrames.Any(frame => frame.Name.Contains("Worker", StringComparison.Ordinal)));
        bool formatsValues = false;
        bool HexLocal() => sideTree.Items.Any(section => section.Text == "Variables" && section.Children?.Any(row => row.Text.StartsWith("workerValue = ", StringComparison.Ordinal) && row.Text.Contains("0x", StringComparison.OrdinalIgnoreCase)) == true);
        test.Run(() =>
        {
            var command = AllCommands().FirstOrDefault(command => command.Text.EndsWith(Said("debugging.hexadecimal_locals"), StringComparison.Ordinal));
            formatsValues = command.Picked is not null;
            command.Picked?.Invoke();
        });
        test.Check("Value format: LLDB advertises native formatting", () => language != "cpp" || formatsValues);
        test.WaitFor(() => !formatsValues || HexLocal(), 10f);
        test.Check("Value format: native adapter formats locals when supported", () => !formatsValues || HexLocal());
        test.Run(() =>
        {
            if (formatsValues) AllCommands().First(command => command.Text.EndsWith(Said("debugging.hexadecimal_locals"), StringComparison.Ordinal)).Picked?.Invoke();
        });
        test.WaitFor(() => Has("workerValue = 7"), 10f);
        test.Check("Value format: decimal locals are restored", () => Has("workerValue = 7"));
        test.Run(() =>
        {
            var row = sideTree.Items.First(section => section.Text == "Variables").Children!.First(item => item.Text == "workerValue = 7");
            sideTree.OnItemContextMenu?.Invoke(row, System.Numerics.Vector2.Zero);
        });
        test.Check("Watch: variable context menu offers native expression", () => contextMenu?.Showing?.Items.Any(item => item.Label == Said("debugging.add_watch")) == true);
        test.Run(() =>
        {
            contextMenu?.Showing?.Items.First(item => item.Label == Said("debugging.add_watch")).OnPicked?.Invoke();
            contextMenu?.Close();
        });
        test.WaitFor(() => sideTree.Items.Any(section => section.Text == Said("debugging.watch") && section.Children?.Any(item => item.Text == "workerValue = 7") == true), 10f);
        test.Check("Watch: local added from context evaluates in selected thread", () => sideTree.Items.Any(section => section.Text == Said("debugging.watch") && section.Children?.Any(item => item.Text == "workerValue = 7") == true));
        test.Run(() =>
        {
            var row = sideTree.Items.First(section => section.Text == Said("debugging.watch")).Children!.First(item => item.Text == "workerValue = 7");
            sideTree.OnItemContextMenu?.Invoke(row, System.Numerics.Vector2.Zero);
        });
        test.Check("Watch: right-click offers edit and remove without deleting", () => contextMenu?.Showing?.Items.Any(item => item.Label == Said("debugging.edit_watch")) == true
            && contextMenu.Showing.Items.Any(item => item.Label == Said("debugging.remove_watch")));
        test.Run(() =>
        {
            var edit = contextMenu?.Showing?.Items.First(item => item.Label == Said("debugging.edit_watch")).OnPicked;
            contextMenu?.Close();
            edit?.Invoke();
        });
        test.WaitFor(() => textDialog is { IsOpen: true }, 5f);
        test.Run(() =>
        {
            if (textDialog is not { IsOpen: true } dialog) return;
            var input = Descendants(dialog.Body).OfType<TextBox>().First();
            input.Text = "workerValue + 1";
            input.OnSubmitted?.Invoke(input.Text);
        });
        test.WaitFor(() => sideTree.Items.Any(section => section.Text == Said("debugging.watch") && section.Children?.Any(item => item.Text == "workerValue + 1 = 8") == true), 10f);
        test.Check("Watch: edited expression evaluates in the selected worker", () => sideTree.Items.Any(section => section.Text == Said("debugging.watch") && section.Children?.Any(item => item.Text == "workerValue + 1 = 8") == true));
        test.Run(() =>
        {
            var row = sideTree.Items.First(section => section.Text == Said("debugging.watch")).Children!.First(item => item.Text == "workerValue + 1 = 8");
            sideTree.OnItemContextMenu?.Invoke(row, System.Numerics.Vector2.Zero);
            var remove = contextMenu?.Showing?.Items.First(item => item.Label == Said("debugging.remove_watch")).OnPicked;
            contextMenu?.Close();
            remove?.Invoke();
        });
        test.Check("Watch: explicit removal removes the expression", () => sideTree.Items.Any(section => section.Text == Said("debugging.watch")
            && section.Children?.All(item => !item.Text.StartsWith("workerValue", StringComparison.Ordinal)) == true));
        test.Check("Watch persistence: editing and removing updates workspace", () => !explorer.workspace.Watches.Contains("workerValue") && !explorer.workspace.Watches.Contains("workerValue + 1"));
        DebugFrame selectedFrame = default;
        test.Run(() =>
        {
            selectedFrame = debugger.stoppedFrames[debugger.stoppedFrame];
            string notes = Path.Combine(root, "notes.txt");
            File.WriteAllText(notes, "Browsing away from a stopped worker.");
            Open(notes);
        });
        test.Check("Frames: selected frame command remains available while browsing", () => AllCommands().Any(command => command.Text.EndsWith(Said("debugging.show_current_frame"), StringComparison.Ordinal)));
        test.Run(() => AllCommands().First(command => command.Text.EndsWith(Said("debugging.show_current_frame"), StringComparison.Ordinal)).Picked?.Invoke());
        test.Check("Frames: command returns to selected worker source", () => document.Path is {} path && selectedFrame.File is {} source
            && PlatformPaths.Same(path, source) && code.Document.Caret.Line == selectedFrame.Line && code.Document.Caret.Column == selectedFrame.Column
            && debugger.stoppedFrames[debugger.stoppedFrame] == selectedFrame);
        test.Run(() => debugger.SelectThread(main));
        test.WaitFor(() => Has("mainValue = 42") && document.Editor.ExecutionLine == line, 10f);
        test.Check("Threads: selecting main restores its source and locals", () => Has("mainValue = 42") && document.Editor.ExecutionLine == line);
        test.Run(() =>
        {
            previousFrame = sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.call_stack"))?.Children?
                .FirstOrDefault(row => row.Icon == "debug-stackframe-focused");
            code.Document.MoveToEnd(false);
            (previousFrame?.Tag as SideTag)?.Pressed?.Invoke();
        });
        test.Check("Frames: current stack row navigates to its source", () => previousFrame is not null && code.Document.Caret.Line == line);
        test.Run(debugger.StepOver);
        test.WaitFor(() => debugger.DebugStopped && document.Editor.ExecutionLine == line + 1, 10f);
        test.Check("Threads: stepping uses the selected thread", () => debugger.DebugStopped && document.Editor.ExecutionLine == line + 1);
        test.Run(() =>
        {
            code.Document.MoveToEnd(false);
            caretAfterStep = code.Document.Caret;
            (previousFrame?.Tag as SideTag)?.Pressed?.Invoke();
        });
        test.Check("Frames: old stack row cannot navigate after another stop", () => code.Document.Caret == caretAfterStep);
        test.Run(RunOrPlay);
        test.WaitFor(() => !debugger.Debugging, 10f);
        test.Check("Threads: continuing allows all threads to finish", () => !debugger.Debugging);
        test.Run(() => { if (debugger.Debugging) debugger.StopDebugging(); debugger.breakpoints.Clear(); CloseDocument(document); });
    }
}
