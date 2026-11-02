namespace Hex.Dawn;

// What a document declares by its language server, for the agent harness's index (DawnScene.McpHarness). Only a server
// that says documentSymbol is asked, and the answer comes back empty when it fell over so nobody waits on it for good.
internal sealed partial class HexLanguages
{
    /// <summary>Asks what a document declares. False for one with no server or a server that can't say.</summary>
    internal bool AskSymbols(HexDocument of, Action<IReadOnlyList<LanguageServerSymbol>> then)
    {
        if (!Serves(of, "documentSymbolProvider", out LanguageServer server, out string path))
            return false;

        When(server.Symbols(path).ContinueWith(asked => asked.IsCompletedSuccessfully ? asked.Result ?? [] : (IReadOnlyList<LanguageServerSymbol>)[], TaskScheduler.Default), then);
        return true;
    }
}
