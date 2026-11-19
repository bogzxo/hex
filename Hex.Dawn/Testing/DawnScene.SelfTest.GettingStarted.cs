using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of the welcome page's Getting started: its groups, a row each for the settings nobody finds by looking
// with how each is now, a switch that changes a setting there and then (and saves it), a name that opens the settings at
// it, and the language servers' row opening the Language Servers window.
internal sealed partial class DawnScene
{
    private void TestGettingStarted(HexPointerScript test)
    {
        string[] rows = ["spelling", "format", "servers", "look", "vs_keys", "import", "no_ai"];
        string[] groups = ["writing", "languages", "look_keys", "ai"];
        IEnumerable<UIComponent> All() => welcome is null ? [] : Descendants(welcome);
        Button? Title(string name) => All().OfType<Button>().FirstOrDefault(button => button.Label == Said("welcome.getting_" + name));
        // A row's switch, the one beside its name
        ToggleButton? SwitchOf(string name) => Title(name)?.Parent?.Parent is StackPanel row ? row.Children.OfType<ToggleButton>().FirstOrDefault() : null;
        bool spelling = false;

        test.Run(() => welcomeWanted = true);
        test.Settle(0.4f);
        test.Check("getting started: the welcome has it, in its groups, a row for each setting", () =>
            welcome is { Visible: true } && groups.All(group => All().OfType<Label>().Any(label => label.Text == Said("welcome.getting_" + group)))
            && rows.All(name => Title(name) is not null));
        test.Check("getting started: a setting's name says it opens in the settings", () =>
            Title("spelling") is { Tooltip.Length: > 0, OnPressed: not null } && Title("no_ai") is { Tooltip.Length: > 0 });
        test.Check("getting started: the settings that are on or off have a switch that says how they are", () =>
            SwitchOf("spelling") is { } toggle && toggle.State == SettingFlag("spell_check") && SwitchOf("format") is not null && SwitchOf("vs_keys") is not null && SwitchOf("no_ai") is not null);
        test.Check("getting started: the language servers' row says how they are", () =>
            All().OfType<Label>().Any(label => label.Text.StartsWith(Said("welcome.getting_servers_none")[..8], StringComparison.Ordinal) || label.Text.Contains("set up", StringComparison.OrdinalIgnoreCase)
                || label.Text.StartsWith("Ready", StringComparison.Ordinal) || label.Text.StartsWith("Checking", StringComparison.Ordinal)));
        test.Run(() => Shot("welcome-getting-started"));

        // The switch changes the setting itself, and the row is made again saying so
        test.Run(() =>
        {
            spelling = SettingFlag("spell_check");
            ToggleButton toggle = SwitchOf("spelling")!;
            toggle.State = !spelling;
            toggle.OnPressed!();
        });
        test.Settle(0.2f);
        test.Check("getting started: Check spelling's switch turns spelling the other way", () => SettingFlag("spell_check") != spelling && SwitchOf("spelling") is { } toggle && toggle.State != spelling);
        test.Run(() => SetSettingFlag("spell_check", spelling));

        test.Run(() => Title("spelling")!.OnPressed!());
        test.Check("getting started: Check spelling's name opens the settings at it", () => SettingsPage is not null && SettingsPage.Settings.Any(setting => setting.Key == "spell_check"));
        test.Run(CloseSettings);
        test.Run(() => (Title("servers")?.Parent?.Parent as StackPanel)!.Children.OfType<Button>().Last().OnPressed!());
        test.Check("getting started: the language servers' button opens the Language Servers window", () => serversWindow is not null);
        test.Run(() =>
        {
            serversWindow?.Close();
            welcomeWanted = false;
        });
        test.Wait();
    }
}
