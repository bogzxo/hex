# Context: Optional Modules & Performance Hot Paths

Token-efficient map for coding agents working on Hex's optional module architecture (`Hex.Core/Modules/`), NativeAOT shared module DLLs (`Hex.Notebooks.Render`), and performance-critical paths across Dawn, Agentic/Noon, and Horizon.

## 1. Optional Module Architecture (`Hex.Core/Modules/`)

| Type | File | Role |
|---|---|---|
| `HexModuleId` | `Hex.Core/Modules/HexModuleId.cs` | Stable string IDs (`link`, `local_ai`, `agentic`, `notebooks`, `designer`) — string-backed so future modules register without changing core enums or schemas. |
| `HexModuleDescriptor` | `Hex.Core/Modules/HexModuleId.cs` | Record describing `Id`, `SettingKey` (`module_*`), `TitleKey`, `HelpKey`, `DefaultEnabled`, `RestartRequired`, `NativeLibrary`, `NativeFilePatterns`. |
| `HexModuleCatalog` | `Hex.Core/Modules/HexModuleId.cs` | Thread-safe registry (`All`, `Find(id)`, `Register(descriptor)`). First-party modules are pre-registered here. |
| `IHexModuleHost` | `Hex.Core/Modules/HexModuleId.cs` | Simulation-thread lifecycle interface (`Id`, `IsActive`, `Activate()`, `Deactivate()`, `Tick(dt)`). |
| `HexModules` | `Hex.Core/Modules/HexModules.cs` | Runtime gate (`--no-module <csv>`, `ApplyArguments`, `ForceOff`, `SetEnabled`, `IsEnabled`, `IsInstalled`) and host registry (`RegisterHost`, `ReconcileHosts`, `TickActiveHosts`). |
| `HexNativeModule` | `Hex.Core/Modules/HexNativeModule.cs` | Cross-platform NativeAOT shared library loader (`ExistsOnDisk`, `FindOnDisk`, `TryLoad`, `TryGetExport`, `CandidateFileNames`). Searches `<ProgramFolder>/modules/` then beside exe. |

### Key invariants
- **Zero-alloc hot path**: `HexModules.IsEnabled(id)` allocates **0 bytes** (enforced by `HexPerfBudgetTests.Module_gate_hot_path_allocates_nothing`).
- **Back-compat with `--no-ai`**: `HexAi.Off` automatically forces `HexModuleId.Agentic` and `HexModuleId.LocalAi` off inside `HexModules.IsEnabled(...)`.
- **MCP split & cold `--no-module` startup**: Disabling `module_agentic` (or `--no-ai`) turns off the Agents panel, Noon, agent ticks, agent extensions, and `AgentsOnly` MCP tools (`hex_agent_*`, `hex_ask`, `hex_harness_stats`, `hex_show_image`, `hex_plan_*`), while core editor/code/harness MCP tools remain available whenever `mcp_server` is enabled (`DawnScene.Mcp.cs`, `DawnScene.McpWorkbench.cs:McpToolList`). `EnsureAgentsPanel()` and `ApplyAgenticModule()` defer building the Agents panel and agentic subsystem until `module_agentic` is actually enabled, so cold `--no-module` launches pay zero UI/allocation cost for disabled modules.
- **Lazy native loading**: `HexModules.IsInstalled` checks disk presence via `HexNativeModule.ExistsOnDisk` without loading the DLL into the process.

### Recipe: Add a new optional module
1. **Register descriptor**: Add a constant to `HexModuleId` and a `HexModuleDescriptor` entry to `HexModuleCatalog.Registered` (`Hex.Core/Modules/HexModuleId.cs`). Include `NativeLibrary` and `NativeFilePatterns` if it ships a separate native DLL.
2. **Settings**: Add the `module_<id>` boolean field + `Flag("module_<id>", ...)` row in `Hex.Core/Settings/HexSettings*.cs`, expose it in `Hex.Shell/Settings/EditorSettingsCatalog.cs`, add `modules.<id>_title` and `modules.<id>_help` in `Hex.Dawn/Assets/hex/localisation/en-gb.hor`, and sync it to `HexModules.SetEnabled(...)` in `DawnScene.Settings.cs`.
3. **Runtime gate & host**: Gate UI creation, commands, and ticks with `HexModules.IsEnabled(HexModuleId.<Id>)`. For modules with live toggle support (`RestartRequired: false`), implement `IHexModuleHost` and register it with `HexModules.RegisterHost(...)` so `ReconcileHosts()` deactivates and releases timers/watchers/caches on toggle-off.
4. **Optional NativeAOT shared DLL** (if decoupling heavy third-party dependencies):
   - Create a separate NativeAOT class library (`<PublishAot>true</PublishAot>`, `NativeLib=Shared`) exporting C-ABI `[UnmanagedCallersOnly(EntryPoint = "...")]` functions.
   - Place the built `.dll`/`.dylib`/`.so` in `<ProgramFolder>/modules/`.
   - Load lazily in Hex via `HexNativeModule.TryLoad("<LibName>")` and `TryGetExport(...)` only when `HexModules.IsEnabled(...)` is true.
