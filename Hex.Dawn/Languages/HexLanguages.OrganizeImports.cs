namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    private bool CanOrganizeImports() => !context.Documents.Current.Editor.ReadOnly && linters.TryGetValue(context.Documents.Current, out var server)
        && server.Failure is null && server.OffersActionKind("source.organizeImports");

    private void OrganizeImports()
    {
        HexDocument of = context.Documents.Current;
        if (!Serves(of, "codeActionProvider", out var server, out var path)) return;
        FlushEditsToServer(server);
        var snapshot = CaptureEdits();
        var request = CaptureNavigation(of, server);
        WhenNavigating(request, server.OrganizeImports(path, ShownName(server).Equals("pyright-langserver", StringComparison.OrdinalIgnoreCase)), action =>
        {
            if (!EditsCurrent(snapshot, [path])) return;
            if (action is null) context.Notices.Say(context.Strings.Said("code_server.no_organize_imports"));
            else TakeAction(server, action, snapshot, path);
        });
    }
}
