using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// The inspector, one row per property of whatever is selected.
internal sealed partial class HexDesigner
{
    /* The inspector: one row per property of whatever is selected, each with an editor for the kind of value it is */

    internal void RebuildInspector()
    {
        inspectorScroll.Visible = true;

        editors.Clear();
        inspectorRows.Clear();
        foreach (var child in inspector.Children.ToArray())
            inspector.Remove(child);

        inspectorScroll.Offset = 0;

        if (document.Selected is not { } selected)
        {
            window.Hint(inspector, context.Strings.Said("designer_inspector.select_something"));
            return;
        }

        // With several things selected the rows are those of the one picked last, and what is changed in them is
        // changed for all of them that have such a thing to change
        if (document.Selection.Count > 1)
            layout.Instantiate(NOTE_TEMPLATE, inspector).Get<Label>("note").Text = context.Strings.Said("designer_inspector.things_selected", ("count", document.Selection.Count.ToString()));

        string kind = UIModule.KindOf(selected) ?? selected.GetType().Name;

        // The rows go into folding sections by what they are about, the kind's own after what everything has, so a
        // long list of properties is the part somebody is working on and not a wall of it
        var sections = new Dictionary<string, Collapsible>();
        foreach (string title in SectionOrder)
            sections[title == KIND_SECTION ? kind.Replace('_', ' ') : title] = null!;

        AddRow(Section(sections, SectionOf("name", kind)), "name", into => NameEditor(selected, into));
        AddRow(Section(sections, SectionOf("kind", kind)), "kind", into =>
        {
            var shown = Editor(EditorTemplate.Kind, into).Get<Label>("kind");
            shown.Text = kind;
            shown.Size = shown.Size with { X = EditorWidth };
            return shown;
        });

        foreach (var (name, value) in HexDocument.ReadProperties(selected))
        {
            // Handlers are functions, there is nothing to type one into
            if (value is FunctionValue or NativeFunctionValue or NullValue)
                continue;

            AddRow(Section(sections, SectionOf(name, kind)), name, into => CreateEditor(selected, name, value, into));
        }
    }

    // The templates of what the inspector is made of, next to the editor's own layout
    private const string NOTE_TEMPLATE = "note.hor", HINT_TEMPLATE = "hint.hor";

    private enum EditorTemplate { Text, Number, YesNo, Choice, Slider, Pair, Four, Colour, Kind, Section }

    private static string TemplateOf(EditorTemplate template) => template switch
    {
        EditorTemplate.Text => "editors/text.hor",
        EditorTemplate.Number => "editors/number.hor",
        EditorTemplate.YesNo => "editors/yes_no.hor",
        EditorTemplate.Choice => "editors/choice.hor",
        EditorTemplate.Slider => "editors/slider.hor",
        EditorTemplate.Pair => "editors/pair.hor",
        EditorTemplate.Four => "editors/four.hor",
        EditorTemplate.Colour => "editors/colour.hor",
        EditorTemplate.Kind => "editors/kind.hor",
        _ => "editors/section.hor"
    };

    /// <summary>Helper method to build one of the inspector's templates inside of something.</summary>
    private UILayout Editor(EditorTemplate template, UIComponent into) => layout.Instantiate(TemplateOf(template), into);

    private UIComponent NameEditor(UIComponent selected, UIComponent into)
    {
        var box = Editor(EditorTemplate.Text, into).Get<TextBox>("box");
        box.Text = document.NameOf(selected);
        box.MaxLength = 32;
        box.Size = box.Size with { X = EditorWidth };
        box.OnChanged = text =>
        {
            if (document.Rename(selected, text))
            {
                treeDirty = codeDirty = true;
                context.Notices.Say(context.Strings.Said("designer_inspector.renamed_to", ("name", text)));
            }
            else
            {
                context.Notices.Say(context.Strings.Said("designer_inspector.name_refused", ("name", text)), error: true);
            }
        };

        return box;
    }

    // The sections of the properties, in the order they come, the one named after the kind standing in for its own
    private const string KIND_SECTION = "*";
    private static readonly string[] SectionOrder = ["general", KIND_SECTION, "placement", "hover", "entrance", "resizing", "look"];

    // Which sections were folded away, by title, kept from one thing selected to the next and the one after
    private readonly HashSet<string> foldedSections = [];