5. **Installer**: `HexModuleDescriptor.OwnedNativeFiles` drives installer file filtering so deselected modules omit their native binaries from the install.

## 2. `Hex.Notebooks.Render` C-ABI Contract

Heavy notebook rendering dependencies (`SkiaSharp`, `Svg.Skia`, `CSharpMath`, `ExCSS`, `libSkiaSharp`) are decoupled from `Hex.exe` into the optional `Hex.Notebooks.Render` NativeAOT shared library (`HexModuleId.Notebooks`):
- **Loader**: `HexNativeModule.TryLoad("Hex.Notebooks.Render")` looks in `<ProgramFolder>/modules/` (fallback: beside `Hex.exe`).
- **Zero startup cost**: `Hex.exe` never links or loads `libSkiaSharp` unless a notebook renders SVG/LaTeX while `HexModules.IsEnabled(HexModuleId.Notebooks)` is true (0 root Skia files in Release publish).
- **Symbol stripping**: `Hex.Notebooks.Render/Hex.Notebooks.Render.csproj` strips `.pdb` symbols via `LeaveTheSymbols` so Release module binaries stay lean.
- **Clean degradation**: If the DLL is missing (uninstalled in Installer) or the module is disabled, `TryLoad` returns `IsLoaded == false` without throwing and notebook views degrade cleanly to text/source fallback.

## 3. Performance-Critical Paths (`file:method` Jump Table)

### Dawn main loop & lazy builders
| Path | Location | Notes |
|---|---|---|
| Main simulation tick | `Hex.Dawn/Shell/DawnScene.cs:UpdateState` | Runs `workTicks`, `lateTicks`, `panelTicks`, status bar, dirty-gated `FitChrome` (`ChromeFitKey`), `DawnScene.Tabs.cs` reused breadcrumbs. |
| Tick registration | `Hex.Dawn/Shell/DawnScene.cs:DeclareTicks` | Delegates built once in `HexTicks` (`Hex.Dawn/Context/HexTicks.cs`); supports `HexTicks.Weighing` for idle allocation profiling. |
| File watcher debounce | `Hex.Dawn/Settings/HexFileWatch.cs:TakeSettled` | Uses `WindowManager.RequestTickIn(remaining)` while settling (never `RequestFrames()`). |
| Lazy view/window builders | `Hex.Dawn/Noon/DawnScene.Noon.cs:EnsureNoon`, `Hex.Dawn/Shell/DawnScene.cs:SyncDesignerStage`, `Hex.Dawn/Agents/DawnScene.AgentsPanel.cs:EnsureAgentsPanel` / `ApplyAgenticModule`, `Hex.Dawn/LocalAi/DawnScene.LocalAiWindow*.cs` | `EnsureAgentsPanel()` / `ApplyAgenticModule()` keep `--no-module` startups cold; `EnsureNoon()` builds Agentic's 3-column UI lazily on first show/warm; `SyncDesignerStage` keeps the 2nd `UICompositor` (`stage`) disabled (`Enabled = false`) while editing code so it costs 0 update/layout/paint work. |
| Render-thread backdrop loading | `Hex.Dawn/Code/DawnScene.Backdrop.cs`, `Hex.Dusk/Shell/DuskScene.Backdrop.cs` | Loads backdrop textures on the render thread under `WindowManager.Exclusive` without blocking simulation or violating GPU thread ownership. |
| Indexed file & span queries | `Hex.Core/Harness/HexIndexedFile.cs`, `Hex.Core/Search/HexSearch*.cs`, `Hex.Core/Outline/HexOutline*.cs`, `Hex.Core/Harness/HexCodeIndex*.cs` | `HexIndexedFile` uses a weak `string[]` line cache so idle memory reclaims cleanly; `HexSearch`, `HexOutline`, and `HexCodeIndex` scan via spans to avoid per-line allocations. |
| Localisation streaming | `Hex.Shell/HexText.cs:TryReadFast` | Zero-AST streaming parser for `.hor` localisation files on startup. |
| Zero-alloc Git & LSP sync | `Hex.Core/Git/GitStatus.cs`, `Hex.Dawn/Git/HexGit*.cs`, `Hex.Dawn/Languages/HexLanguages*.cs`, `Hex.Dawn/Notebooks/DawnScene.Notebook*.cs` | `GitStatus` parses porcelain output with zero-alloc spans, `HexGit.StartMoveProbe` runs directory move scans off the simulation thread; `HexLanguages` passes `CopyLines()` `string[]` snapshots to `LanguageServer` and `NotebookCell.OutputsRevision` skips full-output string hashing in `OutputsSignature`. |
| Disk-backed agent review blobs | `Hex.Core/Agents/HexAgentReviews*.cs` | Stores large agent review diffs/payloads on disk as `.br` (Brotli) blobs instead of holding them in managed RAM. |
| Zero-alloc MCP folder check | `Hex.Dawn/Agents/DawnScene.Mcp.cs:UpdateMcp` / `McpFolders` | Compares workspace folders without per-tick array allocation before rewriting `McpListing`. |
| System & TTC fonts | `Hex.Dawn/Code/HexCodeEditing.Font.cs`, `horizon/Horizon/Rendering/2D/Text/GlyphOutlines.cs` | Loads macOS system monospace fonts (`SF Mono`, `Menlo`, `Monaco`) and `.ttc` TrueType collections via `stbtt_GetFontOffsetForIndex`. |
| Native dialogs | `Hex.Shell/Platform/HexDialogs.cs`, `horizon/Horizon.Platform.Desktop/Mac/MacDialogs.cs` | Routes file/folder dialogs through `IPlatformDialogs` (`NSOpenPanel`/`NSSavePanel` on macOS) with `UIFileDialogs` fallback. |

