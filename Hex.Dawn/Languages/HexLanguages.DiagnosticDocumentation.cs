namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    internal string? DiagnosticDocumentationAtCaret()
    {
        HexDocument of = context.Documents.Current;
        if (ChangePending(of) || !lintFound.TryGetValue(of, out var diagnostics)) return null;
        var caret = of.Editor.Document.Caret;
        foreach (var diagnostic in diagnostics)
            if (diagnostic.DocumentationUrl is {} url && diagnostic.Line == caret.Line
                && caret.Column >= diagnostic.Column && caret.Column < diagnostic.Column + diagnostic.Length) return url;
        return null;
    }

    private void OpenDiagnosticDocumentation()
    {
        if (DiagnosticDocumentationAtCaret() is {} url) context.Windows.OpenInBrowser(url);
    }
}
