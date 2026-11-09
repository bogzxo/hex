namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private int setNextRequest;
    private bool settingNextStatement;

    private bool CanSetNextStatement() => DebugStopped && !settingNextStatement && debugging is { CanGoto: true }
        && stoppedFrame == 0 && stoppedFrames.Count > 0 && stoppedFrames[0].File is {} file
        && context.Documents.Current is { IsText: true, DiffOf: null, Picture: null, Path: {} path } && PlatformPaths.Same(file, path);

    internal void SetNextStatement()
    {
        if (!CanSetNextStatement() || debugging is not {} session) return;
        HexDocument document = context.Documents.Current;
        if (!document.Editor.Document.TextEquals(document.SavedCode))
        {
            context.Notices.Say(context.Strings.Said("debugging.set_next_modified"), error: true);
            return;
        }
        var caret = document.Editor.Document.Caret;
        int version = document.Editor.Document.Version, stop = stops, frame = stoppedFrames[0].Id, request = ++setNextRequest;
        string file = document.Path!;
        bool Current() => ReferenceEquals(debugging, session) && DebugStopped && stops == stop && request == setNextRequest
            && stoppedFrame == 0 && stoppedFrames.Count > 0 && stoppedFrames[0].Id == frame
            && context.Documents.Current == document && document.Path == file && document.Editor.Document.TextEquals(document.SavedCode)
            && document.Editor.Document.Version == version && document.Editor.Document.Caret == caret;
        void Failed(Exception error)
        {
            if (Current()) context.Notices.Say(context.Strings.Said("debugging.set_next_failed", ("why", error.Message)), error: true);
        }
        _ = Prepare();
        async Task Prepare()
        {
            try
            {
                var found = await session.PrepareGoto(file, caret.Line).ConfigureAwait(false);
                debugWork.Enqueue(() =>
                {
                    if (!Current() || found is null) return;
                    if (found.Targets.Count == 0)
                    {
                        context.Notices.Say(context.Strings.Said("debugging.set_next_unavailable"), error: true);
                        return;
                    }
                    void Pick(int target)
                    {
                        if (!Current() || settingNextStatement) return;
                        settingNextStatement = true;
                        _ = Move(target);
                    }
                    async Task Move(int target)
                    {
                        Exception? failure = null;
                        try { await session.Goto(found, target).ConfigureAwait(false); }
                        catch (Exception error) { failure = error; }
                        debugWork.Enqueue(() => { settingNextStatement = false; if (failure is not null) Failed(failure); });
                    }
                    if (found.Targets.Count == 1) Pick(found.Targets[0].Id);
                    else context.Windows.Choose(context.Strings.Said("debugging.set_next_statement"), context.Strings.Said("debugging.set_next_target"),
                        found.Targets.Select(target => (target.Label, (Action)(() => Pick(target.Id)))).ToList());
                });
            }
            catch (Exception error) { debugWork.Enqueue(() => Failed(error)); }
        }
    }
}
