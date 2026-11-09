using Hex.Core;

using Horizon.UI;

namespace Hex.Dawn;

// The code a layout was opened from, kept, and what is saved while nothing was changed in the designer. A layout Hex
// wrote is written again out of its components after a change, which gives the same code back. One somebody wrote by
// hand has things in it Hex can't write (a func, a statement after a declaration, a comment inside the braces), and
// writing that out again threw them away on the first save. Every change in the designer is edited into its code where it
// goes (HexLayoutEdits), or turned down.
internal sealed partial class HexDocument
{
    // The code of the layout as it's kept, null while it's whatever Hex writes for the components (a layout never opened)
    private string? source;

    // Whether the kept code is what Hex writes for the layout, and the name it was written under, which is in it
    private bool hexWrote;
    private string sourceName = string.Empty;

    // What the layout was when the kept code was last brought up to it, as the code of it without any notes, and as its parts
    private string sourceShapeKey = string.Empty;
    private HexLayoutShape? sourceShape;

    // The code as it was opened and what the layout was then, for writing a value put back the way it was written
    private (string Text, HexLayoutShape Shape)? opened;

    // The components given another name since the kept code was brought up to the layout, in the order they were
    private readonly List<(string From, string To)> renamedSince = [];

    // A layout being built only to see what some code comes to, which keeps no code of its own
    private bool checking;

    // The layout being built again as a step of its history (undone, turned down, its code applied), which is still the
    // layout that was opened as far as how its values were written goes
    private bool keepOpened;

    /// <summary>Whether the layout's code is kept as somebody wrote it rather than written by Hex, see the top of this file.</summary>
    public bool KeepsCode => source is not null && !hexWrote;

    /// <summary>Why the last change in the designer was turned down and the layout put back to its code, null if none was. Reading it lets go of it.</summary>
    public string? TakeTurnedDown()
    {
        string? why = turnedDown;
        turnedDown = null;
        return why;
    }

    private string? turnedDown;

    // After the components were built out of some code, the code kept and whether Hex writes it the same way. If it does
    // it's Hex's and written again after a change, if not the changes are edited into it
    private void Adopt(string code)
    {
        renamedSince.Clear();
        if (!keepOpened)
            opened = null;

        hexWrote = Regenerate() == code;
        source = code;
        sourceName = Name;
        sourceShapeKey = GenerateBody(withNotes: false);
        sourceShape = hexWrote ? null : Shape();
        if (sourceShape is not null)
            opened ??= (code, sourceShape);
    }

    // The kept code brought up to the layout as it is now. Most of the time nothing changed, which is the layout written
    // without its notes and held up against what it was
    private string Synced()
    {
        // What a list measured its rows as and the like comes and goes as the layout is laid out, the key leaves it out,
        // so a layout nobody changed is the code it was opened from to the byte
        string key = GenerateBody(withNotes: false);
        if (key == sourceShapeKey && (!hexWrote || Name == sourceName))
        {
            renamedSince.Clear();
            return source!;
        }

        if (hexWrote)
        {
            renamedSince.Clear();
            source = Regenerate();
            sourceName = Name;
            sourceShapeKey = key;
            return source;
        }

        HexLayoutShape now = Shape();
        string? edited = HexLayoutEdits.Rewrite(source!, sourceShape!, now, renamedSince, opened, out string why);
        renamedSince.Clear();
        if (edited is not null && !Builds(edited, key))
        {
            edited = null;
            why = "the code that came out of it didn't build the same layout";
        }

        if (edited is null)
        {
            TurnDown(why);
            return source!;
        }

        source = edited;
        sourceShape = now;
        sourceShapeKey = key;
        return source;
    }

    // Whether some code builds a layout that is what the key says, in a module of its own that nobody sees
    private bool Builds(string code, string key)
    {
        var scratch = new HexDocument(Name, Module.Compositor.CreateModule()) { checking = true };
        scratch.Module.Enabled = false;
        try
        {
            // From where the layout was built, which its includes are next to, a copy saved somewhere else has none
            return scratch.Load(code, builtFrom ?? Path).Success && scratch.GenerateBody(withNotes: false) == key;
        }
        finally
        {
            Module.Compositor.RemoveModule(scratch.Module);
        }
    }

    // A change the code can't take is taken back, the layout built again from the code it has, what was selected selected again
    private void TurnDown(string why)
    {
        turnedDown = why;
        var selected = selection.Select(names.NameOf).OfType<string>().ToList();
        string kept = source!;
        Load(kept, Path, keepHistory: true);

        foreach (string name in selected)
        {
            if (Find(name) is { } found)
                selection.Add(found);
        }
    }

    /* The layout as its parts */

    // Every component as the designer sees it, with the two calls that aren't components
    private HexLayoutShape Shape()
    {
        var parts = new List<HexLayoutPart>();
        foreach (var (component, _) in Walk())
        {
            string? kind = UIModule.KindOf(component);
            var declaration = new System.Text.StringBuilder();
            WriteComponent(declaration, component, settableOnly: true);
            var properties = kind is null ? [] : PropertiesOf(component, kind, NameOf, null, out _, settableOnly: true);
            parts.Add(new HexLayoutPart(NameOf(component), kind ?? component.GetType().Name,
                [.. properties.Select(property => new KeyValuePair<string, string>(property.Key, Flat(property.Value)))], Lines(declaration.ToString())));
        }

        return new HexLayoutShape(parts, DesignCall() is { } design ? Lines(design) : null, HiddenCall() is { } hidden ? Lines(hidden) : null);
    }

    // A value written inside of braces moved back to no indentation, the edits put it at whatever the code has
    private static string Flat(string written) => Lines(written).Replace("\n    ", "\n");

    // What was written with the system's line endings with the code's, \n
    private static string Lines(string written) => written.Replace("\r\n", "\n");
}
