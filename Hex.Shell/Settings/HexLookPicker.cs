using System.Numerics;

using Hex.Core;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Shell;

/// <summary>What a mode does for the picker of looks, Dawn with its own settings and Dusk with the file's.</summary>
internal interface IHexLookOwner
{
    /// <summary>The editor's settings as they are now, Dawn's own, Dusk's read from the file.</summary>
    HexSettings ReadSettings();

    /// <summary>Dusk's theme as it is now.</summary>
    string TerminalTheme { get; }

    /// <summary>
    /// The settings the picker changed kept and put in force, a look's themes as well when one was picked (null when a
    /// look was only saved or deleted).
    /// </summary>
    void Keep(HexSettings settings, HexLook? picked);
}

/// <summary>
/// The looks in the settings, a card for each with a little picture of its glass over a field of light, the one Hex is
/// in ringed in the accent, a click on another to put it on, and under them Save as a profile, Update and Delete for the
/// user's own. Settings that no look has show as a card of their own, Custom, until they're saved as one. Both modes have
/// it, see <see cref="HexLooks"/>.
/// </summary>
internal sealed class HexLookPicker : StackPanel
{
    private readonly IHexLookOwner owner;
    private readonly LookCards cards;
    private readonly Button update, delete;

    // What the cards show, read once a refresh and not every paint, Dusk's come out of the file
    private List<HexLook> looks = [];
    private HexLook? current;
    private HexSettings shown = new(null);

