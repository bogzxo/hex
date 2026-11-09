namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    // csharp-ls currently returns either source or metadata for a symbol. Exercise a mixed response using a real
    // metadata location from that server plus a project declaration, without inventing a virtual URI or its source.
    internal void ShowMixedDefinitionsForTest(HexDocument of, string projectFile)
    {
        if (!Serves(of, "definitionProvider", out var server, out string path)) return;
        var request = CaptureNavigation(of, server);
        var caret = of.Editor.Document.Caret;
        WhenNavigating(request, server.Definition(path, caret.Line, caret.Column), places =>
        {
            if (places.FirstOrDefault(place => !place.OnDisk) is not { Path: not null } metadata) return;
            ShowDefinitionPlaces("Mixed definition destinations", server,
                [new(projectFile, 0, 6, 0, 17), metadata], "Version", request.Workspace, alignName: false);
        });
    }
}
