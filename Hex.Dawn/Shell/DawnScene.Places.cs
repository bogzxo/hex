using Silk.NET.Input;

namespace Hex.Dawn;

internal readonly record struct HexPlace(string Path, int Line, int Column);

// Going back to where you were, Alt+Left and Alt+Right and the mouse's back and forward buttons. F12, a click in the values,
// a problem, find in files, all of them jump somewhere (often another file) and there was no way back. A place is noted
// whenever the caret goes further than a screen in one go or into another file, typing and stepping about don't count.
internal sealed partial class DawnScene
{
    private const int MAX_PLACES = 50;

    // How far the caret has to go in one tick to count as a jump, in lines
    private const int JUMP_LINES = 12;

    private readonly List<HexPlace> placesBack = [], placesForward = [];

    // Where the caret was the last tick, and whether the next move is going back or forward rather than a jump of its own
    private HexPlace? placeWas;
    private bool goingPlaces;
    private bool mouseBackWas, mouseForwardWas;

    /// <summary>Every tick, a jump noted as a place to come back to, and the mouse's back and forward buttons.</summary>
    private void UpdatePlaces()
    {
        var mouse = Engine.Input.Mouse;
        bool back = mouse.IsDown(MouseButton.Button4), forward = mouse.IsDown(MouseButton.Button5);
        if (back && !mouseBackWas) GoPlace(back: true);
        if (forward && !mouseForwardWas) GoPlace(back: false);
        mouseBackWas = back;
        mouseForwardWas = forward;

        HexPlace? now = PlaceNow();
        if (now == placeWas)
            return;

        if (!goingPlaces && placeWas is { } was && now is { } here
            && (!string.Equals(was.Path, here.Path, PlatformPaths.Comparison) || Math.Abs(was.Line - here.Line) >= JUMP_LINES))
        {
            Remember(placesBack, was);
            placesForward.Clear();
        }

        goingPlaces = false;
        placeWas = now;
    }

    // Where the caret is, in a file that has a file. A layout in its designer is a place without a line
    private HexPlace? PlaceNow()
    {
        if (document.Path is not { } path)
            return null;

        if (!document.IsText && document.View == HexView.Design)
            return new HexPlace(path, -1, 0);

        var caret = code.Document.Caret;
        return new HexPlace(path, caret.Line, caret.Column);
    }

    private static void Remember(List<HexPlace> places, HexPlace place)
    {
        if (places.Count > 0 && places[^1] == place)
            return;

        places.Add(place);
        if (places.Count > MAX_PLACES)
            places.RemoveAt(0);
    }

    /// <summary>Back to the place before (or on to the one after, forward), the place left noted the other way.</summary>
    private void GoPlace(bool back)
    {
        List<HexPlace> from = back ? placesBack : placesForward, to = back ? placesForward : placesBack;

        // A place in a file that was closed and is gone from the disk is skipped
        while (from.Count > 0 && !File.Exists(from[^1].Path))
            from.RemoveAt(from.Count - 1);

        if (from.Count == 0)
        {
            Say(back ? "nowhere further back" : "nowhere further on");
            return;
        }

        HexPlace going = from[^1];
        from.RemoveAt(from.Count - 1);
        if (PlaceNow() is { } here)
            Remember(to, here);

        goingPlaces = true;
        if (going.Line < 0)
        {
            Open(going.Path);
            return;
        }

        OpenAt(going.Path, going.Line, going.Column);
    }

    /// <summary>Go's back and forward through the places the caret was.</summary>
    private void AddPlaceCommands()
    {
        commands.Add("Go/places", "Back", () => GoPlace(back: true), "Alt+Left", "arrow-left", UIKeyUse.BoundWhileTyping);
        commands.Add("Go/places", "Forward", () => GoPlace(back: false), "Alt+Right", "arrow-right", UIKeyUse.BoundWhileTyping);
    }
}
