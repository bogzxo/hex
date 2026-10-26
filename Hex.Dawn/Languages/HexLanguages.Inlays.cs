using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// The inlay hints of a file of another language, asked of its server (textDocument/inlayHint) for the whole file.
internal sealed partial class HexLanguages
{
    /// <summary>
    /// Asks a document's server for its inlay hints and does something with them once they're in, on the editor's thread.
    /// False for a document whose server doesn't do them, or that has no server.
    /// </summary>
    internal bool AskInlays(HexDocument of, Action<IReadOnlyList<UICodeInlay>> then)
    {
        if (!Serves(of, "inlayHintProvider", out LanguageServer server, out string path))
            return false;

        // Always answered, a server that fell over says nothing rather than leaving the hints waiting on it for good
        When(server.InlayHints(path, 0, of.Editor.Document.LineCount).ContinueWith(asked => asked.IsCompletedSuccessfully ? asked.Result : [], TaskScheduler.Default), then);
        return true;
    }
}
