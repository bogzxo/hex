using System.Numerics;
using System.Text;
using Hex.Core.Merge;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestMergeResultLanguageServer(HexPointerScript test)
    {
        if (!LanguageServers.IsAvailable("pyright-langserver"))
        {
            test.Run(() => Console.WriteLine("[Hex self-test] merge-result-lsp skipped: Pyright is not installed"));
            return;
        }

        const string saved = "saved_value = 1\n";
        const string ordinaryText = "ordinary_unsaved_name\n";
        const string cancelText = "cancel_result_name\n";
        const string completeText = "complete_result_name\n";
        string root = Path.Combine(testFolder, "merge-result-lsp");
        string path = Path.Combine(root, "original.py");
        HexDocument ordinary = null!, result = null!;
        MergeEditor merge = null!;
        byte[]? completed = null;
        int cancellations = 0;

        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            Directory.CreateDirectory(root);
            File.WriteAllText(path, saved);
            Open(path);
            ordinary = document;
            ordinary.Editor.Document.Replace(ordinaryText);
        });
        test.WaitFor(() => HasDiagnostic(ordinary, "ordinary_unsaved_name"), 15);
        test.Check("merge-result-lsp: ordinary tab analyzes its unsaved text", () =>
            HasDiagnostic(ordinary, "ordinary_unsaved_name") && File.ReadAllText(path) == saved);

        test.Run(() => OpenResult(cancelText));
        test.WaitFor(() => languages.CanServe(result, "hoverProvider"), 15);
        test.Run(() => merge.AcceptCurrent(MergeChoice.Mine));
        test.WaitFor(() => HasDiagnostic(result, "cancel_result_name"), 15);
        test.Check("merge-result-lsp: pathless Result has its own Python diagnostics and leaves ordinary text intact", () =>
            result.Path is null && ReferenceEquals(document, result) && result.Editor.Highlighter?.Name == "Python"
            && HasDiagnostic(result, "cancel_result_name") && !HasDiagnostic(result, "ordinary_unsaved_name")
            && HasDiagnostic(ordinary, "ordinary_unsaved_name") && ordinary.Editor.Text == ordinaryText);
        test.Click(() => Center(Descendants(merge.Root).OfType<Button>().FirstOrDefault(button => button.Label == "Cancel")));
        test.WaitFor(() => DiscardButton() is not null, 5);
        test.Settle(0.3f); // Let the confirmation dialog finish scaling in before targeting its button.
        test.Click(() => Center(DiscardButton()));
        test.WaitFor(() => !documents.Contains(result), 5);
        test.Check("merge-result-lsp: Cancel detaches the Result and preserves the ordinary buffer and original file", () =>
            cancellations == 1 && completed is null && !documents.Contains(result)
            && languages.LinterOf(result) is null && languages.ServerDiagnosticsOf(result) is null
            && ordinary.Editor.Text == ordinaryText && HasDiagnostic(ordinary, "ordinary_unsaved_name") && File.ReadAllText(path) == saved);

        test.Run(() => OpenResult(completeText));
        test.WaitFor(() => languages.CanServe(result, "hoverProvider"), 15);
        test.Run(() => merge.AcceptCurrent(MergeChoice.Mine));
        test.WaitFor(() => HasDiagnostic(result, "complete_result_name"), 15);
        test.Check("merge-result-lsp: a fresh Result gets diagnostics from its own new text", () =>
            HasDiagnostic(result, "complete_result_name") && !HasDiagnostic(result, "cancel_result_name"));
        test.Click(() => Center(Descendants(merge.Root).OfType<Button>().FirstOrDefault(button => button.Label.StartsWith("Save merged file", StringComparison.Ordinal))));
        test.WaitFor(() => !documents.Contains(result), 8);
        test.Check("merge-result-lsp: Complete delivers Result bytes and detaches its server", () =>
            completed is not null && Encoding.UTF8.GetString(completed) == completeText
            && !documents.Contains(result) && languages.LinterOf(result) is null && languages.ServerDiagnosticsOf(result) is null);
        test.Check("merge-result-lsp: the ordinary server and original file still have their own text", () =>
            cancellations == 1 && ordinary.Editor.Text == ordinaryText && HasDiagnostic(ordinary, "ordinary_unsaved_name")
            && File.ReadAllText(path) == saved);
        test.Run(() => ordinary.Editor.Document.Replace(saved));

        void OpenResult(string text)
        {
            var model = MergeModel.FromMarkers("<<<<<<< Mine\n" + text + "=======\ntheirs_value = 2\n>>>>>>> Theirs\n");
            merge = OpenMergeEditor(new("original.py", "Mine", "Theirs", "Base", model,
                bytes => { completed = bytes; return Task.CompletedTask; }, () => cancellations++, path));
            result = document;
        }

        bool HasDiagnostic(HexDocument tab, string name) =>
            languages.ServerDiagnosticsOf(tab)?.Any(item => item.Message.Contains(name, StringComparison.Ordinal)) == true;

        Button? DiscardButton() => chrome.Dialog?.Buttons.FirstOrDefault(button => button.Label == "Close without saving");

        static Vector2 Center(Button? button) => button?.Module is { } module ? module.ToWorld(button.Bounds.Center) : Vector2.Zero;
    }
}
