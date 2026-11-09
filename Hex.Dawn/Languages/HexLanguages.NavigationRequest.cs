using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    private long navigationNumber;
    private sealed record NavigationRequest(HexDocument Document, LanguageServer Server, LanguageServer? DocumentServer, string? Path, int Version,
        UITextPosition Caret, int Workspace, long Number);

    private NavigationRequest CaptureNavigation(HexDocument document, LanguageServer server) =>
        new(document, server, linters.GetValueOrDefault(document), document.Path, document.Editor.Document.Version, document.Editor.Document.Caret,
            editWorkspaceGeneration, ++navigationNumber);

    private bool NavigationCurrent(NavigationRequest request) => request.Number == navigationNumber
        && request.Workspace == editWorkspaceGeneration && context.Documents.Current == request.Document
        && request.Document.Path == request.Path && request.Document.Editor.Document.Version == request.Version
        && request.Document.Editor.Document.Caret == request.Caret
        && linters.GetValueOrDefault(request.Document) == request.DocumentServer && request.Server.Failure is null;

    private void WhenNavigating<T>(NavigationRequest request, Task<T> answer, Action<T> then) =>
        When(answer, value => { if (NavigationCurrent(request)) then(value); });
}
