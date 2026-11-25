using System.Numerics;

using Hex.Core.Modules;
using Hex.Shell;

using Horizon.Core.Tweening;
using Horizon.Engine;
using Horizon.Rendering.Transitions;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// Going between the faces, Dawn, Agentic and Dusk, without a frame of nothing in between. A face picked is changed to
// under a cover of the old one's last frame (Horizon's CrossfadeTransition), which stays whole until the new face has
// been drawn whole and then fades off it, the new face's own part sliding the last few pixels in from the side its button
// is on. As quick as the interface animation setting says, and at 0 nothing moves: the old frame is let go of the moment
// the new one's there. A face the pointer rests on (its button on the title bar), or the keys of the switch held down, is
// warmed ahead: its theme loaded and its layout painted aside in it, so its letters and art are there by the press, and
// Dusk made and set up. Nothing on screen changes for it, and a face warmed already isn't warmed again.
internal sealed partial class DawnScene
{
    // How long the old face takes to fade off the new one, and how far the new one's part slides in from
    private const float FACE_TIME = 0.18f, FACE_SHIFT = 16.0f;

    // What the faces go through, made once and kept with the pictures it holds
    private CrossfadeTransition? faceFade;

    // Whether the faces go through it in a self test, only in the test that times them
    private bool facesMoveInTest;

    // What was warmed last, a pointer resting on a button warms the face once for a look, a size and a window
    private (HexFace Face, string? Theme, float Scale, Vector2 Window) faceWarmed;

    private static readonly HexFace[] FACES = [HexFace.Dawn, HexFace.Noon, HexFace.Dusk];

    /// <summary>
    /// What a change of face goes through, the fade as long as the interface animation setting has it, 0 for the old
    /// frame let go of as soon as the new one is there. Null in a self test (but the one that times them), which looks at
    /// the face straight after it's picked.
    /// </summary>
    private SceneTransition? FaceTransition()
    {
        if (options.SelfTest && !facesMoveInTest)
            return null;

        faceFade ??= new CrossfadeTransition();
        float time = MotionFor(FACE_TIME);
        faceFade.InTime = time;
        HexModes.ArriveTime = time;
        return faceFade;
    }

    /// <summary>A face picked, from the switch, a key or the palette, through the transition. Dusk swaps the scene.</summary>
    private void SwitchFace(HexFace face)
    {
        if (face == HexFace.Dusk)
        {
            SwapMode();
            return;
        }

        if (face == HexFace.Noon && !HexModules.IsEnabled(HexModuleId.Agentic))
            return;
        if (face == HexFace.Noon)
        {
            WarmMarkdownGrammarOnce();
            EnsureNoon();
        }

        if (face == Face || FaceTransition() is not { } fade)
        {
            ShowFace(face);
            return;
        }

        HexFace from = Face;
        Engine.SceneManager.Cover(fade, () =>
        {
            ShowFace(face);
            ArriveFace(from, face);
        });
        float smoothTime = MotionFor(FACE_TIME);
        if (smoothTime > 0.0f)
            Engine.WindowManager.RequestSmooth(smoothTime);
        else
            Engine.WindowManager.RequestFrames();
    }

    // Where a face is on the switch, left to right
    private static int Place(HexFace face) => face switch { HexFace.Dawn => 0, HexFace.Noon => 1, _ => 2 };

    /// <summary>
    /// The part of the face that changes (the body, Agentic's columns) sliding the last few pixels in from the side its
    /// button is on, the title bar and the status bar staying where they are. Drawn only, the layout is where it'll be.
    /// </summary>
    private void ArriveFace(HexFace from, HexFace to)
    {
        float time = options.SelfTest && !facesMoveInTest ? 0.0f : MotionFor(FACE_TIME);
        UIComponent? arriving = to == HexFace.Noon ? noonView : body;
        if (time <= 0.0f || from == to || arriving is null)
            return;

        float side = Place(to) > Place(from) ? 1.0f : -1.0f;
        arriving.VisualOffset = new Vector2(side * FACE_SHIFT, 0.0f);
        arriving.TweenOffset(Vector2.Zero, time).SetEasing(Easing.OutCubic);
    }

    /// <summary>The faces the pointer is over, or the keys of the switch held, warmed ahead. Every tick, nothing when there's neither.</summary>
    private void WarmFaces()
    {
        if (modeToggle?.Parent is not StackPanel row)
            return;

        // By index, a foreach over IReadOnlyList boxes the list's enumerator every tick
        IReadOnlyList<UIComponent> children = row.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is Button { IsHovered: true, Selected: false } button && FaceLabelled(button.Label) is { } hovered)
            {
                WarmFace(hovered);
                return;
            }
        }

        // Ctrl+Alt+Shift, the start of both of the switch's keys (D and N)
        var keys = Engine.Input.Keyboard;
        if (keys.Primary && keys.Alt && keys.Shift)
        {
            WarmFace(Face == HexFace.Noon ? HexFace.Dawn : HexFace.Noon);
            WarmFace(HexFace.Dusk);
        }
    }

    private static HexFace? FaceLabelled(string? label)
    {
        foreach (HexFace face in FACES)
        {
            if (face.Name() == label)
                return face;
        }

        return null;
    }

    /// <summary>
    /// A face made ready to be shown. Dusk made and set up ahead, Agentic's theme loaded and its columns painted aside in
    /// it. Dawn is the editor as it is, there all along.
    /// </summary>
    private void WarmFace(HexFace face)
    {
        if (face == HexFace.Dusk)
        {
            if (!HexModes.Made(HexMode.Dusk) && HexModes.Together)
                HexModes.Warm(HexMode.Dusk, explorer.workspace.Folders.Count > 0 ? explorer.workspace.Folders[0] : browseDirectory);
            return;
        }

        if (face != HexFace.Noon || Face == HexFace.Noon || !HexModules.IsEnabled(HexModuleId.Agentic))
            return;
        EnsureNoon();
        if (noonView is null)
            return;

        UISkinChoice? skin = UISkins.Find(settings.EditorSkin);
        string? noon = NoonThemeOf(skin, settings.EditorTheme);
        var warmed = (face, noon, compositor.Scale, Engine.WindowManager.ViewportSize);
        if (warmed == faceWarmed)
            return;

        // The theme loaded first, on the next frame, and the columns painted in it once it's there
        UISkin? painted = null;
        if (noon is not null && skin is not null)
        {
            compositor.WarmSkin(skin.Directory, skin.File, noon);
            if ((painted = compositor.Warmed) is null)
                return;
        }

        faceWarmed = warmed;
        bool bodyShown = body.Visible;
        body.Visible = false;
        noonView.Visible = true;
        try
        {
            FitNoon();
            compositor.PaintAside(painted);
        }
        finally
        {
            noonView.Visible = false;
            body.Visible = bodyShown;
        }
    }
}
