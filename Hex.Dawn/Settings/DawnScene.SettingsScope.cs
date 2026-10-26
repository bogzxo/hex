using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The settings window's User | Workspace switch over its page, VS Code's two tabs. User is every setting, kept in the
// user's file. Workspace is only what a folder can have its own way, kept in the folder's .hex\settings.hor (see
// HexSettings.Workspace), a row there changed when the folder's file sets it. The sources ask here which one a change
// goes to, so the rows themselves don't have to know.
internal sealed partial class DawnScene
{
    // Whether the window shows the folder's settings rather than the user's
    private bool workspaceScope;

    // The switch's two halves while the window is up
    private Button? userScopeTab, workspaceScopeTab;

    // Every row in the order the window had them, kept while the folder's are shown to put back after
    private List<UISetting>? everySetting;

    // The sections that are a panel rather than rows (the glass, the keys, the extensions, the profiles), by section,
    // taken off while the folder's settings are shown, none of them can be a folder's
    private readonly Dictionary<string, UIComponent> settingsPanels = new(StringComparer.Ordinal);

    /// <summary>Whether the window is on the folder's settings, and a folder is open to have some.</summary>
    internal bool InWorkspaceScope => workspaceScope && settings.WorkspacePath is not null;

    /// <summary>The folder whose own settings are in force, the explorer's first, null while none is open.</summary>
    private string? WorkspaceSettingsFolder() => explorer?.workspace is { Folders: [var first, ..] } ? first : null;

    // What a folder is called on the switch and in what's said
    private static string FolderName(string folder) => Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : folder;

    /// <summary>The switch put over the window's page, on User. Once, as the window is made.</summary>
    private void AddScopeBar(SettingsWindow view)
    {
        workspaceScope = false;
        everySetting = null;

        var bar = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 2.0f, Padding = new UIEdges(14.0f, 6.0f, 14.0f, 0.0f) };
        userScopeTab = bar.Add(new Button
        {
            Label = Said("settings_scope.user"),
            Style = "button_flat",
            Indicator = UIResize.Bottom,
            Tooltip = Said("settings_scope.user_tip"),
            OnPressed = () => ShowScope(false)
        });
        workspaceScopeTab = bar.Add(new Button
        {
            Style = "button_flat",
            Indicator = UIResize.Bottom,
            OnPressed = () => ShowScope(true)
        });

        // On the folder's tab, a quiet line after the tabs saying why there are so few rows
        scopeLine = bar.Add(new Label { Text = $"[color:dim]{Said("settings_page.workspace_only")}[/color]", Align = Horizon.Rendering.Origin.Left, Padding = new UIEdges(12.0f, 0.0f, 0.0f, 0.0f), Visible = false });

        // A row the other scope sets says so after its key
        view.Page.NoteOf = ScopeNote;
        view.Page.NoteTipOf = ScopeNoteTip;