    public HexLookPicker(IHexLookOwner owner, float width)
    {
        this.owner = owner;
        Spacing = 12.0f;
        Padding = new UIEdges(14.0f, 6.0f, 10.0f, 18.0f);
        Anchor = Origin.TopLeft;
        Pivot = Origin.TopLeft;

        Add(new Label { Text = HexText.Get("looks.about"), Wrap = true, Size = new Vector2(width - 24.0f, 0.0f), Align = Origin.TopLeft, Anchor = Origin.Left, Pivot = Origin.Left });
        cards = Add(new LookCards(this, width - 24.0f) { Anchor = Origin.Left, Pivot = Origin.Left });

        var buttons = Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Left, Pivot = Origin.Left });
        buttons.Add(new Button { Label = HexText.Get("looks.save_as"), Icon = "add", Size = new Vector2(0.0f, 30.0f), Animated = false, OnPressed = AskName });
        update = buttons.Add(new Button { Icon = "save", Size = new Vector2(0.0f, 30.0f), Animated = false, OnPressed = UpdatePicked });
        delete = buttons.Add(new Button { Icon = "trash", Size = new Vector2(0.0f, 30.0f), Animated = false, OnPressed = AskDelete });

        Refresh();
    }

    /// <summary>The cards and the buttons read again from the settings, after anything changed them.</summary>
    public void Refresh()
    {
        shown = owner.ReadSettings();
        looks = HexLooks.All(shown);
        current = HexLooks.Current(shown, owner.TerminalTheme);

        // Update is for one of the user's that was picked and changed since, Delete for one of the user's
        HexLook? last = HexLooks.Find(shown, shown.Look);
        bool theirs = last is { BuiltIn: false };
        update.Visible = theirs && current != last;
        update.Label = HexText.Get("looks.update", ("name", shown.Look));
        delete.Visible = theirs;
        delete.Label = HexText.Get("looks.delete", ("name", shown.Look));
    }

    // A card clicked, its look put on
    private void Pick(HexLook look)
    {
        HexSettings settings = owner.ReadSettings();
        HexLooks.Apply(look, settings);
        owner.Keep(settings, look);
        Refresh();
    }

    // Save as a profile, a name asked for and the settings kept under it
    private void AskName()
    {
        if (Module is not { } module)
            return;

        UIDialog dialog = UIDialog.Show(module, HexText.Get("looks.save_title"), HexText.Get("looks.save_text"),
            new DialogChoice(HexText.Get("looks.save")), new DialogChoice(HexText.Get("looks.cancel")));
        var box = dialog.Body.Add(new TextBox { Text = HexLooks.FreshName(owner.ReadSettings(), HexText.Get("looks.my_look")), Size = new Vector2(360.0f, 30.0f), MaxLength = 40 });

        void Save(string name)
        {
            dialog.Close();
            HexSettings settings = owner.ReadSettings();
            if (!HexLooks.Save(settings, name, owner.TerminalTheme))
                return;
            owner.Keep(settings, null);
            Refresh();
        }

        dialog.Buttons[0].OnPressed = () => Save(box.Text);
        box.OnSubmitted = Save;
        box.SelectAll();
        box.Focus();
    }

    // The user's look picked last kept again with the settings as they are now
    private void UpdatePicked()
    {
        HexSettings settings = owner.ReadSettings();
        if (HexLooks.Save(settings, settings.Look, owner.TerminalTheme))
            owner.Keep(settings, null);
        Refresh();
    }

    // The user's look picked last forgotten, once they've said so
    private void AskDelete()
    {
        if (Module is not { } module)
            return;

        string name = shown.Look;
        UIDialog.Show(module, HexText.Get("looks.delete_title", ("name", name)), HexText.Get("looks.delete_text"),
            new DialogChoice(HexText.Get("looks.delete_yes"), () =>
            {
                HexSettings settings = owner.ReadSettings();
                if (HexLooks.Delete(settings, name))
                    owner.Keep(settings, null);
                Refresh();
            }),
            new DialogChoice(HexText.Get("looks.cancel")));
    }

    /// <summary>
    /// The cards themselves, painted rather than made of components, a little picture each of the look's glass over a field
    /// of light the way it sits over the editor, its name and a line about it under.
    /// </summary>
    private sealed class LookCards(HexLookPicker picker, float width) : UIComponent
    {
        private const float CARD_WIDTH = 140.0f, PICTURE = 84.0f, NAME = 22.0f, ABOUT = 18.0f, GAP = 12.0f, ROUND = 12.0f;

        private float CardHeight => PICTURE + NAME + ABOUT + 6.0f;

        // How many go across, and how many there are with Custom when nothing the settings say is a look
        private int Across => Math.Max(1, (int)((width + GAP) / (CARD_WIDTH + GAP)));
        private int Count => picker.looks.Count + (picker.current is null ? 1 : 0);

        protected override bool HitTestVisible => true;

        protected override Vector2 Measure(UISkin skin)
        {
            int rows = (Count + Across - 1) / Across;
            return new Vector2(width, rows * CardHeight + Math.Max(0, rows - 1) * GAP);
        }

        private UIRect CardBounds(int i)
        {
            int column = i % Across, row = i / Across;
            float left = Bounds.Min.X + column * (CARD_WIDTH + GAP), top = Bounds.Max.Y - row * (CardHeight + GAP);
            return new UIRect(new Vector2(left, top - CardHeight), new Vector2(left + CARD_WIDTH, top));
        }

        private int CardAt(Vector2 point)
        {
            for (int i = 0; i < Count; i++)
            {
                if (CardBounds(i).Contains(point))
                    return i;
            }

            return -1;
        }

        private Vector2 Pointer => Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : default;

        protected override void OnPointerUp(Vector2 point)
        {
            int at = CardAt(point);
            if (at >= 0 && at < picker.looks.Count && picker.looks[at] != picker.current)
                picker.Pick(picker.looks[at]);
        }

        protected override void Paint(UIDrawList list)
        {
            UISkin skin = list.Skin;
            int over = IsHovered ? CardAt(Pointer) : -1;
            for (int i = 0; i < Count; i++)
            {
                list.BeginPart(i);
                HexLook? look = i < picker.looks.Count ? picker.looks[i] : null;
                bool picked = look is null || look == picker.current;
                UIGlass? glass = look is null ? (picker.shown.EffectGlass ? HexGlass.Popup(picker.shown.GlassBlur, picker.shown.GlassClarity, picker.shown.GlassRim, picker.shown.GlassBend) : null)
                    : HexLooks.GlassOf(look, picker.shown);
                PaintCard(list, skin, CardBounds(i), look, glass, picked, i == over);
            }

            list.EndParts();
        }

        private static void PaintCard(UIDrawList list, UISkin skin, UIRect card, HexLook? look, UIGlass? glass, bool picked, bool over)
        {
            UIPalette palette = skin.Palette;
            var picture = new UIRect(new Vector2(card.Min.X, card.Max.Y - PICTURE), card.Max);

            // The field of light, a few blooms of the theme's colours over its field, the way GlowField has the editor's
            list.PushClip(picture);
            list.RoundRect(picture, ROUND, Vector4.Lerp(palette.Field, palette.Panel, 0.35f) with { W = 1.0f });
            Bloom(list, picture, new Vector2(0.12f, 0.15f), 0.55f, palette.Accent with { W = 0.75f });
            Bloom(list, picture, new Vector2(0.9f, 0.9f), 0.6f, palette.Syntax.Type with { W = 0.6f });
            Bloom(list, picture, new Vector2(0.55f, 0.05f), 0.45f, palette.Syntax.Directive with { W = 0.5f });
            Bloom(list, picture, new Vector2(0.15f, 0.95f), 0.4f, palette.Syntax.Keyword with { W = 0.5f });

            // What's behind the pane, a few lines of text and a disc of the accent, so how much of it shows through says what
            // the glass is. The three cards were the same dark pane on the same light, and nobody could tell them apart
            Vector4 ink = palette.Text;
            for (int line = 0; line < 3; line++)
            {
                float y = picture.Max.Y - 20.0f - line * 14.0f;
                list.RoundRect(new UIRect(new Vector2(picture.Min.X + 10.0f, y - 4.0f), new Vector2(picture.Max.X - 10.0f - line * 22.0f, y)), 2.0f, ink with { W = 0.5f });
            }
            list.RoundRect(UIRect.FromCenter(new Vector2(picture.Min.X + picture.Width * 0.66f, picture.Min.Y + 32.0f), new Vector2(26.0f)), 13.0f, palette.Accent with { W = 1.0f });

            // A pane over it, liquid glass clear with what's behind sharp through it and a bright rim and a light along its
            // top, frosted milky with what's behind a smudge of colour, flat a solid panel that hides it
            var pane = new UIRect(new Vector2(picture.Min.X + picture.Width * 0.3f, picture.Min.Y + 12.0f), new Vector2(picture.Max.X - 12.0f, picture.Max.Y - 12.0f));
            Vector4 panel = palette.Panel with { W = 1.0f };
            if (glass is { Bend: > 0.0f } liquid)
            {
                list.Glass(pane, 8.0f, liquid.Blur * 0.25f, liquid.Bend * 0.5f);
                list.RoundRect(pane, 8.0f, panel with { W = 0.3f });
                list.RoundOutline(pane, 8.0f, 1.5f, Vector4.One with { W = 0.5f });
                list.RoundRect(new UIRect(new Vector2(pane.Min.X + 8.0f, pane.Max.Y - 3.0f), new Vector2(pane.Max.X - 8.0f, pane.Max.Y - 2.0f)), 0.5f, Vector4.One with { W = 0.55f });
            }
            else if (glass is { } frosted)
            {
                list.Glass(pane, 8.0f, frosted.Blur * 0.6f, 0.0f);
                list.RoundRect(pane, 8.0f, Vector4.Lerp(panel, Vector4.One, 0.14f) with { W = 0.72f });
                list.SilverLining(pane, 8.0f, frosted.Rim);
            }
            else if (look?.Name == HexLooks.VISUAL_STUDIO)
            {
                // Visual Studio's square and solid, its hairline round it
                list.Rect(pane, panel);
                list.Outline(pane, 1.0f, palette.Border);
            }
            else
            {
                list.RoundRect(pane, 8.0f, panel);
                list.RoundOutline(pane, 8.0f, 1.0f, palette.Border);
            }

            list.RoundRect(new UIRect(new Vector2(pane.Min.X + 10.0f, pane.Max.Y - 16.0f), new Vector2(pane.Max.X - 24.0f, pane.Max.Y - 12.0f)), 2.0f, ink with { W = 0.6f });
            list.RoundRect(new UIRect(new Vector2(pane.Min.X + 10.0f, pane.Max.Y - 26.0f), new Vector2(pane.Center.X + 8.0f, pane.Max.Y - 22.0f)), 2.0f, ink with { W = 0.32f });
            list.PopClip();

            // The one Hex is in ringed in the accent a little outside the picture, the one under the pointer lit
            list.RoundOutline(picture.Shrink(new UIEdges(-3.0f)), ROUND + 3.0f, 2.0f, palette.Accent with { W = picked ? 1.0f : 0.0f });
            list.RoundOutline(picture, ROUND, 1.0f, picked ? palette.Border with { W = 0.0f } : over ? ink with { W = 0.35f } : palette.Border);

            // Its name and a line about it
            float scale = skin.TextScale;
            var name = new UIRect(new Vector2(card.Min.X + 2.0f, picture.Min.Y - 6.0f - NAME), new Vector2(card.Max.X, picture.Min.Y - 6.0f));
            list.Text(look?.Name ?? HexText.Get("looks.custom"), name, Origin.Left, scale, picked ? ink : ink with { W = ink.W * 0.85f }, markup: false);
            var about = new UIRect(new Vector2(card.Min.X + 2.0f, name.Min.Y - ABOUT), new Vector2(card.Max.X, name.Min.Y));
            list.Text(About(look), about, Origin.Left, scale * 0.85f, palette.DimText, markup: false);
        }

        // A bloom of light in the picture, where as a share of it, how far as a share of its width
        private static void Bloom(UIDrawList list, UIRect picture, Vector2 at, float reach, Vector4 color) =>
            list.Shadow(UIRect.FromCenter(picture.Min + at * picture.Size, new Vector2(2.0f)), 0.0f, reach * picture.Width, color);

        private static string About(HexLook? look) => look switch
        {
            null => HexText.Get("looks.custom_about"),
            { Name: HexLooks.FLUID } => HexText.Get("looks.fluid_about"),
            { Name: HexLooks.FROSTED } => HexText.Get("looks.frosted_about"),
            { Name: HexLooks.FLAT } => HexText.Get("looks.flat_about"),
            { Name: HexLooks.VISUAL_STUDIO } => HexText.Get("looks.visual_studio_about"),
            _ => HexText.Get("looks.yours")
        };
    }
}
