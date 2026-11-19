# Hex.Dawn: the desktop editor application

Scope: `hex/Hex.Dawn/` except `Testing/` and `Tests/` (HexTestExplorer lives in `Tests/`, the self test in `Testing/`; both in [testing-and-shipping.md](testing-and-shipping.md)). Hex.Core (settings, git process, kernels, MCP protocol, code index) is in [core-and-mcp.md](core-and-mcp.md). The widgets (`CodeEditor`, `UICommands`, `MenuBar`, `TabPanel`, ...) are Horizon.UI, see `horizon/docs/context/ui-framework.md`. All paths are relative to `hex/Hex.Dawn/` unless they start with `hex/` or `horizon/`. Line numbers were verified against the code; `~` marks approximate ones. They drift, grep the symbol.

## 1. What it is

- Hex.Dawn is a **library** (`Hex.Dawn.csproj`: `OutputType=Library`, net11.0, AOT-compatible, `InvariantGlobalization`). The `Hex` program (`hex/Hex/Program.cs`) calls `DawnProgram.Run(args)`. Dusk (terminal mode) is the sibling `Hex.Dusk`; both can share one window (`Hex.Shell.HexModes`).
- Project references: `Hex.Core`, `Hex.Shell` (frame, dialogs, `HexFiles`, `HexText`, `HexModes`, platform, `EditorSettingsCatalog`), `Hex.Installation`, and from the engine `Horizon`, `Horizon.UI`, `Horizon.Tooling` (LSP/DAP clients), `Horizon.Terminal`, `Horizon.HIDL` (`$(HorizonRoot)` defaults to the `horizon/` submodule; a workspace `Directory.Build.props` can override it).
- `GlobalUsings.cs` makes `Hex.Shell`, `Hex.Core`, `Horizon.UI`, `Horizon.UI.Components`, `Horizon.UI.Scripting`, `Horizon.Core.Platform` implicit, so `UICommands`, `CodeEditor`, `LanguageServer`, `HexSettings` appear unqualified.
- The whole app is **one `Scene` subclass, `DawnScene`** (`internal sealed partial class`, `Shell/DawnScene.cs:19`, spread over ~200 files in every folder), plus "part" classes (`HexLanguages`, `HexGit`, `HexDebugging`, `HexTasks`, `HexExplorer`, `HexDesigner`, `HexCodeEditing`, `HexRunning`, `HexExtensions`, `HexProperties`, `HexTerminalFiles`) given a `DawnContext` instead of the scene. The editor UI is a HIDL layout (`Assets/hex/editor.hor`) loaded at runtime and wired by component name. No XAML/Avalonia: everything is Horizon.UI drawn through Vulkan.
- Every string shown is a localisation key (`Said("menus.explorer")`, `Shell/DawnScene.Strings.cs:30` -> `HexText.Get`) resolved from `Assets/hex/localisation/en-gb.hor` (3547 lines). Layouts use `@key` references. New UI text needs a key there.

## 2. Startup path

1. `hex/Hex/Program.cs` `Main`: `PlatformPaths.BesideProgram = true` (settings/caches next to the exe), installs `DesktopPlatform`, handles `--help`, `--lsp` (HIDL language server, `Hex.Core.HidlServer`), `--mcp` stdio bridge (`McpBridge`; plain `--mcp` is always a bridge, `--window --mcp` opens the editor with the server), then picks the mode (`HexIdentity.ModeOf`), sets `DawnProgram.Other = DuskProgram.Scene`, calls `DawnProgram.Run`.
2. `DawnProgram.Run(args)` (`DawnProgram.cs:63`) wraps `Start` (`:81`) and always clears the self-test temp folders. `Start`:
   - parses flags (constants `DawnProgram.cs:11-26`, doc comment `~:36-62` lists each); `HexLaunch.Parse` (`HexLaunch.cs`) classifies loose args into files (with `:line:col`) and one folder. `-g/--goto`, `--open`, `--line`.
   - `Directory.SetCurrentDirectory(HexHost.ProgramFolder)` after resolving user-relative paths.
   - **Single-instance hand-over**: unless `--selftest` or `--window`, `HandOver` (`DawnProgram.cs:229`, called at `:119`) sends `folder|path` / `open|path|line|col` over pipe `DawnScene.RemotePipe` (`Shell/DawnScene.Remote.cs:13`, `horizon-hex-<user>`; `Hex.Shell.HexInstance.Tell`) to a running Dawn and returns. Receiver: `StartRemote` (`Remote.cs:~19`) starts a background thread "Hex remote" that queues into `remoteFolders`/`remoteOpens`; `UpdateRemote` (`:81`) drains them on the UI thread. Self test and `!HexHost.Pipes` (tablet) skip it.
   - builds `HexOptions` (`HexOptions.cs`, one record), creates `GameEngine` with `WindowManagerConfiguration.Default1600x900` (`CustomFrame=true`, `RunMode.Application` normally, `RunMode.Game` for self test, 120 fps/ups, VSync, `AppId="hex-dawn"`, `Gamepads=false`, `FilesBesideProgram=true`).
   - `engine.ScreenshotKey = null` (F12 is go-to-definition), `new DawnScene(options)`, optionally `HexModes.Host(...)` to share the window with Dusk (Ctrl+Alt+Shift+D), `engine.SetScene`, `engine.Run()`. After: `HexModes.Shutdown()`, `LanguageServers.StopAll()` (kills all LSP children), `DawnScene.Profile.Report()`.
3. **Hosted variant** (`DawnProgram.Hosted.cs`), for Hex.Android's activity which makes its own engine: `Hosted(config)` (`:21`) tweaks configuration; `HostedScene` (`:41`) returns the scene; `HostedSelfTest` (`:62`); `HostedLeaving` (`:86`, sets `volatile flushWanted` `:79` and wakes the engine; the scene flushes backups next tick, `Shell/DawnScene.Hosted.cs`); `HostedStopped` (`:93`). `NativeWindow` func (`:15`) lets Hex.exe hand over the desktop window handle for the native self test without Dawn naming the desktop platform. `Shell/DawnScene.Hosted.cs` owns the queues `hostSaid`/`hostPicked` (tablet doc picker).
4. `DawnProgram.Scene(folder)` makes a scene for Dusk-host swapping.

### CLI flags (record in `HexOptions.cs`)

