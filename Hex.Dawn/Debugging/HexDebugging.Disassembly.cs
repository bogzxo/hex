using System.Text;

namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    internal static string DisassemblyFolder => Path.Combine(Path.GetTempPath(), "hex-disassembly");
    internal static bool IsDisassemblyPath(string? path) => path is not null && PlatformPaths.IsUnder(path, DisassemblyFolder);
    private readonly string disassemblyFile = Path.Combine(DisassemblyFolder, Guid.NewGuid().ToString("N"), "Disassembly.asm");
    private bool showingDisassembly;
    private int disassemblyRequest;

    private bool CanShowDisassembly() => DebugStopped && debugging?.CanDisassemble == true
        && stoppedFrame >= 0 && stoppedFrame < stoppedFrames.Count && !string.IsNullOrEmpty(stoppedFrames[stoppedFrame].InstructionPointerReference);

    private void ShowDisassembly() => ShowDisassemblyAt(null);

    private void ShowDisassemblyAt(string? address)
    {
        if (!CanShowDisassembly() || debugging is not {} session) return;
        debuggerSourceRequest++;
        var frame = stoppedFrames[stoppedFrame];
        int stop = stops, selected = stoppedFrame, request = ++disassemblyRequest;
        long thread = threadRequest;
        var weak = new WeakReference<HexDocument>(context.Documents.Current);
        string? path = context.Documents.Current.Path;
        int version = context.Documents.Current.Editor.Document.Version;
        var caret = context.Documents.Current.Editor.Document.Caret;
        bool Current() => debugging == session && DebugStopped && stops == stop && stoppedFrame == selected
            && request == disassemblyRequest && thread == threadRequest && selected < stoppedFrames.Count && stoppedFrames[selected].Id == frame.Id
            && weak.TryGetTarget(out var original) && context.Documents.Current == original && original.Path == path
            && original.Editor.Document.Version == version && original.Editor.Document.Caret == caret;
        _ = Read();
        async Task Read()
        {
            try
            {
                var instructions = await session.Disassemble(address ?? frame.InstructionPointerReference!).ConfigureAwait(false);
                debugWork.Enqueue(() =>
                {
                    if (!Current() || instructions is null) return;
                    if (instructions.Count == 0)
                    {
                        context.Notices.Say(context.Strings.Said("debugging.disassembly_empty"), error: true);
                        return;
                    }
                    try
                    {
                        var source = new StringBuilder();
                        foreach (var instruction in instructions)
                        {
                            static string Line(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
                            source.Append(Line(instruction.Address)).Append("  ").Append(Line(instruction.Bytes).PadRight(24)).Append("  ").Append(Line(instruction.Instruction));
                            if (instruction.Symbol is { Length: > 0 } symbol && !instruction.Instruction.Contains(symbol, StringComparison.Ordinal)) source.Append("  ; ").Append(Line(symbol));
                            source.Append('\n');
                        }
                        string text = source.ToString();
                        Directory.CreateDirectory(Path.GetDirectoryName(disassemblyFile)!);
                        File.WriteAllText(disassemblyFile, text);
                        if (context.Documents.Find(disassemblyFile) is {} existing)
                        {
                            existing.Editor.Text = text;
                            existing.SavedCode = existing.Editor.Text;
                            existing.DiskText = text;
                            existing.Modified = false;
                        }
                        context.Documents.OpenAt(disassemblyFile, 0, 0);
                        if (context.Documents.Find(disassemblyFile) is {} view)
                        {
                            view.Editor.ReadOnly = true;
                            int current = instructions.ToList().FindIndex(item => DebugSession.SameInstructionAddress(item.Address, frame.InstructionPointerReference!));
                            view.Editor.ExecutionLine = current >= 0 ? current : address is null ? 0 : -1;
                            view.Name = context.Strings.Said("debugging.disassembly");
                            showingDisassembly = true;
                            showingMemory = false;
                            BindInstructions(view, session, instructions);
                        }
                    }
                    catch (Exception error) { Failed(error); }
                });
            }
            catch (Exception error) { debugWork.Enqueue(() => { if (Current()) Failed(error); }); }
        }
        void Failed(Exception error) => context.Notices.Say(context.Strings.Said("debugging.disassembly_failed", ("why", error.Message)), error: true);
    }

    private bool InstructionStepping => showingDisassembly && IsDisassemblyPath(context.Documents.Current.Path) && debugging?.CanStepInstruction == true;
}
