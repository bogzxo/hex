namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    internal IReadOnlyList<UICodeDiagnosticLocation> RelatedDiagnosticsAtCaret()
    {
        HexDocument of = context.Documents.Current;
        if (ChangePending(of) || !lintFound.TryGetValue(of, out var diagnostics)) return [];
        var caret = of.Editor.Document.Caret;
        return diagnostics.Where(diagnostic => diagnostic.Line == caret.Line && caret.Column >= diagnostic.Column
            && caret.Column < diagnostic.Column + diagnostic.Length)
            .SelectMany(diagnostic => diagnostic.RelatedLocations ?? []).Distinct().ToArray();
    }

    private void GoToRelatedDiagnostic()
    {
        var related = RelatedDiagnosticsAtCaret();
        HexDocument of = context.Documents.Current;
        if (related.Count == 0 || !linters.TryGetValue(of, out var server)) return;
        var request = CaptureNavigation(of, server);
        void Open(UICodeDiagnosticLocation location)
        {
            if (NavigationCurrent(request) && File.Exists(location.File))
                context.Documents.OpenAt(location.File, location.Line, location.Column);
        }
        if (related.Count == 1) Open(related[0]);
        else context.Windows.Choose(context.Strings.Said("code_server.related_diagnostic"), string.Empty,
            related.Select(location => ($"{location.Message} ? {Path.GetFileName(location.File)}:{location.Line + 1}", (Action)(() => Open(location)))).ToList());
    }
}
