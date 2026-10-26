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
    private int outlineVersionSeen = -1;
    private long outlineChangedAt;
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

        long now = System.Environment.TickCount64;
        if (outlineVersionSeen != version)
        {
            outlineVersionSeen = version;
            outlineChangedAt = now;
            return outlineKept;
        }

        if (now - outlineChangedAt < OUTLINE_SETTLE_MS)
            return outlineKept;

        // The text is put together once, here, and the going through it is done in the background
        outlineWorking = true;
        HexDocument of = document;
        string text = code.Text;
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

        return outlineKept;
    }
}
