using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    internal LanguageServer? FoldingSourceOf(HexDocument of) => linters.TryGetValue(of, out var server) && server.IsReady && server.Failure is null
        && (server.Offers("foldingRangeProvider") || server.LanguageId == "python" && server.Offers("documentSymbolProvider")) ? server : null;

    internal void AskFolding(HexDocument of, Action<IReadOnlyList<UICodeFoldingRange>?> take)
    {
        if (FoldingSourceOf(of) is not {} server || of.Path is not {} path) { take(null); return; }
        FlushEditsToServer(server);
        When(server.FoldingRanges(path, of.Editor.Document.LineCount).ContinueWith(asked => asked.IsCompletedSuccessfully ? asked.Result : null, TaskScheduler.Default), take);
    }
}
