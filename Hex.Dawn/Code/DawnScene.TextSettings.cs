using Hex.Shell;

using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

// How code is typed and drawn, the tab size, spaces or tabs, working out how a file is already indented, the line
// numbers and the guides, and a project's .editorconfig having the last word. The rulers, the whitespace drawn, the
// caret's look and sticky scroll too, and formatting on save which is a row of Editor, Files. The rows are the settings
// window's Editor, Text, at the very top of it, and every change goes straight onto every open file. What the files of
// the workspace leave out (files_exclude, search_exclude and the .gitignores) are rows of Editor, Search kept here too.
internal sealed partial class DawnScene
{
    // How many lines are looked at to work out how a file is indented, the top of a file says it well enough
    private const int DETECT_LINES = 200;

    /// <summary>
    /// A file's editor and document set the way it should be indented and drawn. Its .editorconfig first when that's on,
    /// then how the file is already indented when that's on and it says, then the settings. Read through the settings
    /// field every time, the settings can be read again from their file and be a new one.
    /// </summary>
    internal void ApplyTextSettings(HexDocument of)
    {
        HexSettings kept = settings;
        CodeEditor editor = of.Editor;
        int size = kept.TabSize;
        bool spaces = kept.InsertSpaces;

        HexEditorConfigValues? config = kept.UseEditorConfig && of.Path is { } path ? HexEditorConfig.For(path) : null;
        (bool Spaces, int Size)? detected = kept.DetectIndentation && of.IsText ? DetectIndentation(editor.Document) : null;

        if (detected is { } found)
        {
            spaces = found.Spaces;
            if (found.Spaces)
                size = found.Size;
        }

        if (config is not null)
        {
            if (config.UseTabs is { } tabs)
                spaces = !tabs;
            if ((spaces ? config.IndentSize : config.TabWidth ?? config.IndentSize) is { } configured)
                size = configured;
        }

        editor.TabSize = size;
        editor.Document.Indent = spaces ? new string(' ', editor.TabSize) : "\t";
        editor.LineNumbers = kept.LineNumbers;
        editor.IndentGuides = kept.IndentGuides;
        editor.Rulers = kept.Rulers;
        editor.RenderWhitespace = WhitespaceOf(kept.RenderWhitespace);
        editor.CaretStyle = CaretStyleOf(kept.CursorStyle);
        editor.StickyScroll = kept.StickyScroll;
    }

    // The settings' words for where whitespace is drawn and how the caret looks, as the editor has them
    private static UIWhitespace WhitespaceOf(string said) => said switch
    {
        "none" => UIWhitespace.None,
        "boundary" => UIWhitespace.Boundary,
        "all" => UIWhitespace.All,
        _ => UIWhitespace.Selection
    };

    private static UICaretStyle CaretStyleOf(string said) => said switch
    {
        "block" => UICaretStyle.Block,
        "underline" => UICaretStyle.Underline,
        _ => UICaretStyle.Line
    };

    /// <summary>
    /// How a file is already indented, by its first lines. Tabs when more lines start with a tab than with spaces, and
    /// otherwise spaces the smallest step the lines go in by (2, 4 or 8). Null for a file that doesn't say, nothing in it
    /// indented or an odd step.
    /// </summary>
    internal static (bool Spaces, int Size)? DetectIndentation(UITextDocument document)
    {
        int tabbed = 0, spaced = 0;
        int two = 0, four = 0, eight = 0, odd = 0;
        int count = Math.Min(document.LineCount, DETECT_LINES);
        for (int line = 0; line < count; line++)
        {
            string text = document.LineAt(line);
            if (text.Length == 0 || text.Trim().Length == 0)
                continue;

            if (text[0] == '\t')
            {
                tabbed++;
                continue;
            }

            int leading = 0;
            while (leading < text.Length && text[leading] == ' ')
                leading++;

            // A space or so before a star is a block comment's middle line, not an indentation
            if (leading == 0 || (leading < text.Length && text[leading] == '*'))
                continue;

            spaced++;
            if (leading % 8 == 0) eight++;
            if (leading % 4 == 0) four++;
            if (leading % 2 == 0) two++;
            else odd++;
        }

        if (tabbed == 0 && spaced == 0)
            return null;
        if (tabbed > spaced)
            return (false, 0);

        // The biggest step every line (or nearly every) still lands on
        int even = spaced - odd;
        if (even < spaced * 0.8f)
            return null;
        if (eight == even)
            return (true, 8);
        if (four >= even * 0.9f)
            return (true, 4);
        return (true, 2);
    }

    /// <summary>Every open file set again, after one of the rows changed.</summary>
    private void ApplyTextSettingsEverywhere()
    {
        foreach (HexDocument open in documents)
            ApplyTextSettings(open);
    }

