namespace Hex.Dawn;

// What a language server asks whoever is at the keyboard (window/showMessageRequest), a dialog with its answers on it
// and Not now, and what it says to be read (window/showMessage, serve-d's word on an install), lines of the output.
// serve-d asks to download DCD the first time it starts, which is what completes D, and every question was answered
// with nothing before, so it never did.
internal sealed partial class HexLanguages
{
    // The questions up on the screen, one at a time, the rest waiting their turn
    private readonly Queue<LanguageServerQuestion> questionsWaiting = new();
    private LanguageServerQuestion? questionUp;

    /// <summary>Helper method to take what a server asked and said since the last tick.</summary>
    private void TakeTalk(LanguageServer server)
    {
        while (server.TryTakeQuestion(out LanguageServerQuestion question))
            questionsWaiting.Enqueue(question);

        while (server.TryTakeSaid(out string text, out bool warning))
            context.Notices.Note($"[{server.Name}] {text}", error: warning);
    }

    /// <summary>Every tick, the next question up once the one before it is answered.</summary>
    private void AskNext()
    {
        if (questionUp is { IsAnswered: false } || !questionsWaiting.TryDequeue(out LanguageServerQuestion? question))
            return;

        Ask(question);
    }

    // Helper method to put a question up, out of AskNext so the closure its answers need isn't made on every tick
    private void Ask(LanguageServerQuestion question)
    {
        questionUp = question;
        var choices = question.Actions
            .Select(action => new DialogChoice(action.Trim(), () => question.Answer(action)))
            .Append(new DialogChoice(context.Strings.Said("languages.not_now"), () => question.Answer(null)))
            .ToArray();
        UIDialog.Show(context.Window.Chrome, context.Strings.Said("languages.server_asks", ("command", question.Server)), question.Message, choices);
    }

    /// <summary>The question on the screen now, for a test to answer the way the dialog does.</summary>
    internal LanguageServerQuestion? QuestionUp => questionUp is { IsAnswered: false } ? questionUp : null;
}
