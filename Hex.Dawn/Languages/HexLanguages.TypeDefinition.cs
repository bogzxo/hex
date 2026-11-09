namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    private void GoToTypeDefinition()
    {
        var of = context.Documents.Current;
        if (!Serves(of, "typeDefinitionProvider", out var server, out string path)) return;
        var caret = of.Editor.Document.Caret;
        string word = WordAtCaret(of);
        var request = CaptureNavigation(of, server);
        WhenNavigating(request, server.TypeDefinition(path, caret.Line, caret.Column), places =>
        {
            if (places.Count == 0)
                context.Notices.Say(context.Strings.Said("code_server.no_type_definition", ("word", word)));
            else if (places.Count > 1)
                ShowDefinitionPlaces(context.Strings.Said("code_server.type_definitions_title", ("word", word), ("count", places.Count.ToString())), server, places, word, request.Workspace, alignName: false);
            else if (!places[0].OnDisk)
                OpenSource(server, places[0], word, request, alignName: false);
            else
                context.Documents.OpenAt(places[0].Path, places[0].Line, places[0].Character);
        });
    }
}
