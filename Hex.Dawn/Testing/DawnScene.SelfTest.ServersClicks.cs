using System.Numerics;

using Hex.Core;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of the Language Servers window taking every click the first time. The list down the left and a
// language's Setup and Settings are clicked once each, with the pointer, and the page has to be the one clicked after
// that one click: with the code's box holding the keyboard first, and with what the window shows changing while the
// button is held down (a server or a tool found as it's pressed), which made the window again under the pointer and
// lost the click.
internal sealed partial class DawnScene
{
    private void TestServersClicks(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "servers_clicks");
        string python = Path.Combine(folder, "main.py");

        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(python, "print('hi')\n");
            explorer.OpenFolder(folder);
            Open(python);
            OpenLanguageServers();
        });
        test.WaitFor(() => !languages.SetupBusy, 60.0f);
        test.Settle(0.4f);

        Button? Nav(string label) => serversNav is null ? null
            : Descendants(serversNav).OfType<Button>().FirstOrDefault(button => button.Label.StartsWith(label, StringComparison.Ordinal));
        Button? OnPage(string label) => serversPage is null ? null
            : Descendants(serversPage).OfType<Button>().FirstOrDefault(button => button.Label == label);
        Func<Vector2> At(Func<Button?> find) => () => find() is { Module: not null } button ? button.Module.ToWorld(button.Bounds.Center) : Vector2.Zero;

        string pythonName = string.Empty;
        test.Run(() => pythonName = languages.LanguageNameOf(languages.ServerEntry("python")!));

        // Each once, the keyboard in the code first
        test.Run(() => code.Focus());
        test.Click(At(() => Nav(Said("server_window.all"))));
        test.Check("servers clicks: one click on All languages shows it, with the code holding the keyboard before", () => serversShown == ServersPage.All);
        test.Click(At(() => Nav(Said("server_window.advanced"))));
        test.Check("servers clicks: one click on Advanced shows it", () => serversShown == ServersPage.Advanced);
        test.Click(At(() => Nav(Said("server_window.overview"))));
        test.Check("servers clicks: one click on Overview shows it", () => serversShown == ServersPage.Overview);
        test.Click(At(() => Nav(pythonName)));
        test.Check("servers clicks: one click on Python in the list shows its page, at Setup", () => serversShown == ServersPage.Language && serversLanguage == "python" && !serversSettingsTab);
        test.Click(At(() => OnPage(Said("server_window.settings_tab"))));
        test.Check("servers clicks: one click on Settings shows Python's settings", () => serversShown == ServersPage.Language && serversSettingsTab);
        test.Click(At(() => OnPage(Said("server_window.setup_tab"))));
        test.Check("servers clicks: one click on Setup shows its steps again", () => serversShown == ServersPage.Language && !serversSettingsTab);

        // What the window shows changing while a button is held, the tools and servers looked for again as it's pressed
        void ClickWhileChanging(Func<Vector2> target)
        {
            test.Press(target);
            test.Run(() => languages.ProbeSetup());
            test.Wait();
            test.Release(target);
            test.Wait();
        }

        ClickWhileChanging(At(() => Nav(Said("server_window.all"))));
        test.Check("servers clicks: All languages clicked once while what's found changes under the press still shows it", () => serversShown == ServersPage.All);
        test.WaitFor(() => !languages.SetupBusy, 60.0f);
        ClickWhileChanging(At(() => Nav(pythonName)));
        test.Check("servers clicks: Python clicked once while what's found changes shows its page", () => serversShown == ServersPage.Language && serversLanguage == "python");
        test.WaitFor(() => !languages.SetupBusy, 60.0f);
        ClickWhileChanging(At(() => OnPage(Said("server_window.settings_tab"))));
        test.Check("servers clicks: Settings clicked once while what's found changes shows the settings", () => serversShown == ServersPage.Language && serversSettingsTab);
        test.WaitFor(() => !languages.SetupBusy, 60.0f);
        ClickWhileChanging(At(() => OnPage(Said("server_window.setup_tab"))));
        test.Check("servers clicks: Setup clicked once while what's found changes shows the steps", () => serversShown == ServersPage.Language && !serversSettingsTab);

        // Made again on its own while nothing's held, what was found said once the press is over
        test.WaitFor(() => !languages.SetupBusy && serversVersion == languages.SetupVersion, 60.0f);
        test.Check("servers clicks: once let go, the window catches up with what was found", () => serversVersion == languages.SetupVersion);

        test.Run(() => serversWindow?.Close());
        test.Run(() => explorer.CloseWorkspace());
    }
}
