using Horizon.UI.Components;

namespace Hex.Dawn;

// Inlay hints, the names of the parameters a call passes to written faintly in among the code, VS Code's way. HIDL's are
// worked out here from the functions the file declares (HidlInlays), another language's are asked of its server. Only
// the file on screen, a moment after the typing stops, off the editor's thread. The editor holds back the hints of the
// line being typed on until they come back, so nothing's stuck in the middle of a word in the meantime.
internal sealed partial class DawnScene
{
    // How long the typing has to stop before the hints are worked out again
    private const long INLAYS_SETTLE_MS = 300;

    private (HexDocument? Of, int Version) inlaysFor = (null, -1);
    private int inlaysVersionSeen = -1;
    private long inlaysChangedAt;
    private bool inlaysWorking;

    /// <summary>Every tick, the hints of the file on screen worked out again when its text changed and the typing stopped.</summary>
    private void UpdateInlays()
    {
        if (!settings.InlayHints || inlaysWorking || !document.IsText || document.DiffOf is not null || document.Picture is not null)
            return;

        int version = code.Document.Version;
        if (inlaysFor == (document, version))
            return;

        // A file just shown at once, a change once the typing stops for a moment
        long now = System.Environment.TickCount64;
        if (inlaysFor.Of == document)
        {
            if (inlaysVersionSeen != version)
            {
                inlaysVersionSeen = version;
                inlaysChangedAt = now;
                return;
            }

            if (now - inlaysChangedAt < INLAYS_SETTLE_MS)
                return;
        }

        inlaysFor = (document, version);
        AskInlays(document, code, version);
    }

    // Helper method to have the hints worked out, out of UpdateInlays so the closure its answer needs is only made when
    // it's asked and not on every tick that has nothing to ask
    private void AskInlays(HexDocument of, CodeEditor editor, int version)
    {
        void Take(IReadOnlyList<UICodeInlay> found)
        {
            inlaysWorking = false;
            if (settings.InlayHints && editor.Document.Version == version)
                editor.InlayHints = found;
        }

        if (ReferenceEquals(code.Highlighter, CodeLanguages.Hidl))
        {
            inlaysWorking = true;
            string text = code.Text;
            InBackground("inlays", () => (IReadOnlyList<UICodeInlay>)Hex.Core.HidlInlays.Of(text), Take, quiet: true);
        }
        else if (languages.AskInlays(of, Take))
            inlaysWorking = true;
    }

    /// <summary>The hints let go of in every file, inlay hints turned off, or on again to be worked out afresh.</summary>
    private void ForgetInlays()
    {
        foreach (HexDocument open in documents)
            open.Editor.InlayHints = [];
        inlaysFor = (null, -1);
    }
}