    /// <summary>The rows of Editor, Text in the settings window, kept in the editor's section of the file, or the folder's.</summary>
    private sealed class TextSettingsSource(DawnScene hex) : ISettingsSource
    {
        public string Section => SettingsSections.EDITOR;

        public IReadOnlyList<UISetting> Settings => EditorSettingsCatalog.Text;

        /// <summary>What a setting comes as, for the row to say.</summary>
        public IRuntimeValue? Default(string key) => EditorSettingsCatalog.Usual.GetValueOrDefault(key);

        // What a row shows, the folder's or the user's by the window's switch (DawnScene.SettingsScope)
        public IRuntimeValue? Get(string key) => hex.ScopedValue(key, InForce(key));

        private IRuntimeValue? InForce(string key) => key switch
        {
            "tab_size" => new NumberValue(hex.settings.TabSize),
            "insert_spaces" => Values.Bool(hex.settings.InsertSpaces),
            "detect_indentation" => Values.Bool(hex.settings.DetectIndentation),
            "line_numbers" => Values.Bool(hex.settings.LineNumbers),
            "indent_guides" => Values.Bool(hex.settings.IndentGuides),
            "editorconfig" => Values.Bool(hex.settings.UseEditorConfig),
            "rulers" => new StringValue(HexSettings.RulersText(hex.settings.Rulers)),
            "render_whitespace" => new StringValue(hex.settings.RenderWhitespace),
            "cursor_style" => new StringValue(hex.settings.CursorStyle),
            "sticky_scroll" => Values.Bool(hex.settings.StickyScroll),
            "format_on_save" => Values.Bool(hex.settings.FormatOnSave),
            "files_exclude" => Horizon.HIDL.Library.Natives.List(hex.settings.FilesExclude),
            "search_exclude" => Horizon.HIDL.Library.Natives.List(hex.settings.SearchExclude),
            "search_use_ignore_files" => Values.Bool(hex.settings.SearchUseIgnoreFiles),
            _ => null
        };

        public bool IsModified(string key) => hex.ScopedModified(key) ?? !SettingValues.Same(Get(key), Default(key));

        public void Set(string key, IRuntimeValue value)
        {
            if (!hex.SetInScope(key, value, Apply))
                Apply(key, value);
        }

        // A setting put on every open file and kept, the way its row changing it does
        private void Apply(string key, IRuntimeValue value)
        {
            HexSettings settings = hex.settings;
            switch (key, value)
            {
                case ("tab_size", NumberValue size):
                    settings.TabSize = (int)Math.Clamp(MathF.Round(size.Value), 1.0f, 16.0f);
                    break;
                case ("insert_spaces", BooleanValue spaces):
                    settings.InsertSpaces = spaces.Value;
                    break;
                case ("detect_indentation", BooleanValue detect):
                    settings.DetectIndentation = detect.Value;
                    break;
                case ("line_numbers", BooleanValue numbers):
                    settings.LineNumbers = numbers.Value;
                    break;
                case ("indent_guides", BooleanValue guides):
                    settings.IndentGuides = guides.Value;
                    break;
                case ("editorconfig", BooleanValue config):
                    settings.UseEditorConfig = config.Value;
                    break;
                case ("rulers", StringValue rulers):
                    settings.Rulers = HexSettings.ParseRulers(rulers.Value);
                    break;
                case ("render_whitespace", StringValue { Value: "none" or "selection" or "boundary" or "all" } whitespace):
                    settings.RenderWhitespace = whitespace.Value;
                    break;
                case ("cursor_style", StringValue { Value: "line" or "block" or "underline" } cursor):
                    settings.CursorStyle = cursor.Value;
                    break;
                case ("sticky_scroll", BooleanValue sticky):
                    settings.StickyScroll = sticky.Value;
                    break;
                case ("format_on_save", BooleanValue format):
                    settings.FormatOnSave = format.Value;
                    break;

                // What the walk of the files leaves out, nothing of an open file's. The explorer shows it straight away,
                // quick open and Find in Files go by it the next time they look
                case ("files_exclude", ListValue patterns):
                    settings.FilesExclude = HexSettings.Patterns(patterns);
                    hex.ExcludesChanged();
                    hex.SaveSettings();
                    return;
                case ("search_exclude", ListValue patterns):
                    settings.SearchExclude = HexSettings.Patterns(patterns);
                    hex.ExcludesChanged();
                    hex.SaveSettings();
                    return;
                case ("search_use_ignore_files", BooleanValue ignore):
                    settings.SearchUseIgnoreFiles = ignore.Value;
                    hex.ExcludesChanged();
                    hex.SaveSettings();
                    return;
                default:
                    return;
            }

            hex.ApplyTextSettingsEverywhere();
            hex.SaveSettings();
        }

        public void Reset(string key)
        {
            if (hex.ResetInScope(key, Apply))
                return;

            if (Default(key) is { } usual)
                Set(key, usual);
        }
    }
}
