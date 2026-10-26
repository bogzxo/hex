using System.Collections.Concurrent;

using Horizon.Core.Threading;

namespace Hex.Dawn;

// Automations, the automations object of a tasks.hor next to its tasks, somebody's own commands in the command palette
// (Automation and the title) and in Run Automation of the Terminal menu, on a key of their own when they say one. One with
// a command line runs in a terminal tab the way a task does, in the shell it names. One of HIDL runs off the editor's
// thread (HexAutomationRun) as a piece of work with a card and a stop, for as long as it takes, a push of every
// repository takes a while. What it asks of the editor through hex is done on the editor's thread, handed over here.
internal sealed partial class HexTasks
{
    // How often the automations are looked for again, for their keys, the files are only read again once written to
    private const float AUTOMATIONS_EVERY = 1.0f;

    /// <summary>The extensions, whose hex object an automation of HIDL is handed (its own, with what it adds to it).</summary>
    internal HexExtensions? Extensions { get; set; }

    // What an automation's thread asked the editor's to do, done at the next tick
    private readonly ConcurrentQueue<Action> forEditor = new();
    private readonly int editorThread = System.Environment.CurrentManagedThreadId;

    // What an automation's hex calls itself, one owner an automation, so its hex object is made once and not every run
    private readonly Dictionary<string, HexExtension> automationOwners = new(StringComparer.Ordinal);

    // The automations as they were last looked for, and the keys of theirs that were down the tick before
    private List<HexAutomation> automationsKnown = [];
    private float automationsAge = AUTOMATIONS_EVERY;
    private readonly HashSet<string> automationKeysDown = new(StringComparer.Ordinal);

    private void AddAutomationCommands() =>
        context.Commands.Add("Terminal/tasks", context.Strings.Said("automations.run_automation"), ChooseAutomation, icon: "run-all");

    /// <summary>Every automation of the tasks.hor files there are (TasksFiles), in the order they have them.</summary>
    internal List<HexAutomation> Automations()
    {
        var all = new List<HexAutomation>();
        foreach (var (file, root) in TasksFiles())
            all.AddRange(AutomationsOf(file, root));

        return automationsKnown = all;
    }

    /// <summary>What the automations add to the command palette, each Automation and its title, its key with it.</summary>
    internal IEnumerable<UIChoice> AutomationChoices()
    {
        string quiet = context.Strings.Said("automations.palette");
        foreach (HexAutomation automation in Automations())
        {
            HexAutomation picked = automation;
            yield return new UIChoice(automation.Title, () => RunAutomation(picked)) { Quiet = quiet, Keys = automation.Key, Icon = "run-all" };
        }
    }

    /// <summary>Run Automation, every automation to pick from, or the workspace's tasks.hor opened when there are none.</summary>
    private void ChooseAutomation()
    {
        List<HexAutomation> automations = Automations();
        if (automations.Count == 0)
        {
            context.Notices.Say(context.Strings.Said("automations.none", ("file", TASKS_FILE)));
            ConfigureTasks();
            return;
        }

        context.Windows.Choose(context.Strings.Said("automations.choose_title"), context.Strings.Said("automations.choose_prompt"),
            [.. automations.Select(automation => (automation.Title, (Action)(() => RunAutomation(automation))))]);
    }

    /// <summary>What the tab and the piece of work of an automation are known by, its file and its name.</summary>
    internal static string AutomationKey(HexAutomation automation) => $"automation\n{automation.File}\n{automation.Name}";

    /// <summary>
    /// Runs an automation, its command line in a terminal tab of its own, or its HIDL off the editor's thread as a piece of
    /// work. One of HIDL that's going already has its card brought up again rather than a second run.
    /// </summary>
    internal void RunAutomation(HexAutomation automation)
    {
        if (!automation.IsHidl)
        {
            RunAutomationCommand(automation);
            return;
        }

        string key = AutomationKey(automation);
        if (context.Work.Find(key) is { IsActive: true } going)
        {
            context.Work.Renew(going);
            context.Notices.Say(context.Strings.Said("automations.already_running", ("name", automation.Title)));
            return;
        }

        if (!automationOwners.TryGetValue(key, out HexExtension? owner) || owner.Name != automation.Title)
            automationOwners[key] = owner = new HexExtension(automation.Root, builtIn: false, automation.Title) { Enabled = true };

        // The hex object is put together here, on the editor's thread, its calls handed back to it from the automation's
        Horizon.HIDL.Runtime.ObjectValue editorHex = Extensions?.HexOf(owner) ?? new Horizon.HIDL.Runtime.ObjectValue();
        context.Notices.Say(context.Strings.Said("automations.running", ("name", automation.Title)));
        context.Work.Start(key, context.Strings.Said("automations.work_title", ("name", automation.Title)),
            work => new HexAutomationRun(context, automation, work, OnEditorThread, editorThread).Run(editorHex),
            item => AutomationEnded(automation, item));
    }