    /// <summary>Helper method to say which section of the properties a property goes in.</summary>
    private static string SectionOf(string name, string kind) => name switch
    {
        "name" or "kind" or "visible" or "enabled" or "layer" or "tooltip" => "general",
        "pos" or "size" or "anchor" or "pivot" or "fill" or "padding" or "scale" or "z" => "placement",
        "resize" or "min_size" or "max_size" => "resizing",
        "glass" or "lift" => "look",
        _ when name.StartsWith("hover") => "hover",
        _ when name.StartsWith("intro") => "entrance",
        _ => kind.Replace('_', ' ')
    };

    /// <summary>
    /// Helper method to find the section a row goes into, making it out of its template the first time it is asked for.
    /// It is put in its place among the ones there are by the order they come in, whichever of them happened to be made first.
    /// </summary>
    private Collapsible Section(Dictionary<string, Collapsible> sections, string title)
    {
        if (sections.TryGetValue(title, out var section) && section is not null)
            return section;

        section = Editor(EditorTemplate.Section, inspector).Get<Collapsible>("section");
        section.Title = title switch
        {
            "general" => context.Strings.Said("designer_inspector.section_general"),
            "placement" => context.Strings.Said("designer_inspector.section_placement"),
            "hover" => context.Strings.Said("designer_inspector.section_hover"),
            "entrance" => context.Strings.Said("designer_inspector.section_entrance"),
            "resizing" => context.Strings.Said("designer_inspector.section_resizing"),
            "look" => context.Strings.Said("designer_inspector.section_look"),

            // The kind's own, its name with a capital the way the others have one
            _ => title.Length > 0 ? char.ToUpperInvariant(title[0]) + title[1..] : title
        };
        section.Expanded = !foldedSections.Contains(title);
        section.OnToggled = open =>
        {
            if (open) foldedSections.Remove(title);
            else foldedSections.Add(title);
        };

        sections[title] = section;

        // Before the first one made already that comes after it, and after what is said over all of them
        int index = inspector.Children.TakeWhile(child => child is not Collapsible).Count();
        foreach (var (other, made) in sections)
        {
            if (other == title)
                break;
            if (made is not null && made != section && made.Parent == inspector)
                index = inspector.Children.ToList().IndexOf(made) + 1;
        }

        inspector.MoveChild(section, index);
        return section;
    }

    /// <summary>
    /// Helper method to play the entrance of everything in the layout from the start, the way loading it in a
    /// program does. The whole of it and not only what is selected, a delay means nothing without the rest.
    /// </summary>
    internal void PlayIntros()
    {
        document.PlayIntros();
        context.Notices.Say(context.Strings.Said("designer_inspector.playing_entrances"));
    }

    // The properties that name a piece of art of the skin, the look of anything that has one, a panel's backdrop,
    // what an image shows, the handle of a slider
    private static readonly HashSet<string> ArtProperties = ["style", "background", "region", "handle"];

    // The menu of art that was opened last, for the self-test to find its tiles
    internal ArtPicker? artPicker;

    /// <summary>
    /// Helper method to add a row for a property, the row out of the inspector's template and its editor made inside of
    /// the room it leaves for one. A property with nothing to edit it with gets no row.
    /// </summary>
    private void AddRow(UIComponent section, string name, Func<StackPanel, UIComponent?> make)
    {
        var row = layout.Item(inspector, section);

        var caption = row.Get<Label>("caption");
        caption.Text = name.Replace('_', ' ');
        caption.Size = caption.Size with { X = CaptionWidth };

        var room = row.Get<StackPanel>("editor");
        if (make(room) is not { } editor)
        {
            section.Remove(row.Get<UIComponent>("row"));
            return;
        }

        // What the property does, on its name and on whatever is in the editor that has nothing of its own to say
        string help = UIPropertyHelp.Describe(document.Selected is { } shown ? UIModule.KindOf(shown) : null, name);
        caption.Tooltip = help;
        room.Tooltip = help;
        editors[name] = editor;
        inspectorRows[name] = (row.Get<UIComponent>("row"), section);
    }

    // Every row of the inspector by its property and the section it's in, for the Properties panel's filter
    private readonly Dictionary<string, (UIComponent Row, UIComponent Section)> inspectorRows = [];

