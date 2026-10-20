namespace Hex.Dawn;

// What the designer offers, the components that can be added, the words a property can be, the numbers that have a
// slider, and how wide the inspector gives a property's name and what sets it.
internal sealed partial class HexDesigner
{
    // What can be added, in the order the buttons for it come.
    internal static readonly string[] Palette =
    [
        "stack", "collapsible", "grid", "panel", "scroll", "label", "button", "toggle", "textbox", "number_box", "selector",
        "dropdown", "list", "slider", "color_picker", "progress_bar", "image", "tabs", "divider", "spacer",
        "readout", "meter", "graph", "segments", "include"
    ];

    // The properties that are one of a few words, and the words, see HidlComponents
    internal static Dictionary<string, string[]> Choices => HidlComponents.Choices;

    // The numbers that have a slider next to them, and what its ends are.
    private static readonly Dictionary<string, (float Min, float Max)> Ranges = new()
    {
        ["progress"] = (0.0f, 1.0f),
        ["intro_time"] = (0.0f, 2.0f),
        ["intro_delay"] = (0.0f, 2.0f),
        ["stagger"] = (0.0f, 0.5f),
        ["hover_time"] = (0.0f, 0.5f),
        ["collapse_time"] = (0.0f, 0.5f)
    };

    // How wide what a property is set with is, which is whatever the properties have left next to what it is
    // called. They can be dragged wider (see DawnScene.Settings). What is taken off is the padding either side (8
    // and 8), the name and the gap after it (CaptionWidth and 6), and the scroll bar with the gap it keeps (6 and 6).
    // It was 14 short of that once and every editor ran on under the bar
    private const float EDITOR_MARGIN = 34;
    private const float MIN_EDITOR_WIDTH = 96;

    // The name of a property takes a share of the row and the editor the rest, the name had a fixed 128 once and the
    // editors were squeezed into what a narrow panel had left, the way round nobody wants it
    private const float CAPTION_SHARE = 0.3f, MIN_CAPTION = 70, MAX_CAPTION = 112;
    private float CaptionWidth => MathF.Round(Math.Clamp(inspectorScroll.Size.X * CAPTION_SHARE, MIN_CAPTION, MAX_CAPTION));
    private float EditorWidth => MathF.Max(MIN_EDITOR_WIDTH, inspectorScroll.Size.X - EDITOR_MARGIN - CaptionWidth);
    private const float EDITOR_HEIGHT = 28;
    internal const float EDITOR_TEXT = 0.14f;
    private const float PICKER_HEIGHT = 112;
}
