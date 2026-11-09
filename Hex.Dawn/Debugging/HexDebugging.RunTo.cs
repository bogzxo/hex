namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    internal void RunToCursor()
    {
        if (!DebugStopped || debugging is not { } session || context.Documents.Current is not { IsText: true, Path: { } file } document || IsDebuggerSourcePath(file)) return;
        if (document.Modified)
        {
            context.Notices.Say(context.Strings.Said("debugging.run_to_modified"), error: true);
            return;
        }
        int line = document.Editor.Document.Caret.Line;
        _ = Run();
        async Task Run()
        {
            try { await session.RunTo(file, line).ConfigureAwait(false); }
            catch (Exception error)
            {
                debugWork.Enqueue(() =>
                {
                    if (ReferenceEquals(debugging, session)) context.Notices.Say(context.Strings.Said("debugging.run_to_failed", ("why", error.Message)), error: true);
                });
            }
        }
    }
}
