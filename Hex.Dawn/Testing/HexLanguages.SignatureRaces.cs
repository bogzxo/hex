using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    internal void TestSignatureRaces(HexPointerScript test, string folder)
    {
        HexDocument of = null!;
        SignatureRequest first = null!, latest = null!, edited = null!;
        var slow = new TaskCompletionSource<UISignature?>();
        var fast = new TaskCompletionSource<UISignature?>();
        var changed = new TaskCompletionSource<UISignature?>();
        int requests = 0;
        SignatureRequest At() => new(of, linters[of], of.Editor.Document.Version, of.Editor.Document.Caret, "race", 0);
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "race.cpp");
            File.WriteAllText(file, "int value = 0;\n");
            context.Documents.OpenAt(file, 0, 0);
            of = context.Documents.Current;
        });
        test.WaitFor(() => CanServe(of, "signatureHelpProvider"), 30f);
        test.Run(() =>
        {
            first = At();
            RequestSignature(first, () => { requests++; return slow.Task; });
            of.Editor.Document.MoveTo(new UITextPosition(0, 5));
            latest = At();
            RequestSignature(latest, () => { requests++; return fast.Task; });
        });
        test.Check("Signature races: new caret request does not wait for old response", () => requests == 2 && !slow.Task.IsCompleted);
        test.Run(() => fast.SetResult(new UISignature("new answer", 4, 6)));
        test.WaitFor(() => signed.Request == latest, 5f);
        test.Check("Signature races: newest response is displayed", () => signed.Request == latest && signed.Said?.Text == "new answer");
        test.Run(() => slow.SetResult(new UISignature("old answer", 0, 3)));
        test.Settle(0.2f);
        test.Check("Signature races: late response cannot replace newest signature", () => signed.Request == latest && signed.Said?.Text == "new answer");
        UISignature? stale = null;
        test.Run(() =>
        {
            of.Editor.Document.MoveTo(new UITextPosition(0, 8));
            edited = At();
            stale = RequestSignature(edited, () => changed.Task);
            of.Editor.Text = "int changed = 1;\n";
            changed.SetResult(new UISignature("before edit", 0, 6));
        });
        test.Settle(0.2f);
        test.Check("Signature races: caret change does not reuse old parameter highlight", () => stale is null);
        test.Check("Signature races: edited document rejects late response", () => signed.Request != edited && signed.Said?.Text != "before edit");
        var failed = new TaskCompletionSource<UISignature?>();
        SignatureRequest failure = null!;
        test.Run(() => { failure = At(); RequestSignature(failure, () => failed.Task); failed.SetException(new IOException("fixture failure")); });
        test.WaitFor(() => signed.Request == failure, 5f);
        test.Check("Signature races: failed request settles without stale help", () => signed.Request == failure && signed.Said is null && signing is null);
        test.Run(() =>
        {
            of.Editor.Document.MoveTo(new UITextPosition(0, 3));
            latest = At();
            RequestSignature(latest, () => Task.FromResult<UISignature?>(new UISignature("recovered", 0, 9)));
        });
        test.WaitFor(() => signed.Request == latest, 5f);
        test.Check("Signature races: next request recovers after failure", () => signed.Request == latest && signed.Said?.Text == "recovered");
    }
}
