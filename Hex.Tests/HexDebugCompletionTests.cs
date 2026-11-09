using System.Collections.Concurrent;

using Hex.Dawn;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Tests;

public class HexDebugCompletionTests
{
    [Fact]
    public async Task NewerTextWinsAndOldAnswersCannotReplaceItsNativeRanges()
    {
        var posted = new ConcurrentQueue<Action>();
        var requests = new List<TaskCompletionSource<IReadOnlyList<DebugCompletion>?>>();
        var completion = new HexDebugCompletions((text, column) =>
        {
            var answer = new TaskCompletionSource<IReadOnlyList<DebugCompletion>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            requests.Add(answer);
            return answer.Task;
        }, () => true, posted.Enqueue, "loading");
        var editor = new CodeEditor("box.v");
        editor.Document.MoveTo(new(0, 5));
        Assert.Equal("loading", completion.Complete(editor)!.Value.Waiting);
        editor.Document.Type('a');
        completion.Complete(editor);
        requests[1].SetResult([new("value", "value", "int", "field", 4, 2, 5, 0)]);
        await UntilPosted(posted);
        while (posted.TryDequeue(out var next)) next();
        var item = Assert.Single(completion.Complete(editor)!.Value.Items);
        Assert.Equal(new UITextPosition(0, 4), item.ReplacementStart);
        Assert.Equal(new UITextPosition(0, 6), item.ReplacementEnd);
        Assert.Equal(editor.Document.Version, item.ExpectedVersion);
        requests[0].SetResult([new("veryOld", "veryOld", "", "field", 4, 1, 7, 0)]);
        await UntilPosted(posted);
        while (posted.TryDequeue(out var next)) next();
        Assert.Equal("value", Assert.Single(completion.Complete(editor)!.Value.Items).Text);
    }

    [Fact]
    public async Task RetiredFramesAndMovedCaretsDiscardPendingCompletions()
    {
        var posted = new ConcurrentQueue<Action>();
        bool current = true;
        var answer = new TaskCompletionSource<IReadOnlyList<DebugCompletion>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new HexDebugCompletions((_, _) => answer.Task, () => current, posted.Enqueue, "loading");
        var editor = new CodeEditor("box.v");
        editor.Document.MoveTo(new(0, 5));
        completion.Complete(editor);
        editor.Document.MoveTo(new(0, 4));
        answer.SetResult([new("value", "value", "", "field", 4, 1, 5, 0)]);
        await UntilPosted(posted);
        while (posted.TryDequeue(out var next)) next();
        Assert.Empty(completion.Complete(editor)!.Value.Items);
        current = false;
        Assert.Null(completion.Complete(editor));
    }

    private static async Task UntilPosted(ConcurrentQueue<Action> posted)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (posted.IsEmpty) await Task.Delay(5, timeout.Token);
    }
}
