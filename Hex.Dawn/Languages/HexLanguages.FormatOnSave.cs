namespace Hex.Dawn;

// Formatting on save, VS Code's editor.formatOnSave. Ctrl+S on a file whose server formats has the server format it
// first, its edits made as one step to undo, and then it's written. A server that takes too long doesn't hold the save
// up, the file is written as it is once VS Code's formatOnSaveTimeout has gone by and the late answer is dropped, and so
// is one for a file that was typed into while the server thought about it.
internal sealed partial class HexLanguages
{
    /// <summary>How long a save waits for the server's formatting, in milliseconds, VS Code's formatOnSaveTimeout.</summary>
    internal const int FORMAT_ON_SAVE_TIMEOUT = 750;

    /// <summary>
    /// A document formatted by its server and then whatever comes next done (the save), formatted or not. False, and
    /// nothing done, when its server doesn't format, for the caller to go straight on.
    /// </summary>
    internal bool FormatThen(HexDocument of, Action then)
    {
        if (!Serves(of, "documentFormattingProvider", out LanguageServer server, out string path))
            return false;

        int version = of.Editor.Document.Version;
        Task<IReadOnlyList<LanguageServerEdit>?> asked = server.Format(path, of.Editor.TabSize, insertSpaces: !of.Editor.Document.Indent.StartsWith('\t'));
        When(Task.WhenAny(asked, Task.Delay(FORMAT_ON_SAVE_TIMEOUT)), first =>
        {
            if (first == asked && asked.IsCompletedSuccessfully && asked.Result is { Count: > 0 } edits && of.Editor.Document.Version == version)
                Rewrite(of, edits);
            then();
        });
        return true;
    }
}
