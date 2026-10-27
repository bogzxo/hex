using System.Numerics;

using Hex.Shell;

using Horizon.HIDL.Runtime;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The self test of the Visual Studio look. Picked from the View menu's looks with a C# file open, it puts the flat skin's
// Visual Studio theme on with nothing round, the frame flush, no glass, the code on Visual Studio's near black in Cascadia
// Mono with its blue keywords and pink control ones, and the terminals in Campbell. With HEX_SELFTEST_SHOTS set it leaves
// a picture of the editor, a menu, the palette and the settings in it. Everything goes back as it was at the end.
internal sealed partial class DawnScene
{
    private const string VISUAL_STUDIO_CSHARP = """
        using System;

        namespace Shapes;

        // A shape that knows its own area
        public interface IShape { double Area { get; } }

        public class Circle : IShape
        {
            private const double Tau = 6.28;
            public double Radius { get; set; }
            public double Area => Radius * Radius * Tau / 2;

            public string Describe(int digits)
            {
                if (digits < 0)
                    return "nothing";
                var text = Math.Round(Area, digits).ToString();
                return $"a circle of {text}";
            }
        }

        """;

    private void TestVisualStudioLook(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "selftest_visual_studio"), file = Path.Combine(folder, "VisualStudio.cs");
        HexLook was = new("was", new Dictionary<string, IRuntimeValue>());
        string wasLook = string.Empty, wasTheme = string.Empty;
        bool wasGlass = false;

        test.Run(() =>
        {
            chrome.Dialog?.Close();
            CloseSettings();

            // What the settings were in, to put back, the code's font and C#'s colours with the rest a look keeps
            var values = HexLooks.Capture(settings, terminals.Settings.Theme);
            values[HexLooks.CODE_FONT] = new StringValue(settings.CodeFont);
            values[HexLooks.CSHARP_COLORS] = new StringValue(settings.CSharpColors);
            was = new HexLook("was", values);
            wasLook = settings.Look;
            wasTheme = settings.EditorTheme;
            wasGlass = settings.EffectGlass;

            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "selftest_visual_studio.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, VISUAL_STUDIO_CSHARP.ReplaceLineEndings("\n"));
            Open(file);
        });
        test.Wait();

        // Picked from the View menu, the way a hand picks it
        test.Run(() => lookMenu!.Everything().First(pair => pair.Item.Label == HexLooks.VISUAL_STUDIO).Item.OnPicked?.Invoke());
        test.WaitFor(() => compositor.Skin?.Theme == HexLooks.VISUAL_STUDIO_THEME, 3.0f);
        test.Settle(0.3f);

        // csharp-ls's names in the picture when it's on this machine, the locals and parameters their own blue
        if (HexServerCatalog.Locate("csharp-ls") is not null)
            test.WaitFor(() => code.SemanticSpans.Any(span => span.Kind == SyntaxKind.Parameter), 90.0f);

        static bool Near(Vector4 a, Vector4 b) => Vector4.Distance(a, b) < 0.01f;
        static Vector4 Rgb(uint rgb) => new(((rgb >> 16) & 0xFF) / 255.0f, ((rgb >> 8) & 0xFF) / 255.0f, (rgb & 0xFF) / 255.0f, 1.0f);

        test.Check("visual studio: the look is picked and the editor is in its theme", () =>
            HexLooks.Current(settings, terminals.Settings.Theme)?.Name == HexLooks.VISUAL_STUDIO
            && settings.EditorSkin == TOOL_SKIN_NAME && settings.EditorTheme == HexLooks.VISUAL_STUDIO_THEME);
        test.Check("visual studio: nothing is round, the cards flush and square, the popups square", () =>
            compositor.Skin is { CornerRadius: 0.0f, PopupRadius: 0.0f }
            && left.Radius == 0.0f && center.Radius == 0.0f && right.Radius == 0.0f && left.Inset == 0.0f && center.Inset == 0.0f);
        test.Check("visual studio: and nothing is on glass", () =>
            !Glassy && left.Glass is null && compositor.PopupGlass is null && settings.Effects == "none");
        test.Check("visual studio: the code is on its near black, in Cascadia Mono", () =>
            Near(document.Editor.Color ?? default, Rgb(0x1E1E1E)) && settings.CodeFont == "cascadia_mono");
        test.Check("visual studio: C# has its blue keywords, pink control ones and teal types", () =>
            CodeLanguages.ColorsFor(code.Highlighter, compositor.Skin!.Palette) is var colors
            && Near(colors.Of(SyntaxKind.Keyword), Rgb(0x569CD6)) && Near(colors.Of(SyntaxKind.Control), Rgb(0xD8A0DF))
            && Near(colors.Of(SyntaxKind.Class), Rgb(0x4EC9B0)) && Near(colors.Of(SyntaxKind.Local), Rgb(0x9CDCFE)));
        test.Check("visual studio: and the terminals are in Campbell", () =>
            terminals.Settings.Theme == HexLooks.VISUAL_STUDIO_THEME
            && Horizon.UI.Components.TerminalPalette.FromPalette(compositor.Skin!.Palette) is var ansi && Near(ansi.Ansi[5], Rgb(0x881798)) && Near(ansi.Ansi[1], Rgb(0xC50F1F)));
        VisualStudioShot(test, "visual_studio_editor.png");

        // A menu, the palette and the settings, square and flat as well
        test.Run(() => menu.Open(1));
        test.Wait();
        VisualStudioShot(test, "visual_studio_menu.png");
        test.Run(menu.Close);
        test.Run(CommandPalette);
        test.WaitFor(() => chrome.Dialog is { IsOpen: true }, 3.0f);
        VisualStudioShot(test, "visual_studio_palette.png");
        test.Run(() => chrome.Dialog?.Close());
        test.Run(() => OpenSettings(EditorSettingsCatalog.PROFILES));
        test.Wait();
        test.Check("visual studio: the settings window has no glass either", () => SettingsWindow is { Glass: null });
        VisualStudioShot(test, "visual_studio_settings.png");
        test.Run(CloseSettings);
        test.Wait();

        // Everything back the way it was
        test.Run(() =>
        {
            HexLooks.Apply(was, settings);
            ((IHexLookOwner)this).Keep(settings, was);
            settings.Look = wasLook;
            if (document.Path is { } open && Horizon.Core.Platform.PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.WaitFor(() => compositor.Skin?.Theme == wasTheme, 3.0f);
        test.Check("visual studio: and put back, the theme, the glass and the round as they were", () =>
            settings.EditorTheme != HexLooks.VISUAL_STUDIO_THEME && compositor.Skin is { CornerRadius: > 0.0f } && left.Radius > 0.0f
            && Glassy == wasGlass);
    }

    // A picture of the screen into HEX_SELFTEST_SHOTS when it's set, once whatever was opening or closing has finished
    private void VisualStudioShot(HexPointerScript test, string name)
    {
        test.Settle(0.8f);
        SemanticShot(test, name);
    }
}
