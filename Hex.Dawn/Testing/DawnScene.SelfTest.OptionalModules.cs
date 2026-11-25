using Hex.Core.Modules;
using Hex.Shell;

using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Optional modules in Editor · Modules: all 5 built-in modules registered and enabled by default,
// surfaced in Settings, lazy Designer stage disabled while editing code, restart-only toggling with
// "Restart required" row notes, themed Restart Hex button (both in the Modules panel and top scope bar),
// clicking a boolean row itself to toggle, and clean state restoration.
internal sealed partial class DawnScene
{
    private void TestModules(HexPointerScript test)
    {
        bool linkWas = settings.ModuleLink;
        bool localAiWas = settings.ModuleLocalAi;
        bool agenticWas = settings.ModuleAgentic;
        bool notebooksWas = settings.ModuleNotebooks;
        bool designerWas = settings.ModuleDesigner;

        string[] allIds = [HexModuleId.Link, HexModuleId.LocalAi, HexModuleId.Agentic, HexModuleId.Notebooks, HexModuleId.Designer];
        string[] allKeys = ["module_link", "module_local_ai", "module_agentic", "module_notebooks", "module_designer"];

        test.Run(() => ShowFace(HexFace.Dawn));
        OnCode(test, dataFile);

        // 1. Catalog and defaults
        test.Check("modules: catalog registers all 5 built-in modules and all 5 are enabled by default", () =>
            allIds.All(id => HexModuleCatalog.All.Any(descriptor => descriptor.Id == id && descriptor.DefaultEnabled))
            && allIds.All(id => HexModules.IsEnabled(id)));

        // 2. Settings UI shows all 5 module toggles in Editor · Modules
        test.Run(() => OpenSettings(EditorSettingsCatalog.MODULES));
        test.WaitFor(() => SettingsWindow is { Visible: true } && SettingsPage is not null, 2.0f);
        test.Wait();

        test.Check("modules: Editor · Modules contains all 5 module settings", () =>
            SettingsPage is { } page
            && allKeys.All(key => page.Settings.Any(setting => setting.Key == key && setting.Group == EditorSettingsCatalog.MODULES)));

        test.Check("modules: no pending restart notice or restart button before any module toggle changes", () =>
            !ModuleRestartButtonVisible
            && ModulesRestartPanel is { HasPending: false }
            && allKeys.All(key => SettingsPage?.NoteShown(key) is null));

        test.Run(() => SettingsPage!.Find("@id:module_agentic"));
        test.Wait();
        test.Check("modules: searching @id:module_agentic in Settings shows the matching module toggle", () =>
            SettingsPage is { RowsShown: 1 } page && page.IsRowShown("module_agentic"));

        test.Run(() => SettingsPage!.Find("@section:modules"));
        test.Wait();
        test.Check("modules: searching @section:modules shows all 5 module toggles", () =>
            SettingsPage is { RowsShown: 5 } page && allKeys.All(page.IsRowShown));

        test.Run(() => SettingsPage!.Find(string.Empty));
        test.Wait();

        // 3. Lazy Designer stage: while viewing a code file, the 2nd UICompositor is disabled
        test.Check("modules: while viewing code, Designer stage compositor and backdrop are disabled", () =>
            document.IsText && !stage.Enabled && !backdrop.Enabled);

        // 4. Toggling optional modules saves preference without mutating live HexModules, shows "Restart required" + Restart button
        test.Run(() =>
        {
            SettingsPage!.Enter("module_agentic", Values.Bool(false));
            SettingsPage.Enter("module_designer", Values.Bool(false));
            SettingsPage.Enter("module_notebooks", Values.Bool(false));
        });
        test.Wait();
        test.Check("modules: toggling module_agentic, module_designer, module_notebooks saves settings without mutating live HexModules", () =>
            !settings.ModuleAgentic
            && !settings.ModuleDesigner
            && !settings.ModuleNotebooks
            && HexModules.IsEnabled(HexModuleId.Agentic)
            && HexModules.IsEnabled(HexModuleId.Designer)
            && HexModules.IsEnabled(HexModuleId.Notebooks)
            && AgenticEnabled);

        string restartLabel = Said("settings_page.restart_required");
        test.Check("modules: pending module toggles show Restart required note and reveal themed Restart Hex buttons", () =>
            SettingsPage is { } page
            && page.NoteShown("module_agentic") == restartLabel
            && page.NoteShown("module_designer") == restartLabel
            && page.NoteShown("module_notebooks") == restartLabel
            && page.NoteShown("module_link") is null
            && ModuleRestartButtonVisible
            && ModulesRestartPanel is { PendingCount: 3, RestartButton.Visible: true });

        // 5. Clicking a boolean setting row itself (title area, not its toggle) toggles the flag
        test.Run(() => SettingsPage!.Find("@id:module_link"));
        test.Wait();
        test.Click(() => SettingsPage?.RowParts("module_link") is { } parts ? chrome.ToWorld(parts.Title.Center) : test.Pointer.Position);
        test.Wait();
        test.Check("modules: clicking the module_link row itself toggles the setting and marks it Restart required", () =>
            !settings.ModuleLink
            && HexModules.IsEnabled(HexModuleId.Link)
            && SettingsPage?.NoteShown("module_link") == restartLabel
            && ModuleRestartButtonVisible
            && ModulesRestartPanel is { PendingCount: 4 });

        // 6. Clicking the themed Restart Hex button invokes HexModes.Restart cleanly (intercepted via RestartLauncher)
        (HexMode Mode, string? Folder)? restartRequested = null;
        test.Run(() =>
        {
            HexModes.RestartLauncher = (mode, folder) =>
            {
                restartRequested = (mode, folder);
                return false; // keep the self-test window open
            };
            try
            {
                ModuleRestartButton?.OnPressed?.Invoke();
            }
            finally
            {
                HexModes.RestartLauncher = null;
            }
        });
        test.Wait();
        test.Check("modules: themed Restart Hex button invokes HexModes.Restart for Dawn", () =>
            restartRequested is { Mode: HexMode.Dawn });

        // 7. Toggling all modules back to active session state clears Restart required and hides the Restart button
        test.Run(() =>
        {
            SettingsPage!.Find(string.Empty);
            SettingsPage.Enter("module_link", Values.Bool(linkWas));
            SettingsPage.Enter("module_local_ai", Values.Bool(localAiWas));
            SettingsPage.Enter("module_agentic", Values.Bool(agenticWas));
            SettingsPage.Enter("module_notebooks", Values.Bool(notebooksWas));
            SettingsPage.Enter("module_designer", Values.Bool(designerWas));
        });
        test.Wait();
        test.Check("modules: toggling modules back to session state clears Restart required and hides Restart buttons", () =>
            SettingsPage is { } page
            && allKeys.All(key => page.NoteShown(key) is null)
            && !ModuleRestartButtonVisible
            && ModulesRestartPanel is { PendingCount: 0, RestartButton.Visible: false });

        // 8. Restore editor state cleanly
        test.Run(() =>
        {
            CloseSettings();
            ShowFace(HexFace.Dawn);
        });
        OnCode(test, dataFile);
        test.Wait();
        test.Check("modules: restoring all 5 module flags leaves HexModules and the editor clean", () =>
            allIds.All(HexModules.IsEnabled)
            && Face == HexFace.Dawn
            && SettingsWindow is null
            && !stage.Enabled);
    }
}
