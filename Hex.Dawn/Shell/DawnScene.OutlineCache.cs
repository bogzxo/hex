namespace Hex.Dawn;

// The outline of the file on screen, kept for what reads it every key, the breadcrumbs and the outline's side. Worked out
// fresh it was the whole text put together as one string and gone through again on every key, 30 ms a key in a file of
// twenty thousand lines. Now it's worked out a moment after the typing stops, off the editor's thread, and what reads it
// in the meantime gets the last one, a line or two out at worst for as long as the typing goes on.
internal sealed partial class DawnScene
{
    // How long the typing has to stop before the outline is worked out again
    private const long OUTLINE_SETTLE_MS = 300;

    private List<OutlineEntry> outlineKept = [];
    private (HexDocument? Of, int Version) outlineKeptFor = (null, -1);
    private HexSettle<int> outlineSettle = new(OUTLINE_SETTLE_MS);
    private bool outlineWorking;

    /// <summary>
    /// The outline of the file on screen as last worked out. A document just shown is worked out there and then, a change
    /// to it once the typing stops for a moment. Every tick, so it's asked for when it's due.
    /// </summary>
    private List<OutlineEntry> KeptOutline()
    {
        int version = code.Document.Version;
        if (outlineKeptFor.Of != document)
        {
            outlineKept = OutlineHere();
            outlineKeptFor = (document, version);
            return outlineKept;
        }

        if (outlineKeptFor.Version == version || outlineWorking)
            return outlineKept;

        if (!outlineSettle.Settled(version, System.Environment.TickCount64))
            return outlineKept;

        WorkOutOutline(version);
        return outlineKept;
    }

    // The text of the code as of a version, put together once for whatever works it out in the background (the outline,
    // the hints, the semantic colours), which all asked within a moment of the typing stopping, a copy of a big file each
    private (Horizon.UI.Components.UITextDocument? Of, int Version, string? Text) codeTextKept;

    private string CodeText(Horizon.UI.Components.CodeEditor editor)
    {
        var of = editor.Document;
        if (ReferenceEquals(codeTextKept.Of, of) && codeTextKept.Version == of.Version && codeTextKept.Text is { } kept)
            return kept;
        string text = editor.Text;
        codeTextKept = (of, of.Version, text);
        return text;
    }

    /// <summary>The text let go of, a closed file's isn't kept.</summary>
    private void ForgetCodeText() => codeTextKept = default;

    // Helper method to work the outline out again in the background, out of KeptOutline so the closure it needs is made
    // only then and not on every tick that asks for the outline
    private void WorkOutOutline(int version)
    {
        // The text is put together once, here, and the going through it is done in the background
        outlineWorking = true;
        HexDocument of = document;
        string text = CodeText(code);
        string? path = document.Path;
        InBackground("outline", () => HexOutline.Outline(text, path), outline =>
        {
            outlineWorking = false;
            if (document != of)
                return;

            outlineKept = outline;
            outlineKeptFor = (of, version);

            // What shows it put together again with the new one
            crumbsFor = default;
            treeDirty |= Side == HexSide.Outline;
        }, quiet: true);
    }
}