    /// <summary>How an automation of HIDL ended, said along the bottom, or in a card with what went wrong.</summary>
    private void AutomationEnded(HexAutomation automation, WorkItem item)
    {
        switch (item.State)
        {
            case WorkState.Succeeded:
                context.Notices.Say(context.Strings.Said("automations.ended", ("name", automation.Title)));
                break;
            case WorkState.Cancelled:
                context.Notices.Say(context.Strings.Said("automations.stopped", ("name", automation.Title)));
                break;
            default:
                context.Notices.Notify(context.Strings.Said("automations.failed", ("name", automation.Title), ("why", item.Error ?? "?")), error: true);
                break;
        }
    }

    /// <summary>
    /// An automation's command line in a terminal tab of its own, the same tab the next time while its shell is there, in
    /// the shell it says. A shell that isn't here is said rather than the line run in another, bash in PowerShell is
    /// nonsense.
    /// </summary>
    private void RunAutomationCommand(HexAutomation automation)
    {
        string? shell = ShellProfile(automation.Shell);
        if (shell is not null && context.Terminals.Host.ProfileCalled(shell) is null)
        {
            context.Notices.Say(context.Strings.Said("automations.no_shell", ("name", automation.Title), ("shell", automation.Shell)), error: true);
            return;
        }

        context.Terminals.RunInTab(AutomationKey(automation), automation.Title, automation.WorkingFolder(), automation.Command, (exit, _) =>
        {
            context.Notices.Say(exit == 0
                ? context.Strings.Said("automations.ended", ("name", automation.Title))
                : context.Strings.Said("automations.failed", ("name", automation.Title), ("why", exit.ToString())), error: exit != 0);
        }, shell);
        context.Notices.Say(context.Strings.Said("automations.running", ("name", automation.Title)));
    }

    /// <summary>
    /// The terminal profile a shell an automation names runs in, bash Git Bash on Windows (it's the bash there is) and
    /// the shell of that name elsewhere, the others by their ids (pwsh, powershell, cmd, a wsl one). Null for the usual one.
    /// </summary>
    internal static string? ShellProfile(string shell) => shell.Trim().ToLowerInvariant() switch
    {
        "" => null,
        "bash" or "git_bash" or "git bash" => OperatingSystem.IsWindows() ? "git_bash" : "bash",
        _ => shell.Trim()
    };

    /// <summary>Something an automation's thread asks of the editor's, done at its next tick, the editor woken for it.</summary>
    private void OnEditorThread(Action what)
    {
        forEditor.Enqueue(what);
        context.Wake();
    }

    /// <summary>Every tick, what the automations asked of the editor done, and their keys looked at.</summary>
    internal void Update(float dt)
    {
        while (forEditor.TryDequeue(out Action? what))
            what();

        automationsAge += dt;
        if (automationsAge >= AUTOMATIONS_EVERY)
        {
            automationsAge = 0.0f;
            Automations();
        }

        UpdateAutomationKeys();
    }

    // An automation with a key goes off when it's pressed, a key without control or alt only while nothing's typed into
    private void UpdateAutomationKeys()
    {
        var keyboard = context.Keyboard;
        foreach (HexAutomation automation in automationsKnown)
        {
            if (automation.Key.Length == 0 || UIKeys.Read(automation.Key) is not { } key || (context.Typing && !key.Control && !key.Alt))
                continue;

            bool down = keyboard.IsDown(key.Key) && keyboard.Control == key.Control && keyboard.Shift == key.Shift && keyboard.Alt == key.Alt;
            string id = AutomationKey(automation);
            if (!down)
            {
                automationKeysDown.Remove(id);
                continue;
            }

            if (automationKeysDown.Add(id))
                RunAutomation(automation);
        }
    }
}
