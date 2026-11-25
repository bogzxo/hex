namespace Hex.Dawn;

// The self test of the effects. Everything off first (the effects none, the glass and the glide switched off, which is
// the Flat look), the caret gliding on its own switch and a save bursting with the effects subtle and the sparks on,
// sparks off the caret as it types with them all, the glass under the sides and the palette on its own switch whatever
// the effects are, and everything put back as it was at the end.
internal sealed partial class DawnScene
{
    // A picture of the window as it is, into the folder HEX_SELFTEST_SHOTS names, nothing without it
    private void GlassShot(HexPointerScript test, string name) => test.Run(() =>
    {
        if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
            Engine.CaptureScreenshot(Path.Combine(shots, name));
    });

    private void TestEffects(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "effects.txt");
        string was = settings.Effects;
        float wasGlide = settings.CaretGlide;
        float wasAnimation = settings.UiAnimation;
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
            settings.CaretGlide = Hex.Shell.EditorSettingsCatalog.CARET_GLIDE;
            settings.UiAnimation = 1.0f;
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
        GlassShot(test, "effects_glass_palette.png");
        test.Settle(0.2f);
        test.Check("effects: one blur for every pane at the most", () => compositor.GlassDrawn.Blurs is > 0 && compositor.GlassDrawn.Blurs <= compositor.GlassDrawn.Panes);
        test.Run(() => chrome.Dialog?.Close());
        test.WaitFor(() => compositor.GlassDrawn.Panes < palette, 3.0f);
        test.Check("effects: and the palette over the code is a pane more, gone with it", () => palette > sides && compositor.GlassDrawn.Panes < palette);

        // The settings' own picture of the glass, for whoever is looking at how it bends (HEX_SELFTEST_SHOTS)
        if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 })
        {
            // Over a picture where one is named (HEX_SELFTEST_BACKDROP), glass over a flat dark editor bends nothing to see
            var wasBackdrop = (settings.BackdropOn, settings.BackdropImage, settings.BackdropOpacity);
            string? picture = System.Environment.GetEnvironmentVariable("HEX_SELFTEST_BACKDROP");
            string codeFile = Path.Combine(testFolder, "effects_glass.cs");
            HexDocument? wasShown = null;
            test.Run(() =>
            {
                // A screen of highlighted code under the glass, which is what it's over most of the time
                wasShown = document;
                File.WriteAllLines(codeFile, Enumerable.Range(0, 70).Select(line => (line % 7) switch
                {
                    0 => $"    /// <summary>Line {line} of the code the glass sits over, words and numbers and names.</summary>",
                    1 => $"    public static readonly Vector4 Colour{line} = new({line}.0f, 0.5f, 0.25f, 1.0f); // a comment out to the side",
                    2 => $"    private readonly Dictionary<string, List<int>> lookup{line} = new(StringComparer.Ordinal);",
                    3 => $"    public async Task<bool> Compute{line}Async(string name, int count = {line}, CancellationToken cancel = default)",
                    4 => $"        => await Task.Run(() => name.Length * count > {line * 3} && lookup{line - 2}.ContainsKey(\"key {line}\"), cancel);",
                    5 => $"    if (value is {{ Length: > {line} }} text && text.StartsWith(\"glass\", StringComparison.Ordinal)) return true;",
                    _ => string.Empty
                }));
                Open(codeFile);
                SwitchView(HexView.Code);
            });
            test.Settle(1.0f);
            test.Run(() =>
            {
                if (picture is not { Length: > 0 } || !File.Exists(picture))
                    return;
                (settings.BackdropOn, settings.BackdropImage, settings.BackdropOpacity) = (true, picture, 1.0f);
                ApplyBackdrop();
            });
            test.Settle(0.6f);
            test.Run(CommandPalette);
            test.WaitFor(() => chrome.Dialog is { IsOpen: true }, 3.0f);
            test.Settle(0.6f);
            GlassShot(test, "effects_glass_palette_picture.png");
            test.Settle(0.3f);
            test.Run(() => chrome.Dialog?.Close());
            test.WaitFor(() => chrome.Dialog is null, 3.0f);

            test.Run(() => OpenSettings(Hex.Shell.EditorSettingsCatalog.GLASS));
            test.WaitFor(() => SettingsPage is not null, 3.0f);
            test.Settle(0.8f);
            GlassShot(test, "effects_glass_settings.png");
            test.Settle(0.3f);
            test.Run(() =>
            {
                CloseSettings();
                (settings.BackdropOn, settings.BackdropImage, settings.BackdropOpacity) = wasBackdrop;
                ApplyBackdrop();
                if (document.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(codeFile))
                    Close();
                if (wasShown is not null && documents.Contains(wasShown))
                    Show(wasShown);
            });
            test.WaitFor(() => SettingsWindow is null, 3.0f);
            test.Settle(0.3f);
        }
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

        float arrivalStart = 0, arrivalLast = 0;
        bool progressed = false, reversed = false;
        HexDocument? previous = null;
        test.Run(() =>
        {
            previous = document;
            settings.UiAnimation = 0.5f;
            ApplyEffects();
            string next = Path.Combine(testFolder, "switch-tab.txt");
            File.WriteAllText(next, "Second editor");
            Open(next);
            arrivalStart = arrivalLast = code.Opacity;
        });
        test.WaitFor(() =>
        {
            float opacity = code.Opacity;
            progressed |= opacity > arrivalStart && opacity < 1;
            reversed |= opacity < arrivalLast;
            arrivalLast = opacity;
            return opacity >= 1;
        }, 2);
        test.Check("effects: tab content stays readable and brightens monotonically", () =>
            arrivalStart >= 0.8f && arrivalStart < 1 && progressed && !reversed && code.Opacity == 1);
        test.Run(() =>
        {
            CloseNow();
            Show(previous!);
            settings.UiAnimation = 0;
            ApplyEffects();
        });
        test.Wait();
        test.Check("effects: disabling motion settles a returning editor immediately", () => code.Opacity == 1);

        test.Run(() =>
        {
            settings.Effects = was;
            settings.EffectSparks = wasSparks;
            settings.EffectMotion = wasMotion;
            settings.CaretGlide = wasGlide;
            settings.UiAnimation = wasAnimation;
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
