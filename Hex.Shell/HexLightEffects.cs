using System.Numerics;

using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>
/// The effects made of light and a few sparks that both modes have, each a switch of its own under the effects (and only
/// when the effects are on at all). A soft glow under the caret (Dawn's) or the cursor (Dusk's) that follows it about, a
/// spotlight under the pointer the way Windows' Reveal lit what you were over, a ring out of every click, and a trail
/// of embers behind the pointer when it's moved quickly. The engine's Lights and Sparks draw them, and with the pointer
/// still and the caret where it was nothing new is painted, an idle window stays idle.
/// </summary>
public sealed class HexLightEffects
{
    // The keys of the lights, the caret's and the pointer's
    private const int CARET = 1, POINTER = 2;

    // How far the pointer has to go in a tick for embers, in UI pixels
    private const float TRAIL_SPEED = 14.0f;

    private Vector2 pointerWas;
    private bool downWas, seen;

    /// <summary>Whether the caret or the cursor has a glow under it.</summary>
    public bool CaretGlow { get; set; } = true;

    /// <summary>Whether the pointer has a spotlight under it.</summary>
    public bool Spotlight { get; set; }

    /// <summary>Whether a click throws a ring out.</summary>
    public bool Ripples { get; set; }

    /// <summary>Whether a quick pointer leaves embers behind it.</summary>
    public bool Trail { get; set; }

    /// <summary>
    /// Every tick, what lights where. The points are in the effects module's own, the caret's null when nothing's being
    /// typed into.
    /// </summary>
    public void Update(Lights lights, Sparks sparks, Vector2 pointer, bool down, Vector2? caret, Vector4 accent, Vector4 text)
    {
        if (CaretGlow && caret is { } at && at != Vector2.Zero)
            lights.Shine(CARET, at, 64.0f, accent with { W = 0.22f });

        if (Spotlight)
            lights.Shine(POINTER, pointer, 150.0f, text with { W = 0.07f });

        if (Ripples && down && !downWas)
            lights.Ripple(pointer, accent with { W = 0.9f });

        if (Trail && seen && Vector2.Distance(pointer, pointerWas) > TRAIL_SPEED)
            sparks.Emit(pointer, 1, accent, speed: 40.0f, life: 0.4f, size: 2.5f);

        pointerWas = pointer;
        downWas = down;
        seen = true;
    }

    /// <summary>Reads the switches out of the editor section of the settings, what they come as when it doesn't say.</summary>
    public void Read(IReadOnlyDictionary<string, Horizon.HIDL.Runtime.IRuntimeValue> editor)
    {
        static bool Flag(IReadOnlyDictionary<string, Horizon.HIDL.Runtime.IRuntimeValue> values, string key, bool otherwise) =>
            values.TryGetValue(key, out var value) ? value is Horizon.HIDL.Runtime.BooleanValue { Value: true } : otherwise;

        CaretGlow = Flag(editor, "effect_caret_glow", true);
        Spotlight = Flag(editor, "effect_spotlight", false);
        Ripples = Flag(editor, "effect_ripples", false);
        Trail = Flag(editor, "effect_trail", false);
    }
}
