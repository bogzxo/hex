using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of the welcome page's Getting started: a short list of steps with how many are done over them, each
// ticked off once its thing is so or it's been seen to from the list (and kept so in the settings), a switch that changes
// a setting there and then, a name that opens the settings at it, the language servers' row opening the Language
// Servers window, and Hide folding it to its one line.
internal sealed partial class DawnScene
{
    private void TestGettingStarted(HexPointerScript test)
    {
        string[] steps = GettingSteps;
        IEnumerable<UIComponent> All() => welcome is null ? [] : Descendants(welcome);
        Button? Title(string name) => All().OfType<Button>().FirstOrDefault(button => button.Label == Said("welcome.getting_" + name));
        // A row's switch or button, the one beside its name or under it on a narrow page
        StackPanel? RowOf(string name) => Title(name)?.Parent?.Parent as StackPanel;
        ToggleButton? SwitchOf(string name) => RowOf(name) is { } row ? Descendants(row).OfType<ToggleButton>().FirstOrDefault() : null;
        Button? ActionOf(string name) => RowOf(name) is { } row ? Descendants(row).OfType<Button>().LastOrDefault(button => button != Title(name)) : null;
        Symbol? TickOf(string name) => RowOf(name)?.Children.OfType<Symbol>().FirstOrDefault();
        bool Says(string text) => All().OfType<Label>().Any(label => label.Visible && label.Text == text);
        string Count(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool persistent = false, hidden = false;
        string doneBefore = string.Empty;
        float sizeBefore = 1.0f;

        // In Dawn whatever ran before left it in, the welcome is Dawn's
        test.Run(() =>
        {
            ShowFace(Hex.Shell.HexFace.Dawn);
            welcomeWanted = true;
            doneBefore = settings.WelcomeDone;
            hidden = settings.WelcomeChecklistHidden;
            persistent = settings.PersistentProcess;
            sizeBefore = settings.InterfaceSize;
            settings.WelcomeDone = string.Empty;
            settings.WelcomeChecklistHidden = false;
            gettingShownAnyway = true;
            // What a part before left open closed, the welcome is over the layout Hex started with alone
            CloseAll();
            RefillGettingStarted();
        });
        test.Settle(0.6f);
        test.Check("getting started: the welcome has it, a row for each step and no more than six of them", () =>
            welcome is { Visible: true } && steps.Length <= 6 && steps.All(name => Title(name) is not null));
        test.Check("getting started: over the steps it says how many are done", () =>
            Says(Said("welcome.getting_progress", ("done", Count(steps.Count(GettingDone))), ("count", Count(steps.Length))))
            || (steps.All(GettingDone) && Says(Said("welcome.getting_all_done"))));
        test.Check("getting started: each step's tick says whether it's done", () =>
            steps.All(name => TickOf(name) is { } tick && tick.Icon == (GettingDone(name) ? "pass-filled" : "circle-large-outline")));
        test.Check("getting started: a setting's name says it opens in the settings", () =>
            Title("look") is { Tooltip.Length: > 0, OnPressed: not null } && Title("persistent") is { Tooltip.Length: > 0 });
        test.Check("getting started: the steps that are on or off have a switch that says how they are", () =>
            SwitchOf("persistent") is { } toggle && toggle.State == SettingFlag("persistent_process") && SwitchOf("vs_keys") is not null);
        test.Check("getting started: the language servers' row says how they are", () =>
            RowOf("servers") is { } row && Descendants(row).OfType<Label>().Any(label => label.Text == ServersSaid()));
        test.Check("getting started: the line for somebody coming from VS Code is under the steps", () => Says(Said("welcome.getting_import_line")));
        test.Run(() => Shot("welcome-getting-started"));

        // The switch changes the setting itself, and the step is ticked off with it
        test.Run(() =>
        {
            ToggleButton toggle = SwitchOf("persistent")!;
            toggle.State = !persistent;
            toggle.OnPressed!();
        });
        test.Settle(0.2f);
        test.Check("getting started: Persistent Process's switch turns it the other way and ticks the step off", () =>
            settings.PersistentProcess != persistent && SwitchOf("persistent") is { } toggle && toggle.State != persistent
            && settings.WelcomeDone.Split(',').Contains("persistent") && TickOf("persistent")?.Icon == "pass-filled");
        test.Run(() => SetSettingFlag("persistent_process", persistent));

        // Changed somewhere else, the interface size here from the settings, the step's ticked off all the same
        test.Run(() =>
        {
            SetInterfaceSize(1.25f, remember: true);
            SaveSettings();
        });
        test.Settle(0.4f);
        test.Check("getting started: the interface size changed elsewhere ticks its step off and says what it is now", () =>
            GettingDone("size") && TickOf("size")?.Icon == "pass-filled" && RowOf("size") is { } row
            && Descendants(row).OfType<Label>().Any(label => label.Text.Contains(DescribeSize(1.25f), StringComparison.Ordinal)));
        test.Run(() =>
        {
            SetInterfaceSize(sizeBefore, remember: true);
            SaveSettings();
        });
        test.Settle(0.4f);

        test.Run(() => Title("persistent")!.OnPressed!());
        test.Check("getting started: a step's name opens the settings at it", () => SettingsPage is not null && SettingsPage.Settings.Any(setting => setting.Key == "persistent_process"));
        test.Run(CloseSettings);
        test.Run(() => ActionOf("servers")!.OnPressed!());
        test.Check("getting started: the language servers' button opens the Language Servers window", () => serversWindow is not null);
        test.Run(() => serversWindow?.Close());
        test.Settle(0.2f);
        test.Check("getting started: a look at the servers' window alone doesn't tick their step off", () => !settings.WelcomeDone.Split(',').Contains("servers"));

        // Hide folds it to its one line and is kept, Show brings the steps back
        test.Run(() => All().OfType<Button>().First(button => button.Label == Said("welcome.getting_hide")).OnPressed!());
        test.Settle(0.2f);
        test.Check("getting started: Hide folds it to the line saying how many are done, and that's kept", () =>
            settings.WelcomeChecklistHidden && Title("look") is null && All().OfType<Button>().Any(button => button.Label == Said("welcome.getting_show")));
        test.Run(() => Shot("welcome-getting-started-folded"));
        test.Run(() => All().OfType<Button>().First(button => button.Label == Said("welcome.getting_show")).OnPressed!());
        test.Settle(0.2f);
        test.Check("getting started: and Show brings the steps back", () => !settings.WelcomeChecklistHidden && Title("look") is not null);

        test.Run(() =>
        {
            settings.WelcomeDone = doneBefore;
            settings.WelcomeChecklistHidden = hidden;
            gettingShownAnyway = false;
            SaveSettings();
            welcomeWanted = false;
        });
        test.Wait();
    }
}
