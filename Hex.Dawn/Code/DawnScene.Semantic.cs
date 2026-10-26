using Horizon.UI.Components;

namespace Hex.Dawn;

// Semantic highlighting, every name coloured by what it is, ReSharper's way. HIDL's is worked out here from the file
// itself (HidlSemantics), another language's is asked of its server. D's server (serve-d) doesn't say, so D's is worked
// out here as well (DSemantics) until a server that does is there. Only the file on screen, as it comes up and a moment
// after the typing stops, off the editor's thread, the same way the inlay hints go. The editor keeps each line's names
// for as long as the line is what it was when they came, a line typed into goes back to the grammar's colours meanwhile.
// A server still loading its project may say nothing at first, it's asked again a few times, and whenever it says what
// it said is out of date.
internal sealed partial class DawnScene
{
    // How long the typing has to stop before the names are worked out again
    private const long SEMANTICS_SETTLE_MS = 300;

    // How long before asking a server that said nothing again, and how many times
    private const long SEMANTICS_RETRY_MS = 1500;
    private const int SEMANTICS_RETRIES = 6;

    private (HexDocument? Of, int Version, int Refreshes) semanticsFor = (null, -1, 0);
    private (HexDocument? Of, int Version) ownSemanticsFor = (null, -1);
    private int semanticsVersionSeen = -1, semanticsTries;
    private long semanticsChangedAt, semanticsRetryAt;
    private bool semanticsWorking;

    /// <summary>Every tick, the names of the file on screen worked out again when its text changed and the typing stopped.</summary>
    private void UpdateSemantics()
    {
        if (!settings.SemanticHighlighting || semanticsWorking || !document.IsText || document.DiffOf is not null || document.Picture is not null || code.Highlighter is null)
            return;

        int version = code.Document.Version, refreshes = languages.SemanticRefreshesOf(document);
        long now = System.Environment.TickCount64;
        bool sameText = semanticsFor.Of == document && semanticsFor.Version == version;
        bool retry = semanticsRetryAt != 0 && now >= semanticsRetryAt;
        if (sameText && semanticsFor.Refreshes == refreshes && !retry)
            return;

        if (semanticsFor.Of != document)
        {
            semanticsTries = 0;
            semanticsRetryAt = 0;
        }
        else if (!sameText)
        {
            // A change once the typing stops for a moment
            if (semanticsVersionSeen != version)
            {
                semanticsVersionSeen = version;
                semanticsChangedAt = now;
                return;
            }

            if (now - semanticsChangedAt < SEMANTICS_SETTLE_MS)
                return;
        }

        HexDocument of = document;
        CodeEditor editor = code;
        Func<string, List<Horizon.UI.Scripting.SemanticSpan>>? own = OwnSemantics(code.Highlighter);
        semanticsRetryAt = 0;

        // Worked out here, HIDL always and D while no server says
        void Own()
        {
            if (own is null)
                return;
            semanticsWorking = true;
            ownSemanticsFor = (of, version);
            string text = editor.Text;
            InBackground("semantics", () => (IReadOnlyList<Horizon.UI.Scripting.SemanticSpan>)own(text), found =>
            {
                semanticsWorking = false;
                if (settings.SemanticHighlighting && editor.Document.Version == version)
                    editor.SemanticSpans = found;
            }, quiet: true);
        }

        void Take(IReadOnlyList<Horizon.UI.Scripting.SemanticSpan>? found)
        {
            semanticsWorking = false;
            bool nothing = found is null || (found.Count == 0 && editor.Document.LineCount > 1);
            if (!nothing && settings.SemanticHighlighting && editor.Document.Version == version)
                editor.SemanticSpans = found!;

            // Nothing from a server that's likely still loading what it needs, asked again in a moment, and what's worked
            // out here meanwhile for a language that can be
            if (nothing && ++semanticsTries <= SEMANTICS_RETRIES && ReferenceEquals(document, of))
                semanticsRetryAt = System.Environment.TickCount64 + SEMANTICS_RETRY_MS;
            else if (!nothing)
                semanticsTries = 0;
            if (nothing && ownSemanticsFor != (of, version))
                Own();
        }

        if (ReferenceEquals(code.Highlighter, CodeLanguages.Hidl))
        {
            semanticsFor = (document, version, refreshes);
            Own();
            return;
        }

        switch (languages.AskSemantics(of, Take))
        {
            case SemanticAsk.Asked:
                semanticsFor = (document, version, refreshes);
                semanticsWorking = true;
                break;

            // The server's still starting, asked again on a later tick, and what can be worked out here meanwhile
            case SemanticAsk.NotYet:
                if (ownSemanticsFor != (of, version))
                    Own();
                break;

            default:
                semanticsFor = (document, version, refreshes);
                Own();
                break;
        }
    }

    // What works a language's names out here rather than asking a server, null for one that's left to its server
    private static Func<string, List<Horizon.UI.Scripting.SemanticSpan>>? OwnSemantics(Horizon.UI.Scripting.ICodeHighlighter? highlighter) => highlighter switch
    {
        _ when ReferenceEquals(highlighter, CodeLanguages.Hidl) => HidlSemantics.Of,
        Horizon.UI.Scripting.TextMateHighlighter { ScopeName: "source.d" } => DSemantics.Of,
        _ => null
    };

    /// <summary>The names let go of in every file, semantic highlighting turned off, or on again to be worked out afresh.</summary>
    private void ForgetSemantics()
    {
        foreach (HexDocument open in documents)
            open.Editor.SemanticSpans = [];
        semanticsFor = (null, -1, 0);
        ownSemanticsFor = (null, -1);
        semanticsTries = 0;
        semanticsRetryAt = 0;
    }
}
