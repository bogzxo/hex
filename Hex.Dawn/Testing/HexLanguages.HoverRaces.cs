using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    internal void TestHoverRaces(HexPointerScript test, string folder)
    {
        HexDocument of = null!;
        HoverRequest first = null!, latest = null!, edited = null!;
        var slow = new TaskCompletionSource<string?>();
        var fast = new TaskCompletionSource<string?>();
        var changed = new TaskCompletionSource<string?>();
        HoverRequest At(int column) => new(of, linters[of], of.Path, of.Editor.Document.Version, new(0, column), editWorkspaceGeneration);
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "race.cpp");
            File.WriteAllText(file, "int value = 0;\n");
            context.Documents.OpenAt(file, 0, 0);
            of = context.Documents.Current;
        });
        test.WaitFor(() => CanServe(of, "hoverProvider"), 30f);
        test.Run(() =>
        {
            first = At(0); latest = At(4);
            RequestHover(first, () => slow.Task);
            RequestHover(latest, () => fast.Task);
            fast.SetResult("new answer");
        });
        test.WaitFor(() => hovered.Request == latest, 5f);
        test.Check("Hover races: latest request completes while old response is pending", () => hovered.Said == "new answer" && !slow.Task.IsCompleted);
        test.Run(() => slow.SetResult("old answer"));
        test.Settle(0.2f);
        test.Check("Hover races: older response cannot overwrite current help", () => hovered.Request == latest && hovered.Said == "new answer");
        test.Run(() =>
        {
            edited = At(8); RequestHover(edited, () => changed.Task);
            of.Editor.Text = "int changed = 1;\n";
            changed.SetResult("before edit");
        });
        test.Settle(0.2f);
        test.Check("Hover races: edits reject delayed help", () => hovered.Request != edited && hovering is null);
        test.Run(() =>
        {
            latest = At(4);
            RequestHover(latest, () => Task.FromException<string?>(new IOException("fixture failure")));
        });
        test.WaitFor(() => hovered.Request == latest, 5f);
        test.Check("Hover races: failure settles without retaining pending request", () => hovered.Said is null && hovering is null);
        test.Run(() =>
        {
            latest = At(5);
            RequestHover(latest, () => Task.FromResult<string?>("recovered"));
        });
        test.WaitFor(() => hovered.Request == latest, 5f);
        test.Check("Hover races: next position recovers after failure", () => hovered.Said == "recovered");
        test.Run(() =>
        {
            edited = At(6); var answer = new TaskCompletionSource<string?>();
            RequestHover(edited, () => answer.Task); EditWorkspaceChanged(); answer.SetResult("old workspace");
        });
        test.Settle(0.2f);
        test.Check("Hover races: workspace change rejects delayed help", () => hovered.Request != edited);
        test.Run(() =>
        {
            latest = At(5);
            RequestHover(latest, () => latest.Server.Hover(of.Path!, 0, 5));
        });
        test.WaitFor(() => hovered.Request == latest, 10f);
        test.Check("Hover races: native clangd hover still resolves", () => hovered.Request == latest && hovered.Said?.Contains("changed") == true);
    }
}
