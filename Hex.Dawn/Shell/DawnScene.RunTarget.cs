using System.Numerics;

namespace Hex.Dawn;

// The arrow beside the Run button, what F5 and the button run. It drops a menu of the workspace's run tasks, its .NET
// programs and the file on screen (HexTasks.RunTargets), the one picked ticked and kept with the workspace. The button
// says what it runs. A project picked is debugged with F5 where there's a debugger for it and run with Ctrl+F5, a task
// runs as tasks do, and the file on screen is what F5 always did.
internal sealed partial class DawnScene
{
    private void WireRunTarget() => layout.Get<Button>("run_target_button").OnPressed = ShowRunTargets;

    /// <summary>The menu of what can run, under the arrow, the one that runs now ticked.</summary>
    private void ShowRunTargets()
    {
        var targets = new Menu(Said("run_target.title"));
        foreach (HexRunChoice choice in tasks.RunChoices())
        {
            HexRunChoice picked = choice;
            string icon = choice.Task is not null ? "tasklist" : choice.Project is not null ? "project" : "file";
            MenuItem item = targets.Add($"{choice.Label}   {choice.Detail}", () => ChooseRunTarget(picked), icon: icon);
            item.IsChecked = () => picked.Target is { } target ? target.Same(Context.Workspace.RunTarget) : Context.Workspace.RunTarget is null;
        }

        UIComponent arrow = layout.Get<Button>("run_target_button");
        Context.Windows.MenuAt(targets, new Vector2(arrow.Bounds.Min.X, arrow.Bounds.Max.Y));
    }

    /// <summary>Something picked to run, kept with the workspace and said on the button.</summary>
    internal void ChooseRunTarget(HexRunChoice choice)
    {
        Context.Workspace.RunTarget = choice.Target;
        ShowRunTarget();
        Say(Said("run_target.chosen", ("name", choice.Label)));
    }

    /// <summary>
    /// The Run button saying what it runs, Play for a layout, Run for the file on screen, or the name of what was picked
    /// with a line about it, and the arrow beside it while there's code rather than a layout. Something picked has the
    /// button whatever's on screen, a file that has nothing to run of its own only has it when nothing is.
    /// </summary>
    private void ShowRunTarget()
    {
        bool tools = LayoutTools;
        var play = layout.Get<Button>("play_button");
        HexRunChoice? chosen = tools ? null : tasks.ChosenRun();
        play.Label = chosen?.Label ?? Said(tools ? "toolbar.play" : "toolbar.run");
        play.Tooltip = chosen is not null
            ? Said("run_target.run_tip", ("name", chosen.Label), ("detail", chosen.Detail))
            : Said(tools ? "toolbar.play_tip" : "toolbar.run_tip");
        play.Visible = chosen is not null || Runnable(document);
        layout.Get<Button>("run_target_button").Visible = !tools;
        layout.Get<UIComponent>("tools_rule3").Visible = play.Visible || !tools;
    }

    /// <summary>
    /// F5 (debug) or Ctrl+F5 for what was picked beside the Run button, false when that's the file on screen and it's
    /// left to what F5 did before. A project is debugged when there's a debugger for it and F5 asked, run otherwise.
    /// </summary>
    private bool RunChosen(bool debug)
    {
        if (tasks.ChosenRun() is not { } chosen)
            return false;

        if (chosen.Task is { } task)
        {
            tasks.Run(task);
            return true;
        }

        if (chosen.Project is { } project)
        {
            if (debug && project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && debugger.Netcoredbg() is not null)
                debugger.DebugProject(project);
            else
                tasks.Run(HexTasks.ProjectTask(project));
            return true;
        }

        return false;
    }
}