### Agentic & Noon hot paths
| Phase | Location | Optimisation contract |
|---|---|---|
| **Idle** | `DawnScene.Noon.Context.cs:DrawNoonContext` | Caches `noonClockDrawn` tuple so `ClockLine` allocates 0 B while standing still. |
| **Idle** | `DawnScene.AgentsPanel.cs:AgentFor`, `CanComposeAgent`, `DawnScene.AgentSystems.cs:HasAgentSystems` | Index-based `for` loops (no LINQ/closures) on 10 Hz panel ticks. |
| **Idle** | `DawnScene.AgentsPanel.Chips.cs:RememberAgentProgram`, `DawnScene.AgentData.cs:MeasureAgentData` | Only calls `WindowManager.NoteChange()` when state actually changed and UI is visible (no 15 s / 30 s idle GPU wakeups). |
| **Switching** | `HexAgentRowList.cs:BeginBatch/EndBatch/MakeLater`, `DawnScene.AgentsPanel.Rows.cs:DrawAgentRows` | Batches `VirtualStack.ItemsChanged()` once per switch; uses shared `MakeLater` callback instead of per-row closures. |
| **Switching** | `DawnScene.Noon.Context.cs:DrawNoonContext` | Reuses scratch `noonContextTools`, `noonContextToolTop`, `noonContextFiles` collections; no LINQ sorting/counting. |
| **Streaming** | `Hex.Core/Agents/HexAgentChat.cs:HexAgentEntry.AppendText` | Lazy `StringBuilder` backing avoids O(N²) string copying on `Say(..., append: true)`. |
| **Streaming** | `DawnScene.AgentsPanel.Rows.cs:TryUpdateStreamingMarkdown`, `StableMarkdownSplit` | Freezes completed markdown blocks above the last `\n\n` outside code fences; re-parses only the tail paragraph in place without replacing the row in `VirtualStack`. |
| **Scrolling & Groups** | `DawnScene.AgentsPanel.ToolGroups.cs:ToolGroupRow/GroupSeconds/ToolKinds`, `DawnScene.AgentsPanel.Turns.cs:TurnFooter`, `DawnScene.AgentsPanel.Rows.cs:Bubble/MadeLater/Empty` | Zero-LINQ loops, `stackalloc` interval sort (`GroupSeconds`), `ReadOnlySpan<char>` `MeasureLine` in `Bubble`, reverse-index child removal without `ToArray()`. |

