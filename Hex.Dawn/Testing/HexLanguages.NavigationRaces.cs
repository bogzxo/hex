using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    internal void TestNavigationRaces(HexPointerScript test, string folder)
    {
        string source = Path.Combine(folder, "source.cpp"), destination = Path.Combine(folder, "destination.cpp");
        HexDocument of = null!;
        TaskCompletionSource<bool> answer = null!, newer = null!, inner = null!;
        int accepted = 0;
        TaskCompletionSource<bool> Ask(Action? action = null)
        {
            var reply = new TaskCompletionSource<bool>();
            WhenNavigating(CaptureNavigation(of, linters[of]), reply.Task,
                _ => { if (action is not null) action(); else context.Documents.OpenAt(destination, 0, 0); });
            return reply;
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(source, "int source = 0;\n");
            File.WriteAllText(destination, "int destination = 1;\n");
            context.Documents.OpenAt(source, 0, 0);
            of = context.Documents.Current;
        });
        test.WaitFor(() => CanServe(of, "definitionProvider"), 30f);
        test.Run(() => { answer = Ask(); of.Editor.Document.MoveTo(new UITextPosition(0, 5)); answer.SetResult(true); });
        test.Settle(0.2f);
        test.Check("Navigation races: moved caret rejects delayed jump", () => context.Documents.Current == of && of.Editor.Document.Caret.Column == 5);
        test.Run(() => { answer = Ask(); of.Editor.Text = "int changed = 0;\n"; answer.SetResult(true); });
        test.Settle(0.2f);
        test.Check("Navigation races: edited source rejects delayed jump", () => context.Documents.Current == of);
        test.Run(() => { answer = Ask(); newer = Ask(() => accepted++); newer.SetResult(true); });
        test.WaitFor(() => accepted == 1, 5f);
        test.Run(() => answer.SetResult(true));
        test.Settle(0.2f);
        test.Check("Navigation races: newer request supersedes older request at same caret", () => accepted == 1 && context.Documents.Current == of);
        test.Run(() =>
        {
            var request = CaptureNavigation(of, linters[of]);
            inner = new TaskCompletionSource<bool>();
            WhenNavigating(request, Task.FromResult(true), _ => WhenNavigating(request, inner.Task,
                _ => context.Documents.OpenAt(destination, 0, 0)));
        });
        test.Settle(0.2f);
        test.Run(() => { of.Editor.Document.MoveTo(new UITextPosition(0, 3)); inner.SetResult(true); });
        test.Settle(0.2f);
        test.Check("Navigation races: second-stage source response respects caret changes", () => context.Documents.Current == of && of.Editor.Document.Caret.Column == 3);
        test.Run(() => { answer = Ask(); EditWorkspaceChanged(); answer.SetResult(true); });
        test.Settle(0.2f);
        test.Check("Navigation races: workspace change rejects delayed jump", () => context.Documents.Current == of);
        test.Run(() => { answer = Ask(); answer.SetResult(true); });
        test.WaitFor(() => context.Documents.Current.Path == destination, 5f);
        test.Check("Navigation races: unchanged request navigates normally", () => context.Documents.Current.Path == destination);
    }
}
