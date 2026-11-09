namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    private bool CanSwitchHeaderSource(HexDocument of) => CanServe(of, "hoverProvider")
        && linters.TryGetValue(of, out var server)
        && (ShownName(server).Equals("clangd", StringComparison.OrdinalIgnoreCase)
            || ShownName(server).StartsWith("clangd-", StringComparison.OrdinalIgnoreCase));

    private void SwitchHeaderSource()
    {
        HexDocument of = context.Documents.Current;
        if (!CanSwitchHeaderSource(of) || !Serves(of, "hoverProvider", out var server, out var path)) return;
        WhenNavigating(CaptureNavigation(of, server), server.SwitchSourceHeader(path), target =>
        {
            if (target is not null && File.Exists(target)) context.Documents.OpenAt(target, 0, 0);
            else context.Notices.Say(context.Strings.Said("code_server.no_header_source"));
        });
    }
}