### Throttling & smoothness contract (Horizon)
- **`WindowManager.NoteInput()`** (`Horizon/Core/Windowing/WindowManager.Application.cs`, called from `UICompositor.Input.cs`): Records user input so calm/idle throttling lifts immediately for responsive interaction.
- **`WindowManager.RequestTickIn(seconds)`** (`Horizon/Core/Windowing/WindowManager.Application.cs`): Schedules a future simulation tick without forcing GPU frames (use for debounces, carets, decorative animations).
- **`WindowManager.NoteChange()`**: Marks a UI state change; requests `CALM_FRAMES` (1/60 s) when calm or `FRAMES_AFTER` (50 ms) after input. Prefer over `RequestFrames()` on the simulation thread.
- **`WindowManager.RequestSmooth()`**: Temporarily lifts calm throttling (`CALM_PERIOD` = 24 Hz focused / 10 Hz unfocused) for user-followed animations (scroll glides, springs, panel easing).
- **`WindowManager.PaceCalmFrames()`**: Paces render frames while calm to half a calm tick so background spinners/streams do not spin the GPU at 120 Hz.
- **`PlatformPower` / `DesktopPower`** (`Horizon/Platform/PlatformPower.cs`, `Horizon.Platform.Desktop/`): Observes OS Low Power Mode (macOS `NSProcessInfoPowerStateDidChangeNotification`) so `WindowManager` reduces background cadence automatically on battery saver.
- **`LoopTiming`** (`Horizon/Core/Threading/LoopTiming.cs`): Uses per-thread `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` on Windows 10 1803+ and skips global `timeBeginPeriod(1)` for desktop apps (`NeedsSharpTimer`), preserving laptop battery.
- **`UIComponent.PaintVersion`** (`Horizon.UI/Drawing/UIDrawList.Reuse.cs`): Replays cached draw quads when version + `PaintContext` match. Verify with `HORIZON_UI_REUSE_CHECK=1`.
- **`SettingsPage.RowsStack`** (`Horizon.UI/Components/Controls/Settings/SettingsPage.Rows.cs`): Viewport-virtualizes off-screen settings rows (exposing `SettingsPage.RowItems` for full traversal in `DawnScene.SelfTest.cs:Descendants`) so a ~150-row settings window only measures/arranges/paints visible rows.
- **`SdlTextEditing` & `TextMateHighlighter.CutNear`** (`Horizon.Platform.Desktop/Input/SdlTextEditing.cs`, `Horizon.UI/Scripting/TextMateHighlighter.cs`): Delivers native IME preedit and intact UTF-8 emoji commits; `CutNear` avoids splitting UTF-16 surrogate pairs during chunked syntax highlighting.

## 4. Benchmarks & Perf Guard Tests

| Tool / Test | Location | What it guards / measures |
|---|---|---|
| `HexPerfBudgetTests` | `Hex.Tests/HexPerfBudgetTests.cs` | Per-thread allocation budgets: typing + prediction (`<= 128 B`), `History.NoteSoon` (`<= 1024 B`), `History.Note` (`<= 16x`), idle Link (`<= 256 B` / `<= 8 B`), idle prediction (`<= 8 B`), adopted Local AI (`<= 512 B`), `HexModules.IsEnabled` (`0 B`), `HexAgentChat` streaming append (`<= 256 B`) & tool call recording (`<= 1024 B`). |
| `modules` self-test | `Hex.Dawn/Testing/DawnScene.SelfTest.Modules.cs` | Verifies all 5 built-in modules in `HexModuleCatalog` and `Editor · Modules`, lazy `stage.Enabled == false` during code editing, live `module_designer`/`module_agentic`/`module_notebooks`/`module_local_ai`/`module_link` toggles, lazy `EnsureNoon()` activation, and clean restore. |
| `agentic-perf` self-test | `Hex.Dawn/Testing/DawnScene.SelfTest.AgenticPerf.cs` | Measures 12 chats × 40 turns across `idle` (with `HexTicks.Weighing`), `switching`, `scrolling`, `streaming`, and `groups`; writes `agentic-perf.json`. |
| `uicost` self-test | `Hex.Dawn/Testing/DawnScene.SelfTest.UiCost.cs` | Measures per-component paint/measure cost via `UIPaintProfile`. |

### Verified Release NativeAOT Benchmarks
| Metric | Verified Release NativeAOT Result |
|---|---|
| `Hex.exe` binary size | **59.76 MiB** |
| Root Skia files beside `Hex.exe` | **0** (decoupled into `modules/Hex.Notebooks.Render`) |
| Cold first frame | **480–528 ms** |
| Settled Working Set (`hex` repo) | **158.5–176.2 MiB** |
| 240 Hz typing median | **4.1 ms** |
| GPU glass window drag | **0.433 ms** |
