using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using System.Numerics;

namespace Hex.Dawn;

// Getting started on the welcome: a short list of what's worth setting up in Hex that ticks itself off. Each step is done
// when the thing is so (a look picked, an agent installed, Persistent Process on, the workspace's language servers set up)
// or once it's been seen to from here, kept in the settings as welcome_done. A row is its tick, its name (which opens it
// in the settings), how it is now, and the switch or the button that does it there and then. How many are done is over
// them, and Hide folds the list to that line; once they're all done it's folded to it by itself.
internal sealed partial class DawnScene
{
    // What Getting started said last, by the language servers' version, made again when it moves
    private int gettingSetupShown = -1;

    // Shown again with Show this session, all of it done or not
    private bool gettingShownAnyway;

    /// <summary>The steps of Getting started there are, in order: the ones about agents away with No AI.</summary>
    private string[] GettingSteps => HexAi.Off
        ? ["look", "size", "vs_keys", "persistent", "servers"]
        : ["look", "size", "agents", "vs_keys", "persistent", "servers"];

    /// <summary>Whether a step of Getting started is done, by the thing itself or by having been seen to from the list.</summary>
    private bool GettingDone(string step) => Marked(step) || step switch
    {
        "look" => !string.Equals(settings.Look, HexSettings.USUAL_LOOK, StringComparison.Ordinal),
        "size" => settings.InterfaceSize != 1.0f,
        "agents" => AgentSystems().Any(system => StateOf(system) != AgentStates.Missing),
        "vs_keys" => settings.VisualStudioKeysChosen,
        "persistent" => settings.PersistentProcess,
        "servers" => ServersLeft() is (> 0, 0, false),
        _ => false
    };

    // Whether a step was seen to from the list, kept in the settings
    private bool Marked(string step) => settings.WelcomeDone.Split(',').Contains(step, StringComparer.Ordinal);

    /// <summary>A step ticked off, it was seen to from the list.</summary>
    private void MarkGettingDone(string step)
    {
        if (Marked(step))
            return;
        settings.WelcomeDone = settings.WelcomeDone.Length == 0 ? step : settings.WelcomeDone + "," + step;
        SaveSettings();
    }

    // The workspace's language servers: how many languages, how many steps left before they work, and whether they're
    // still being looked at
    private (int Languages, int Left, bool Checking) ServersLeft()
    {
        List<HexServerEntry> here = languages.WorkspaceEntries();
        int left = 0;
        bool checking = false;
        foreach (HexServerEntry entry in here)
        {
            var steps = languages.SetupStepsFor(entry);
            left += HexServerSetup.LeftOf(steps);
            checking |= steps.Any(step => step.State == SetupState.Checking);
        }
        return (here.Count, left, checking);
    }

