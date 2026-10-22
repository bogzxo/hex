using System.Collections.Concurrent;

using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// Completing what is typed in the languages that aren't HIDL, by asking the language server a file is linted by
// (HexLanguages). The server answers on its own thread some time later, so the list can't be there the moment it is
// asked for: the word being typed is asked about once, from where it starts, nothing is offered until the answer comes
// back, and then the list opens by itself. The editor narrows it down as the word goes on, so typing more of the same
// word asks nothing more, unless the server said it only sent some of it.
internal sealed partial class HexLanguages
{
    // Where the word being completed starts, and the line before it, which is what the server's answer is good for
    private readonly record struct ServerWord(int Line, int Start, string Ahead);

    // What a document's server was last asked and what it said, by which asking it was (the last one is the one that counts)
    private sealed class ServerAsking
    {
        public ServerWord Word;
        public int Generation;
        public string Prefix = string.Empty;
        public bool Typed;
        public bool Answered;
        public bool Incomplete;
        public UICompletionRequest? Offered;
    }

    private readonly Dictionary<HexDocument, ServerAsking> serverAsked = [];
    private readonly ConcurrentQueue<(HexDocument Of, int Generation, LanguageServerCompletions? Found)> serverAnswers = new();
    private int serverGeneration;

    /// <summary>
    /// What the server of a file of another language offers at the caret, what it said last time while it is still
    /// asked. Null when the file has no server that is running, for the editor's own completions to go on.
    /// </summary>
    private UICompletionRequest? ServerCompletions(HexDocument of, CodeEditor editor, out bool handled)
    {
        handled = false;
        if (of.Path is not { } path || !linters.TryGetValue(of, out LanguageServer? server) || server.Failure is not null)
            return null;

        handled = true;
        UITextDocument doc = editor.Document;
        UITextPosition caret = doc.Caret;
        string line = doc.LineAt(caret.Line);
        int start = caret.Column - doc.WordBeforeCaret().Length;
        var word = new ServerWord(caret.Line, start, line[..start]);
        string prefix = line[start..caret.Column];

        // The same word, which the editor narrows down by itself, unless the server wants asking as it goes
        if (serverAsked.TryGetValue(of, out ServerAsking? asked) && asked.Word == word)
        {
            asked.Typed = editor.CompletionTyped;
            if (!asked.Answered)
                return asked.Offered ?? Waiting(of, word.Start);
            if (!asked.Incomplete || asked.Prefix == prefix)
                return asked.Offered;
        }

        // Typed, nothing started is nothing to ask about, unless it comes straight after a dot
        char? trigger = start > 0 && line[start - 1] == '.' ? '.' : null;
        if (editor.CompletionTyped && prefix.Length == 0 && trigger is null)
            return null;

        // The server has to have the text as it is now, which it would otherwise get a moment after the typing stops
        if (lintIn.Remove(of))
            server.Change(path, editor.Text);

        bool again = asked is { Answered: true, Incomplete: true } && asked.Word == word;
        var asking = new ServerAsking
        {
            Word = word,
            Generation = ++serverGeneration,
            Prefix = prefix,
            Typed = editor.CompletionTyped,

            // What it said before stays on offer while it is asked again
            Offered = again ? asked!.Offered : null
        };
        serverAsked[of] = asking;

        int generation = asking.Generation;
        server.Complete(path, caret.Line, caret.Column, start, trigger, again).ContinueWith(answered =>
        {
            serverAnswers.Enqueue((of, generation, answered.IsCompletedSuccessfully ? answered.Result : null));
            context.Wake();
        }, TaskScheduler.Default);

        return asking.Offered ?? Waiting(of, start);
    }

    // The list while the server's still thinking, a quiet row that says who's being asked. The first list from serve-d
    // takes seconds, and nothing on screen for that long read as completion not working
    private UICompletionRequest Waiting(HexDocument of, int start) =>
        new(start, [], context.Strings.Said("languages.completion_waiting", ("server", ServerShownOf(of) ?? "the language server")));

    /// <summary>Every tick, what the servers answered is taken in, and the list opens for what is still being typed.</summary>
    private void UpdateServerCompletions()
    {
        while (serverAnswers.TryDequeue(out var answer))
        {
            if (!serverAsked.TryGetValue(answer.Of, out ServerAsking? asked) || asked.Generation != answer.Generation)
                continue;

            asked.Answered = true;
            asked.Incomplete = answer.Found?.Incomplete ?? false;
            asked.Offered = answer.Found is { } found ? new UICompletionRequest(asked.Word.Start, found.Items) : null;

            // Only for the word it was asked about, the caret having gone somewhere else is the end of it
            CodeEditor editor = answer.Of.Editor;
            if (!editor.IsFocused || !context.Documents.All.Contains(answer.Of))
                continue;

            UITextPosition caret = editor.Document.Caret;
            int start = caret.Column - editor.Document.WordBeforeCaret().Length;
            if (caret.Line == asked.Word.Line && start == asked.Word.Start)
                editor.Complete(asked.Typed);
        }
    }

    /// <summary>A file of another language was closed, what its server offered goes with it.</summary>
    private void ForgetServerCompletions(HexDocument closed) => serverAsked.Remove(closed);
}
