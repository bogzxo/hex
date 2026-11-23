using Hex.Core;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Predictions in the terminal's command line: the same engine as the editor's (Hex.Core/Prediction) on a text of the
// commands that were run and the line being typed, what it answers shown as dim ghost text after the line, Tab or Right to
// take it. Off until edit_predictions_terminal is switched on (Settings, AI: Terminal predictions) next to edit_predictions.
// Nothing is asked at a prompt for a password or while a program that isn't the shell at its prompt has the terminal, and
// what is typed there never gets here: the host keeps none of it in the history this reads (see TerminalSecrets).
internal sealed partial class DawnScene
{
    private PredictionEngine? terminalPredictions;
    private TerminalView? terminalPredictedIn;
    private string terminalPredictedLine = string.Empty;
    private int terminalPredictionVersion;
    private string[]? terminalPredictionLines;
    private PredictionOptions? terminalPredictionOptions;
    private HexSettings? terminalPredictionOptionsOf;

    /// <summary>Every tick: the engine asked for the line at the prompt, a finished prediction put after it, all of it put away when it's off.</summary>
    private void UpdateTerminalPredictions()
    {
        if (!HexTerminalPredictionGlue.On(settings))
        {
            if (terminalPredictions is not null)
                StopTerminalPredictions();
            return;
        }

        terminalPredictions ??= new PredictionEngine(TimeProvider.System)
        {
            ShortcutOnly = false,
            Options = TerminalPredictionOptions,
            Snapshot = TerminalPredictionSnapshot
        };

        TerminalView? view = PanelOpen && bottom.Selected == CONSOLE_PAGE ? terminalPanel.FocusedView : null;
        if (!ReferenceEquals(view, terminalPredictedIn))
        {
            terminalPredictedIn?.ClearGhost();
            terminalPredictedIn = view;
            terminalPredictedLine = string.Empty;
        }
        if (view is null)
            return;

        string line = view.LineText;
        bool atShell = view.IsFocused && view.Process?.IsRunning == true && TerminalHost.AtShellPrompt(view.Screen)
            && view.Process?.RunsAProgram != true;
        bool open = HexTerminalPredictionGlue.Gate(true, view.IsFocused, atShell, atShell && view.AtSecretPrompt(line), view.LineAtCaretEnd, line);

        if (!open)
        {
            if (view.GhostLine is not null)
                view.ClearGhost();
            terminalPredictedLine = string.Empty;
            terminalPredictions.Forget(view);
            return;
        }

        if (line != terminalPredictedLine)
        {
            terminalPredictedLine = line;
            terminalPredictionVersion++;
            if (view.GhostLine != line)
                view.ClearGhost();
            terminalPredictions.Edited(view, terminalPredictionVersion);
        }

        terminalPredictions.Tick();
        if (terminalPredictions.Take() is { } result && terminalPredictionLines is { } lines && result.Version == terminalPredictionVersion
            && HexTerminalPredictionGlue.Ghost(result, lines) is { } ghost)
            view.SetGhost(line, ghost);
    }

    private void StopTerminalPredictions()
    {
        terminalPredictions?.ShutDown();
        terminalPredictions = null;
        terminalPredictedIn?.ClearGhost();
        terminalPredictedIn = null;
        terminalPredictedLine = string.Empty;
        terminalPredictionLines = null;
        terminalPredictionOptions = null;
        terminalPredictionOptionsOf = null;
    }

    private PredictionOptions? TerminalPredictionOptions()
    {
        HexSettings now = settings;
        if (!HexTerminalPredictionGlue.On(now) || !PredictionSettings.Configured(now))
            return null;
        if (terminalPredictionOptions is null || !ReferenceEquals(terminalPredictionOptionsOf, now))
        {
            terminalPredictionOptions = MakePredictionOptions(now);
            terminalPredictionOptionsOf = now;
        }
        return terminalPredictionOptions;
    }

    // The commands run and the line, on the editor's thread when the typing has paused. Null when the gate has shut since
    private PredictionSnapshot? TerminalPredictionSnapshot(object asked)
    {
        if (terminalPredictedIn is not { } view || !ReferenceEquals(asked, view) || view.AtSecretPrompt(view.LineText))
            return null;

        string line = view.LineText;
        string[] lines = HexTerminalPredictionGlue.Lines(terminals.History.Lines, line);
        terminalPredictionLines = lines;
        return new PredictionSnapshot(view, terminalPredictionVersion, HexTerminalPredictionGlue.PATH, HexTerminalPredictionGlue.LANGUAGE,
            lines, lines.Length - 1, line.Length);
    }
}
