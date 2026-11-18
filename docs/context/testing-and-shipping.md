# Hex: testing and shipping

Agent-oriented map of how Hex is tested, packaged, versioned and released. All paths are relative to `hex/` unless
absolute. Claims were checked against the code; "unclear" marks what was not established.

## 1. Test landscape at a glance

| Layer | Where | Framework | Needs a window/GPU? | Run by CI? |
| --- | --- | --- | --- | --- |
| Unit tests | `Hex.Tests/` (about 640 `[Fact]`/`[Theory]`) | xUnit 2.9.3, Microsoft.NET.Test.Sdk 18.10.1, net11.0 | No | Yes (`ci.yml`, Win/Linux/macOS) |
| Installer tests | `Hex.Installer.Tests/` (about 36) | xUnit, same versions | No | Yes (`ci.yml`) |
| In-app self-tests (Dawn) | `Hex.Dawn/Testing/` (217 files, about 187 registered suites) | Home-grown scripted pointer (`HexPointerScript`), run inside the live editor | Yes (window plus Vulkan) | No ("want a window and a Vulkan device", comment in `.github/workflows/ci.yml`) |
| In-app self-tests (Dusk) | `Hex.Dusk/Testing/` (about 33 suites) | Home-grown `DuskScript` (copy of the same idea) | Yes | No |
| Layout check | `--check` / `--exercise` flags, `Hex.Dawn/Testing/DawnScene.Checks.cs` | in-app | Yes | No |
| Benchmarks | `docs/benchmarks.md`, `scripts/measure-*.ps1` | PowerShell, measures a published build | Yes, Windows only | No |
| Android device run | `Hex.Android` plus `HORIZON_HEX_SELFTEST` | same self-test, on the tablet | Device | No |

`Hex.Dawn/Tests/` is NOT a test suite. It is the product feature "Test Explorer" (Visual Studio style): `HexTestExplorer.*.cs`
(Dotnet, DotnetParallel, CTest, Python, Problems, Rows, Search). Its own self-tests live in `Hex.Dawn/Testing/HexTestExplorer.*SelfTest.cs`.

There is no screenshot-comparison (visual diff) test. Screenshots are opt-in debugging aids (see 4.6). The only golden
files are `Hex.Tests/Goldens/settings_{changed,high,low}.txt`.

## 2. Unit tests (`Hex.Tests`)

### 2.1 Layout and wiring
- `Hex.Tests/Hex.Tests.csproj`: `IsTestProject`, `net11.0`, `JsonSerializerIsReflectionEnabledByDefault=false` (same JSON metadata
  restriction as the NativeAOT exe, so reflection-based serialization in new code will fail tests), `AllowUnsafeBlocks` (Dusk COM table tests).
