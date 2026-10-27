namespace Hex.Dawn;

// The self test of the effects. Everything off first (the effects none, the glass and the glide switched off, which is
// the Flat look), the caret gliding on its own switch and a save bursting with the effects subtle and the sparks on,
// sparks off the caret as it types with them all, the glass under the sides and the palette on its own switch whatever
// the effects are, and everything put back as it was at the end.
internal sealed partial class DawnScene
{
    private void TestEffects(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "effects.txt");
        string was = settings.Effects;
        bool wasSparks = settings.EffectSparks, wasMotion = settings.EffectMotion, wasGlass = settings.EffectGlass;

        test.Run(() =>
        {
            File.WriteAllText(file, "sparks\n");
            Open(file);
            settings.Effects = "none";
            settings.EffectMotion = false;
            settings.EffectGlass = false;
            ApplyEffects();
        });
        test.Settle(0.2f);
        test.Check("effects: all off, the caret jumps and nothing flies", () => code.CaretGlide == 0.0f && sparks.Count == 0);
        test.Check("effects: and nothing is on glass", () => compositor.PopupGlass is null && left.Glass is null && compositor.GlassDrawn.Panes == 0);

        test.Run(() =>
        {
            settings.Effects = "subtle";
            settings.EffectSparks = true;
            settings.EffectMotion = true;
            ApplyEffects();
            code.Focus();
            code.Document.Insert("typed ");
        });
        test.Wait();
        test.Check("effects: subtle, the caret glides and typing throws nothing", () => code.CaretGlide > 0.0f && sparks.Count == 0);
        test.Run(Save);
        test.Check("effects: and a save throws a burst", () => sparks.Count > 20);
        test.Settle(1.2f);
        test.Check("effects: which falls away by itself", () => sparks.Count == 0);

        // The glass, the sides on it as soon as the effects are, and the palette over the code on a pane of its own
        // Whatever an earlier part left up (a dialog, a menu) is put away first, or it's counted in with the sides
        int sides = 0, palette = 0;
        test.Run(() =>
        {
            chrome.Dialog?.Close();
            settings.EffectGlass = true;
            ApplyEffects();
        });
        test.WaitFor(() => compositor.GlassDrawn.Panes > 0 && chrome.Dialog is null, 3.0f);
        test.Settle(0.3f);
        test.Run(() => sides = compositor.GlassDrawn.Panes);
        test.Check("effects: subtle, the sides sit on glass", () => left.Glass is not null && compositor.PopupGlass is not null && sides > 0);
        test.Run(CommandPalette);
        test.WaitFor(() => chrome.Dialog is { IsOpen: true } && compositor.GlassDrawn.Panes > sides, 3.0f);
        test.Run(() => palette = compositor.GlassDrawn.Panes);
        test.Check("effects: one blur for every pane at the most", () => compositor.GlassDrawn.Blurs is > 0 && compositor.GlassDrawn.Blurs <= compositor.GlassDrawn.Panes);
        test.Run(() => chrome.Dialog?.Close());
        test.WaitFor(() => compositor.GlassDrawn.Panes < palette, 3.0f);
        test.Check("effects: and the palette over the code is a pane more, gone with it", () => palette > sides && compositor.GlassDrawn.Panes < palette);
        test.Run(() => settings.EffectGlass = false);
        test.Run(ApplyEffects);
        test.WaitFor(() => compositor.GlassDrawn.Panes == 0, 3.0f);
        test.Check("effects: and switched off on its own, the glass goes and the rest stays", () => left.Glass is null && code.CaretGlide > 0.0f && compositor.GlassDrawn.Panes == 0);
        test.Run(() => settings.EffectGlass = true);
        test.Run(ApplyEffects);

        test.Run(() =>
        {
            settings.Effects = "all";
            ApplyEffects();
            code.Focus();
        });
        test.Wait();
        test.Run(() => code.Document.Insert("more "));
        test.Wait();
        test.Check("effects: all, typing throws sparks off the caret", () => sparks.Count > 0);

        // The light, a glow under the caret while code is typed into, gone once it's switched off, and a ring off a click
        test.Run(() =>
        {
            settings.EffectCaretGlow = true;
            settings.EffectRipples = true;
            ApplyEffects();
            code.Focus();
        });
        test.Settle(0.3f);
        test.Check("effects: a glow under the caret while code is typed into", () => lights.Lit);
        test.Run(() =>
        {
            settings.EffectCaretGlow = false;
            ApplyEffects();
        });
        test.Settle(0.8f);
        test.Check("effects: and it fades away once it's switched off", () => !lights.Lit);
        test.Click(() => chrome.ToWorld(code.Bounds.Center));
        test.Wait();
        test.Check("effects: a click throws a ring out", () => lights.Lit);
        test.Settle(0.8f);
        test.Check("effects: which spreads and goes", () => !lights.Lit);
        test.Run(() =>
        {
            settings.EffectCaretGlow = true;
            ApplyEffects();
        });

        test.Run(() =>
        {
            settings.Effects = was;
            settings.EffectSparks = wasSparks;
            settings.EffectMotion = wasMotion;
            settings.EffectGlass = wasGlass;
            ApplyEffects();

            // Closed without asking, it was typed into and a question about saving it sat over every part after this one
            CloseNow();
        });
        test.Settle(0.2f);
        test.Check("effects: and put back nothing is left flying", () => was != "none" || sparks.Count == 0);
        test.Check("effects: and the glass is as it was", () => wasGlass == (left.Glass is not null) && wasGlass == (compositor.PopupGlass is not null));
    }
}
