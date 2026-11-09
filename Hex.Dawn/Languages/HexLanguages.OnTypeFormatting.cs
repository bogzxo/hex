namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    private long typeFormattingRequest;

    internal void FormatTyped(HexDocument of, char typed)
    {
        if (!settings.FormatOnType || !of.IsText || of.DiffOf is not null || of.Editor.Document.HasExtraCarets
            || !linters.TryGetValue(of, out var owner) || !owner.TriggersFormatting(typed)
            || !Serves(of, "documentOnTypeFormattingProvider", out var server, out var path)) return;
        var text = of.Editor.Document;
        int version = text.Version, workspace = editWorkspaceGeneration;
        var caret = text.Caret;
        var anchor = text.Anchor;
        long number = ++typeFormattingRequest;
        var weak = new WeakReference<HexDocument>(of);
        var asked = server.FormatOnType(path, caret.Line, caret.Column, typed, of.Editor.TabSize, !text.Indent.StartsWith('\t'));
        When(asked.WaitAsync(TimeSpan.FromMilliseconds(750)).ContinueWith(result => result.IsCompletedSuccessfully ? result.Result : null, TaskScheduler.Default), edits =>
        {
            if (edits is not { Count: > 0 } || number != typeFormattingRequest || !settings.FormatOnType || workspace != editWorkspaceGeneration
                || !weak.TryGetTarget(out var open) || context.Documents.Current != open || open.Path != path
                || linters.GetValueOrDefault(open) != server || server.Failure is not null || open.Editor.Document.Version != version
                || open.Editor.Document.Caret != caret || open.Editor.Document.Anchor != anchor || open.Editor.Document.HasExtraCarets) return;
            // Automatic formatting is atomic and undoable. Invalid or obsolete edits leave typing alone.
            context.Documents.Rewrite(open, EditorEdits(edits));
        });
    }
}