- References: Hex.Dusk, Hex.Core, Hex.Dawn, and the engine projects at `$(HorizonRoot)` (default `..\horizon\`): Horizon, Horizon.UI,
  Horizon.Terminal, Horizon.HIDL. Note: NOT Hex.Shell directly (reached transitively) and NOT the `Hex` exe project.
- Dawn/Core/Shell/Dusk declare `InternalsVisibleTo Hex.Tests`, so tests reach `internal` types (`Hex.Dawn/Hex.Dawn.csproj:29-30`, Hex.Core, Hex.Shell, Hex.Dusk).
- No `xunit.runner.json`; default xUnit parallelism between classes. Tests that time things opt out via
  `[Collection(Timed.NAME)]` (`Hex.Tests/Timed.cs`, `DisableParallelization`); used in CatalogExtensionTests, HexAgentSystemsTests,
  HexAgentsPanelTests, NotebookEngineeringTests, NotebookTests. Reason in the file: a loaded CI runner made timing tests 5x slower.
- `Hex.Tests/Catalog.cs`: finds the `hex-extensions` git submodule by walking up from `AppContext.BaseDirectory`; throws
  `DirectoryNotFoundException("hex-extensions isn't checked out, git submodule update --init hex-extensions")` otherwise. Extension tests
  (`HelloDollyTests`, `CatalogExtensionTests`, `ExtensionCatalogTests`, `EmmetTests`) run the real `.hor` extension files from it.
- `Hex.Tests/FakeHex.cs`: a fake `hex` HIDL object (caret, replace_text, setting, command, say, menu, prompt, problems) so extensions
  run in a bare `HIDLRuntime`.
- `Hex.Tests/engineering-requirements.txt`: pinned `ipykernel`, `numpy`, `matplotlib` for the real-kernel notebook tests (CI pip-installs it on Python 3.13).

### 2.2 Commands (run from `hex/`)
```sh
dotnet test Hex.Tests/Hex.Tests.csproj                                   # all (also the `test` task in tasks.hor)
dotnet test Hex.Tests --filter "FullyQualifiedName~HexHarnessTests"       # one class
dotnet test Hex.Tests --filter "FullyQualifiedName=Hex.Tests.HelloDollyTests.TheCommandPutsUpAnotherGreeting"  # one test
dotnet test Hex.Installer.Tests                                           # installer tests
# what CI runs:
dotnet test Hex.Tests --configuration Release --verbosity normal --blame-hang-timeout 5m --blame-hang-dump-type none
```
On a failed/hung CI run `Hex.Tests/TestResults/` is uploaded as artifact `test-results-<os>`; the blame sequence file's last test is the hang.

### 2.3 Environment-gated tests (silently return = pass when the variable is absent)
Many tests do `if (Environment.GetEnvironmentVariable("X") != "1") return;`, so a green run does NOT prove toolchain-dependent code works.

| Variable | Enables |
| --- | --- |
| `HEX_TEST_CMAKE=1` | CMake/CTest/preset tests (CMakePresetTests, CMakeProjectTests, CTestTests, BuildProblemsTests) |
| `HEX_TEST_CLANG=<path>` | native diagnostics / clang build problems (BuildProblemsTests, NativeDiagnosticTests) |
| `HEX_TEST_DOTNET_SDK=1` | real dotnet SDK build problems (BuildProblemsTests) |
| `HEX_TEST_PYTHON`, `HEX_TEST_PYTEST`, `HEX_TEST_PYTHON_WITHOUT_PYTEST` | Python unittest/pytest tests (PythonUnitTestsTests, PythonPytestTests, PythonTestCancellationTests, PythonEnvironmentCreationTests) |
| `HEX_DEBUGPY_PYTHON`, `HEX_TEST_DEBUGPY_INSTALL` | debugpy session/setup (PythonDebugSessionTests, PythonDebuggerSetupTests) |
| `HEX_NESTED_BENCH_ROOT`, `HEX_NESTED_BENCH_OUT` | nested-repository scan benchmark (NestedRepositoryScanTests) |
| `HEX_RECORD_GOLDENS=1` | HexSettingsGoldenTests rewrites `Goldens/settings_*.txt` instead of comparing |

### 2.4 How to add a unit test
1. Create `Hex.Tests/<Thing>Tests.cs`, `namespace Hex.Tests;`, `public class ...Tests` (xUnit; `Xunit` is a global using).
2. Temp state: use `Path.Combine(Path.GetTempPath(), "hex-xyz-" + Guid.NewGuid().ToString("N"))`, implement `IDisposable` to delete it
   (pattern in `HexHarnessTests`, `HexSettingsGoldenTests`, `InstallationTests`).
3. Extension logic: copy `HelloDollyTests` (builds a `HIDLRuntime { BaseDirectory = folder, Output = null }`, declares a fake `hex` via
   `runtime.UserScope.DeclareSystem("hex", obj)`, `RunFile(extension.hor)`, `Invoke(on_start, ...)`). Or reuse `FakeHex`.
4. Timing assertions: add `[Collection(Timed.NAME)]`.
5. Needs a toolchain: gate on an env var like the table above and `return` early (documented convention).
6. Internal API access works with no extra setup (InternalsVisibleTo). Avoid `System.Text.Json` reflection.
Representative areas of the ~190 files: Git (GitTests, GitStash, GitCommits), debugging (HexDebug*Tests), notebooks (Notebook*), agents/MCP
(HexAgent*, McpTests, HexAutomationTests), settings (HexSettings*, goldens), HIDL language (Hidl*Tests), project/dotnet/CMake/Python tests, installer-adjacent `InstallCommandTests`.

### 2.5 Installer tests (`Hex.Installer.Tests`)
xUnit; compiles in `Hex.Installer/{InstallerLayout,InstallerDestination,SetupOptions}.cs` by link and copies the installer `.hor` assets and
sources to the output. References `Hex.Installation`. Files: InstallationTests, InstallChoicesTests, InstallerDestinationTests,
InstallerLayoutTests, InstallerLocalisationTests, InstallerUpdateTests, SetupOptionsTests, WindowsSetupBundleTests. They fake the GitHub release JSON
(`ReleaseClient.Parse`), zips, SHA-256 digests; they must not touch real launchers or the registry (per `docs/installer.md`).

## 3. In-app self-tests (Dawn)

### 3.1 Mechanism
- Entry: `Hex.exe --selftest [suite ...] [--exit] [--size WxH] [--watch] [--scale N]` (`Hex.Dawn/DawnProgram.cs`; flag constants at the top).
  Arguments after `--selftest` that are not flags are suite names (`ValuesOf(args, "--selftest")` -> `HexOptions.Suites`, `HexLaunch.cs`).
- `DawnProgram.Start` in self-test mode: sweeps stale `horizon-hex-selftest-*` temp folders (`SweepSelfTestFolders`), makes a private workspace
  `%TEMP%/horizon-hex-selftest-<pid>` and a private Dusk home `horizon-hex-selftest-dusk-<pid>` (`DUSK_HOME`), never hands over to a running Hex, runs the engine
  as `RunMode.Game` (not Application) at 240 updates/s unless `--watch` (then 120) and scale 1.0, `ScaleToDisplay=false`. Folders are deleted when the run ends.
- `--exit`: quit with exit code 1 if anything failed (`UpdateSelfTest` in `Hex.Dawn/Testing/DawnScene.SelfTest.cs` -> `Quit(selfTest.Failed)`).
- `--watch`: pace 8x slower to follow by eye; side panels animate (otherwise `SlideTime = 0`).
- `--wheel`: also wait for the real mouse wheel (doc comment in `DawnProgram.cs`).

### 3.2 Definition and registration
- Registry: `DawnScene.SelfTestSuites` in `Hex.Dawn/Testing/DawnScene.SelfTest.cs`, an array of `(string Name, string About, Action<HexPointerScript> Run)`.
  Order in the array = run order. A suite may chain several `TestXxx(test)` methods in a lambda (e.g. `layout`, `code`, `settings`, `workspace`, `extensions`).
- A suite method is `private void TestXxx(HexPointerScript test)` in a `DawnScene` partial file `Hex.Dawn/Testing/DawnScene.SelfTest.<Name>.cs`.
  It does NOT run steps; it QUEUES them on the script: `test.Run(() => ...)`, `test.Click(() => point)`, `test.Hover/Drag/RightClick/MiddleClick`,
  `test.Settle(seconds)` (wall clock), `test.Wait()` (3 updates), `test.WaitFor(() => cond, timeoutSeconds)`, `test.Check("name", () => bool)`, `test.Press/Release`.
  Lambdas are evaluated lazily when their step plays, so targets/conditions must be closures, never values captured at queue time.
- `HexPointerScript` (`Hex.Dawn/Testing/HexPointerScript.cs`): steps advance by update count (move 2, press 1, wait 3, times `Pace`) except `Settle`/`WaitFor`
  which use seconds. A step or check that throws counts as a failure ("step threw: ..."), the run continues. At the end it prints `N of M checks passed.`
- `StartSelfTest`: for each requested suite in order it prints `[Hex self-test] <name>`, calls `MakeTestFiles()` (writes `STAND_IN_LAYOUT` to `selftest_menu.hor` and
  `TEST_DATA` to `selftest_data.hor` if missing, so a suite run alone has what an earlier suite would have made), runs the suite, queues `TidyAfterSuite`
  (`DawnScene.SelfTest.Tidy.cs`: closes dialogs, closes modified test-owned documents without asking so one suite's "Unsaved Changes" dialog does not poison the next),
  then prints `[Hex self-test] <name> took X.X s`.
- Helpers in `DawnScene.SelfTest.cs`: `At/Part/Kind/Row/TreeRow/Explorer/FileRow` (pointer targets), `Pick(menuTitle, itemFn)` and `PickFromMenu`, `OnCode(test, path)`,
  `Reveal`, `Near`, `Fits`, `TypeInto`. Fields: `testFolder`, `savedFile`, `dataFile`, `brokenFile`.
- Other infrastructure: `HexMcpTestClient.cs` (pipe MCP client used by the `mcp` suite), `DawnScene.Checks.cs` (`--check`), helper classes
  `HexLanguages.*Races.cs`/`HexTestExplorer.*SelfTest.cs` (suites that take the language/test-explorer object, e.g. `languages.TestHoverRaces(test, folder)`).

### 3.3 Default set and unknown names
- No suite named = all except `pyright`, `npminstall`, `native-shortcuts`, `repository` (`AskedSuites`). Those four must be named explicitly (the first takes the foreground / needs global tools / real network / needs `HEX_TEST_SOLUTION`).
- Unknown name = prints `[Hex self-test] there is no X, the parts are` then every `name  about` line, and fails (check "self-test: the parts asked for are parts of it").
  `hex_selftest` with `suites="list"` exploits this to list names.
- `docs/hacking.md` claims "a hundred seconds, all of it" and lists 5 parts; that is stale (187 suites, several minutes plus the 30-minute `document-memory`).

### 3.4 Running
```sh
cd hex/Hex/bin/Debug/net11.0        # the real build output (after: dotnet build Hex/Hex.csproj)
./Hex.exe --selftest code --exit --size 1600x900
./Hex.exe --selftest emmet tools --exit
./Hex.exe --selftest list-me-please --exit   # unknown name prints the catalogue
```
Output prefix: `[Hex self-test] pass: <check>` / `FAIL: <check>` / `step threw: ...` / `<n> of <m> checks passed.` Exit code 0/1 with `--exit`.
Why from `Hex/bin`: `HexExtensions.Find()` (`Hex.Dawn/Extensions/HexExtensions.cs:79`) adds the `hex-extensions` submodule only in self-test mode via
`FindUp(ProgramFolder, "hex-extensions")`, i.e. walking UP from the exe folder. `hex/Hex/bin/...` is under `hex/`, which contains `hex-extensions/`; a copy
elsewhere (temp dir, scratch workspace) does not find it, so every extension-dependent suite (emmet, tools, catalog, extensions, hello dolly...) breaks.
Build output also has `Assets/`, `shaders`, `extensions`, `extension_catalog` next to the exe (verified listing of `Hex/bin/Debug/net11.0`).
The run opens a window; the machine needs a Vulkan device and the window should not be obscured by user input (`native-shortcuts` takes the foreground and posts real keys).

### 3.5 MCP: `hex_selftest`
Defined in `Hex.Dawn/Agents/DawnScene.McpWorkbench.cs:78` (impl `SelfTestRun`, ~379). Args: `suites` (space/comma separated, or `list`), `timeout` seconds
(default 600, clamped 10..3600). It spawns a SEPARATE Hex (`Environment.ProcessPath`) with `--selftest <suites> --exit`, `HORIZON_AUDIO=off`, hidden window, collects
stdout/stderr, keeps only `[Hex self-test]` lines and returns: `<suites>: <last "checks passed" tally>, exit code N` plus `took` lines plus a `N failed` list
(lines beginning `FAIL` or `step threw`). Error result when exit code != 0 or on timeout (process tree killed). Because it launches the running exe, it is only as good as
that exe's location (see 3.4).

### 3.6 How to add a self-test suite
1. New file `Hex.Dawn/Testing/DawnScene.SelfTest.Foo.cs`, `internal sealed partial class DawnScene { private void TestFoo(HexPointerScript test) { ... } }`.
   Model: `DawnScene.SelfTest.Emmet.cs` (write a file under `testFolder`, `Open(path)`, `test.Settle`, `test.Check(...)`, `document.Editor.*`, `Save()`, `CloseDocument(document)`).
2. Register one line in `SelfTestSuites` (`DawnScene.SelfTest.cs`): `("foo", "what it covers", TestFoo),`. Name is the CLI token (case-insensitive).
   Put it where its prerequisites hold (order matters in a full run). If it must not run by default, add its name to the exclusion list in `AskedSuites`.
3. Work inside `testFolder` only; never touch user settings (home is redirected: `HEX_HOME`/`DUSK_HOME`).
4. Make every check name start with the suite name ("emmet: ...") so failures are greppable.
5. Wait on conditions (`WaitFor`) rather than long `Settle`s; leave nothing modified/open that `TidyAfterSuite` cannot close.
6. Run it: `Hex/bin/Debug/net11.0/Hex.exe --selftest foo --exit` (rebuild first: `dotnet build Hex/Hex.csproj`).
For logic that needs no UI, prefer a unit test in `Hex.Tests` (fast, CI-run).

### 3.7 Env vars used by self-tests
`HEX_SELFTEST_SHOTS` (folder, screenshots, 27 uses), `HEX_TEST_SOLUTION` + `HEX_TEST_REPOSITORY_RUN_TESTS=1` / `HEX_TEST_REPOSITORY_NAVIGATION=1` (suite `repository`),
`HEX_NOTEBOOK_TEST_FILE` (+ `HEX_NOTEBOOK_TEST_EXECUTE`) (suite `notebookfile`, skipped with a message when absent), `HEX_MEMORY_SESSION_SECONDS` (default 1800, allowed 10..86400, suite `document-memory`),
`HEX_SELFTEST_REPEAT=N` (1..50, runs the asked suites N times in one process and prints heap after a full collection, committed, working set, private bytes, handles and threads after each round, for leak hunting), `HEX_UICOST_PROFILE`, `HEX_TYPING_DIAGNOSTICS=1`, `HEX_NOTEBOOK_HTML_SAMPLE/OUT`, `HEX_PRESET_*`, `HEX_FIGHT_GAME` (move editor on Fighter2D), plus the same `HEX_TEST_*` toolchain variables as 2.3 (cmake, clang, python, pytest).
Toolchain suites (csharp, d, pyright, clangd, native-debug, python-debug...) go quiet/skip or fail depending on csharp-ls, serve-d, clangd, lldb, debugpy, pyright, netcoredbg, cmake being on the machine ("when it's on this machine" in many suite descriptions).

## 4. Catalogue of Dawn suites (`SelfTestSuites`)
Names are CLI tokens. File = `Hex.Dawn/Testing/DawnScene.SelfTest.<X>.cs` unless noted; where the suite name differs, the method is `TestX` in the file of that name.

### 4.1 Shell, UX, settings, designer
| Suite | Covers | File |
| --- | --- | --- |
| native-shortcuts (explicit only) | real Windows key messages via native dispatcher | NativeKeys |
| ux, interactions, modal-shortcuts, shell, menus, scrollbars, minimap, middle-scroll, triple-click, window-click, zoom-persistence, solstice, ticks | mode focus, tab overflow, palette dismissal, dialogs owning keys, shell layout, menu limits, scrollbars, minimap, autoscroll, theme looks, per-tick steps | Ux, Interactions, ModalShortcuts, Shell, Menus, Scrollbars, Minimap, MiddleScroll, TripleClick, WindowClick, ZoomPersistence, Solstice, Ticks |
| layout | the designer: add/select/edit/undo/canvas/save/open/tabs/sliding sides (24 sub-tests) | Editing, Interactions plus many (TestEditorItself... TestLayoutTexts) |
| settings | settings window, search, looks, VS look, settings.hor sync, wheel, backdrop arrange | Settings, SettingsLooks, VisualStudio, SettingsFile, SettingsWheel, BackdropArrange |
| lossless | every engine/Hex `.hor` round-trips byte-for-byte | Lossless |
| file-icons, effects, problems, updates, save-integrity, reload-integrity | glyphs, effects, Problems panel, update handoff cancel, encoding/Save As, failed reload | FileIcons, Effects, Problems, Updates, DocumentSave, ReloadIntegrity |
| workspace, explorerfiles, dialogs, newproject, recent, scratchpads, launch | explorer ops/clipboard, Hex's own dialogs, New Project, recent files, scratchpads, how Hex starts | Workspace, ExplorerFiles, Dialogs, NewProject, Recent, Scratchpads, Launch |
| agents-layout, agents-markdown, mcp | Agents panel, Markdown in replies, the MCP server through `HexMcpTestClient` | AgentLayout, AgentMarkdown, Mcp |
| extensions, tools, catalog, emmet, tasks, automations, run_target, properties | HIDL extensions, move editor (also on Fighter2D moves), catalog sync/install/update, Emmet, tasks.hor, automations incl. Push all, run-target arrow, Properties panel | Extensions, Tools, Catalog, Emmet, Tasks, Automations, RunTarget, Properties |
| terminal, pty, work | terminal page on Dusk host, real pty, background work cards | Terminal, Pty, Work |
| git, conflicts | git in a made repo; real merge conflict settled | Git, Conflicts |
| search, markdown, anyfile, still, surface, code-surface | search view/Replace in Files, Markdown preview, any-file editing, code stays still while typing, drawing | Search, Markdown, AnyFile, Still, Surface |

### 4.2 Code editing and languages
| Suite | Covers | File |
| --- | --- | --- |
| code | the big code part: TestCode, FindBar, Places, values/console, docs, peek, inlays, lenses, semantic HIDL, languages, table, completion, parity | Code plus siblings |
| languages, semantic, lens, hidl-inlays, csharp, d, pyright | highlighting, semantic colours, reference-count lenses, parameter hints, csharp-ls, serve-d, Pyright | Languages, Semantic, CodeLens, Inlays, CSharp, D, Pyright |
| completion-imports(-boundary), completion-documentation(-race), completion-triggers, header-path-completion, cpp-include-completion, clangd-services/-extensions/-midword | LSP completion variants | CompletionImports, CompletionDocumentation, CompletionTriggers, HeaderPaths, CppIncludes, Clangd |
| range-formatting, on-type-formatting, organize-imports, using-quick-fix, rename-safety, python-rename, selection-ranges, type-definition, type-hierarchy, call-hierarchy, native-highlights, native-folding, native-inlays, native-signatures, python/csharp/quoted-signatures, signature-documentation, diagnostic-locations/-documentation, header-source, framework-navigation, definition-results, moved-files, server-startup, missing-sdk, nuget-restore, python-stubs, python-environment, python-create-environment, python-terminal, dotnet-terminal, dotnet-project-terminal, npminstall (explicit) | native LSP features per language | files of the same names under Testing/ |
| hover-races, signature-races, navigation-races, language-edit-races | delayed server responses cannot overwrite newer state | `HexLanguages.*Races.cs`, `HexLanguages.EditRaceSelfTest.cs` |

### 4.3 Debugging
| Suite | Covers | File |
| --- | --- | --- |
| debug, debug-cancel, debug-pause, debug-attach, debug-modules, debug-sources, disassembly, debug-memory, debug-threads, instruction-breakpoints, function-breakpoints, function-breakpoint-persistence, breakpoint-history, data-breakpoints, set-next-statement, debug-completion | C# (netcoredbg), plus Python/C++ variants | Debug, DebugCancel, Pause, Attach, Modules, DebugSources, Disassembly, Memory, Threads, InstructionBreakpoints, FunctionBreakpoints, BreakpointHistory, DataBreakpoints, SetNext, DebugCompletion |
| python-debug, native-debug, cmake-debug, cmake-preset-debug, cmake-configurations | debugpy, LLVM/lldb, CMake targets | PythonDebug, NativeDebug, CMakeDebug |
| csharp/python/cpp-{run-to, exceptions, threads, edit-variable, data-tips}, python-list-tips, variable-pages, watch-object, watch-value, watch-persistence, frame-values, debug-workspace-switch, cpp-pause, python-pause | per-language debugger UI | RunTo, ExceptionBreakpoints, Threads, EditVariable, NativeDataTips, VariablePages, WatchObject/Value/Persistence, FrameValues |
| launch-profile-environment, dotnet-task-environment, dotnet-target-frameworks, dotnet-runtime-options | F5 environment and framework selection | LaunchProfiles, RunEnvironment, TargetFrameworks, RuntimeOptions |

### 4.4 Build systems and test explorer
| Suite | Covers | File |
| --- | --- | --- |
| cmake-configure, cmake-library-configurations, cmake-switch-preset, cmake-problems, ctest | CMake and CTest | CMakeConfigure, CMakeSwitchPreset, CMakeProblems, CTestBuild plus `HexTestExplorer.CTestSelfTest.cs` |
| python-tests(-nested), pytest, test-explorer-toggle, test-configuration, test-parallel-frameworks, test-target-frameworks, test-workspace, test-debug-cancel | Test Explorer | `HexTestExplorer.*SelfTest.cs`, TestExplorerToggle, TestDebugCancel |
| save-evaluation, project-references, solution-switch, repository (explicit) | MSBuild evaluation, references, solution switching, real-solution qualification via `HEX_TEST_SOLUTION` | SaveEvaluation, ProjectReferences, SolutionSwitch, Repository |

### 4.5 Notebooks, memory, performance
| Suite | Covers | File |
| --- | --- | --- |
| notebook, notebookengineering, notebookfile, notebook-wrap, notebook-zoom, notebookhtml | Jupyter with a real kernel, SVG/LaTeX, HTML export | Notebook* |
| typing | timing of typing into big files, "no stutter allowed" | Typing |
| uicost | UI cost sitting still/typing/scrolling, reported not checked | UICost |
| document-retention, source-retention, document-memory | GC retention; `document-memory` is a 30-minute session by default | DocumentRetention, DocumentMemory, Garbage |
| garbage | allocations while sweeping the pointer (runs last) | Garbage |

### 4.6 Screenshots
`HEX_SELFTEST_SHOTS=<folder>` makes marked steps (BackdropArrange, CodeLens, ...) save PNGs; no baseline compare. Dusk: `DUSK_SELFTEST_SHOTS`.
The MCP `hex_screenshot` and Android `HORIZON_SCREENSHOT=shot.png@5` are separate mechanisms.

## 5. Dusk (`Hex.Dusk`) and its tests
- Dusk is Hex's terminal emulator mode, a LIBRARY (`OutputType Library`) inside the same executable: `Hex/Program.cs` starts it with `--dusk`, or when the exe is named `dusk`
  (`HexIdentity.ModeOf`). Dawn = editor mode, Dusk = terminal mode, both lazily constructed and switchable in one window (Ctrl+Alt+Shift+D). Not a separate product or variant of the editor.
  It was formerly the standalone `bogzxo/dusk` repository, copied in (comment in `Hex.Dusk/Hex.Dusk.csproj`). Entry `Hex.Dusk/DuskProgram.cs` (`Run`), scene `Shell/DuskScene*.cs`, plugins API `Plugins/`, assets `Assets/dusk`.
- Built as part of `Hex/Hex.csproj` (`ProjectReference ..\Hex.Dusk`). Run: `Hex.exe --dusk [--profile cmd] [--cwd dir] [--run "cmd"] [--size WxH] [--toggle] [--window]`.
- Self-test: `Hex.exe --dusk --selftest [suite ...] --exit` (`DuskScene.SelfTest.cs`, registry about 33 suites: graphite, solstice, bell, interactions, ux, symbols, start, plugins, tabs, splits, mouse, palette,
  themes, closing, shell, keys, history, files, sessions, repl, line, output, scroll, panes, tui, blocks, dawn, effects, path, micro, tuikeys, claude, backdrop). `--app` runs as an Application (sleeps between frames) instead of Game.
  Suites that press REAL keys (keys, history, repl) send them only while Dusk is foreground and only run when named or with `--realkeys`. Home is `%TEMP%/dusk-selftest-home` (`DUSK_HOME`), `DUSK_HEX=none`.
  Script class `Hex.Dusk/Testing/DuskScript.cs` mirrors `HexPointerScript`. Dusk's suites micro/claude depend on `micro` / Claude Code being installed (unclear what happens when absent).
- Dawn also has a Dusk-related suite (`DuskFiles`, part of `terminal`). Unit tests reach Dusk internals via `InternalsVisibleTo Hex.Tests` (e.g. DefaultTerminalTests, COM class through its function table).
- Windows default-terminal registration is described in `docs/hacking.md` (HKCU only, no admin).

## 6. Android (`Hex.Android`)
- Dawn only on an Android tablet (`net11.0-android`, app id `dev.horizon.hex`, min SDK 26). Same Hex.Dawn/Core/Shell with an Android head (`HexActivity.cs`, `HexAndroidFiles.cs`, `HexDocuments.cs`).
  NOT in `Hex.sln` (so desktop builds never need the Android workload) and not built by CI. No Dusk app (terminal page inside Dawn only). `HexHost` (Hex.Core) gates features the platform cannot do. Capability table in `docs/android.md`.
- Debug build: `dotnet build hex/Hex.Android/Hex.Android.csproj -c Debug -r android-arm64 -p:HorizonRoot=... -p:AndroidSdkDirectory=... -p:JavaSdkDirectory=...`; needs the engine's shader pack `horizon/Horizon/shaders/packed/*.hzshaders` (build stops otherwise).
- Release: `dotnet publish ... -c Release -r android-arm64` -> NativeAOT (`PublishAot=true` default, `OptimizationPreference=Speed`), signed APK `bin/Release/net11.0-android/android-arm64/publish/dev.horizon.hex-Signed.apk`.
  Verify the APK holds `libHex.Android.so` and no `libcoreclr.so`/Mono; NativeAOT on the .NET 11 Android SDK is experimental. Never `pm clear`; use `adb install -r`.
- Install/run/logs: `adb install -r`, `adb shell am start -n dev.horizon.hex/dev.horizon.hex.HexActivity`, `adb logcat -s Horizon AndroidRuntime DOTNET`.
- Settings are `HORIZON_*` string extras or `files/horizon.env`: `HORIZON_HEX_FOLDER`, `HORIZON_HEX_OPEN`, `HORIZON_HEX_SELFTEST=code,workspace|all` (runs in `cache/hex-selftest`, via `DawnProgram.HostedSelfTest`),
  `HORIZON_HEX_EXIT=on`, `HORIZON_SCREENSHOT`, `HORIZON_AUDIO`, `HORIZON_UPSCALER`, `HORIZON_RENDER_SCALE`. Self-test lines go to logcat and `files/debug.log` (`HexPointerScript.Say` on Android).
- Helper scripts (`artifacts/android-platform-baseline/hex-android-*.ps1`) and results (`hex-android-results.md`) are in the workspace `artifacts/`, which is NOT in this checkout (unclear if present).
- README paths hard-code one developer's machine (`C:/Users/bogdan/...`, `D:/Microsoft/VSShared/...`, device serial `R52M416M2TH`); adapt.

## 7. Packaging, installer, versioning, release

### 7.1 Projects
- `Hex/` the exe (Dawn+Dusk, `PublishAot=true`; Release: WinExe, self-contained, trimmed, no pdb). `Hex/Properties/PublishProfiles/FolderProfile.pubxml` = Release win-x64 NativeAOT (README benchmark profile).
  Native toolchain needed: VS C++ build tools + Windows SDK (Windows), clang + zlib dev (Linux), Xcode CLT (macOS). `Hex.csproj` has a target that puts `Microsoft Visual Studio\Installer` on PATH for vswhere.
- `Hex.Installer/` native Horizon UI app (Setup): `Program.cs`, `InstallerScene.cs`, `SetupOptions.cs` (flags `--target --uninstall --update --version --package --wait-pid --wait-start --preview`), UI layout `Assets/installer/setup.hor`,
  options `options.hor`, strings `Assets/installer/localisation/en-gb.hor`, `WindowsSetupBundle.targets` (Windows publish packs everything into one `Hex.Installer.exe`, SHA-256 overlay; `-p:BundleWindowsInstaller=false` for loose output).
- `Hex.Installation/` logic library: `ReleaseClient` (GitHub release JSON, asset `Hex-<rid>.zip`, digest, host allow-list, prerelease refusal), `InstallTransaction` (stage, swap, rollback), `InstallPaths`, `InstallChoices`, `PlatformRegistration`
  (shortcuts, App Paths, Launch Services), `InstallerHandoff`, `PreparedInstallerUpdate`, `WindowsSetupBundle`, `InstallText`. State file `.hex-install.json`.
- Update flow inside Hex: Help -> Check for Updates (`Hex.Dawn/Shell/DawnScene.Updates.cs`; self-test suite `updates`). Downloads and verifies the ZIP while Hex is open, relocates the NEW release's installer to a temp dir, close prompt, installer waits for parent pid, re-verifies SHA-256, swaps. Full detail: `docs/installer.md`.
- Install locations: Windows `%LOCALAPPDATA%/Programs/Hex`; Linux `~/.local/opt/hex`; macOS `~/Applications/Hex.app/Contents/MacOS`. macOS unsigned/not notarized. No crash-atomic replace; `.previous-<id>` dirs can remain.
- Manual dev: `dotnet run --project Hex.Installer -- --preview` (offline UI), `dotnet test Hex.Installer.Tests`, `dotnet publish Hex.Installer -c Release -r win-x64`.

### 7.2 Versioning
- Version single source: `Directory.Build.props` `<Version>3.0.0</Version>` and `<VersionName>Solstice</VersionName>`, applied to every project whose name starts with `Hex` (condition on `MSBuildProjectName`); also written as `AssemblyMetadata VersionName`.
  The props file also imports the workspace-level `Directory.Build.props` found above it (so the engine keeps its own version).
- Build number: `Hex/HexBuildNumber.targets` increments a counter in `.hex-build` (repo root, git-ignored, first value 4420) on every non-design-time build of the exe, and writes `obj/.../build.txt` copied beside the exe/publish. Shown after the codename in the title bar.
  Consequence: any build of `Hex.csproj` mutates `.hex-build`; do not commit it.
- SDK pinned in `global.json`: `11.0.100-rc.2.26504.105`, `rollForward latestMajor`, prerelease allowed. CI uses `dotnet-version 11.0.x`, quality preview.
- Release notes: `RELEASE_NOTES.md` (first line = title, e.g. `# Hex 3.0.0 Solstice`; the workflow derives the GitHub release title from it, "preview"/"prerelease" in line 1 marks a prerelease).

### 7.3 CI workflows (`.github/workflows`)
| Workflow | Trigger | Does | Gates |
| --- | --- | --- | --- |
| `ci.yml` "CI" | push/PR to master, development; manual | matrix windows/ubuntu/macos-latest: checkout with submodules (hex-extensions) + `bogzxo/horizon@development` into `horizon/` (recursive); setup .NET 11 preview; nuget cache; `dotnet build Hex/Hex.csproj -c Release`; build Hex.Installer; `dotnet test Hex.Installer.Tests`; Python 3.13 + engineering requirements; `dotnet test Hex.Tests` with `--blame-hang-timeout 5m`; on failure upload `Hex.Tests/TestResults` | unit + installer tests only. Self-tests are NOT run. `Hex.Android` and `Hex.Dusk` self-tests are not built/run. Toolchain-gated tests silently pass |
| `build.yml` "Build" | push to master, tags `v*`, manual | matrix win-x64, linux-x64, osx-arm64, osx-x64 (macos-15-intel, `UseLdClassicXCodeLinker=false`), linux-arm64, win-arm64: `dotnet publish Hex/Hex.csproj -c Release -r <rid>` and Hex.Installer; on tag: verifies tag equals `Directory.Build.props` Version (`v3.0.0` vs `3.0.0`); Windows: checks PE subsystem = GUI (2) for Hex.exe and Hex.Installer.exe, single-file setup, bad-arg exit code 1, no leftover extraction folder; macOS builds `.icns` and a `Hex Setup.app`; copies installer into `Hex/installer`; zips `Hex-<rid>.zip`, `Hex-Setup-<rid>.zip` or `.exe`; uploads artifacts | packaging checks only; no tests are run here |
| release job (in build.yml) | tag `v*` | downloads artifacts, `gh release create <tag> dist/*.zip dist/*.exe --notes-file RELEASE_NOTES.md`, title from notes, prerelease when tag has `-` or notes title says preview | updates need an asset `Hex-<rid>.zip` with a SHA-256 digest |
- Branches: work on `development`; `master` is what is released (`docs/hacking.md`). Releasing: bump `Directory.Build.props`, edit `RELEASE_NOTES.md`, merge to master, push tag `vX.Y.Z`.
- CI builds against the engine's `development` branch, not a pinned commit.

### 7.4 Local automation (`tasks.hor`)
Tasks: `build` (`dotnet build Hex/Hex.csproj`, Ctrl+Shift+B), `build_all` (`dotnet build Hex.sln`), `test` (`dotnet test Hex.Tests/Hex.Tests.csproj`; comment: "the self tests are Hex.exe --selftest"), `run`
(`dotnet run --project Hex/Hex.csproj --no-build -- --window`, depends on build; `--window` stops it handing off to the running Hex). A build into the folder the running Hex lives in moves locked files aside (`Hex.Core.Projects.HexSelfBuild`).
Workspace-level `../tasks.hor` has Pull all / Push all (refuses commits with Claude credit lines via its `CREDIT` regex) / status.

### 7.5 Scripts (`scripts/`)
- `measure-readme.ps1 -PublishedDirectory -Repository -OutputDirectory [-Samples 10]`: Windows benchmark of a published Hex (start to first frame, idle working set, CPU) with one discarded warm-up per mode, normal app mode not selftest; sets `HEX_HOME`, `HORIZON_LOG_STARTUP`, `HEX_PROFILE`.
- `measure-ux-startup.ps1 -BaselineDirectory -CurrentDirectory`: A/B startup comparison of two builds, 30 samples default, alternating order.
- `install-desktop.sh`: Linux `.desktop` entry/icon install. `build-engineering-notebook.py`: generates the engineering notebook sample used by notebook tests/docs.

## 8. Docs that double as evidence
`docs/benchmarks.md` + `docs/benchmarks/2026-11-04/` (raw samples; numbers in README: 492 ms Dawn / 474 ms Dusk first frame, 45.0 MiB zip), `docs/agent-harness-validation.md` (harness vs grep token savings, 691 files indexed),
`docs/pyright-validation.md`, `docs/ux-revamp-validation.md` (+ `ux-startup-samples.json`), `docs/notebook-support-validation.md`, `docs/notebook-explorer-integration-validation.md` (+ `notebook-startup-samples.json`).
They record manual validation runs (e.g. "481/481 tests passed" in pyright doc; stale versus today's about 640), not automated gates. `docs/hacking.md` links `performance.md`, which does not exist.

## 9. Gotchas
- Run self-tests from `hex/Hex/bin/<Config>/net11.0/`, never from a copy; `hex-extensions` is found by walking up. Hence also `hex_selftest` uses the running exe, so a scratch-workspace Hex misses it.
- Self-tests need a real window and Vulkan device; headless runners fail. Do not interact with the machine during `native-shortcuts` or the real-key Dusk suites.
- Order dependence: suites are designed to be run alone (`MakeTestFiles` stand-ins), but a full run is the reference. A suite that leaves modified documents is mopped by `TidyAfterSuite` only for files under `testFolder`.
- A full default run is long: `document-memory` alone is 30 minutes unless `HEX_MEMORY_SESSION_SECONDS` is set (min 10); debug/notebook/LSP suites each take a minute-plus. `hex_selftest` default timeout 600 s is too short for big sets; pass `timeout` (max 3600).
- Toolchain suites need csharp-ls, serve-d, clangd, lldb/LLVM, debugpy, pyright, netcoredbg, cmake, python+ipykernel; missing ones skip/print or fail. Unit tests with env gates pass vacuously.
- `typing`, `uicost`, `garbage` are timing/allocation sensitive; expect flakiness on loaded machines. Unit tests that time work are in the non-parallel `timed` collection.
- Each build of `Hex.csproj` bumps `.hex-build`; building Hex while a Hex runs from the same folder is handled by `HexSelfBuild` (old files moved aside).
- Test `.csproj` reference Dusk/Dawn directly, so unit test builds compile the whole editor (slow first build). Engine must be at `../horizon/` or `-p:HorizonRoot=`.
- `JsonSerializerIsReflectionEnabledByDefault=false` in tests: reflection-based JSON works in the app only via source generators; tests will catch violations.
- `docs/hacking.md` self-test numbers are stale (see 3.3). README says Hex benchmark is one machine only.
- Windows-only validation: Linux/macOS packaging and installer behavior are "must be validated on those systems" (`docs/installer.md`); CI only builds them.
- Unclear: whether CI has ever run the self-tests (no workflow does); whether `Hex.Dusk` self-tests are part of any routine run.

## 10. Where to look for X
| Need | Look at |
| --- | --- |
| A suite's name/description/order | `Hex.Dawn/Testing/DawnScene.SelfTest.cs` (`SelfTestSuites`), or `hex_selftest suites=list` |
| Scripted pointer API | `Hex.Dawn/Testing/HexPointerScript.cs` |
| Self-test CLI parsing | `Hex.Dawn/DawnProgram.cs`, `Hex.Dawn/HexLaunch.cs`, `Hex.Dawn/HexOptions.cs` |
| Self-test MCP tool | `Hex.Dawn/Agents/DawnScene.McpWorkbench.cs` (`SelfTestRun`) |
| Layout `--check/--exercise` | `Hex.Dawn/Testing/DawnScene.Checks.cs` |
| Per-suite state cleanup | `Hex.Dawn/Testing/DawnScene.SelfTest.Tidy.cs` |
| Why extensions are found in tests | `Hex.Dawn/Extensions/HexExtensions.cs` (`Find`, `CATALOG_SUBMODULE`), `Hex.Tests/Catalog.cs` |
| Unit-test fakes | `Hex.Tests/FakeHex.cs`; `HelloDollyTests.cs` as a template |
| Test Explorer feature | `Hex.Dawn/Tests/HexTestExplorer*.cs` |
| Dusk entry/flags/tests | `Hex.Dusk/DuskProgram.cs`, `Hex.Dusk/Testing/DuskScene.SelfTest.cs` |
| Android head and run settings | `Hex.Android/README.md`, `Hex.Android/HexActivity.cs`, `docs/android.md`, `Hex.Dawn/DawnProgram.Hosted.cs` |
| Version / codename | `Directory.Build.props`; build number `Hex/HexBuildNumber.targets` |
| Release process | `.github/workflows/build.yml`, `RELEASE_NOTES.md`, `docs/installer.md` |
| Installer logic and tests | `Hex.Installation/`, `Hex.Installer/`, `Hex.Installer.Tests/` |
| CI gates | `.github/workflows/ci.yml` |
| Dev tasks | `tasks.hor`, `../tasks.hor` |
| Performance method | `docs/benchmarks.md`, `scripts/measure-*.ps1` |
