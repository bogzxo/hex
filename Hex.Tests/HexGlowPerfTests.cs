using System.Numerics;

using Hex.Shell;

using Horizon.Rendering.Spriting;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Tests;

public class HexGlowPerfTests
{
    private static readonly UIFont Font = new(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf");
    private static readonly UISkin Skin = new(Font);
    private static readonly UISkin AltSkin = new(Font);
    private static readonly UIRect Screen = new(Vector2.Zero, new Vector2(1280.0f, 720.0f));

    private static void Paint(UIDrawList list, UIDrawList? previous, UIComponent root, UIRect bounds, int tick, UISkin? skin = null)
    {
        UISkin active = skin ?? Skin;
        root.MeasureTree(active);
        root.ArrangeTree(bounds);
        list.Previous = previous;
        list.Begin(active, tick, 1.0f / 120.0f, tick / 120.0f);
        root.PaintTree(list);
        list.End();
    }

    [Fact]
    public void Opaque_veil_skips_occluded_under_layers_with_exact_veil_colour()
    {
        var glow = new GlowField();
        glow.UseSkin(Skin);
        var veil = new Vector4(0.11f, 0.13f, 0.18f, 1.0f);
        glow.Veil = veil;
        var hole = new UIRect(new Vector2(200.0f, 100.0f), new Vector2(1000.0f, 620.0f));
        glow.Hole = () => hole;

        var list = new UIDrawList();
        Paint(list, null, glow, Screen, 1);

        // Four non-empty strips around the hole, 1 opaque veil quad per strip (4 instead of 24)
        Assert.Equal(4, list.Items.Length);
        uint expectedColor = SpriteItem.PackColor(veil);
        for (int i = 0; i < list.Items.Length; i++)
        {
            Assert.Equal(expectedColor, list.Items[i].Color);
            Assert.Equal(SpriteItem.NoTexture, list.Items[i].Flags);
        }
        Assert.Null(list.FramesDiffer());

        // Without a hole, only 1 opaque quad is emitted
        glow.Hole = null;
        Paint(list, null, glow, Screen, 2);
        Assert.Equal(1, list.Items.Length);
        Assert.Equal(expectedColor, list.Items[0].Color);
        Assert.Null(list.FramesDiffer());

        // A hole flush against the bottom-left corner leaves only 2 non-empty strips
        var cornerHole = new UIRect(Vector2.Zero, new Vector2(600.0f, 400.0f));
        glow.Hole = () => cornerHole;
        Paint(list, null, glow, Screen, 3);
        Assert.Equal(2, list.Items.Length);
        Assert.Null(list.FramesDiffer());
    }

    [Fact]
    public void Translucent_veil_preserves_visible_glow_layers_and_skips_invisible_ones()
    {
        var glow = new GlowField();
        glow.UseSkin(Skin);
        var veil = new Vector4(0.11f, 0.13f, 0.18f, 0.55f);
        glow.Veil = veil;
        var hole = new UIRect(new Vector2(200.0f, 100.0f), new Vector2(1000.0f, 620.0f));
        glow.Hole = () => hole;

        var list = new UIDrawList();
        Paint(list, null, glow, Screen, 1);

        uint baseColor = SpriteItem.PackColor(glow.Base);
        uint veilColor = SpriteItem.PackColor(veil);
        int baseQuads = 0, bloomQuads = 0, veilQuads = 0;
        for (int i = 0; i < list.Items.Length; i++)
        {
            SpriteItem item = list.Items[i];
            if ((item.Flags & SpriteItem.ShapeFlag) != 0)
                bloomQuads++;
            else if (item.Color == baseColor)
                baseQuads++;
            else if (item.Color == veilColor)
                veilQuads++;
        }

        Assert.Equal(4, baseQuads);
        Assert.True(bloomQuads > 0, "Expected visible bloom quads under a translucent veil.");
        Assert.Equal(4, veilQuads);
        Assert.Null(list.FramesDiffer());

        // Completely invisible layers emit zero quads
        glow.Base = new Vector4(0.2f, 0.3f, 0.4f, 0.0f);
        glow.Veil = new Vector4(0.2f, 0.3f, 0.4f, 0.0005f);
        glow.Blooms.Clear();
        glow.Blooms.Add(new GlowBloom(new Vector2(0.5f, 0.5f), 0.5f, new Vector4(1.0f, 1.0f, 1.0f, 0.0f)));
        Paint(list, null, glow, Screen, 2);
        Assert.Equal(0, list.Items.Length);
    }

    [Fact]
    public void GlowField_reuses_draw_list_when_static_and_invalidates_on_changes_with_CheckReuse()
    {
        UIRect? hole = new UIRect(new Vector2(180.0f, 90.0f), new Vector2(1040.0f, 640.0f));
        int holeCalls = 0;
        var glow = new GlowField
        {
            Hole = () =>
            {
                holeCalls++;
                return hole;
            }
        };
        glow.UseSkin(Skin);
        glow.Veil = new Vector4(0.12f, 0.14f, 0.19f, 0.6f);

        var a = new UIDrawList();
        var b = new UIDrawList();
        var fresh = new UIDrawList();

        // First paint records; second paint with identical state reuses
        holeCalls = 0;
        Paint(a, null, glow, Screen, 1);
        Assert.Equal(0, a.Reused);
        Assert.Equal(1, holeCalls);

        holeCalls = 0;
        Paint(b, a, glow, Screen, 2);
        Assert.Equal(1, b.Reused);
        Assert.Equal(1, holeCalls);

        Paint(fresh, null, glow, Screen, 2);
        Assert.True(b.SameAs(fresh), "Reused GlowField draw list must match a fresh paint byte-for-byte.");
        Assert.Null(b.FramesDiffer());

        // CheckReuse = true verifies zero diffs against the recorded previous list
        bool previousCheck = UIDrawList.CheckReuse;
        try
        {
            UIDrawList.CheckReuse = true;
            Paint(a, b, glow, Screen, 3);
            Assert.Equal(0, a.Reused);
            Assert.True(a.SameAs(fresh));
            Assert.Null(a.FramesDiffer());
        }
        finally
        {
            UIDrawList.CheckReuse = previousCheck;
        }

        // Re-verify normal reuse works after CheckReuse
        Paint(b, a, glow, Screen, 4);
        Assert.Equal(1, b.Reused);

        // Moving Hole invalidates immediately
        hole = new UIRect(new Vector2(260.0f, 120.0f), new Vector2(1100.0f, 660.0f));
        Paint(a, b, glow, Screen, 5);
        Assert.Equal(0, a.Reused);
        Paint(fresh, null, glow, Screen, 5);
        Assert.True(a.SameAs(fresh));

        // Next static tick reuses the new Hole layout
        Paint(b, a, glow, Screen, 6);
        Assert.Equal(1, b.Reused);

        // Changing Veil (including switching to opaque) invalidates immediately
        glow.Veil = new Vector4(0.12f, 0.14f, 0.19f, 1.0f);
        Paint(a, b, glow, Screen, 7);
        Assert.Equal(0, a.Reused);
        Assert.Equal(4, a.Items.Length);
        Paint(fresh, null, glow, Screen, 7);
        Assert.True(a.SameAs(fresh));

        // Next static tick in opaque mode reuses
        Paint(b, a, glow, Screen, 8);
        Assert.Equal(1, b.Reused);

        // Changing Bounds invalidates immediately
        var smaller = new UIRect(Vector2.Zero, new Vector2(960.0f, 540.0f));
        Paint(a, b, glow, smaller, 9);
        Assert.Equal(0, a.Reused);
        Paint(fresh, null, glow, smaller, 9);
        Assert.True(a.SameAs(fresh));

        // Changing Skin invalidates immediately
        Paint(b, a, glow, smaller, 10, AltSkin);
        Assert.Equal(0, b.Reused);
        Paint(fresh, null, glow, smaller, 10, AltSkin);
        Assert.True(b.SameAs(fresh));
    }

    [Fact]
    public void Idle_or_zero_alpha_lights_and_sparks_emit_nothing_and_allocate_nothing()
    {
        var lights = new Lights();
        var sparks = new Sparks();
        var effects = new HexLightEffects { CaretGlow = false, Spotlight = false, Ripples = false, Trail = false };
        var list = new UIDrawList();

        // Warm up once
        effects.Update(lights, sparks, new Vector2(100.0f, 100.0f), false, null, Vector4.One, Vector4.One);
        lights.UpdateTree(1.0f / 60.0f);
        sparks.UpdateTree(1.0f / 60.0f);
        Paint(list, null, lights, Screen, 1);
        Paint(list, null, sparks, Screen, 2);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++)
        {
            effects.Update(lights, sparks, new Vector2(100.0f + i, 100.0f), false, null, Vector4.One, Vector4.One);
            lights.UpdateTree(1.0f / 60.0f);
            sparks.UpdateTree(1.0f / 60.0f);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);

        // Zero-alpha lights and sparks emit zero quads
        lights.Shine(1, new Vector2(200.0f, 200.0f), 64.0f, new Vector4(1.0f, 0.5f, 0.2f, 0.0f));
        lights.Ripple(new Vector2(200.0f, 200.0f), new Vector4(1.0f, 0.5f, 0.2f, 0.0f));
        lights.UpdateTree(1.0f / 60.0f);
        Paint(list, null, lights, Screen, 3);
        Assert.Equal(0, list.Items.Length);

        sparks.Emit(new Vector2(200.0f, 200.0f), 8, new Vector4(1.0f, 0.5f, 0.2f, 0.0f));
        Paint(list, null, sparks, Screen, 4);
        Assert.Equal(0, list.Items.Length);
    }
}
