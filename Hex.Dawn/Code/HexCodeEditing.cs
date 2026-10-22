using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The editor of code made clever: completing what's typed, saying what the word under the pointer is, what a call
// takes, going to where something is declared and finding where it's used, renaming it, the find bar, the docs of a
// file's keys, and the code's font. Made with the context and the editor's window, for whichever document is on screen.
internal sealed partial class HexCodeEditing
{
    private readonly DawnContext context;
    private readonly IEditorWindow window;

    // What of the editor is used everywhere in here, by the names it had in the scene
    private HexDocument document => context.Documents.Current;
    private IReadOnlyList<HexDocument> documents => context.Documents.All;
    private CodeEditor code => window.Code;
    private UILayout layout => window.Layout;
    private UIModule chrome => window.Chrome;
    private UICompositor compositor => window.Compositor;
    private HexSettings settings => context.Settings.Values;
    private UICommands commands => context.Commands;
    private bool LayoutTools => document.InDesigner;
    private bool tabsDirty { get => window[DawnParts.Tabs]; set => window[DawnParts.Tabs] = value; }
    private readonly Panel codeScroll;

    // What the parts complete, say about a word and say about a call in some files (the terminal's, the languages'),
    // asked before what the code editor knows itself
    internal readonly List<HexCompletionSource> completionSources = [];
    internal readonly List<Func<HexDocument, CodeEditor, UITextPosition, string?>> hoverSources = [];
    internal readonly List<Func<HexDocument, string, int, UISignature?>> signatureSources = [];

    // The languages, whose servers go to definitions, find references and rename in their files before Hex does it its way
    internal HexLanguages? languages;

    // Ctrl+F, what was looked for last, and with it what the find bar opens with
    internal string lastFind = string.Empty;

    public HexCodeEditing(DawnContext with)
    {
        context = with;
        window = with.Window;
        codeScroll = layout.Get<Panel>("code_scroll");
    }

    /// <summary>What a call in a document takes, what a part says first, what the code editor knows otherwise.</summary>
    internal UISignature? Signature(HexDocument of, CodeEditor editor, string name, int argument) =>
        SourcedSignature(of, name, argument) ?? SignatureOf(editor, name, argument);

    /// <summary>What a part says a call in a document takes, the first that says anything, null for none.</summary>
    private UISignature? SourcedSignature(HexDocument of, string name, int argument)
    {
        foreach (var source in signatureSources)
        {
            if (source(of, name, argument) is { } said)
                return said;
        }

        return null;
    }
}
