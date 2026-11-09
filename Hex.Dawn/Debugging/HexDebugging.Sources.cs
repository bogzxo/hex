using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    internal static string DebuggerSourceFolder => Path.Combine(Path.GetTempPath(), "hex-debug-sources");
    internal static bool IsDebuggerSourcePath(string? path) => path is not null && PlatformPaths.IsUnder(path, DebuggerSourceFolder);
    private readonly string debuggerSourceInstance = Guid.NewGuid().ToString("N");
    private DebugSession? sourceSession;
    private string sourceSessionFolder = "";
    private int debuggerSourceRequest;
    private readonly Dictionary<int, string> debuggerSourceFiles = [];
    private readonly Dictionary<string, DebugSource> debuggerSourceBindings = new(PlatformPaths.Comparer);

    private bool CanUseBreakpointCommands() => !context.Documents.Current.InDesigner && context.Documents.Current.IsText
        && !IsMemoryPath(context.Documents.Current.Path) && !IsDebuggerSourcePath(context.Documents.Current.Path);

    private bool IsCurrentDebuggerSource(HexDocument document) => document.Path is {} path && DebugStopped && debugging is {} session
        && debuggerSourceBindings.TryGetValue(path, out var source) && session.OwnsSource(source)
        && stoppedFrame >= 0 && stoppedFrame < stoppedFrames.Count && stoppedFrames[stoppedFrame].Source?.Reference == source.Reference;

    private void ShowDebuggerSource(DebugFrame frame, DebugSource source)
    {
        if (!DebugStopped || debugging is not {} session || !session.OwnsSource(source)) return;
        if (!ReferenceEquals(sourceSession, session))
        {
            sourceSession = session;
            sourceSessionFolder = Path.Combine(DebuggerSourceFolder, debuggerSourceInstance, Guid.NewGuid().ToString("N"));
            debuggerSourceFiles.Clear();
            debuggerSourceBindings.Clear();
        }
        int request = ++debuggerSourceRequest, stop = stops, selected = stoppedFrame;
        long thread = threadRequest;
        var weak = new WeakReference<HexDocument>(context.Documents.Current);
        string? path = context.Documents.Current.Path;
        int version = context.Documents.Current.Editor.Document.Version;
        var caret = context.Documents.Current.Editor.Document.Caret;
        var focus = context.Window.Compositor.Focus;
        bool Current() => debugging == session && DebugStopped && stops == stop && stoppedFrame == selected && threadRequest == thread
            && request == debuggerSourceRequest && selected < stoppedFrames.Count && stoppedFrames[selected].Id == frame.Id
            && weak.TryGetTarget(out var original) && context.Documents.Current == original && original.Path == path
            && original.Editor.Document.Version == version && original.Editor.Document.Caret == caret
            && (context.Window.Compositor.Focus is not TextBox || ReferenceEquals(context.Window.Compositor.Focus, focus));
        _ = Read();
        async Task Read()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var content = await session.ReadSource(source, deadline.Token).ConfigureAwait(false);
                debugWork.Enqueue(() =>
                {
                    if (!Current() || content is null) return;
                    try
                    {
                        if (!debuggerSourceFiles.TryGetValue(source.Reference, out var cache))
                        {
                            string fileName = HexDebugSourceText.FileName(source.Name, source.Path, content.MimeType, debuggingKind);
                            // debugpy may assign another reference at each stop for the same compiled source. Reuse
                            // its tab only when the logical source and exact text match within this session.
                            if (source.Name.Length > 0 || !string.IsNullOrEmpty(source.Path))
                                cache = debuggerSourceBindings.FirstOrDefault(pair => pair.Value.Name == source.Name && pair.Value.Path == source.Path
                                    && Path.GetFileName(pair.Key) == fileName && File.Exists(pair.Key) && File.ReadAllText(pair.Key) == content.Text).Key;
                            cache ??= Path.Combine(sourceSessionFolder, source.Reference.ToString(), fileName);
                            debuggerSourceFiles[source.Reference] = cache;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                        if (!File.Exists(cache) || File.ReadAllText(cache) != content.Text) File.WriteAllText(cache, content.Text);
                        if (context.Documents.Find(cache) is {} existing && existing.Editor.Text != content.Text)
                        {
                            existing.Editor.Text = content.Text;
                            existing.SavedCode = existing.Editor.Text;
                            existing.DiskText = content.Text;
                            existing.Modified = false;
                            existing.CodeEdited = false;
                        }
                        context.Documents.OpenAt(cache, frame.Line, frame.Column);
                        if (context.Documents.Find(cache) is {} view)
                        {
                            view.Editor.ReadOnly = true;
                            view.Editor.OnGutterPressed = null;
                            view.Editor.ExecutionLine = frame.Line;
                            view.Name = context.Strings.Said("debugging.source_snapshot", ("name", DebuggerSourceName(source)));
                            debuggerSourceBindings[cache] = source;
                            showingDisassembly = showingMemory = false;
                        }
                    }
                    catch (Exception error) { Failed(error); }
                });
            }
            catch (Exception error) { debugWork.Enqueue(() => { if (Current()) Failed(error); }); }
        }
        void Failed(Exception error) => context.Notices.Say(context.Strings.Said("debugging.source_failed", ("why", error.Message)), error: true);
    }

    private void ClearDebuggerSources()
    {
        debuggerSourceRequest++;
        sourceSession = null;
        sourceSessionFolder = "";
        debuggerSourceFiles.Clear();
        debuggerSourceBindings.Clear();
    }

    private string DebuggerSourceName(DebugSource source)
    {
        string name = source.Name.Length > 0 ? source.Name : (source.Path ?? "").Replace('\\', '/').Split('/').Last();
        return name.Length > 0 ? name : context.Strings.Said("debugging.source_unnamed", ("reference", source.Reference.ToString()));
    }
}