    /// <summary>
    /// The rows narrowed down to the properties whose names have some text in them, the sections left with none taken away
    /// with them, everything back for an empty filter. The Properties panel's filter box.
    /// </summary>
    internal void FilterRows(string text)
    {
        text = text.Trim();
        var shownSections = new HashSet<UIComponent>();
        foreach (var (name, (row, section)) in inspectorRows)
        {
            row.Visible = text.Length == 0 || name.Replace('_', ' ').Contains(text, StringComparison.OrdinalIgnoreCase) || name.Contains(text, StringComparison.OrdinalIgnoreCase);
            if (row.Visible)
                shownSections.Add(section);
        }

        foreach (var (_, section) in inspectorRows.Values)
            section.Visible = text.Length == 0 || shownSections.Contains(section);
    }

    /// <summary>A number box of an editor's template, given its value, its width and what a change does.</summary>
    private static NumberBox Number(UILayout made, string part, float width, float value, Action<float> changed)
    {
        var number = made.Get<NumberBox>(part);
        number.Value = value;
        number.Size = number.Size with { X = width };
        number.OnValueChanged = changed;
        return number;
    }

    private UIComponent? CreateEditor(UIComponent target, string name, IRuntimeValue value, StackPanel into)
    {
        switch (value)
        {
            case NumberValue number when Ranges.ContainsKey(name) || (name == "value" && target is Slider):
            {
                // A slider for getting there quickly and the number next to it for getting there exactly
                var (min, max) = target is Slider ranged && name == "value" ? (ranged.Min, ranged.Max) : Ranges[name];
                var made = Editor(EditorTemplate.Slider, into);

                var slider = made.Get<Slider>("slider");
                slider.Min = min;
                slider.Max = max;
                slider.Value = number.Value;
                slider.Size = slider.Size with { X = EditorWidth - 72 };

                NumberBox exact = Number(made, "exact", 68, number.Value, changed =>
                {
                    slider.Value = changed;
                    Set(target, name, new NumberValue(changed));
                });
                exact.DragStep = (max - min) / 100.0f;

                slider.OnChanged = changed =>
                {
                    // To the hundredth, nobody wants 0.3478 seconds
                    changed = MathF.Round(changed, 2);
                    exact.Value = changed;
                    Set(target, name, new NumberValue(changed));
                };
                return made.Get<StackPanel>("row");
            }

            case NumberValue number:
                return Number(Editor(EditorTemplate.Number, into), "number", EditorWidth, number.Value, changed => Set(target, name, new NumberValue(changed)));

            case BooleanValue boolean:
            {
                // A switch, the way a Mac's inspector has yes or no. It was "< yes >" with the accent's arrows either
                // side, on every other row
                var toggle = Editor(EditorTemplate.YesNo, into).Get<ToggleButton>("toggle");
                toggle.Size = toggle.Size with { X = EditorWidth };
                toggle.State = boolean.Value;
                toggle.OnPressed = () => Set(target, name, new BooleanValue(toggle.State));
                return toggle;
            }

            case StringValue text when Choices.TryGetValue(name, out var choices):
            {
                var choice = Editor(EditorTemplate.Choice, into).Get<Dropdown>("choice");
                choice.Options = choices;
                choice.Size = choice.Size with { X = EditorWidth };
                choice.Value = text.Value;
                choice.OnChanged = chosen => Set(target, name, new StringValue(chosen));
                return choice;
            }

            case StringValue text:
            {
                var box = Editor(EditorTemplate.Text, into).Get<TextBox>("box");
                box.Text = text.Value;
                box.Size = box.Size with { X = EditorWidth };

                // A quote can't be written into a script, so one never gets as far as the component
                box.OnChanged = typed => Set(target, name, new StringValue(typed.Replace('"', '\'')));

                // What names a piece of the skin's art gets a button that shows the lot of it to pick from, the
                // art of the skin the layouts are shown in and not the plain one the editor wears
                if (ArtProperties.Contains(name) && stage.Skin is { } art)
                {
                    box.ButtonLabel = "...";
                    box.Tooltip = context.Strings.Said("designer_inspector.art_button_tooltip");
                    box.OnButtonPressed = () => artPicker = ArtPicker.Show(chrome, art, box.Text, chosen =>
                    {
                        box.Text = chosen;
                        Set(target, name, new StringValue(chosen));
                        context.Notices.Say(context.Strings.Said("designer_inspector.property_is", ("property", name), ("chosen", chosen)));
                    });
                }

                return box;
            }

            case Vector2Value vector:
            {
                Vector2 current = vector.Value;
                var made = Editor(EditorTemplate.Pair, into);

                // The rest of what is selected only takes the number that was changed, each keeps its other one.
                // Setting the x of five things mustn't put them all at the same y
                Number(made, "x", (EditorWidth - 4) / 2, current.X, x =>
                {
                    current.X = x;
                    Set(target, name, new Vector2Value(current), theirs => theirs is Vector2Value had ? new Vector2Value(had.Value with { X = x }) : null);
                });
                Number(made, "y", (EditorWidth - 4) / 2, current.Y, y =>
                {
                    current.Y = y;
                    Set(target, name, new Vector2Value(current), theirs => theirs is Vector2Value had ? new Vector2Value(had.Value with { Y = y }) : null);
                });
                return made.Get<StackPanel>("row");
            }

            case Vector4Value vector:
            {
                // Colours go from 0 to 1, everything else with four numbers is edges in pixels. A colour is picked by eye
                // on top, the numbers it comes to underneath for when they have to be exact
                bool colour = name is "color" or "tint";
                Vector4 current = vector.Value;
                var made = Editor(colour ? EditorTemplate.Colour : EditorTemplate.Four, into);
                var numbers = new NumberBox[4];
                ColorPicker? picker = colour ? made.Get<ColorPicker>("picker") : null;
                string[] parts = ["a", "b", "c", "d"];

                for (int i = 0; i < 4; i++)
                {
                    int component = i;
                    numbers[i] = Number(made, parts[i], (EditorWidth - 12) / 4, current[i], changed =>
                    {
                        current[component] = changed;
                        if (picker is not null) picker.Color = current;
                        Set(target, name, new Vector4Value(current), theirs =>
                        {
                            if (theirs is not Vector4Value had) return null;

                            Vector4 kept = had.Value;
                            kept[component] = changed;
                            return new Vector4Value(kept);
                        });
                    });
                    numbers[i].DragStep = colour ? 0.02f : 1.0f;
                }

                if (picker is null)
                    return made.Get<StackPanel>("row");

                picker.Size = new Vector2(EditorWidth, PICKER_HEIGHT);
                picker.Color = current;
                picker.OnChanged = picked =>
                {
                    current = new Vector4(MathF.Round(picked.X, 3), MathF.Round(picked.Y, 3), MathF.Round(picked.Z, 3), MathF.Round(picked.W, 3));
                    for (int i = 0; i < 4; i++)
                        numbers[i].Value = current[i];

                    Set(target, name, new Vector4Value(current));
                };
                return made.Get<StackPanel>("both");
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Helper method to set a property of what the inspector shows, and of everything else that is selected and has
    /// a property by that name, so a row changed with ten things selected is changed for the ten.
    /// </summary>
    /// <param name="forOthers">
    /// What the rest of the selection gets, worked out of what each of them has now, for a change that is only to a
    /// part of the value (one number of a position). Null from it leaves that one alone, left out they all get the value.
    /// </param>
    internal void Set(UIComponent target, string name, IRuntimeValue value, Func<IRuntimeValue, IRuntimeValue?>? forOthers = null)
    {
        if (HexDocument.Write(target, name, value) is { } problem)
        {
            context.Notices.Say(problem, error: true);
            return;
        }

        int others = 0;
        foreach (var other in document.Selection)
        {
            if (other == target || !HexDocument.TryRead(other, name, out var theirs))
                continue;

            IRuntimeValue? wanted = forOthers is null ? value : forOthers(theirs);

            // One that won't have it (a kind of value it doesn't take) is left as it was, the rest still get theirs
            if (wanted is null || HexDocument.Write(other, name, wanted) is not null)
                continue;

            others++;
            if (name.StartsWith("intro") || name == "stagger")
                other.PlayIntro();
        }

        if (others > 0)
            context.Notices.Say(context.Strings.Said("designer_inspector.changed_for_things", ("property", name.Replace('_', ' ')), ("count", (others + 1).ToString())));

        codeDirty = true;

        // The tree greys out what is hidden, and the layers are listed by what the components say they are on
        if (name is "visible" or "layer")
            treeDirty = true;

        // An entrance is easier to set up when it is played back every time something about it changes
        if (name.StartsWith("intro") || name == "stagger")
        {
            // The others were set off above, settling here would stop them again
            if (others == 0) document.SettleIntros();
            target.PlayIntro();
        }

        // What a container is shown filled with is up to these two
        if (name is "template" or "preview_count" or "file" && document.RefreshPreviews() is { } missing)
            context.Notices.Say(missing, error: true);
    }
}
