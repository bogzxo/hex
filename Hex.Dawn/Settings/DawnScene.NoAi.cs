namespace Hex.Dawn;

// No AI switched in the settings. The agents are made or not as the editor is (HexAi), there's no taking them out of
// one that has them, so the switch says it waits for Hex to start again and offers to do that now, the way Restart
// with New Build starts the same Hex again on the same workspace.
internal sealed partial class DawnScene
{
    /// <summary>The card saying No AI comes with the next start, with a restart on it, and nothing once it's back as the run has it.</summary>
    internal void NoAiChanged()
    {
        noAiCard?.Close();
        noAiCard = null;
        if (settings.NoAi == HexAi.Off)
            return;

        noAiCard = Notify(Said(settings.NoAi ? "no_ai.on_after_restart" : "no_ai.off_after_restart"), false, (Said("no_ai.restart"), RestartForNoAi));
    }

    private UINotification? noAiCard;

    /// <summary>The same Hex started again on this workspace, the one that's open closed the way its window would be.</summary>
    private void RestartForNoAi()
    {
        if (System.Environment.ProcessPath is not { } exe || !File.Exists(exe))
            return;

        // A window of its own, a folder handed to the Hex that's running would land back in this one
        var arguments = new List<string> { "--window" };
        if (explorer.workspace.Folders.FirstOrDefault() is { } folder)
            arguments.AddRange(["--folder", folder]);
        RestartInto(exe, arguments);
    }
}
