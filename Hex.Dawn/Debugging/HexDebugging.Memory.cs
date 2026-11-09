namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    internal static string MemoryFolder => Path.Combine(Path.GetTempPath(), "hex-debug-memory");
    internal static bool IsMemoryPath(string? path) => path is not null && PlatformPaths.IsUnder(path, MemoryFolder);
    private readonly string memoryFile = Path.Combine(MemoryFolder, Guid.NewGuid().ToString("N"), "Memory.txt");
    private const int MEMORY_PAGE = 256;
    private string? memoryInput, memoryReference;
    private long memoryOffset;
    private int memoryReferenceStop, memoryRequest;
    private bool showingMemory, memoryAsking;

    private bool CanShowMemory() => DebugStopped && debugging?.CanReadMemory == true && stoppedFrame >= 0 && stoppedFrame < stoppedFrames.Count;
    private bool IsCurrentMemoryView() => context.Documents.Current.Path is {} path && PlatformPaths.Same(path, memoryFile);
    private bool CanPageMemory() => CanShowMemory() && !memoryAsking && IsCurrentMemoryView() && memoryInput is not null;

    private void ShowMemory()
    {
        if (!CanShowMemory() || debugging is not {} session) return;
        debuggerSourceRequest++;
        int stop = stops, frame = stoppedFrames[stoppedFrame].Id;
        long thread = threadRequest;
        context.Windows.Prompt(context.Strings.Said("debugging.memory"), context.Strings.Said("debugging.memory_address"), memoryInput ?? string.Empty,
            context.Strings.Said("debugging.memory_read"), string.Empty, input =>
            {
                if (debugging == session && CanShowMemory() && stops == stop && threadRequest == thread && stoppedFrames[stoppedFrame].Id == frame && !string.IsNullOrWhiteSpace(input))
                    ReadMemoryView(input.Trim(), 0);
            });
    }

    internal void AddMemoryAction(Menu menu, SideTag tag)
    {
        if (!CanShowMemory() || tag.DebugCurrent is not {} current || !current() || tag.DebugValue is not {} value) return;
        string? reference = value.MemoryReference ?? (value.Type?.Contains('*') == true ? HexMemoryText.Address(value.Value) : null);
        if (string.IsNullOrWhiteSpace(reference)) return;
        var session = debugging;
        int stop = stops, frame = stoppedFrames[stoppedFrame].Id;
        long thread = threadRequest;
        menu.Add(context.Strings.Said("debugging.memory_view"), () =>
        {
            if (debugging == session && CanShowMemory() && stops == stop && threadRequest == thread && stoppedFrames[stoppedFrame].Id == frame && current())
                ReadMemoryView(value.EvaluateName ?? tag.DebugExpression ?? reference, 0, reference);
        }, icon: "file-binary");
    }

    private void RefreshMemory()
    {
        if (CanPageMemory()) ReadMemoryView(memoryInput!, memoryOffset, stops == memoryReferenceStop ? memoryReference : null);
    }

    private void PageMemory(int direction)
    {
        if (!CanPageMemory()) return;
        long next = memoryOffset + direction * (long)MEMORY_PAGE;
        if (next is >= -9007199254740991 and <= 9007199254740991)
            ReadMemoryView(memoryInput!, next, stops == memoryReferenceStop ? memoryReference : null);
    }

    private void ReadMemoryView(string input, long offset, string? nativeReference = null)
    {
        if (!CanShowMemory() || debugging is not {} session) return;
        debuggerSourceRequest++;
        int stop = stops, selected = stoppedFrame, frame = stoppedFrames[selected].Id, request = ++memoryRequest;
        long thread = threadRequest;
        var weak = new WeakReference<HexDocument>(context.Documents.Current);
        string? path = context.Documents.Current.Path;
        int version = context.Documents.Current.Editor.Document.Version;
        var caret = context.Documents.Current.Editor.Document.Caret;
        memoryAsking = true;
        bool Current() => debugging == session && DebugStopped && stops == stop && stoppedFrame == selected && threadRequest == thread
            && request == memoryRequest && selected < stoppedFrames.Count && stoppedFrames[selected].Id == frame
            && weak.TryGetTarget(out var original) && context.Documents.Current == original && original.Path == path
            && original.Editor.Document.Version == version && original.Editor.Document.Caret == caret;
        _ = Read();
        async Task Read()
        {
            try
            {
                string? address = HexMemoryText.Address(input);
                string? reference = nativeReference ?? (address == input ? address : null);
                if (reference is null)
                {
                    var evaluated = await session.EvaluateSaying(input, frame).ConfigureAwait(false);
                    reference = evaluated.Value?.MemoryReference ?? (evaluated.Value is {} value ? HexMemoryText.Address(value.Value) : null);
                    if (string.IsNullOrEmpty(reference)) throw new InvalidOperationException(evaluated.Error ?? context.Strings.Said("debugging.memory_no_reference"));
                }
                var block = await session.ReadMemory(reference, offset, MEMORY_PAGE).ConfigureAwait(false);
                debugWork.Enqueue(() =>
                {
                    if (request == memoryRequest) memoryAsking = false;
                    if (!Current() || block is null) return;
                    try
                    {
                        string text = context.Strings.Said("debugging.memory_snapshot", ("expression", input.Replace('\r', ' ').Replace('\n', ' ')),
                            ("address", block.Address), ("count", block.Data.Length.ToString())) + "\n\n" + HexMemoryText.Rows(block, MEMORY_PAGE);
                        if (block.UnreadableBytes > 0) text += "\n" + context.Strings.Said("debugging.memory_unreadable", ("count", block.UnreadableBytes.ToString())) + "\n";
                        else if (block.Data.Length < MEMORY_PAGE) text += "\n" + context.Strings.Said("debugging.memory_end") + "\n";
                        Directory.CreateDirectory(Path.GetDirectoryName(memoryFile)!);
                        File.WriteAllText(memoryFile, text);
                        if (context.Documents.Find(memoryFile) is {} existing)
                        {
                            existing.Editor.Text = text;
                            existing.SavedCode = existing.DiskText = text;
                            existing.Modified = false;
                        }
                        context.Documents.OpenAt(memoryFile, 0, 0);
                        if (context.Documents.Find(memoryFile) is {} view)
                        {
                            view.Editor.ReadOnly = true;
                            view.Editor.ExecutionLine = -1;
                            view.Name = context.Strings.Said("debugging.memory_title");
                        }
                        memoryInput = input;
                        memoryOffset = offset;
                        memoryReference = reference;
                        memoryReferenceStop = stop;
                        showingMemory = true;
                        showingDisassembly = false;
                    }
                    catch (Exception error) { Failed(error); }
                });
            }
            catch (Exception error)
            {
                debugWork.Enqueue(() =>
                {
                    if (request == memoryRequest) memoryAsking = false;
                    if (Current()) Failed(error);
                });
            }
        }
        void Failed(Exception error) => context.Notices.Say(context.Strings.Said("debugging.memory_failed", ("why", error.Message)), error: true);
    }

    private void ClearMemoryView()
    {
        memoryRequest++;
        memoryAsking = showingMemory = false;
        memoryInput = memoryReference = null;
        memoryOffset = 0;
    }
}
