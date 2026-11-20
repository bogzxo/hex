namespace Hex.Dawn;

// The reference counts over the declarations of a file of another language, asked of its server. A server that does code
// lenses (csharp-ls) says where they go for the whole file and works a count out only when that one's resolved. Any other
// is asked what the file declares (documentSymbol) and then for the references of each. Only the ones in sight are ever
// resolved or asked about, DawnScene.CodeLens sees to that. Every answer comes back, empty when the server fell over, so
// whoever's counting what's still out never waits on one for good.
internal sealed partial class HexLanguages
{
    internal readonly record struct LensRefreshState(LanguageServer? Server, int Semantics, int Lenses);
    internal LensRefreshState LensRefreshesOf(HexDocument of) => linters.TryGetValue(of, out var server)
        ? new(server, server.SemanticRefreshes, server.CodeLensRefreshes) : default;

    /// <summary>Whether a document's server says code lenses, which are then what its counts come from.</summary>
    internal bool DoesLenses(HexDocument of) => CanServe(of, "codeLensProvider");

    /// <summary>Asks a document's server for its code lenses. False for one that has no server or whose server doesn't do them.</summary>
    internal bool AskServerLenses(HexDocument of, Action<IReadOnlyList<LanguageServerLens>> then)
    {
        if (!Serves(of, "codeLensProvider", out LanguageServer server, out string path))
            return false;

        WhenList(server.CodeLenses(path), then);
        return true;
    }

    /// <summary>Asks what a document declares, for a server that can say where each is used. False for one that can't.</summary>
    internal bool AskDeclarations(HexDocument of, Action<IReadOnlyList<LanguageServerSymbol>> then)
    {
        if (!CanServe(of, "referencesProvider") || !Serves(of, "documentSymbolProvider", out LanguageServer server, out string path))
            return false;

        // What a page or a sheet "declares" is every tag and every rule, which nothing refers to the way code does, and an
        // HTML file had "0 references" over every line of it
        if (server.LanguageIdOf(path) is "html" or "css" or "scss" or "less" or "json" or "jsonc" or "yaml" or "markdown" or "toml" or "xml")
            return false;

        WhenList(server.Symbols(path), then);
        return true;
    }

    /// <summary>Asks what a code lens says once it's worked out, null when the server couldn't say. False for no server.</summary>
    internal bool AskLensTitle(HexDocument of, LanguageServerLens lens, Action<string?> then)
    {
        if (!Serves(of, "codeLensProvider", out LanguageServer server, out _))
            return false;

        When(server.ResolveLens(lens), null, then);
        return true;
    }

    /// <summary>Asks everywhere the name at a place in a document is used, the declaration among them. False for no server that can say.</summary>
    internal bool AskReferences(HexDocument of, int line, int character, Action<IReadOnlyList<LanguageServerPlace>> then)
    {
        if (!Serves(of, "referencesProvider", out LanguageServer server, out string path))
            return false;

        When(server.References(path, line, character), [], then);
        return true;
    }
}
