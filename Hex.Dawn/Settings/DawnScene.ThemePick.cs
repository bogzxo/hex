using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// Color Theme, like VS Code's Color Theme picker. Every skin and theme the editor can be drawn in, in the chooser the
// palette uses, the editor drawn in each as the list goes past it. Enter keeps the one it's on, Escape or anything else
// that closes the list puts back the one it was in.
internal sealed partial class DawnScene
{
    private void ChooseColorTheme()
    {
        string wasSkin = settings.EditorSkin, wasTheme = settings.EditorTheme;
        var all = EditorSkins();

        // The one it's in now first, so the list opens on it and nothing changes until something else is gone to
        int now = all.FindIndex(choice => choice.Skin.Name == wasSkin && (choice.Theme ?? string.Empty) == wasTheme);
        if (now > 0)
        {
            var current = all[now];
            all.RemoveAt(now);
            all.Insert(0, current);
        }

        var byText = new Dictionary<string, (UISkinChoice Skin, string? Theme)>(StringComparer.Ordinal);
        foreach (var (skin, theme, text) in all)
            byText.TryAdd(text, (skin, theme));

        bool kept = false;
        string? showing = null;

        // The editor in a theme for as long as the list is on it, nothing saved
        void Show(string text)
        {
            if (text == showing || !byText.TryGetValue(text, out var choice))
                return;

            showing = text;
            UseEditorSkin(choice.Skin.Name, choice.Theme, save: false);
            SettingsPage?.Refresh("editor_skin");
        }

        var choices = all.Select(choice => (choice.Text, (Action)(() =>
        {
            kept = true;
            UseEditorSkin(choice.Skin.Name, choice.Theme, save: true);
            SettingsPage?.Refresh("editor_skin");
        }))).ToList();

        ChooseFrom(Said("settings_looks.color_theme_title"), Said("settings_looks.color_theme_hint"), choices);
        if (textDialog is not { } dialog)
            return;

        showing = all.Count > 0 ? all[0].Text : null;

        // The chooser says nothing as its list moves, the list itself does, and typing narrows it to another first row
        // without the list counting that as a move
        ListBox? list = Inside<ListBox>(dialog.Body);
        TextBox? box = Inside<TextBox>(dialog.Body);
        if (list is not null)
        {
            list.OnChanged = Show;
            if (box is not null)
            {
                var narrowed = box.OnChanged;
                box.OnChanged = typed =>
                {
                    narrowed?.Invoke(typed);
                    if (list.Index >= 0 && list.Index < list.Items.Length)
                        Show(list.Items[list.Index]);
                };
            }
        }

        // Closed without a pick, the old one back. A pick closes it first and then runs, so this puts the old one back and
        // the pick puts its own on straight after, both before anything's drawn
        dialog.Closed = () =>
        {
            if (kept || showing is null)
                return;

            showing = null;
            if (settings.EditorSkin != wasSkin || settings.EditorTheme != wasTheme)
            {
                UseEditorSkin(wasSkin, wasTheme, save: false);
                SettingsPage?.Refresh("editor_skin");
            }
        };
    }

    // The first of a kind of component anywhere inside another, the chooser's list and its box
    private static T? Inside<T>(UIComponent of) where T : UIComponent
    {
        foreach (UIComponent child in of.Children)
        {
            if (child is T found)
                return found;
            if (Inside<T>(child) is { } deeper)
                return deeper;
        }

        return null;
    }
}
