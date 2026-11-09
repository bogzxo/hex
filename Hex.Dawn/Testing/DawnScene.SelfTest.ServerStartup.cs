using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestServerStartup(HexPointerScript test)
    {
        LanguageServer server = null!;
        string reason = "startup fixture closed before initialize";
        int changed = 0;
        test.Run(() =>
        {
            server = LanguageServer.Over("python", "python", testFolder, new MemoryStream(), new MemoryStream());
            changed = languages.ServersChanged;
        });
        test.WaitFor(() => server.Failure is not null, 5);
        test.Check("server startup: closed transport fails before initialization", () => server.Failure is not null && !server.IsReady);
        test.Run(() => languages.ServerStopped(server, reason));
        test.Check("server startup: failure offers retry and settings", () => toasts.Cards.Any(card =>
            card.Text.Contains(reason, StringComparison.Ordinal)
            && card.Actions.Any(action => action.Label == Said("languages.start_again"))
            && card.Actions.Any(action => action.Label == Said("code_server.open_settings"))));
        test.Settle(0.3f);
        test.Check("server startup: does not enter an automatic restart loop", () => languages.ServersChanged == changed + 1);
        test.Run(() => toasts.Cards.First(card => card.Text.Contains(reason, StringComparison.Ordinal))
            .Actions.First(action => action.Label == Said("languages.show_server_output")).Pressed());
        test.Check("server startup: output action explains when stderr is empty", () =>
            said.Any(row => row.Text == Said("languages.no_server_output")));
        test.Run(() => toasts.Cards.First(card => card.Text.Contains(reason, StringComparison.Ordinal))
            .Actions.First(action => action.Label == Said("code_server.open_settings")).Pressed());
        test.Check("server startup: settings action opens settings", () => SettingsPage is not null);
        test.Run(() =>
        {
            CloseSettings();
            foreach (var card in toasts.Cards.Where(card => card.Text.Contains(reason, StringComparison.Ordinal))) card.Close();
            server.Dispose();
        });
    }
}