| Flag | HexOptions field | Notes |
|---|---|---|
| `--workspace dir` | `Workspace` | layouts folder; default `layouts` next to exe |
| `--open f` / `--goto f:l:c` / bare paths | `Open`, `Files`, `Line` | last file is shown; bare folder -> `Folder` |
| `--folder dir` | `Folder` | start with that workspace |
| `--size WxH`, `--scale n` | `Scale` | window size / UI scale |
| `--check a.hor dir`, `--exercise` | `Check`, `Exercise` | headless layout round-trip (`exercise` WRITES the files) |
| `--selftest [suites]`, `--wheel`, `--watch`, `--exit` | `SelfTest`, `Suites`... | temp folder `horizon-hex-selftest-<pid>`, `DUSK_HOME` redirected; exit 1 on failure |
| `--view code/table/design` | `View` | applied by the `asked_view` late tick (`ApplyAskedView`) |
| `--console lines...` | `Console` | HIDL console lines after load (`asked_console` panel tick) |
| `--browse` | `Browse` | start with an open dialog |
| `--fresh` | `Fresh` | no workspace/tabs/backups restore |
| `--window` | (none) | never hand over to a running Dawn |
| `--mcp` | `Mcp` | MCP server up for this run |
| `--no-ai` | `NoAi` | No AI for this run (`HexAi.Force`, also in `Program.Main` so Dusk's selector has no Agentic); setting `no_ai`. `HexAi.Off` gates the agents panel, Noon, chats, agent ticks, Agents menu, agent extensions (`HexExtensionCatalog.IsAgentSystem`) and agent MCP tools (`AgentsOnly`). `--selftest no-ai` implies it |

Env: `HEX_PROFILE=<ms>` writes slow ticks and per-step cost (`Shell/HexProfiler.cs`), `HEX_CONSOLE=on` (debug console, Program.cs).

## 3. Main window composition

`Shell/DawnScene.cs` is the root. Read its constructor (`:109-331`) first; it is a long ordered wiring script. Landmarks: `chrome.LoadLayout(EDITOR_LAYOUT)` `:136`, `HideLayersByVisible` `:141` (defined `:335`), `layout.Get<T>(...)` block `:144-207`, parts built `:173-228` (`codeEditing :173`, `explorer :175`, `git :176`, `running :183`, `designer :184`, `extensions :186`, `languages :189`, `codeEditing.languages = languages :191`, `BuildPanel :199`, `BuildTerminal :202`, `debugger :218`, `tasks :228`), `commands.Build(menu) :248`, `LoadKeys :249`, `SetScale(...) :260`, skin remember `:328`. `PostInit :346`.

- Two UI compositors: `stage` (draws the layouts being *edited*, in the game's skin; the canvas is a hole through the editor) and `compositor` (the editor's own UI, flat skin `Assets/uix/flat/skin.hor`). `backdrop` is a stage module (`Assets/hex/backdrop.hor`). `chrome` = module of `compositor` loaded from `EDITOR_LAYOUT = "Assets/hex/editor.hor"` (`DawnScene.cs:21`).
- Chrome: custom title bar (no system frame; `compositor.ResizesWindow = true`), `MenuBar menu`, toolbar (`play_button` -> `RunOrPlay`, `Menus.cs:22`), activity bar, side bar (`left`), middle (`center`: tabs, breadcrumbs, `code_scroll`, canvas, other views), right (`right`: Properties), bottom `TabPanel bottom` (Problems 0, Output 1, Terminal 2), `StatusBar`, `Notifications toasts`.
- Sizing: `Shell/DawnScene.Chrome.cs` (`SetScale :80`, `FitChrome :234`, `ReadScale :66`, scale list `HexFrame.Scales`), `Sides.cs` (grips, drag-to-collapse, `MIN_SIDE/MAX_SIDE`), `Split.cs` (two editor groups max, Ctrl+\), `Quiet.cs` (Zen/F11), `Materials.cs` + `Effects.cs` (glass/glow looks).
- Side bar views: `enum HexSide { Explorer, Search, Outline, Design, Debug, Tests, SourceControl, Agents }` (`Shell/DawnScene.SideBar.cs:7`). `Side` swaps `codeSide`/`designSide` (outline vs toolbox per document kind). Tree views register via `IPanels.AddSide(side, title, Func<List<UITreeItem>> rows)` -> `sideViews` (`Context/DawnScene.Context.cs:361`, `AddSide :363`), rendered by `RebuildSideView` (`:437`) through the shared `side_tree` (`Shell/DawnScene.SideTree.cs`). The Agents side is NOT tree-based: own layout `agents_view.hor` (`AGENTS_LAYOUT`, `Agents/DawnScene.AgentsPanel.cs:14`, `BuildAgentsPanel :38`, `PlaceAgentsView :86`). Activity buttons `act_*` wired in `WireActivityBar` (`SideBar.cs:89`); `ApplySide :240`.
- Views of a document: `enum HexView { Design, Code, Table, Extension, Notebook, Markdown }` (`Documents/HexView.cs`); `ApplyView` (`Shell/DawnScene.Views.cs:25`), `SwitchView :106`, F7 `ToggleView :136`. Panels built by `BuildNotebookView` (`Notebooks/DawnScene.Notebook.cs:47`) and `BuildMarkdownPreview` (`Notebooks/DawnScene.Markdown.cs:22`).
- Documents: `List<HexDocument> documents`, current `document`. `HexDocument` (`Documents/HexDocument*.cs`, partial) is a **layout** (UIModule on the stage, code generated from components, undo history: `.Code.cs`, `.History.cs`, `.Arrange.cs`, `.Source.cs`) or **text** (`IsText`, owns `Editor` = `CodeEditor`), picture (`Picture`), diff (`DiffOf`), notebook. `InDesigner` = layout in design view. Open path: `Documents/DawnScene.Documents.cs` `Open(path) :109` -> `Open(path, left) :115` -> workspace file / solution / picture / binary prompt / `FileKindFor` (built-in extension "designer" opens `.hor` with `#define layout`) / `OpenText`. Tabs: `Shell/DawnScene.Tabs.cs` (native `TabStrip`, `UpdateCrumbs :189`).

### 3.1 Partial-class file map of `DawnScene` (file -> what it owns)

| Folder / files | Owns |
|---|---|
| `Shell/DawnScene.cs` | ctor, fields, `UpdateState`, `DeclareTicks`, `Say :372`, `Notify :365`, `PostInit` |
| `Shell/DawnScene.Menus.cs` | `DeclareMenus :122`, `AddWindowCommands :157`, `RunOrPlay :57`, `RunWithoutDebugging :95` |
| `Shell/DawnScene.Commands.cs` | `AddHelpCommands :185`, SaveAll, GoToSymbol, F8 step problem, `ShowText` windows |
| `Shell/DawnScene.CommandPalette.cs` | palette/choosers (`ChooseFrom`) |
| `Shell/DawnScene.Keys*.cs`, `KeysWindow*.cs` | key dispatch, `dawn_keys.hor`, VS key layer, shortcuts window (+Filter, When, Conflicts) |
| `Shell/DawnScene.Chrome.cs`, `Sides.cs`, `SideBar.cs`, `SideTree.cs`, `SidePlaces.cs`, `SideResults*.cs`, `ValuesSide.cs`, `Properties.cs` | layout fitting, scale, side bar switching and the shared tree, values/properties side |
| `Shell/DawnScene.Tabs.cs`, `Views.cs`, `Split.cs`, `Places.cs`, `StatusBar.cs`, `Quiet.cs` | tabs/crumbs, view switching, editor groups, nav history (Alt+Left/Right, `AddPlaceCommands :104`), status bar, Zen |
| `Shell/DawnScene.Panel.cs`, `Terminal.cs`, `TerminalProblems.cs`, `PythonTerminal.cs`, `DotnetTerminal.cs`, `DebugBar.cs` | bottom panel pages, terminal page, matchers, floating debug bar |
| `Shell/DawnScene.Welcome.cs`, `PlainWelcome.cs`, `Scratchpads.cs`, `Changelog.cs`, `About.cs`, `Updates.cs`, `Restart.cs`, `Horizon.cs`, `Modes.cs`, `Remote.cs`, `Hosted.cs`, `Launch.cs`, `LaunchProfiles.cs`, `RunTarget.cs`, `Conveniences.cs`, `ShortcutHints.cs`, `AskLines.cs`, `OutlineCache.cs`, `Effects.cs`, `Materials.cs`, `Motion.cs` | start page, scratch docs, update check, restart-into-self-built, Dusk swap, pipe, tablet, run target and launch profiles, recents/auto save, hint text, off-thread outline, looks |
| `Code/DawnScene.Code.cs :349 AddCodeCommands`, `Find.cs :352`, `GoToAll.cs`, `SearchView.cs`, `SearchReplace.cs`, `Declared.cs`, `Peek.cs`, `LanguageMode.cs`, `TextSettings.cs`, `CodeLens*.cs`, `Inlays.cs`, `Semantic.cs`, `Folding.cs`, `Outlining.cs`, `Backdrop*.cs` | editor policy (see section 7 Code) |
| `Documents/DawnScene.Documents.cs :631 AddDocumentCommands`, `Backups.cs`, `DiskChanges.cs`, `Images.cs`, `Recent.cs`, `Untouched.cs` | open/save/close, unsaved backups, external change handling, picture viewer, recent list |
| `Settings/DawnScene.Settings.cs` (`LoadSettings :21`, `SaveSettings :52`, `AddSettingsCommands :61`), `SettingsPage.cs`, `SettingsFile.cs`, `SettingsScope.cs`, `SettingsImport.cs`, `SettingsPalette.cs`, `SettingsDiagnostics.cs`, `EditorSkin.cs`, `ThemePick.cs`, `Looks.cs`, `RenderScale.cs`, `ZoomSettings.cs`, `ExtensionSettings.cs` | settings window and file, themes, scale |
| `Context/DawnScene.Context.cs` | the `I*` interface implementations handed to `DawnContext` (section 6) |
| `Workspace/DawnScene.Background.cs`, `ExplorerFiles.cs`, `ExplorerHead.cs`, `Refresh.cs`, `NewProject.cs`, `Solution*.cs` | the one `WorkScheduler`, explorer glue, Solution Explorer |
| `Git/DawnScene.Git.cs`, `Diff.cs`, `History.cs`, `Conflicts.cs`, `CommitBox.cs` | git colours, diff tabs, local history, merge lenses |
| `Agents/DawnScene.AgentsPanel*.cs`, `Agent.cs`, `Mcp*.cs` | chat UI, MCP tools (section 7 Agents) |
| `Notebooks/DawnScene.Notebook*.cs`, `Markdown.cs` | notebook view, markdown preview |
| `Extensions/DawnScene.ExtensionUI.cs`, `ExtensionsWindow.cs`, `FileKinds.cs` | extension screens, Extensions window, file kinds |

## 4. Tick loop (the real main loop)

`DawnScene.UpdateState(dt)` (`Shell/DawnScene.cs:390`) order: viewport sync, `--check` runs, one-off `GC.Collect(optimized)` after 3 s, zoom settings, shortcut hints, `UpdateWindow` (dialog results, closing, hosted, render scale, `HexModes.Update`, chrome colour via `ColourChrome` `:449`, `PlaceCanvas`), `UpdateDesigner` (canvas pointer, **`UpdateKeys`**, text edit, code regen throttled by `HIDDEN_CODE_EVERY = 0.1 s` `:96`), `RebuildMarked` (`tabsDirty, treeDirty, inspectorDirty, codeDirty`), then three step lists, status bar + crumbs, `base.UpdateState` (components), `FitChrome`, self-test update.

`DeclareTicks` (`:506`) builds three `HexTicks` lists (`Context/HexTicks.cs`):
- `workTicks`: `finished` (`TakeFinishedWork`), horizon, welcome, disk, settings_file, backups, find_bar, places, backdrop, split, running_item, folders, values, **properties**, peek, file_changes, **languages, git, tasks**, value_box, visual_studio_offer.
- `lateTicks`: extension_ui, effects, auto_save, **debugger**, debug_bar, asked_view.
- `panelTicks`: table, notebooks, markdown, terminal, remote, mcp, console, asked_console, panel.

`IHexPart { TickName; Tick(dt) }` (`HexTicks.cs:8`) is implemented by exactly five parts (`HexLanguages :72`, `HexGit :78`, `HexTasks :84`, `HexProperties :90`, `HexDebugging :96`). Add a part with `.Add(part)` (`:50`) or a method with `.Add("name", method)`. Steps are delegates built once (no per-tick garbage); the list is the order.

## 5. Command system

Defined in **Horizon.UI** (`horizon/Horizon.UI/Components/Popups/UICommands.cs`), one instance `DawnScene.commands` (also `DawnContext.Commands`).

- **Declare menus** once in `DeclareMenus` (`Shell/DawnScene.Menus.cs:122`): `commands.Declare("File", "new","save",...)` for File, Edit, Selection, Layout, Notebook, View, Go, Run, Test, Terminal, Git, Help. Args after the title are **groups** (rule-separated). `DeclareSubmenu("File/new","scratch",title,icon,groups...)` makes a flyout. `ShowMenuWhen(title, Func<bool>)` hides a menu conditionally. No menu may list more than 20 rows (self test enforces); extras go in submenus or `PaletteOnly`.
- **Add a command**: `commands.Add("Menu/group", label, Action, key = "", icon = "", UIKeyUse use = Shown)` returns the `MenuItem`; set `.IsVisible`, `.IsEnabled`, `.IsChecked`, `.PaletteOnly`. Path `"View/appearance/look:parts"` = submenu `appearance`, group `look:parts`. `UIKeyUse.Shown` only writes the key text; `Bound` fires the command unless typing; `BoundWhileTyping` fires in text boxes too (Ctrl+/F-keys). Chords: `"Ctrl+K Z"`. Keys of a conditional menu's command fire first and only when `UICommands.CanPick`.
- **Key with no menu item**: `commands.Bind(Key.F1, action, control:, whileTyping:, shift:, alt:, then:)` (`Menus.cs:160`, `:204`, end of `Terminal.cs`).
- `commands.Build(menu)` runs once near the end of the ctor (`DawnScene.cs:248`, just before `LoadKeys`): **commands added after Build don't appear**. Commands are added in part ctors or `Add*Commands`: `AddWindowCommands` (`Menus.cs:157`), `AddDocumentCommands` (`Documents/DawnScene.Documents.cs:631`), `AddPlaceCommands` (`Places.cs:104`), `AddSettingsCommands` (`Settings/DawnScene.Settings.cs:61`), `AddFindCommands` (`Code/DawnScene.Find.cs:352`), `AddCodeCommands` (`Code/DawnScene.Code.cs:349`), `AddHelpCommands` (`Shell/DawnScene.Commands.cs:185`), `AddNotebookCommands` (`Notebooks/DawnScene.NotebookCommands.cs:38`), `AddLanguageModeCommands`, `HexExplorer.AddWorkspaceCommands`, `HexDesigner.AddDesignerCommands` (`Designer/HexDesigner.Selection.cs:407`), `HexLanguages.AddServerCommands` (`Languages/HexLanguages.Navigation.cs:709`), debugging inline in the `HexDebugging` ctor (`Debugging/HexDebugging.cs:~47-115`, e.g. `Run/debug` F5 `:47`), Git at `HexGit.SourceControl.cs:21-24`.
- **Palette** (Ctrl+Shift+P / F1): `Shell/DawnScene.CommandPalette.cs`. `PaletteChoices()` lists every visible+enabled item of every visible menu (`bar.Everything()`), plus extension commands (`extensions.Commands()`), task automations (`tasks.AutomationChoices()`), settings commands (`Settings/DawnScene.SettingsPalette.cs`), and Dusk terminal commands (`terminals.Commands.All`). Rendered by `ChooseFrom` -> `Hex.Shell` `UIChooser.Show` (same chooser Dusk uses); prefixes `>`, `@`, `#`; `UIRecentList`. Quick open (Ctrl+P), Go to Symbol, Go to All (`Code/DawnScene.GoToAll.cs`) share it. MCP `hex_commands`/`hex_run_command` use the same list (`Agents/DawnScene.McpWorkbench.cs`).
- **Key dispatch**: `UpdateKeys` (`Shell/DawnScene.Keys.cs:422`) -> `commands.Keys.Update(keyboard, typing)`. Suppressed when a dialog is up, a terminal/arrange overlay has focus, or a popup owns keys.
- **Rebinding**: `File > Keyboard Shortcuts` (`Shell/DawnScene.KeysWindow*.cs`: filter `@conflicts`/`@source:`, `.When.cs` context rules, `.Conflicts.cs` Replace/Keep Both; layout `Assets/hex/keys.hor`). Saved by command *label* in `dawn_keys.hor` (`KEYS_FILE`, `Keys.cs:25`; `keysPath` `:70` is null when not `kept`; migrates from old `keys.hor` via `was:`); core is `Rebind` (`Keys.cs:149`, called `:413`). VS key layer: `Keys.VisualStudio.cs` `ApplyKeyLayer :111` applies VS bindings through `Rebind` under hand edits; offered once when a `.sln` opens (`Keys.VisualStudioOffer.cs`, tick `visual_studio_offer`). Terminal-owned keys (Ctrl+J, F6, Ctrl+`, F5, Shift+F5, F1) are registered as terminal commands in `BuildTerminal` (`Terminal.cs:~90-110`) so the shell never sees them.
- `menu.AltKeyTaken` stops Alt+letter opening a menu when a command owns the key.
- F5 = `RunOrPlay` (`Menus.cs:57`): chosen run target -> debugger source -> debuggable file (C#, D, native, Python; `debugger.InstallingFirst` `:76`) -> layout play -> run task -> `running.RunCode()`. Ctrl+F5 `RunWithoutDebugging :95`. Run target dropdown: `Shell/DawnScene.RunTarget.cs`, `Tasks/HexTasks.RunTargets.cs`, launch profiles `Shell/DawnScene.LaunchProfiles.cs`.

### Recipe: add a command
```csharp
// in a part ctor (has DawnContext `context`) or a DawnScene.Add*Commands method, BEFORE commands.Build
context.Commands.Add("Edit/find", context.Strings.Said("mypart.do_thing"), DoThing, "Ctrl+Alt+T", "search", UIKeyUse.BoundWhileTyping)
    .IsEnabled = () => context.Documents.Current.IsText;
```
(real example: `Git/HexGit.SourceControl.cs:21`, `context.Commands.Add("View/sides", Said("git.side_command"), () => context.Panels.ShowSide(HexSide.SourceControl), "Ctrl+Shift+G", "source-control", UIKeyUse.BoundWhileTyping)`.)
1. Add the key under the right section of `Assets/hex/localisation/en-gb.hor`. 2. If the group name is new, add it to `DeclareMenus`. 3. `PaletteOnly = true` for palette-only. 4. Check key clashes in the Shortcuts window. 5. Menu with >20 rows fails the menus self-test suite; whether it also asserts duplicate keys is unverified.

### Recipe: add a side-bar view
1. Add the member to `HexSide` (`SideBar.cs:7`) and a button `act_xxx` in `Assets/hex/shell/activity_bar.hor`; wire in `WireActivityBar` (`SideBar.cs:89`, also the Tab-order array).
2. In the part ctor: `context.Panels.AddSide(HexSide.Xxx, title, Rows)` (`Rows()` returns `List<UITreeItem>`); `RefreshSide` when data changes (`HexGit` does it from `Changed`); header controls via `AddSideHead`; `ShowSide` from a View/sides command.
3. Non-tree view (like Agents): instantiate a layout into `left`, add a `SideGrip`, toggle in `ApplySide (:240)` / `PlaceAgentsView`.

### Recipe: add a bottom-panel page / problem source
Pages are `TabPanel bottom` pages in `Assets/hex/shell/panel.hor` (Problems 0, Output 1, Terminal 2; `PROBLEMS_PAGE`, `OUTPUT_PAGE` `Shell/DawnScene.Panel.cs:29`, `CONSOLE_PAGE :27`). `ShowPanel(page)` `:206`. Extensions add pages via `hex` API -> `IExtensionScreens.MakePanelPage` (impl `Extensions/DawnScene.ExtensionUI.cs`). Extra problem sources: `IPanels.AddProblems(Func<IReadOnlyList<HexProblem>>)`. Output text: `Note` (`MAX_OUTPUT = 400` lines).

## 6. DawnContext / part pattern (`Context/`)

`Context/DawnContext.cs` hands parts narrow interfaces: `IDocuments :58` (All/Current/Open/OpenAt/Rewrite; events Opened/Edited/Closed/Saved; hooks `AddDiagnostics :100`/`AddCompletions`/`AddHovers`/`AddSignatures`; `DiagnosticsChanged :103`), `IWorkspace :116` (Folders/Roots/RunTarget/Configuration/file-change events), `INotices :168` (`Say` status bar, `Note` Output panel, `Notify` toast), `IWork :181` (`Start(key,...)` cancellable background work with a card; `InBackground<T>(doing, work, done)` completing on the UI thread), `IWindows :206`, `ISettingsHost :280` (`Values`, `Save`, `AddSource`), `ITerminals :299`, `IPanels :338`, `IStrings :399` (`Said`), `IEditorWindow :410` (layout/compositor/chrome/Code/views/dirty flags `this[DawnParts.Tabs]`), plus `Wake() :39`, `Redraw() :42`, `Keyboard`, `Typing`. `DawnScene` implements all of them (`Context/DawnScene.Context.cs:16`) and builds `Context` lazily (`:34`). New features take `DawnContext`, register hooks, never reference `DawnScene`. Some parts still receive extra delegates/properties from the scene (`git.ColourOf`, `debugger.Tasks`, `languages.PythonInterpreterChanged`, `tasks.RestartInto`, `tasks.Extensions`).

Diagnostics model: any part contributes squiggles via `AddDiagnostics(doc => IReadOnlyList<UICodeDiagnostic>?)`, then `DiagnosticsChanged(doc)`; completions via `HexCompletionSource`; hover/signature similar. LSP, the D compiler check, terminal problem matchers and `Settings/DawnScene.SettingsDiagnostics.cs` all feed this.

## 7. Folder guide

### Shell/ (58 files)
Window chrome and everything not owned by a part; file map in 3.1. Key types: `HexProblem` record (`Panel.cs`), `HexProfiler`, `HexNotes`, `HexWhatsNew`, `HexAbout`. `StatusBar.cs` caches shown items by tuple (no per-tick allocation). `Conveniences.cs`: recent commands/files, reopen closed (Ctrl+Shift+T), `UpdateAutoSave :101`. `Scratchpads.cs`: unsaved scratch docs (`docs/scratchpad-plan.md`). `Restart.cs`: restart into a self-built Hex. Notifications: `Say` (status bar 10 s, also logs to Output, `:372`), `Notify` (toast, errors sticky, `:365`).

### Code/ (25)
Not the text engine: the **editor control is `Horizon.UI.CodeEditor`** (`horizon/Horizon.UI/Components/Controls/CodeEditor*.cs`, Gutter, Minimap, Squiggles, Inlays, Lenses, NativeFolding, Semantic, SignaturePopup...) over `UITextDocument*.cs`. Code/ is Hex's policy:
- `DawnScene.Code.cs` (`PrepareEditor :49` sets zoom, wrap, minimap, font, `SignatureSource`, glide; called `:252` on open), `TextSettings.cs` (tabs, .editorconfig, rulers, format on save), `Find.cs` (Ctrl+F/H/G/P, F8 check-all), `SearchView.cs` + `SearchReplace.cs` (workspace search, off-thread, 250 ms debounce), `GoToAll.cs`, `Declared.cs` (F12 via harness index when no LSP), `CodeLens*.cs`, `Inlays.cs`, `Semantic.cs`, `Folding.cs`, `Outlining.cs`, `Peek.cs` (Alt+F12), `LanguageMode.cs` (Ctrl+K M, file associations), `Backdrop*.cs` (picture behind code), `HexTicker.cs`.
- `HexCodeEditing*.cs` (part): `Completion` (completion + F12/Shift+F12, HIDL vocabulary), `Hints` (hover, context menu, rename), `Signatures`, `Docs` (`// @doc`), `FindBar` (`find_bar.hor`), `Font` (Hack toggle). Source lists `completionSources`/`hoverSources`/`signatureSources` (`HexCodeEditing.cs:29`) are asked before the editor's built-in HIDL knowledge; `HexLanguages` adds the LSP source.

### Languages/ (42) - LSP client glue
`HexLanguages` (part, `IHexPart`) drives `Horizon.Tooling.LanguageServers.LanguageServer` (JSON-RPC over stdio, `horizon/Horizon.Tooling/LanguageServers/`). Servers are declared in `Assets/hex/language_servers.hor` (`let servers = {...}` `:33`): csharp (csharp-ls `--solution {solution}`), python (pyright-langserver), slang, cpp (clangd), typescript, json, markdown (marksman), d (serve-d), yaml, go, java (jdtls), rust, bash, toml, docker, svelte. Per-entry keys: `command, args, language, extensions, about, install, update, repo, version, reload, needs, settings` (documented in the file's header comment). HIDL has no external server: Hex is its server (`Hex.exe --lsp`).
- Lifecycle: `Documents.Opened` -> `StartLinting` (`HexLanguages.cs:59`; server per doc via `ServerRootFor :97`), edits debounced `LINT_DELAY = 0.3 s` (`:21`); `Missing.cs` once-per-session install card; `Updates.cs` install/update/version; `Restore.cs` `dotnet restore` for never-restored projects; `Stopped.cs` crash handling; `Prerequisites.cs`/`Needs.cs`.
- `ServerEntries` (`HexLanguages.cs:174`) merges the file with settings-added servers (`Added.cs`, `HexAddedServer` in `hex/Hex.Core/Settings/HexAddedServer.cs`, `AddServer :44`); settings UI `Settings.cs` (`ServerSource :106`) + `Editor.cs` (add/edit dialog, `AddServer(new HexAddedServer(...)) :223`), registered `HexLanguages.cs:81` via `Settings.AddSource`.
- Features: `Completion.cs` (`serverAnswers` ConcurrentQueue `:32`), `Navigation.cs` (730 lines: definition, references, rename, hover, signatures, `AddServerCommands :709`), `Hierarchy*.cs`, `TypeHierarchy.cs`, `TypeDefinition.cs`, `Semantic.cs` (`ApplyCSharpColors :53`, called `HexLanguages.cs:57`), `Inlays.cs`, `CodeLens.cs`, `Fixes.cs`/`FixPreview.cs`, `FormatOnSave.cs`, `OnTypeFormatting.cs`, `OrganizeImports.cs`, `Usings.cs`, `Folding.cs`, `Highlights.cs`, `Selection.cs`, `Overrides.cs`, `HeaderSource.cs`, `EditSnapshot.cs`, `Harness.cs` (feeds declarations to the code index), `Sources.cs`, `Questions.cs`, `DefinitionResults.cs`, `Diagnostic*.cs`.
- Extras: `Compiler.cs` (D files checked with dmd/ldmd2 `-o-`, 0.8 s delay, dub describe), `Python.cs`/`PythonSetup.cs` (interpreter picker, venv), `CMake.cs`.
- Gotcha: the C# server needs a .sln/.csproj in the root (`needs`) or it is not started and the file is only coloured; `reload` file changes restart it after 1.5 s.

### Recipe: add a language server
1. Add an entry to `servers` in `Assets/hex/language_servers.hor`, copying `python` (`:44`): `command`, `args`, `language` (the LSP language id), `extensions`, `about`, `install`/`update` (string or `{windows, macos, linux}` object), `repo` (`owner/name`), `version` args; `reload`/`needs` globs only when the server reads projects once; `settings` for server config (`{root}` and `{solution}` are substituted).
2. If Hex should also colour the language, the language id must exist in the editor's language table (otherwise the settings UI shows the name from the file, `HexLanguages.Editor.cs:28`).
3. No C# change is needed for the lint/completion/navigation path: `ServerEntries` picks it up, `StartLinting` starts it for matching extensions. Test with `Hex --selftest languages`.
4. A user can do the same without editing the file: Settings > Language servers (`HexAddedServer`).

### Debugging/ (45) - DAP client glue
`HexDebugging` (part, `IHexPart`, `Debugging/HexDebugging.cs`, 1210 lines, class `:22`) uses `Horizon.Tooling.Debugging.DebugSession`. Adapters: **netcoredbg** for C# (installed on first F5 into the settings folder, `InstallNetcoredbg`; setting `debugger_path`, workspace-scope in `HexSettings.Workspace.cs`), **lldb-dap** for D and native (`.D.cs`, `.Native.cs`, `.CMake.cs`, `.CTest.cs`), **debugpy** for Python (`.Python.cs:50`, `-m debugpy.adapter`, `.PythonSetup.cs`, `.PythonTests.cs`), attach (`.Attach.cs`). `.Tests.cs` debugs `dotnet test` via `VSTEST_HOST_DEBUG=1`.
- UI: `HexSide.Debug`, `Shell/DawnScene.DebugBar.cs` (`debug_bar.hor`), breakpoints (`.Breakpoints.cs`, `.SourceBreakpoints.cs`, conditions `HexDebugExpressions*.cs`/`HexDebugEvaluator.cs`, tracking across edits `HexBreakpointTracking.cs`, `.Functions.cs`, `.DataBreakpoints.cs`, `.InstructionBreakpoints.cs`), watch, `HexDebugVariables.cs`, `.DataTips.cs`, `.Immediate.cs`, `.Disassembly.cs`, `.Memory.cs`, `.Modules.cs`, `.Threads.cs`, `.Exceptions.cs`, `.SetNext.cs`, `.RunTo.cs`, `.Frameworks.cs`, `.Ends.cs`, `.Kept.cs` (persist breakpoints/watches), `.Agents.cs` (MCP debug tools support, `Agents/DawnScene.McpDebug.cs`).
- Keys are VS Code's: F5, F9, F10/F11/Shift+F11, Shift+F5, Ctrl+Shift+F5, Ctrl+F10. F11 also toggles Zen when nothing is stopped. Flags: `debugger.DebugStopped`, `debugger.Debugging`, `building`. Adapter work crosses threads through `debugWork` (`ConcurrentQueue<Action>`, `:142`) drained in the tick.

### Agents/ (23) - Agents panel and MCP server
1. **Agents side panel** (`DawnScene.AgentsPanel*.cs`, `agents_view.hor`): `.AgentsPanel.cs` (build, `NewAgentChat`, `RunnableAgents`), `.Chats.cs` (chat list, nested sub-agents), `.Rows.cs` (transcript rows, incremental), `.Options.cs` (pickers from the extension's `run.options`), `.Run.cs` (`SendToAgent`/`StartAgent`: spawns the program configured by an **extension** via `hex.agent`, prompt on stdin, JSON lines to the extension's `on_event`, MCP server started first), `.Motion.cs`. `DawnScene.Agent.cs`: "Explain/Change with Agent" draft. Model `hex/Hex.Core/Agents/HexAgentChat.cs:47`; profiles `Hex.Core/Harness/HexAgentProfiles.cs`. Concrete systems are extensions (`hex-extensions/claude_code`, `codex`); Dawn knows none by name. Command `View/agent` Ctrl+Alt+A.
2. **MCP server** (`DawnScene.Mcp*.cs`): off unless setting `mcp_server` or `--mcp`. `HexMcpTools` (`Mcp.cs:319`) queues each call (`QueueMcp :152`, `ConcurrentQueue mcpCalls :23`) and `UpdateMcp :166` drains it as a coroutine: tools return `IEnumerable<McpToolResult?>`, `yield return null` waits a tick. `HexMcpTool(Meta, Run, Timeout = 30 s, RunAway)` (`Mcp.cs:301`); `RunAway` tools run off-thread (harness, self test). Groups composed in `McpToolList` (`McpWorkbench.cs:87`): Screen (`McpScreen.cs`, click/key/type via `HexAgentPointer`), Editor, Workbench, Notebook, Agent (hints, `McpAgent.cs`), Agents (`McpAgents.cs`, `Depth`/parallel limits), Harness (`McpHarness.cs`), Terminal, Debug. Lines/columns 1-based in MCP, 0-based inside Dawn. Element ids: `McpUiTree.cs`. Protocol and pipe: [core-and-mcp.md](core-and-mcp.md); user doc `hex/docs/agents.md`.
   - **Recipe: add an MCP tool**: write a method `IEnumerable<McpToolResult?> Name(JsonObject args)`, add `new(new McpTool("hex_name", title, desc, McpSchema.Object([...], required...)), Method)` to one of the `*Tools()` lists (pattern `McpAgent.cs:8-16`). Touch the editor only inside `Run` (UI thread); mark `RunAway` for pure off-thread work.

### Git/ (21)
git CLI via `hex/Hex.Core/Git/` (`GitProcess`, `GitRepository`), not libgit2. `HexGit` (part, `IHexPart`): repos found in workspace roots + nested to depth 5 every 6 s (`.Nested.cs`), per-repo status, one git call per repo at a time in the background; `Changed` + `Version`. Settle 0.4 s after a file change, HEAD/index polled 1 s. Files: `.SourceControl.cs`, `.Commit.cs`, `.CommitBox.cs` + `DawnScene.CommitBox.cs` (Ctrl+Enter), `.Branches.cs`/`.BranchOps.cs`, `.Merge.cs`, `.Stash.cs`, `.Sync.cs`, `.Hunks.cs`, `.Blame.cs`, `.Active.cs`, `.Headings.cs`, `.Filter.cs`, `.Changes.cs`. Scene side: `DawnScene.Git.cs` (explorer/tab colours), `.Diff.cs` (Alt+F5), `.History.cs` (local history copy per save under `local_history/`, `LocalHistoryRoot :12`), `.Conflicts.cs` (Alt+1/2/3).

### Designer/ (14)
Visual designer for **Horizon.UI layouts** (HIDL `.hor` with `#define layout`, as Fighter2D and `editor.hor` use). `HexDesigner.Catalog.cs` (`Palette :8`, `PaletteGroups :16`), `.Palette.cs`, `.Inspector.cs`, `.Lists.cs`, `.Selection.cs` (`AddDesignerCommands :407`), `.Editing.cs`, `.Handles.cs`, `.Pointer.cs`, `.Canvas.cs`, `.Texts.cs`/`.Languages.cs` (writes language keys to the game's localisation), `HexLayoutDebugger.cs`. Layout code is *generated* from live components (`HexDocument.GenerateCode`) and re-parsed on code edit. Exposed as built-in extension `Assets/hex/extensions/designer` (`native: "designer"`) via `HexDesignerKind : IHexFileKind`; switching it off opens layouts as plain code.

### Notebooks/ (17)
Native `.ipynb` UI. A notebook is a text document (JSON) plus view `HexView.Notebook`; cells written back `NOTEBOOK_WRITE_AFTER = 0.3 s` after change (`DawnScene.Notebook.cs:22`). Model + kernel protocol in `hex/Hex.Core/Notebooks/` (own ZMTP/Jupyter client). UI: `.Notebook.cs`, `.NotebookCells.cs`, `.NotebookKernel.cs` (start on first run, run/interrupt/restart/select), `.NotebookOutputs.cs`, `.NotebookRendering.cs` + `NotebookRender*.cs` (CPU raster of images, SVG, LaTeX, off-thread), `.NotebookTables.cs`/`.NotebookCsv.cs`, `.NotebookExport.cs`, `.NotebookCommands.cs`, `DawnScene.Markdown.cs`, `HexNotebook.cs` (per-notebook state), `NotebookScrollPanel.cs`, `NotebookBackdrop.cs`.

### Terminal/ (2) and the terminal page
`HexBuildProblems.cs` (compiler/CMake/linker output -> `TerminalProblem`), `HexTerminalFiles.cs` (makes Dusk's `settings.hor`, `keys.hor`, plugins terminal-aware). The terminal is the engine's `TerminalPanel` + `TerminalHost`, built in `Shell/DawnScene.Terminal.cs:31` (`layout.Get<TerminalPanel>("terminal") :33`), `Name="hex"`, shared with Dusk (same settings/keys/history). `HexTerminalApi` (`Terminal.cs:250`) is the hex object handed to plugins. The HIDL **console** is a terminal profile (`HexRunning.CONSOLE_PROFILE`, `Values/HexRunning.Console.cs:19`). The first tab opens lazily when the page is first shown.

### Settings/ (14)
`HexSettings` is in `hex/Hex.Core/Settings/` (`HexSettings.cs` fields, `.Table.cs` key table, `.Workspace.cs`, `HexAddedServer`, `VsCodeSettings`, `HexEditorConfig`); persisted as HIDL `settings.hor` in the settings folder, **shared with Dusk**; workspace overrides `<folder>/.hex/settings.hor` (keys listed in `HexSettings.Workspace.cs:~27`; `.hex` can never enable MCP). Dawn side: `Settings.cs` (`LoadSettings :21`, `SaveSettings :52`), `SettingsPage.cs` (738 lines; Ctrl+, window; `EditorSource :288` builds rows; sections `"Editor · Panels"`, `"Extensions · Installed"`), `SettingsFile.cs` (settings.hor as a tab, reloaded 0.2 s after any write; invalid file keeps old values and reports the line), `SettingsScope.cs` (User|Workspace), `SettingsImport.cs` (VS Code import), `SettingsPalette.cs`, `SettingsDiagnostics.cs`, `EditorSkin.cs` (`ColourChrome :87`)/`ThemePick.cs`/`Looks.cs`, `RenderScale.cs`, `ZoomSettings.cs`, `ExtensionSettings.cs` + `ExtensionSettingsSource.cs`.

### Recipe: add a setting
1. `hex/Hex.Core/Settings/HexSettings.cs`: add the field (`public bool Minimap = true; :184`).
2. `HexSettings.Table.cs`: add a row to the table, e.g. `Flag("mcp_server", s => s.McpServer, (s, v) => s.McpServer = v)` (`:83`); also `Number(key, get, set, min, max)`, `Choice(key, get, set, "a", "b")`, `OnDisk`, `Special` for custom read/write. If it must be workspace-overridable, add the key to the list in `HexSettings.Workspace.cs`.
3. Settings window row: for editor-level settings add a `UISetting` to `hex/Hex.Shell/Settings/EditorSettingsCatalog.cs` (`new("minimap", Said(...title), UIFormKind.Flag, Said(...help), SECTION)` `:143`, default in `Usual :295`) and its current value to `InForce` in `SettingsPage.cs` (`"minimap" => Values.Bool(hex.settings.Minimap)`, `:322`). Part-specific settings instead implement `ISettingsSource` and register `context.Settings.AddSource(...)` (see `HexLanguages.cs:81`).
4. Texts: `settings_catalog.<key>_title/_help` in `en-gb.hor` (`:2396`) and search aliases (`:2528`).
5. Apply on change in `LoadSettings`/the apply path (`Settings.cs`), or read `settings.X` where used.

### Extensions/ (21)
HIDL-scripted extensions. `HexExtensions` (part) loads `Assets/hex/extensions/*` (built in: `designer`) and the user `extensions/` folder next to Hex, each with `extension.hor`; `on_start`, `on_run` events; the `hex` object is built in `HexExtensions.Api.cs` from groups in `.Api.cs`, `.More.cs`, `.Edits.cs`, `.Files.cs`, `.Caret.cs`, `.Problems.cs`, `.Timers.cs`, `.Projects.cs`, `.Settings.cs`, `.Agents.cs` (`hex.agent`). Catalog `.Catalog.cs` (checks `bogzxo/hex-extensions` daily; install/update = copy), `.Sources.cs`. UI: `DawnScene.ExtensionUI.cs` (`IExtensionScreens`; handlers get ~1 s, an extension that throws is disabled), `DawnScene.ExtensionsWindow.cs` (Ctrl+Shift+X; layout `extensions_window.hor`), `DawnScene.FileKinds.cs`, `IHexFileKind.cs`, `HexInterop.cs`, `HexExtension.cs`. Bundled catalog `hex/hex-extensions/` (claude_code, codex, emmet, hello_dolly, move_editor, pretty_json, snippets, text_tools, todo). Docs `hex/docs/extensions.md`.

### Workspace/ (17)
`HexExplorer` (part): workspace folders (`HexWorkspace` in Hex.Core, `.hex-workspace`; untitled one `UntitledWorkspacePath`, `Workspace.cs:27`), file tree, create/rename/trash/clipboard (`.Files.cs`, `.Clipboard.cs`), `RestoreWorkspace`, `HexFolders.cs` (cached directory reads + `FileSystemWatcher`s, `TakeFolderChanges`). Scene side: `DawnScene.Background.cs` (the one `WorkScheduler work :15`, `TakeFinishedWork :100`), `Solution*.cs` (Solution Explorer of .sln/.slnx via `Hex.Core.Projects.DotnetSolutionTree`, NuGet, add project/file), `NewProject.cs`, `ExplorerHead`, `Refresh`.

### Documents/ (13)
`HexDocument*` (section 3), `DawnScene.Documents.cs` (open/save/close, `Show`, `SaveFormatted`), `Backups.cs` (unsaved text every `BACKUP_EVERY = 2 s` `:9` into a backup folder, restored next start, dropped on clean close; the self test uses its own), `DiskChanges.cs` + `HexDiskWatch.cs` (external change: quiet reload as an undo step if unmodified, card if modified; `changed` ConcurrentQueue `:20`), `Recent.cs` (`hex_recent.txt`, 8 entries), `Images.cs`, `Untouched.cs`.

### Tasks/ (10)
`HexTasks` (part, `IHexPart`): `tasks.hor` (`TASKS_FILE :16`, in the workspace root or `.hex/`, one object per task); runs in a reused named terminal tab or Dusk window; dependencies first; Ctrl+Shift+B build, F5 run task. `.Setup.cs` ("Set Up F5"), `.Automations.cs` + `HexAutomationRun*.cs` (HIDL automations: palette entries, off-thread with a card, editor calls marshalled to UI), `.RunTargets.cs`, `.SelfBuild.cs`, `.Fixes.cs`, `.Native.cs`, `.Python.cs`.

### Values/ (5)
`HexRunning`: right-side values tree of a HIDL file, evaluated shortly after typing stops (`HexValues`); table view (F7, `.Table.cs`: objects-of-objects as rows, language file as key x language grid, editable cells); console (`.Console.cs`); runner (`.Runner.cs`, `runner.hor`); `.Editing.cs`.

### Properties/ (4)
`HexProperties` (part, `IHexPart`): right panel as VS Properties window, `HexPropertiesShows { Component, Values, Files }`. For files it edits MSBuild Build Action / Copy to Output by asking the SDK (`DotnetProjects`, off-thread; `projectsChanged` volatile `:243`) and writing the `.csproj` (`.Project.cs`, `.ProjectFile.cs`). Alt+Enter. Layout `Assets/hex/shell/properties.hor`.

### Context/ (3)
`DawnContext.cs`, `DawnScene.Context.cs`, `HexTicks.cs` (section 6 and 4).

## 8. Assets/hex layouts

`editor.hor` (161 lines, `#define layout`) is the skeleton, built on `compositor`: `root` stack -> `top` (`title_bar`, 40 px, icon, `@window.title`) with `menu` (`menu_bar`) -> `body` (horizontal stack) holding includes `activity_bar` (`shell/activity_bar.hor`), `side_bar` (`shell/side_bar.hor`), `center` (stack, padding 10) then `properties` (include `shell/properties.hor`) -> `status_bar`, `toasts` (notifications, bottom right). Inside `center`: include `editor_tabs` (`shell/editor_tabs.hor`), then the swappable view panels each on a **layer**: `canvas` (layer canvas), `code_scroll` (code), `data_table` (table), `ext_view` (extension), `notebook_view`, `markdown_preview`, `image_view` (+ `image_picture`, `image_info`), and the include `panel` (`shell/panel.hor`: Problems/Output/Terminal pages). Last line: `compositor.hidden_layers([...])` lists layers hidden at start (files, code, table, extension, notebook, markdown, image, values); the code turns those to `Visible=false` (`HideLayersByVisible`) and shows one per view. Names are the contract: C# does `layout.Get<T>("name")`, so renaming a component without updating `DawnScene.cs:144-207` crashes at start (self test and `--check` catch it).

Other files: `shell/` includes `activity_bar.hor` (154), `side_bar.hor` (244), `editor_tabs.hor` (188), `panel.hor` (62), `properties.hor` (69), row templates `tree_row.hor`, `property_row.hor`, `output_row.hor`, `palette_button.hor`. Top-level: `agents_view.hor`, `ask.hor`, `choose.hor` (UIChooser), `colour_dialog.hor`, `debug_bar.hor`, `extensions_window.hor`, `find_bar.hor`, `hint.hor`, `keys.hor` (shortcuts window), `language_servers.hor` (data, not layout), `link.hor`, `note.hor`, `runner.hor`, `search_view.hor`, `text_window.hor`, `value_editor.hor`, `view_button.hor`, `welcome.hor` (284), `backdrop.hor`; `editors/*.hor` are settings-row editors (choice, colour, four, kind, number, pair, section, slider, text, yes_no); `localisation/en-gb.hor`; `extensions/designer/extension.hor`; `images/`. Layout text uses `@key` localisation refs.

## 9. State, persistence, threading, theming

### Persistence files
All through `HexFiles.Kept(name, was:)` (`hex/Hex.Shell/HexFiles.cs`) = `TerminalFiles.UserFolder`, the settings folder next to the exe. Self test and `--check` pass `kept=false`/null paths so nothing real is read or written.

| File | Owner |
|---|---|
| `settings.hor` | `HexSettings` (shared with Dusk); workspace `<folder>/.hex/settings.hor` |
| `dawn_keys.hor` | `Keys.cs:25`, rebinds by command label |
| `hex_recent.txt` | `Documents/DawnScene.Recent.cs:15` |
| `hex_skin.txt` | `UISkins.Remember`, `DawnScene.cs:29,328` |
| `hex_ui_scale_on_screen.txt` | `Chrome.cs:16` (`ScalePath :64`) |
| recent commands/files/scratch languages lists | `Conveniences.cs:18-19`, `Scratchpads.cs:18` (`UIRecentList`, only when `KeepsRecent`) |
| `hex_untitled.hex-workspace` | `HexExplorer.UntitledWorkspacePath` |
| `local_history/` | `Git/DawnScene.History.cs:12` |
| backups folder | `Documents/DawnScene.Backups.cs` (every 2 s; skipped with `--fresh` restore) |
| `settings/mcp` token + pipe | `Hex.Core/Mcp` |
| harness index | `Hex.Core` code index, kept between runs |
| `tasks.hor`, `.hex/` | per workspace, `HexTasks` |

Workspace state (`HexWorkspace`: open folders, expanded nodes, open files, run target, configuration/solution/launch profile) lives in `.hex-workspace` or the untitled one; restored by `HexExplorer.RestoreWorkspace`.

### Threading recipes
- UI components and `CodeEditor`/documents may only be touched in `UpdateState` and its ticks. The engine runs in `RunMode.Application` and sleeps when idle, so other threads must call `Wake()` (`DawnContext.cs:39`) after queuing work, and `Redraw()` for a frame.
- **Background work**: `context.Work.Start(key, title, ctx => {...}, done)` for cancellable work with a card, or `InBackground<T>(doing, work, done)`; the callback runs on the UI thread, drained by `TakeFinishedWork -> workCards.Update()` (`Workspace/DawnScene.Background.cs:100`). Read editor state (text, caret, version) *before* starting; never inside the job.
- **Producer thread -> UI**: pipe/LSP/DAP/MCP threads push onto a `ConcurrentQueue` and the part drains it in its tick: `remoteOpens` (`Remote.cs`), `mcpCalls` (`Mcp.cs:23`), `debugWork` (`HexDebugging.cs:142`), `serverAnswers` (`HexLanguages.Completion.cs:32`), `changed` (`HexDiskWatch.cs:20`), `hostPicked` (`Hosted.cs:13`); simple flags use `volatile` (`flushWanted`, `mcpChanged`, `projectsChanged`, `renderPlanChanged`). Always follow with `Wake()`.
- **Debounce text-triggered work**: lint 0.3 s, search 0.25 s, backups 2 s, hidden code regen 0.1 s, notebook write 0.3 s, outline after typing stops (`OutlineCache.cs`); keep a generation counter and drop stale answers (`serverAnswers` carries `Generation`).
- **Dirty flags** instead of rebuilding in handlers: set `tabsDirty/treeDirty/inspectorDirty/codeDirty` (or `this[DawnParts.Tabs]` via `IEditorWindow`); rebuilt once in `RebuildMarked`.
- MCP tools run as tick-per-step coroutines on the UI thread; use `RunAway` only for pure off-thread work.
- No per-tick garbage: cache the last-shown tuple (status bar), build delegates once (`HexTicks`).

### Theming
Editor UI = flat skin `Assets/uix/flat` with themes; `UseEditorSkin(name, theme, save)` (`Settings/DawnScene.EditorSkin.cs`); `ColourChrome :87` recolours the chrome per tick from `compositor.Skin`; `DimColor` etc. read the skin (`DawnScene.cs:49`). The skin of the layout being edited is separate (`UISkins`, `hex_skin.txt`, Layout > Skin). Theme picker previews live and reverts on cancel (`ThemePick.cs`). Looks (Fluid/Frosted/Flat) are `Hex.Shell` profiles (`HexLookPicker`, `IHexLookOwner`). C# syntax colours forced via `ApplyCSharpColors`. Icons are codicon-style names (`"debug-alt"`, `"git-commit"`). Scale: `HexFrame.Scales`, `ScaleToDisplay` follows OS DPI except in self test.

## 10. Conventions and gotchas

- Everything is `internal`; `InternalsVisibleTo` Hex and Hex.Tests.
- Each file starts with a long prose comment saying what the part is; read it. Style: wordy comments, `Said(key)` for all strings; UI has no tests beyond the in-process self test (`Testing/`).
- Partial-class sprawl: a feature is split across `DawnScene.X.cs` (scene glue) and `HexX.*.cs` (part). Search both.
- Ctor wiring order matters: `languages` before `codeEditing.languages = languages` (`:189-191`); commands before `commands.Build(menu)` (`:248`); `BuildPanel` (`:199`) before `BuildTerminal` (`:202`); `LoadKeys` after Build.
- Commands/menu items added after `Build` silently do not show.
- Menu rule: <=20 rows per menu; submenus via `DeclareSubmenu`/`AddMenu`.
- Keys: write `Ctrl` even for macOS (Mac maps to Command). `UIKeyUse.Shown` commands do NOT get a key handler.
- Lines are 1-based in MCP/agent API, 0-based internally (`OpenAt(path, line, col)` is 0-based).
- Renaming a named component in a `.hor` breaks `layout.Get<T>` at start.
- A new localisation key missing from `en-gb.hor` shows the raw key; keys are nested objects, not flat.
- `HexSettings` is in Hex.Core, rows in Dawn/Hex.Shell; `.hex/settings.hor` only overrides listed keys and never `mcp_server`.
- `F12` is go-to-definition: the engine screenshot key is disabled.
- The self test must not touch real files: guard new persistence with `options.SelfTest` / `kept`.
- Hosted/Android: Dawn never names the desktop platform; use the `NativeWindow` delegate pattern and avoid desktop-only APIs.
- `docs/hacking.md` mentions `Editing/` (Hex.Core), `Platform/`/`Dialogs/` (Hex.Shell): not in Dawn.
- TODO/FIXME markers: essentially none in Dawn source.
- Unverified: how `OpenOnExtensionView`/`ViewsFor` interplay with `HexView.Extension`; whether the menus self test asserts duplicate keys.

## 11. Where to look for X

| Need | Look at |
|---|---|
| CLI flag / startup | `DawnProgram.cs` (`Start :81`), `HexLaunch.cs`, `HexOptions.cs`, `hex/Hex/Program.cs` |
| Android/host entry | `DawnProgram.Hosted.cs`, `Shell/DawnScene.Hosted.cs` |
| Constructor wiring order | `Shell/DawnScene.cs:109-331` |
| Per-frame order | `Shell/DawnScene.cs:390` (`UpdateState`), `:506` (`DeclareTicks`), `Context/HexTicks.cs` |
| Add menu item / key | `Shell/DawnScene.Menus.cs`, any `Add*Commands`, `UICommands.cs` in Horizon.UI |
| Key rebinding / conflicts | `Shell/DawnScene.Keys*.cs`, `KeysWindow*.cs`, `Assets/hex/keys.hor` |
| Command palette / quick open | `Shell/DawnScene.CommandPalette.cs`, `Code/DawnScene.Find.cs`, `Code/DawnScene.GoToAll.cs` |
| Side bar views | `Shell/DawnScene.SideBar.cs`, `SideTree.cs`, `Context/DawnScene.Context.cs:361` |
| Bottom panel | `Shell/DawnScene.Panel.cs`, `Terminal.cs`, `Assets/hex/shell/panel.hor` |
| Status bar / toasts | `Shell/DawnScene.StatusBar.cs`, `Notify` (`DawnScene.cs:365`) |
| Tabs / breadcrumbs / split | `Shell/DawnScene.Tabs.cs`, `Split.cs`, `Assets/hex/shell/editor_tabs.hor` |
| Open / save / close files | `Documents/DawnScene.Documents.cs`, `Documents/HexDocument.cs` |
| Unsaved backups, disk changes | `Documents/DawnScene.Backups.cs`, `DiskChanges.cs` |
| Editor text control internals | `horizon/Horizon.UI/Components/Controls/CodeEditor*.cs`, `UITextDocument*.cs` |
| Completion/hover/signature | `Code/HexCodeEditing*.cs`, `Languages/HexLanguages.Completion.cs` |
| LSP servers list | `Assets/hex/language_servers.hor`, `Languages/HexLanguages.cs :174` |
| LSP protocol client | `horizon/Horizon.Tooling/LanguageServers/` |
| Debugging | `Debugging/HexDebugging*.cs`, `horizon/Horizon.Tooling/Debugging/` |
| Git | `Git/HexGit*.cs`, `hex/Hex.Core/Git/` |
| Agent chat UI | `Agents/DawnScene.AgentsPanel*.cs`, `Assets/hex/agents_view.hor` |
| MCP tools | `Agents/DawnScene.Mcp*.cs`, `hex/Hex.Core/Mcp/` |
| Layout designer | `Designer/`, `Assets/hex/editor.hor`, `HexDesignerKind.cs` |
| Notebooks | `Notebooks/`, `hex/Hex.Core/Notebooks/` |
| Settings UI / persistence | `Settings/DawnScene.SettingsPage.cs`, `hex/Hex.Core/Settings/HexSettings.Table.cs`, `hex/Hex.Shell/Settings/EditorSettingsCatalog.cs` |
| Themes | `Settings/DawnScene.EditorSkin.cs`, `ThemePick.cs`, `Assets/uix/flat` |
| Extensions API | `Extensions/HexExtensions.Api.cs`, `hex/docs/extensions.md` |
| Tasks / F5 | `Tasks/HexTasks*.cs`, `Shell/DawnScene.Menus.cs` `RunOrPlay :57` |
| Explorer / workspace | `Workspace/HexExplorer*.cs`, `hex/Hex.Core/Workspace/HexWorkspace.cs` |
| Solution Explorer | `Workspace/DawnScene.Solution*.cs` |
| Strings | `Assets/hex/localisation/en-gb.hor`, `Shell/DawnScene.Strings.cs` |
| Persistence files | section 9 table, `hex/Hex.Shell/HexFiles.cs` |
| Background work | `Workspace/DawnScene.Background.cs`, `IWork` in `Context/DawnContext.cs :181` |
| Profiling | `Shell/HexProfiler.cs`, env `HEX_PROFILE` |
| Dawn <-> Dusk swap | `Shell/DawnScene.Modes.cs`, `hex/Hex.Shell/HexModes.cs` |
| Single-instance hand-over | `DawnProgram.cs` `HandOver :229`, `Shell/DawnScene.Remote.cs` |
| Self test / tests | [testing-and-shipping.md](testing-and-shipping.md) |