    /// <summary>
    /// Getting started made at a width: its head with how many are done and Hide or Show, then the steps unless it's
    /// folded, then a line for somebody coming from VS Code.
    /// </summary>
    private void FillGettingStarted(StackPanel list, float width)
    {
        foreach (UIComponent child in list.Children.ToArray())
            list.Remove(child);
        gettingSetupShown = languages.SetupVersion;

        string[] steps = GettingSteps;
        bool[] ticked = [.. steps.Select(GettingDone)];
        int done = ticked.Count(tick => tick);
        bool all = done == steps.Length;
        bool folded = settings.WelcomeChecklistHidden || (all && !gettingShownAnyway);
        string count = steps.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string doneCount = done.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // The head: the name and how many are done on the left, Hide or Show on the right, the bar of them under it
        var head = list.Add(new Panel { Size = new Vector2(width, 28), Color = Vector4.Zero, Anchor = Origin.Left, Pivot = Origin.Left });
        var named = head.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 10, Anchor = Origin.Left, Pivot = Origin.Left });
        named.Add(new Label { Text = Said("welcome.getting_started"), TextScale = 0.17f, Bold = true, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
        named.Add(new Label
        {
            Text = all ? Said("welcome.getting_all_done") : Said("welcome.getting_progress", ("done", doneCount), ("count", count)),
            TextScale = 0.13f, Dim = true, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left
        });
        head.Add(new Button
        {
            Label = Said(folded ? "welcome.getting_show" : "welcome.getting_hide"),
            Tooltip = Said(folded ? "welcome.getting_show_tip" : "welcome.getting_hide_tip"),
            Style = "button_flat", LabelScale = 0.135f, Animated = false, Size = new Vector2(64, 26),
            Anchor = Origin.Right, Pivot = Origin.Right,
            OnPressed = () =>
            {
                settings.WelcomeChecklistHidden = !folded;
                gettingShownAnyway = folded;
                SaveSettings();
                RefillGettingStarted();
            }
        });
        // A bar of a piece for each step, in the success colour for one that's done and the track's for one that isn't, in
        // the steps' order so it says which as well as how many
        const float GAP = 4.0f;
        var bar = list.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = GAP, Size = new Vector2(width, 4), Anchor = Origin.Left, Pivot = Origin.Left });
        float piece = MathF.Floor((width - GAP * (steps.Length - 1)) / steps.Length);
        for (int i = 0; i < steps.Length; i++)
        {
            bar.Add(new Panel
            {
                Size = new Vector2(piece, 4), Radius = 2.0f, Anchor = Origin.Left, Pivot = Origin.Left,
                Color = ticked[i] ? compositor.Skin?.Palette.Success ?? Vector4.One : compositor.Skin?.TrackColor ?? Vector4.Zero
            });
        }
        if (folded)
            return;
        list.Add(new Spacer { Size = new Vector2(width, 6) });

        for (int i = 0; i < steps.Length; i++)
            GettingRow(list, width, steps[i], ticked[i]);

        // Coming from VS Code, its settings brought over; and without agents at all, No AI
        list.Add(new Spacer { Size = new Vector2(width, 6) });
        list.Add(new Label
        {
            Text = Said("welcome.getting_import_line"), Markdown = true, TextScale = 0.135f, Dim = true, Wrap = true,
            Size = new Vector2(width, 0), Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left,
            OnLink = _ => ImportVsCodeSettings()
        });
        if (!HexAi.Off)
        {
            list.Add(new Label
            {
                Text = Said("welcome.getting_no_ai_line"), Markdown = true, TextScale = 0.135f, Dim = true, Wrap = true,
                Size = new Vector2(width, 0), Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left,
                OnLink = _ => OpenSettingAt("no_ai")
            });
        }
    }

    // A step's row: its tick, its name over how it is now, and its switch or button at the right
    private void GettingRow(StackPanel list, float width, string step, bool done)
    {
        (string? key, string said, UIComponent control) = step switch
        {
            "look" => ("look", Said("welcome.getting_look_about", ("look", HexLooks.Current(settings, terminals.Settings.Theme)?.Name ?? settings.Look)),
                GettingAction(step, done ? "change" : "pick", () => OpenSettingAt("look"))),
            "size" => ("interface_size", Said("welcome.getting_size_about", ("size", DescribeSize(compositor.Scale))),
                GettingAction(step, "change", () => OpenSettingAt("interface_size"))),
            "agents" => ((string?)null, AgentsSaid(),
                GettingAction(step, done ? "open" : "set_up", () => ShowFace(Hex.Shell.HexFace.Noon))),
            "vs_keys" => ("visual_studio_keys", Said(settings.VisualStudioKeys ? "welcome.getting_vs_keys_on" : "welcome.getting_vs_keys_about"),
                GettingSwitch(step, "visual_studio_keys")),
            "persistent" => ("persistent_process", Said("welcome.getting_persistent_about"), GettingSwitch(step, "persistent_process")),
            _ => ((string?)null, ServersSaid(), GettingAction(step, ServersLeft().Left > 0 ? "set_up" : "open", () => OpenLanguageServers()))
        };

        // The switch or the button at the right, or under the words where the column is too narrow for both side by side
        float controlWidth = control.Size.X;
        bool under = width - 28 - controlWidth - 16 < 150;
        float words = under ? width - 28 : width - 28 - controlWidth - 16;
        var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(width, 0) };

        // The tick: a filled check in the success colour once it's done, an empty circle before. A step done is quieter
        Horizon.UI.Skinning.UISkin? skin = compositor.Skin;
        row.Add(new Symbol
        {
            Icon = done ? "pass-filled" : "circle-large-outline",
            Color = done ? skin?.Palette.Success : skin?.Palette.DimText,
            Size = new Vector2(20, 24), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft,
            Tooltip = Said(done ? "welcome.getting_done_tip" : "welcome.getting_todo_tip")
        });
        var text = row.Add(new StackPanel { Spacing = 0, Size = new Vector2(words, 0), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        text.Add(new Button
        {
            Label = Said("welcome.getting_" + step),
            Style = "button_flat",
            LabelScale = 0.15f,
            LabelColor = done ? skin?.Palette.DimText : null,
            Align = Origin.Left,
            Animated = false,
            Size = new Vector2(words, 24),
            Tooltip = key is not null ? Said("welcome.getting_in_settings") : string.Empty,
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft,
            OnPressed = key is not null ? () => OpenSettingAt(key) : control is Button button ? button.OnPressed : null
        });
        // Under the name's words, which start after the flat button's padding
        var about = text.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 0, Size = new Vector2(words, 0), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        about.Add(new Spacer { Size = new Vector2(skin?.ButtonPadding.Left ?? 12, 1) });
        about.Add(new Label { Text = said, Dim = true, TextScale = 0.125f, Wrap = true, Size = new Vector2(words - 24, 0), Align = Origin.TopLeft, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        if (under)
        {
            var below = text.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 0, Size = new Vector2(words, 0), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
            below.Add(new Spacer { Size = new Vector2(MathF.Max(0, (skin?.ButtonPadding.Left ?? 12) - 4), 1) });
            below.Add(control);
        }
        else
            row.Add(control);
        list.Add(row);
        list.Add(new Spacer { Size = new Vector2(width, 4) });
    }

    // A setting that's on or off as a switch, changed the way the settings change it, the step ticked off with it
    private UIComponent GettingSwitch(string step, string key)
    {
        bool on = SettingFlag(key);
        var toggle = new ToggleButton(on ? Said("extensions_window.on") : Said("extensions_window.off"), on)
        {
            Size = new Vector2(84, 30), Style = "switch", Anchor = Origin.TopLeft, Pivot = Origin.TopLeft,
            Tooltip = Said("welcome.getting_" + step)
        };
        toggle.OnPressed = () =>
        {
            SetSettingFlag(key, toggle.State);
            if (step == "vs_keys")
                settings.VisualStudioKeysChosen = true;
            MarkGettingDone(step);
            RefillGettingStarted();
        };
        return toggle;
    }

    // A step's button: the thing done and the step ticked off
    private Button GettingAction(string step, string label, Action pressed) => new()
    {
        Label = Said("welcome.getting_" + label),
        Style = "button_flat",
        LabelScale = 0.14f,
        Size = new Vector2(84, 30),
        Animated = false,
        Anchor = Origin.TopLeft,
        Pivot = Origin.TopLeft,
        OnPressed = () =>
        {
            // The servers' step is done by the servers being set up, not by a look at the window
            if (step != "servers")
                MarkGettingDone(step);
            pressed();
            RefillGettingStarted();
        }
    };

    // The agents there are to run, which are installed, in words
    private string AgentsSaid()
    {
        HexAgentProfile[] systems = AgentSystems();
        List<string> ready = [.. systems.Where(system => StateOf(system) != AgentStates.Missing).Select(system => system.Name)];
        // No agent system known at all (none of their extensions here yet) says where they come from, where the names
        // it would list were nothing and it read "Install  to start one"
        return ready.Count > 0 ? Said("welcome.getting_agents_ready", ("agents", Joined(ready)))
            : systems.Length == 0 ? Said("welcome.getting_agents_unknown")
            : Said("welcome.getting_agents_none", ("agents", Joined([.. systems.Select(system => system.Name)])));
    }

    // The workspace's languages and what's left before they work, in words
    private string ServersSaid()
    {
        List<HexServerEntry> here = languages.WorkspaceEntries();
        if (here.Count == 0)
            return Said("welcome.getting_servers_none");
        var steps = here.Select(entry => (Entry: entry, Steps: languages.SetupStepsFor(entry))).ToList();
        if (steps.Any(pair => pair.Steps.Any(step => step.State == SetupState.Checking)))
            return Said("welcome.getting_servers_checking");
        int left = steps.Sum(pair => HexServerSetup.LeftOf(pair.Steps));
        var waiting = steps.Where(pair => HexServerSetup.LeftOf(pair.Steps) > 0).Select(pair => languages.LanguageNameOf(pair.Entry)).ToList();
        return left > 0 ? Said("welcome.getting_servers_left", ("count", left.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("languages", Joined(waiting)))
            : Said("welcome.getting_servers_ready", ("languages", Joined([.. here.Select(languages.LanguageNameOf)])));
    }

    /// <summary>Every welcome page's Getting started made again at the width it was, after something it says changed.</summary>
    private void RefillGettingStarted()
    {
        foreach (WelcomeSizing sizing in welcomeSizes)
            if (sizing.GettingWidth > 0)
                FillGettingStarted(sizing.Getting, sizing.GettingWidth);
    }

    // The settings' source a key is one of, the one the settings window would change it through. The editor's own are looked
    // in first and the parts' sources made only for a key that isn't one of those
    private ISettingsSource? SettingSourceOf(string key, ISettingsSource? editor = null)
    {
        editor ??= new EditorSource(this);
        if (Has(editor))
            return editor;

        foreach (var added in settingSources)
        {
            ISettingsSource source = added.Source();
            if (Has(source))
                return source;
        }

        return null;

        bool Has(ISettingsSource source)
        {
            foreach (UISetting setting in source.Settings)
            {
                if (setting.Key == key)
                    return true;
            }

            return false;
        }
    }

    // Whether a setting that's on or off is on, as the settings window reads it
    private bool SettingFlag(string key, ISettingsSource? editor = null) =>
        SettingSourceOf(key, editor)?.Get(key) is Horizon.HIDL.Runtime.BooleanValue { Value: true };

    // A setting that's on or off changed through its source, applied and saved the way the settings window does it
    private void SetSettingFlag(string key, bool on)
    {
        if (SettingSourceOf(key) is not { } source)
            return;
        source.Set(key, new Horizon.HIDL.Runtime.BooleanValue(on));
        SaveSettings();
        SettingsPage?.Refresh(key);
    }

    /// <summary>The settings, brought up scrolled to one setting with it picked out.</summary>
    private void OpenSettingAt(string key)
    {
        OpenSettings(null);
        settingsView?.Reveal(key);
    }
}
