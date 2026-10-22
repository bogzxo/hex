namespace Hex.Core;

// What Hex knows about the kinds of components a layout makes, what each is for and its icon, and the words a property
// that is one of a few can be. The designer's palette and inspector say them, and so do completion and the hovers of
// the code, in the editor and in the language server alike.
internal static class HidlComponents
{
    // The properties that are one of a few words, and the words.
    private static readonly string[] Origins = ["center", "top_left", "top", "top_right", "left", "right", "bottom_left", "bottom", "bottom_right"];
    public static readonly Dictionary<string, string[]> Choices = new()
    {
        ["anchor"] = Origins,
        ["pivot"] = Origins,
        ["align"] = Origins,
        ["fill"] = ["none", "horizontal", "vertical", "both"],
        ["direction"] = ["vertical", "horizontal"],
        ["intro"] = ["none", "pop", "fade", "slide"],
        ["hover"] = ["none", "grow", "shrink", "lift", "nudge", "pulse", "glow"]
    };

    /// <summary>
    /// Helper method for what a kind of component is for, the line the palette shows when the pointer rests on its button.
    /// </summary>
    public static string Describe(string kind) => kind switch
    {
        "stack" => "A row or a column of things, with a gap between them",
        "grid" => "Cells in rows and columns, so many across",
        "collapsible" => "A stack under a bar with a title, which folds it away",
        "panel" => "A box to put things in, or a backdrop",
        "include" => "Another layout file built in here, its parts yours by name",
        "scroll" => "A panel that scrolls when there is more than fits",
        "label" => "A piece of text, which can wrap",
        "button" => "A button with a label, pressed by a click or the navigator",
        "toggle" => "A box that is ticked or isn't",
        "textbox" => "A line somebody types into",
        "number_box" => "A number, dragged across or typed",
        "selector" => "One of a few options, stepped through with arrows",
        "dropdown" => "One of a list, which drops down",
        "list" => "A list of texts in a box, one chosen",
        "slider" => "A number between two ends, slid along",
        "color_picker" => "A colour, picked by hue, shade and how solid it is",
        "progress_bar" => "How full something is, a health bar say",
        "image" => "A picture, a sprite of the skin or a file",
        "tabs" => "Pages with a strip of tabs along the top",
        "divider" => "A line between things",
        "spacer" => "Nothing, of a size, for a gap",
        "readout" => "A number that changes all the time, and what it is counted in",
        "meter" => "A thin line that says how full something is",
        "graph" => "The last so many of something as bars",
        "segments" => "One bar cut up by how much of a whole things are",
        "symbol" => "One of the icons every skin has, on its own",
        "status_bar" => "The strip along the bottom of an editor, pieces on the left and the right",
        "tab_strip" => "A row of tabs for documents, with an x on each",
        "breadcrumbs" => "Where something is, as a path of pieces",
        _ => string.Empty
    };

    /// <summary>The icon of a kind of component, in the toolbox, the tree and the breadcrumbs.</summary>
    public static string KindIcon(string kind) => kind switch
    {
        "stack" => "list-flat",
        "collapsible" => "unfold",
        "grid" => "table",
        "panel" => "primitive-square",
        "scroll" => "list-selection",
        "label" => "symbol-text",
        "button" => "symbol-event",
        "toggle" => "check",
        "textbox" => "symbol-string",
        "number_box" => "symbol-number",
        "selector" => "arrow-swap",
        "dropdown" => "chevron-down",
        "list" => "list-unordered",
        "slider" => "settings",
        "color_picker" => "symbol-color",
        "progress_bar" => "loading",
        "image" => "file-media",
        "tabs" => "browser",
        "divider" => "dash",
        "spacer" => "arrow-both",
        "readout" => "dashboard",
        "meter" => "pulse",
        "graph" => "graph",
        "segments" => "symbol-ruler",
        "menu_bar" => "menu",
        "code" or "code_editor" => "code",
        "group" => "group-by-ref-type",
        "include" => "file-symlink-file",
        "symbol" => "smiley",
        "status_bar" => "info",
        "tab_strip" => "files",
        "breadcrumbs" => "chevron-right",
        _ => "symbol-misc"
    };
}
