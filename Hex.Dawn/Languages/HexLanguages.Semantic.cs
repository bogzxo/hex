using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

/// <summary>How asking a document's server for what its names are went.</summary>
internal enum SemanticAsk
{
    // Asked, the answer comes later
    Asked,

    // Its server is still starting, ask again in a moment
    NotYet,

    // No server, or one that doesn't do them
    Never
}

// What the names of a file of another language are, asked of its server (textDocument/semanticTokens), and the colours
// C# is shown in, ReSharper's unless the settings say Visual Studio's or the skin's own.
internal sealed partial class HexLanguages
{
    /// <summary>
    /// Asks a document's server what every name in it is and does something with it once it's in, on the editor's
    /// thread, null for a server that couldn't say.
    /// </summary>
    internal SemanticAsk AskSemantics(HexDocument of, Action<IReadOnlyList<SemanticSpan>?> then)
    {
        if (of.Path is null || !linters.TryGetValue(of, out LanguageServer? running) || running.Failure is not null)
            return SemanticAsk.Never;
        if (!running.IsReady)
            return SemanticAsk.NotYet;
        if (!Serves(of, "semanticTokensProvider", out LanguageServer server, out string path))
            return SemanticAsk.Never;

        // The lines as they are now, for what the server doesn't say, which locals are written to again (SemanticWrites)
        // and the delegates, records and extension methods its legend hasn't got (SemanticDeclarations)
        var document = of.Editor.Document;
        var lines = new string[document.LineCount];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = document.LineAt(i);

        // Always answered, a server that fell over says null rather than leaving it waiting for good
        When(server.SemanticTokens(path, lines.Length).ContinueWith(asked =>
            asked.IsCompletedSuccessfully && asked.Result is { } found ? (IReadOnlyList<SemanticSpan>?)SemanticWrites.Mark(SemanticDeclarations.Refine([.. found], lines), lines) : null, TaskScheduler.Default), then);
        return SemanticAsk.Asked;
    }

    /// <summary>How many times a document's server said what it said of the names is out of date, 0 for none.</summary>
    internal int SemanticRefreshesOf(HexDocument of) => linters.TryGetValue(of, out LanguageServer? server) ? server.SemanticRefreshes : 0;

    /// <summary>C# in the colours the settings say, ReSharper's, Visual Studio's or whatever the skin has.</summary>
    internal void ApplyCSharpColors()
    {
        switch (settings.CSharpColors)
        {
            case "visual_studio":
                CodeLanguages.SetColors(CSHARP_SCOPE, UISyntaxColors.VisualStudioDark(), UISyntaxColors.VisualStudioLight());
                break;
            case "skin":
                CodeLanguages.ForgetColors(CSHARP_SCOPE);
                break;
            default:
                CodeLanguages.SetColors(CSHARP_SCOPE, UISyntaxColors.ReSharperDark(), UISyntaxColors.ReSharperLight());
                break;
        }
    }

    private const string CSHARP_SCOPE = "source.cs";
}
