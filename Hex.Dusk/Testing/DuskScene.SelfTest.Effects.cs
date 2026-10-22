using Horizon.UI.Components;

namespace Hex.Dusk;

// The self test of the effects in the terminal. As they come (the Fluid Glass look) the popups are on glass and nothing
// is thrown, sparks off the cursor as it's typed along a prompt with them all, a burst when a command of a couple of
// seconds goes well, and nothing left flying or frosted once they and the glass are off (the Flat look).
internal sealed partial class DuskScene
{
    private void TestEffects(DuskScript test)
    {
        TerminalView View() => panel.FocusedView!;

        test.Check("effects: as they come the popups are on glass and nothing flies", () => compositor.PopupGlass is not null && sparks.Count == 0);

        test.Run(() =>
        {
            effects = "all";
            effectSparks = effectGlass = true;
            ApplyEffects();
        });
        test.Check("effects: on, the popups sit on glass", () => compositor.PopupGlass is not null);

        test.Run(() => Run("palette"));
        test.WaitFor(() => compositor.GlassDrawn.Panes > 0, 3.0f);
        test.Check("effects: and the palette is a pane of it over the terminals", () => compositor.GlassDrawn.Panes > 0);

        // The palette's commands picked last over the rest, thinner (ListBox.Lead), kept in the self test's own home
        static ListBox? Listed(UIComponent under) => under as ListBox ?? under.Children.Select(Listed).FirstOrDefault(found => found is not null);
        string picked = string.Empty;
        test.Run(() =>
        {
            RecentCommands.Clear();
            picked = Listed(chrome.Dialog!)!.Items[2];
            chrome.Dialog?.Close();
        });
        test.Wait();
        test.Run(() =>
        {
            RecentCommands.Remember(picked);
            Run("palette");
        });
        test.Wait();
        test.Check("palette: the command picked last is over the rest, on its own", () =>
            chrome.Dialog is { } shown && Listed(shown) is { Lead: 1 } list && list.Items[0] == picked && list.Items.Count(item => item == picked) == 1);
        test.Run(() =>
        {
            RecentCommands.Clear();
            chrome.Dialog?.Close();
        });
        test.Wait();

        test.WaitFor(() => View().Screen.AtPrompt, 10.0f);
        test.Run(() => View().Focus());
        test.Wait();
        test.Run(() => View().Send("e"));
        test.Wait();
        test.Run(() => View().Send("cho dusk-sparks"));
        test.WaitFor(() => sparks.Count > 0, 3.0f);
        test.Check("effects: all, typing at the prompt throws sparks off the cursor", () => sparks.Count > 0);
        test.Run(() => View().Send("\r"));
        test.WaitFor(() => View().Screen.AtPrompt && sparks.Count == 0, 5.0f);

        test.Run(() => View().Send(OperatingSystem.IsWindows() ? "Start-Sleep -Milliseconds 2200; 'slept'\r" : "sleep 2.2; echo slept\r"));
        test.WaitFor(() => sparks.Count > 20, 8.0f);
        test.Check("effects: and a command that took a while and went well throws a burst", () => sparks.Count > 20);

        test.Run(() =>
        {
            effects = "none";
            effectGlass = false;
            ApplyEffects();
        });
        test.Wait();
        test.Check("effects: and off with the glass nothing flies and nothing's frosted", () => sparks.Count == 0 && compositor.PopupGlass is null);
    }
}