        view.Window.Remove(view.Page);
        view.Window.Add(new ScopedBody(bar, view.Page) { Fill = UIFill.Both });
        RefreshScopeBar();
    }

    /// <summary>The switch's halves for what's open now, the folder's named, and back on User when no folder is.</summary>
    private void RefreshScopeBar()
    {
        if (userScopeTab is null || workspaceScopeTab is null)
            return;

        string? folder = WorkspaceSettingsFolder();
        if (workspaceScope && settings.WorkspacePath is null)
        {
            ShowScope(false);
            return;
        }

        userScopeTab.Selected = !workspaceScope;
        workspaceScopeTab.Selected = workspaceScope;
        workspaceScopeTab.Enabled = folder is not null && settings.WorkspacePath is not null;
        workspaceScopeTab.Label = folder is null ? Said("settings_scope.workspace_none") : Said("settings_scope.workspace", ("name", FolderName(folder)));
        workspaceScopeTab.Tooltip = folder is null || settings.WorkspacePath is not { } at
            ? Said("settings_scope.no_folder")
            : File.Exists(at) ? Said("settings_scope.workspace_tip", ("path", at)) : Said("settings_page.workspace_no_file", ("path", at));
        if (scopeLine is not null)
            scopeLine.Visible = workspaceScope;
    }

    // The dim line after the tabs on the folder's tab
    private Label? scopeLine;

    /// <summary>What the scope line after the tabs says while it shows, null while it doesn't. For the self test.</summary>
    internal string? ScopeLineShown => scopeLine is { Visible: true } line ? line.Text : null;

    /// <summary>
    /// What a row says after its key about the other scope. On User a setting the folder's file sets is set by the
    /// workspace, its value there isn't the one shown. On Workspace a setting the folder leaves alone that the user has
    /// changed is modified in User as well. Null for the rest.
    /// </summary>
    private string? ScopeNote(string key)
    {
        if (!HexSettings.IsWorkspaceKey(key) || settings.WorkspacePath is null)
            return null;
        if (!InWorkspaceScope)
            return settings.IsOverridden(key) ? Said("settings_page.set_by_workspace") : null;
        return !settings.IsOverridden(key) && UserModified(key) ? Said("settings_page.also_in_user") : null;
    }

    // What the note says when the pointer rests on it, the value the other scope has
    private string? ScopeNoteTip(string key)
    {
        if (ScopeNote(key) is null)
            return null;
        if (!InWorkspaceScope)
        {
            string folder = WorkspaceSettingsFolder() is { } at ? FolderName(at) : string.Empty;
            return Said("settings_page.set_by_workspace_tip", ("name", folder), ("value", WrittenValue(settings.WorkspaceValues.GetValueOrDefault(key))));
        }

        return Said("settings_page.also_in_user_tip", ("value", WrittenValue(settings.UserValue(key))));
    }

    // Whether the user's own value of a setting isn't what it comes as
    private bool UserModified(string key) => !SettingValues.Same(settings.UserValue(key), HexSettings.Usual.GetValueOrDefault(key));

    // A value the way the settings file writes it, empty for none or one it can't write
    private static string WrittenValue(IRuntimeValue? value)
    {
        if (value is null)
            return string.Empty;
        try
        {
            return Horizon.HIDL.HIDLWriter.Write(value);
        }
        catch (NotSupportedException)
        {
            return string.Empty;
        }
    }

    /// <summary>The window on the folder's settings, only the rows a folder can have, or on the user's, every row.</summary>
    private void ShowScope(bool workspace)
    {
        if (settingsView is not { } view)
            return;

        workspace &= settings.WorkspacePath is not null;
        if (workspace != workspaceScope)
        {
            workspaceScope = workspace;
            SettingsPage page = view.Page;
            if (workspace)
            {
                everySetting = [.. page.Settings];
                page.Settings = [.. everySetting.Where(setting => HexSettings.IsWorkspaceKey(setting.Key))];
                foreach (string group in settingsPanels.Keys)
                    view.SetPanel(group, null);
            }
            else
            {
                if (everySetting is not null)
                    page.Settings = everySetting;
                everySetting = null;
                foreach (var (group, panel) in settingsPanels)
                    view.SetPanel(group, panel);
            }

            page.Refresh();
        }

        RefreshScopeBar();
    }

    /// <summary>A section that's a panel put in the window, kept to put back after the folder's settings were shown.</summary>
    private void SetSettingsPanel(SettingsWindow view, string group, UIComponent? content)
    {
        if (content is null)
            settingsPanels.Remove(group);
        else
            settingsPanels[group] = content;

        if (!workspaceScope)
            view.SetPanel(group, content);
    }

    /* Which file a row's change goes to */

    /// <summary>
    /// What a row shows. On the folder's settings what its file sets, what's in force otherwise. On the user's a setting
    /// the folder covers shows what the user has.
    /// </summary>
    private IRuntimeValue? ScopedValue(string key, IRuntimeValue? effective)
    {
        if (!HexSettings.IsWorkspaceKey(key))
            return effective;
        if (InWorkspaceScope)
            return settings.WorkspaceValues.GetValueOrDefault(key) ?? effective;
        return settings.IsOverridden(key) ? settings.UserValue(key) : effective;
    }

    /// <summary>Whether a row shows changed on the folder's settings, set in its file. Null on the user's, the source's own say.</summary>
    private bool? ScopedModified(string key) =>
        InWorkspaceScope && HexSettings.IsWorkspaceKey(key) ? settings.IsOverridden(key) : null;

    /// <summary>
    /// A row changed, taken by the scope when it's the folder's business. On the folder's settings it's written to the
    /// folder's file and put in force with <paramref name="apply"/>. On the user's, a setting the folder covers is kept
    /// for the user's file and the folder's stays in force, which is said. False is the source's to do as ever.
    /// </summary>
    private bool SetInScope(string key, IRuntimeValue value, Action<string, IRuntimeValue> apply)
    {
        if (!HexSettings.IsWorkspaceKey(key))
            return false;

        if (InWorkspaceScope)
        {
            if (settings.SetWorkspace(key, value) is { } problem)
                Say(problem, error: true);
            KnowWorkspaceSettingsFile();
            apply(key, value);
            return true;
        }

        if (!settings.IsOverridden(key))
            return false;

        settings.SetUser(key, value);
        SaveSettings();
        Say(Said("settings_scope.overridden", ("key", key), ("name", WorkspaceSettingsFolder() is { } folder ? FolderName(folder) : string.Empty)));
        return true;
    }

    /// <summary>
    /// A row's reset, taken by the scope when it's the folder's business. On the folder's settings it comes out of the
    /// folder's file and the user's is put back in force. On the user's, a setting the folder covers goes back to what it
    /// comes as for the user's file only. False is the source's to do as ever.
    /// </summary>
    private bool ResetInScope(string key, Action<string, IRuntimeValue> apply)
    {
        if (!HexSettings.IsWorkspaceKey(key))
            return false;

        if (InWorkspaceScope)
        {
            if (!settings.IsOverridden(key))
                return true;

            IRuntimeValue? users = settings.UserValue(key);
            if (settings.RemoveWorkspace(key) is { } problem)
                Say(problem, error: true);
            KnowWorkspaceSettingsFile();
            if (users is not null)
                apply(key, users);
            return true;
        }

        if (!settings.IsOverridden(key))
            return false;

        settings.SetUser(key, null);
        SaveSettings();
        return true;
    }

    // The switch along the top and the page under it, the page taking what the switch leaves
    private sealed class ScopedBody : Panel
    {
        private readonly UIComponent bar, body;

        public ScopedBody(UIComponent bar, UIComponent body)
        {
            this.bar = Add(bar);
            this.body = Add(body);
        }

        protected override void Arrange(UIRect content)
        {
            float tall = bar.Visible ? bar.DesiredSize.Y : 0.0f;
            bar.ArrangeTree(new UIRect(new Vector2(content.Min.X, content.Max.Y - tall), content.Max));
            body.ArrangeTree(new UIRect(content.Min, new Vector2(content.Max.X, content.Max.Y - tall)));
        }
    }
}
