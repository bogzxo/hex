using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private bool shortcutHintsDirty = true;

    private string WithCommandShortcut(string text, string commandKey)
    {
        string label = Said(commandKey).TrimEnd('.');
        foreach (var (item, _) in Commands())
            if (LabelOf(item) == label)
                return string.IsNullOrEmpty(item.Shortcut) ? text : $"{text} ({UIShortcutText.Shown(item.Shortcut)})";
        return text;
    }

    // Bindings change infrequently; do not enumerate commands or allocate tooltip strings on every idle tick.
    private void UpdateShortcutHints()
    {
        if (!shortcutHintsDirty)
            return;
        shortcutHintsDirty = false;
        layout.Get<Button>("design_view").Tooltip = WithCommandShortcut(Said("toolbar.design_tip"), "menus.toggle_designer_code");
        layout.Get<Button>("code_view").Tooltip = WithCommandShortcut(Said("toolbar.code_tip"), "menus.toggle_designer_code");
        layout.Get<Button>("table_view").Tooltip = WithCommandShortcut(Said("toolbar.table_tip"), "menus.toggle_designer_code");
        layout.Get<Button>("hide_right").Tooltip = WithCommandShortcut(Said("properties.hide"), "menus.properties");
        foreach (var welcomePage in welcomeSizes)
        {
            welcomePage.CommandsButton.Label = WithCommandShortcut(Said("welcome.commands"), "menus.command_palette");
            welcomePage.Invalidate();
        }
    }
}
