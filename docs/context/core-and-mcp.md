# Hex.Core, Hex.Shell, the entry exe, the MCP server, the harness and extensions

Orientation for coding agents. Paths are relative to `hex/` unless noted. Every `path:line` was checked against the code at the time of writing; `~` marks an approximate line. Lines drift, so re-find with `hex_symbols` / grep. Sibling docs (not repeated here): `dawn-app.md` (the editor, DawnScene, commands, tick loop, all Dawn folders) and `testing-and-shipping.md` (unit tests, self-test suites, `hex_selftest`, installer). Product-level docs: `docs/agents.md` (user view of MCP), `docs/extensions.md` (extension API for users), `docs/agent-harness-plan.md`, `docs/hacking.md`.

## 1. Architecture

All projects are `net11.0`, NativeAOT-friendly; `IsAotCompatible` and `IL2026/IL3050` are errors in Hex.Core (so: no reflection JSON, see section 12).

| Project | Role |
| --- | --- |
| `Hex.Core` | Window-free model and services. References the engine (`Horizon`, `Horizon.Tooling`, `Horizon.UI`, `Horizon.Terminal`, `Horizon.HIDL`). Nearly everything `internal`; `InternalsVisibleTo` Hex, Hex.Shell, Hex.Dawn, Hex.Dusk, Hex.Tests. About 163 .cs files. Public exceptions: `HexHost` (`HexHost.cs:10`), `DSemantics`. |
| `Hex.Shell` | Chrome shared by Dawn and Dusk, see section 3. |
| `Hex.Dawn` | The editor. One giant `internal sealed partial class DawnScene` over many files. See `dawn-app.md`. All MCP *tools* live here (they drive the editor). |
| `Hex.Dusk` | Terminal mode (`DuskProgram`, `DuskScene`, plugin API in `Plugins/`). |
| `Hex.Android` | Tablet head: same Dawn/Core/Shell, no desktop project. Sets `HexHost` flags. Not in `Hex.sln`. |
| `Hex` | The exe: `Hex/Program.cs` only (270 lines). References Shell, Dawn, Dusk. |
| `Hex.Tests`, `Hex.Installer*`, `Hex.Installation` | Tests and installer, see `testing-and-shipping.md`. |

Dependency direction: Hex -> {Dawn, Dusk, Shell} -> Core. Core has the model and protocol pieces (MCP framing, harness index, git, notebook kernel, project evaluation, settings, search). Dawn wires Core to UI and is the only owner of documents, terminals, debugger, explorer.

`Hex.Core/HexHost.cs:10` static capability flags set by the head before the editor exists: `ProgramFolder` (exe folder; Assets, shaders, built-in extensions), `Name`, `Processes`, `RunsDownloads`, `Pipes`, `NativeDialogs`, `PickFile/PickFolder` (callbacks, Android), `Report`. Helpers `NoProcesses(what)` (:57) and `NoDownloads(what)` (:61) return a "why not" string or null: `if (HexHost.NoProcesses("git") is { } why) { say(why); return; }`. Always check before spawning a process or opening a pipe. `StartMcp` refuses when `!HexHost.Pipes`.

## 2. Startup path and every CLI mode

### 2.1 `Hex/Program.cs` `Main` (line 17)
1. `PlatformPaths.BesideProgram = true` (:24): settings, backups, shader caches, driver pipeline cache live next to the exe (`<exe>/settings`, `data`, `cache`).
2. `TerminalSettings.UsualTheme = HexSettings.USUAL_TERMINAL_THEME` (:27), `DesktopPlatform.Install()` (~:30), `HexSelfBuild.CleanUpSoon()` (~:33; deletes `*.old` binaries from a build of Hex over itself, `Hex.Core/Projects/HexSelfBuild.cs:99`).
3. **`--help` / `-h` / `-?` / `/?` / `/h`** (:35): attaches to the parent console (`ToTheParentConsole`, a Release Hex has no console), prints `HELP` (const at file end), returns 0.
4. **`--lsp`** (:46, plain `args.Contains`): `Hex.Core.HidlServer.Serve()` (`Hex.Core/Hidl/HidlServer.cs:66`), HIDL's language server over stdio, no window, no logger.
5. **`--mcp-connect`, or `--mcp` without `--window`/`--selftest`** (:51): `McpBridge.IsBridgeMode(args)` (`Hex.Core/Mcp/McpBridge.cs:20`) -> `McpBridge.Run(args, exePath)` (:25): stdio<->pipe bridge, no window, no HexLog (the bridge never initializes the logger so it does not hold `debug.log`).
6. `HexLog.Initialize(AppContext.BaseDirectory)` (:54), `StartupTrace.Mark("main")`. Debug builds with env `HEX_CONSOLE=on` allocate a console (`ConsoleForTheLog` ~:195, skipped for `--mcp`, `--lsp`, `-Embedding`).
7. `ProcessEnvironment.ImportLoginShell()` (login-shell PATH, off thread), `TerminalKinds.Register()` (terminal components known to layouts for both modes).
8. `HexIdentity.ModeOf(args, exe)` (:71, `Hex.Shell/HexIdentity.cs`) -> Dawn unless `--dusk` (case-insensitive, `HexIdentity.cs:38`) or exe named dusk. `HexIdentity.Claim(mode)` sets the AppUserModelID (separate taskbar buttons).
9. `--dusk` is stripped; `Dropped(rest, mode, out more)` (:104) turns bare paths (before the first `-` option) into `--open file` / `--folder dir` (Dawn reads its own bare paths via `HexLaunch`, so for Dawn it returns args untouched) or `--cwd dir` (Dusk). Extra dropped files are handed to a running Dawn by `HandOver` (:156) through `HexInstance.Tell(DawnScene.RemotePipe, "open|file|0")` for up to 20 s.
10. `DawnProgram.Other = DuskProgram.Scene`, `DuskProgram.Other = DawnProgram.Scene`, `DawnProgram.NativeWindow` (Win32 handle for the native self test): the mode swap wiring (`HexModes`, Ctrl+Alt+Shift+D). Then `DuskProgram.Run(rest)` (:86) or `DawnProgram.Run(rest)` (:93).

### 2.2 Dawn flags (`Hex.Dawn/DawnProgram.cs`; consts at :11-28, `Run` :63, `Start` :81; full table in `dawn-app.md` section 2)
`--open`, `--line`, `--goto/-g file:line:col` (HexLaunch), `--folder`, `--workspace` (layout dir), `--window` (never hand over to a running Dawn), `--view design|code|table`, `--scale`, `--size WxH`, `--check files...` (headless layout round trip), `--exercise` (WRITES files), `--selftest [suites]`, `--exit`, `--wheel`, `--watch`, `--browse`, `--console lines...`, `--fresh` (no restore), `--mcp`. `HexOptions.Mcp = args.Contains("--mcp")` (~DawnProgram.cs:140); `DawnScene.McpWanted` (`Hex.Dawn/Agents/DawnScene.Mcp.cs:54`) = `(settings.McpServer && !SelfTest || options.Mcp) && Check.Length == 0`. So `--window --mcp` = GUI with server on.

### 2.3 Dusk flags (`Hex.Dusk/DuskProgram.cs:13-25`)
`--cwd`, `--profile` (shell), `--size`, `--scale`, `--selftest`, `--exit`, `--watch`, `--app`, `--realkeys`, `--toggle` (tells a running Dusk to toggle, :83), `--window` (own window rather than a tab of the running Dusk), `--run cmd`, `-e cmd`. Without `--window`/`--selftest` a new Dusk tells the running one `tab|folder|profile|run` over `HexInstance` and exits (:89).

### 2.4 MCP-related modes at a glance
| Invocation | What it does |
| --- | --- |
| `Hex --mcp` (stdio client started it) | Bridge: finds the Hex that has the folder open (`--folder`, a bare folder arg, or cwd), else starts `Hex --window --folder X --mcp` under a global mutex, then pumps bytes. No window of its own. |
| `Hex --mcp-connect` | Attach-only bridge. Never launches; exit 1 + stderr message if no server matches. For hosts (Codex) that start one connection per chat. |
| `Hex --window --mcp` | The editor GUI with the server on for this run. |
| `Hex --mcp-connect --folder F --agent-chat ID` | A per-chat bridge: `ID` becomes the tag in the handshake, so the server knows which Agents-panel chat is calling (`McpBridge.cs:56`). Built by `hex.mcp_server(chat)`. |
| `Hex --selftest mcp --exit` | Runs the in-editor MCP suite (`Hex.Dawn/Testing/DawnScene.SelfTest.Mcp.cs`). `--selftest` disables bridge mode even with `--mcp`. |
| `Hex --lsp` | HIDL language server, not MCP. |

`Hex.Core/Mcp/McpBridge.cs`: `FolderOf` (:~100), `Resolve`/`Select` (:~75; explicit folder never falls back to another workspace; with no explicit folder and exactly one running Hex, that one is reused), `UnderLaunchLock` (~:80; named mutex `hex-mcp-start-<sha256 of discovery folder>`, waits 90 s + 5 s; rechecks discovery after taking the lock so simultaneous chats attach to the first editor), `StartHex` (:~125, waits for the discovery file). `START_WAIT_MS = 90_000`, `CONNECT_MS = 5_000`. Stdout is protocol only; diagnostics go to stderr as `[Hex --mcp] ...` (`Say`). `McpBridge.Launch.cs` holds the process start and the byte `Pump`.

## 3. Hex.Shell (`Hex.Shell/`)
Chrome and platform glue shared by both modes, so Dawn and Dusk look and behave as one program. References Hex.Core only; public types so Dusk/Dawn/Exe can see them.
- Identity and instances: `HexIdentity` (:17; `HexMode { Dawn, Dusk }` at :6, `ModeOf`, `Claim`), `HexInstance` (:13; one instance per mode: first Dawn/Dusk listens on a pipe `PipeOf(mode)`, later starts send a line such as `open|file|line` or `tab|folder|profile|run` and exit; `Tell`, `TryTake` on the program thread), `HexModes` (:14; Dawn and Dusk in one window, scenes made on first need and kept `Persistent`, `Host` creates scenes; swap = Ctrl+Alt+Shift+D), `HexModeSwitch` (shared title-bar mode selector).
- Look and feel: `HexFrame` (:31; window frame/shape, field of light, effects), `HexGlass` (glass sliders `glass_blur/clarity/rim/bend`, shared by both modes), `HexLightEffects` (caret glow, pointer spotlight, click rings, embers), `HexMotion`, `HexHorizon` (the status line under the title bar), `HexSplitter` (:21; drag strip over a boundary), `HexTerminalStatus`.
- Support: `HexLog` (single `debug.log` beside Hex, truncated on each GUI launch), `HexVersion` (reads `Version`/`VersionName` from `Directory.Build.props`), `HexText` (localisation lookups `HexText.Get(key, (name, value)...)` for text shared by both modes), `HexFiles` (where Hex keeps its own files: the settings folder next to the exe).
- `Settings/`: `HexLooks` (:30; named looks/themes, `.VisualStudio` imports VS themes), `HexLookPicker`, `EditorSettingsCatalog` (:18; the settings window's catalogue of editor settings, text in `SettingsPageTexts`), `GlassPreview`, `WindowsSource`, `DefaultTerminalSource` (settings sources for the settings window).
- `Platform/`: `HexDialogs` (native dialogs), `HexDefaultTerminal` (:28; registers Hex as Windows' default terminal; started with `-Embedding`), `HexCom` (COM interop), `HexExplorerMenu` (Explorer context-menu entries), `HexUserPath`.
- `Testing/RealKeys.cs`: posts real keys to the desktop window for the native self test.
- `Assets/`: icons and the shared `uix` skin.

## 4. Hex.Core subsystem guide (one section per folder; consumers are in `Hex.Dawn/<same folder>` unless stated)

### 4.1 Projects/ (34 files)
MSBuild/.NET, CMake, tests, templates, self-build.
- `DotnetProjects` (`DotnetProjects.cs:13`, `Shared` :16): evaluates MSBuild projects through the SDK off the editor thread (`DotnetEvaluation` -> items and properties), cached, invalidated by a `FileSystemWatcher` on the project, `Directory.Build.props/.targets`, `Directory.Packages.props` and file add/remove. The nearest SDK-style project owns a file. `.Properties`/`.Settings` partials. `DotnetCli` (:228): runs `dotnet` as a child, stripping inherited MSBuild env vars, sets `UseSharedCompilation=false`.
- Solutions: `DotnetSolutions`, `DotnetSolutionTree(.Projects/.Dependencies)` (sln/slnx parse for the Solution view), `ProjectXml`, `ProjectFileEdits*`, `ProjectReferences`, `CSharpItems` (text-preserving csproj edits), `NugetSearch`.
- Run/debug: `DotnetLaunch` (launchSettings profiles), `DotnetRunLine` (a task that is just `dotnet run` -> F5 debugs it), `DotnetCommandLine`, `DotnetDebugTarget`, `DotnetRuntimeOptions`.
- Tests: `DotnetTests*` (discovery in source, results, filters, reconcile), `CTest`.
- `HexSelfBuild` (`HexSelfBuild.cs:12`): building Hex into the folder it runs from: locked exe/dll/pdb renamed to `name.old` first, restored if the build wrote nothing, removed at next start. Mirrored in the repo's `tasks.hor` (`build`, `build_all`, ...).
- `CMakeProject(.Presets/.Selection/.Compilation)`: configure/build, presets, `compile_commands` for clangd; `CMakeProject.Run` is a generic process runner Python reuses.
- `ProjectTemplates` / `ProjectCreation`: New Project. Extensions add with `hex.project_template`; `ProjectTemplates.Forget(extensionId)` on restart.
- Consumers: `Hex.Dawn/Workspace/`, `Tasks/`, `Debugging/`, `Languages/` (restore on project change), Solution view.

### 4.2 Notebooks/ (21 files)
Native Jupyter client, no libzmq or Jupyter install needed. Docs: `docs/notebook-support-plan.md`.
- Model: `Notebook` (:43) / `NotebookCell` (:18): kinds Code/Markdown/Raw, outputs as `JsonObject`, `Rest` keeps unknown nbformat keys, default nbformat 4.5. `NotebookFile` (:14) reads/writes .ipynb Jupyter-compatibly (1-space indent, line lists) for minimal diffs.
- Kernel: `KernelSpecs` (:18; JUPYTER_PATH, user/system dirs, `share/jupyter` of Pythons on PATH, synthesises a spec for Pythons with ipykernel). `Kernel` (`Kernel.cs:29`, partials `.Channels`, `.Interrupt`): writes a connection file with random ports/key, spawns the kernel, shell/control/iopub/stdin/heartbeat sockets over `ZmtpSocket` (`ZmtpSocket.cs:30`, own ZMTP 3.0 NULL mechanism), HMAC-signed `JupyterMessage`. Events are queued in `Kernel.Events` for the editor thread (no callbacks from socket threads). Kernel dies with Hex (child process group).
- `NotebookRun` (:16): execute_request id -> cell, stream merging with `\r`, display updates, clear_output, input requests. Support: `NotebookTables`, `NotebookHtml*` (export + sanitizer + highlight), `NotebookMarkdown`, `NotebookMath`, `AnsiText`, `NotebookCsv`, `NotebookAttachments`.
- Consumers: `Hex.Dawn/Notebooks/` (`HexNotebook`, rendering, `DawnScene.NotebookKernel.cs` EnsureKernel/StartKernel), MCP `hex_notebook*` (`DawnScene.McpNotebook*.cs`).

### 4.3 Harness/ (14 files), see section 6
### 4.4 Git/ (12 files)
All git access shells out to the `git` on PATH, no libgit2. `GitProcess.Run(folder, args, input, patience, unattended)` (`GitProcess.cs:48`) -> `GitResult(Exit, Output, Error)`; never throws, exit -1 on failure; 30 s default patience; `GitProcess.Account` selects the GitHub account; `Installed` blocks, use `InstalledNow` on the UI thread. `GitRepository.Find(path)` (`GitRepository.cs:18`) walks up to `.git` (dir or file for worktrees/submodules). Parts: `GitRepository.Branches`, `GitStatus` (porcelain parse, `GitChange`), `GitLineDiff` / `GitWordDiff` / `GitDiffText`, `GitBlame`, `GitCommits`, `GitStash`, `GitMerge`, `GitConflicts`. Everything is blocking: run on a background thread. Consumer: `Hex.Dawn/Git/HexGit*.cs` does that and marshals back (Source Control view, gutter, blame, merge editor). Gated by `HexHost.NoProcesses`.

### 4.5 Mcp/ (9 files), see section 5
### 4.6 Editing/ (9 files)
Comment-preserving edits of HIDL/layout text by value path. `HexSourceTree` (:19; path -> `HexNode` value + source span + `HexEntry`s), `HexSourceEdits` (:17), `HexValueEdits` (:14; batches of `HexEdit`), `HexValueText` (formats values), `HexLayoutText` (:24; statements of a layout file), `HexLayoutShape` (:35; parts of a layout), `HexLayoutEdits` (:12; designer changes written back into hand-written layouts), `HexLanguageEdits` (:12). `DocumentFile` (:6) is the atomic write that preserves encoding. Exposed to extensions as `hex.edit/insert/remove/rename/reorder/edits/undo_file/redo_file` (`HexExtensions.Edits.cs:20`). Consumers: Dawn designer (`Hex.Dawn/Designer/`), `Documents/`, extensions.

### 4.7 Hidl/ (8 files)
HIDL (`.hor`) analysis behind the editor and `Hex --lsp`: `HidlAnalysis`, `HidlSemantics`, `HidlReferences`, `HidlInlays`, `HidlLocals`, `HidlFunctions`, `HidlComponents` (what components a layout knows), and `HidlServer` (`HidlServer.cs:20`; `Serve()` :66 reads LSP on stdin/stdout; handles `shutdown`). HIDL has no entry in `language_servers.hor`: Hex is its own server. Consumers: Dawn code editor (diagnostics, completion, hover, go to definition for `.hor`), harness (`.hor` outline uses the real lexer).

### 4.8 Search/
`HexSearch` (`HexSearch.cs:18`, partial `.Find` :14, `.Replace` :16; `HexResult` :11; `TextFilesUnder(roots)`, `RelativeTo`, `ReadLines`), `HexGlob` (:10), `HexScope` (:8; include/exclude boxes), `HexExcludes` (:9; `files_exclude`, `search_exclude`), `HexIgnoreFile` (:9; .gitignore when `search_use_ignore_files`). Consumers: Dawn Find in Files/Search side view, quick open, the harness index (`TextFilesUnder`), MCP `hex_find_in_files` (editor group, uses Dawn's search) and harness tools.

`HexIgnoreFile` and `HexGlob` also parse the root `.hex-link-ignore` for Link. `HexLinkIgnore` refreshes the rules from disk. `HexLinkTree` applies them to manifests and watcher events, and `HexLinkFiles.Resolve` checks them for sync reads and writes. Private Publish checks both draft and authority rules. Replica projection and claim exchange apply local rules.

### 4.9 Workspace/ (6 files)
`HexWorkspace` (`HexWorkspace.cs:18`; `.hex-workspace` multi-root file), `WorkspacePaths` (:4), `HexRunTarget` (:11; what F5/Run uses: project, file, task), `HexLocalHistory` (:16; kept versions, `HexKeptVersion`), `HexTrash` (recycle-bin moves), `HexFileFacts` (:17; encoding/EOL/indent sniffing `HexTextFacts`, image `HexImageFacts`). Consumers: `Hex.Dawn/Workspace/` (explorer, places), status bar, local history view.

### 4.10 Settings/ (6 files)
- Format is HIDL (`.hor`). `HexSettings` (`HexSettings.cs:19`, `FILE = "hex_settings.hor"` :21; `.Table.cs` has the `HexSettingRow` table :10, class part :34; `.Workspace.cs` :12). Dawn builds it with the shared terminal settings file plus the program-folder legacy file (`Hex.Dawn/Settings/DawnScene.Settings.cs:30`). The real folder is `TerminalFiles.UserFolder` (`horizon/.../Hosting/TerminalFiles.cs:24`): env `HEX_HOME` (or `DUSK_HOME`), otherwise `<exe>/settings` because Hex sets `BesideProgram`, else `%AppData%\Hex` / `~/Library/Application Support/Hex` / `~/.config/hex`. The same folder holds `keys.hor`, `plugins/`, `mcp/` (discovery files), `harness/` (index cache), sessions. Sections: `editor`, `terminal`, `extensions` (keyed by extension id).
- Each setting = a field on `HexSettings` + a `HexSettingRow` (file key, parse, write, clamp, e.g. `Flag("mcp_server", ...)` at `HexSettings.Table.cs:83`). Only values differing from the default are written. Missing/invalid file = defaults. Also add a catalog entry in `Hex.Shell/Settings/EditorSettingsCatalog.cs` and localisation text; copy an existing key such as `format_on_save` end to end (a few more touch points may exist; run the settings self-test suites).
- Workspace overrides: `<folder>/.hex/settings.hor` (`WORKSPACE_FOLDER/FILE` `HexSettings.Workspace.cs:15`), only for keys in the `WorkspaceKeys` set (:18; `IsWorkspaceKey` :120). `mcp_server` and other user-only keys are never workspace-scoped (a cloned repo must not open the MCP door; `DawnScene.SettingsDiagnostics.cs` flags it).
- Others: `HexEditorConfig` (:22; .editorconfig), `VsCodeSettings` (:24; imports VS Code `settings.json`, JSONC), `HexAddedServer` (:12; user-added language servers).
- MCP access: `hex_settings_get/set`.

### 4.11 Python/ (5 files)
`PythonEnvironment` (create `.venv`, terminal env with VIRTUAL_ENV/PATH; failed venvs kept as `.failed-<guid>`), `PythonDebuggerSetup` (debugpy), `PythonRunLine`, `PythonUnitTests`, `PythonPytest`. Language server `python` = pyright (`docs/pyright-validation.md`). Consumers: Dawn Debugging/Tasks/Tests, notebook kernel choice.

### 4.12 Languages/ (5 files)
`HexServerCatalog` (`HexServerCatalog.cs:71`; `HexServerEntry` :29) parses `Hex.Dawn/Assets/hex/language_servers.hor` (`let servers = { csharp, python, slang, cpp, typescript, json, markdown, d, yaml, go, java, rust, bash, toml, docker, svelte, ... }`, each with command, args, language, extensions, about, install/update/repo/version, reload, needs, settings). `HexReleases` (:17; GitHub releases, also used by the extension catalog download), `HexCodeLens` (:9; reference-count helpers), `DSemantics` (:15 + `.Lexer`; Hex's own semantic tokens for D). The LSP client itself is the engine's `Horizon.Tooling/LanguageServers/LanguageServer*.cs`; Dawn glue is `Hex.Dawn/Languages/HexLanguages*.cs`.

### 4.13 Agents/ (4 files), see section 7
### 4.14 Values/ (3 files)
`HexEvaluation` (:25; run a `.hor` through the HIDL runtime, capture declared values and timing, `HexValues` :17), `HexFixes` (:11; "did you mean" fixes), `HexLints` (:16; lints). Consumers: Dawn `Values/` (values panel, value box), diagnostics.

### 4.15 Tasks/ (3 files)
`HexTask` (`HexTask.cs:10`; one entry of `tasks.hor`: command, folder, group, default, depends_on, terminal "panel" | "dusk"), `HexAutomation` (:16; user commands in the palette), `TasksFileText` (:8; text edits of `tasks.hor`). Dawn runs them (`Hex.Dawn/Tasks/`); MCP `hex_run_task` / `hex_task_output`.

### 4.16 Outline/ (3 files), see 6.4
### 4.17 Documents/ (2 files)
`HexBackups` (`HexBackups.cs:12`; hot-exit backups of unsaved buffers, `Found` record), `HexNames` (:9; "Untitled-N" style names). Consumers: `Hex.Dawn/Documents/`.

### 4.18 Extensions/ (1 file)
`HexExtensionCatalog` (`HexExtensionCatalog.cs:13`): syncs a catalog source (local folder, `owner/name` GitHub zip of `main`, or git URL), `KindOf` classification (`HexExtensionSourceKind` :224), `FolderNameOf` (readable + FNV hash). Consumers: `Hex.Dawn/Extensions/HexExtensions.Catalog.cs` / `.Sources.cs` and the Extensions window. The runtime API is in Dawn (section 8).

## 5. The MCP server

### 5.1 Layers (all `Hex.Core/Mcp/`, newline-delimited JSON-RPC 2.0)
| File | What |
| --- | --- |
| `McpProtocol.cs:9` | Versions `2025-11-25, 2025-06-18, 2025-03-26, 2024-11-05` (:12), `Negotiate` (:24), `AtLeast`, error codes (:18), `Result`/`Error` builders. |
| `McpServer.cs:13` | `McpServer(IMcpTools tools, name, version, instructions)`, one per client. Methods (:112-121): `initialize`, `ping`, `tools/list`, `tools/call` (`CallTool` :179), `resources/list|read|subscribe|unsubscribe`, `resources/templates/list`; notifications `initialized`, `cancelled` (cancels the CTS for that request id). Batches supported. `CallingClient` (:43, `AsyncLocal<string?>`, set in `CallTool` :192) = client name from `initialize`. `InstructionsFor` (:31) varies instructions per client; `Resources` (:34). Arguments are checked with `McpSchema.Check` first; bad arguments return a tool error (`isError`), an unknown tool a protocol error. |
| `McpTool.cs` | `McpTool(Name, Title, Description, InputSchema, ReadOnly)` (:10; `Describe(version)` adds `title` >= 2025-06-18 and `annotations` {title, readOnlyHint, openWorldHint:false} >= 2025-03-26), `McpToolResult` (:33; `Text`, `Error`, `Image`, `AddText`, `AddImage`, `AllText`), `interface IMcpTools { Tools; Call(name, args, ct) }` (:71). |
| `McpSchema.cs` | See 5.2. |
| `McpSession.cs:14` | Reads lines from a stream; each request answered on its own Task (ping answers while a screenshot is taken); writes under a lock; waits up to 30 s (:74) for pending answers on close. `McpGate` (:157): first line from a client must be `HEX-MCP <token> [tag]`; reply `OK`/`NO`; constant-time compare; tag (<=64 chars) = chat id of the Agents panel. `Admit` (:169) server side, `Knock` (:208) client side. |
| `McpPipeHost.cs:12` | Named pipe server (`PipeOptions.CurrentUserOnly`, :65), thread "mcp pipe" (:43), `MOST = 32` clients, `ADMIT_MS = 5000` to present the token; `makeServer(tag)` factory per client; `Clients` list for the status bar. |
| `McpDiscovery.cs:23` | Each server writes `<UserFolder>/mcp/<pid>.json` = `McpListing{Pid, Pipe, Token, Folders}` (:15; atomic write; stale files of dead pids removed). `PipeOf(pid)` = `hex-mcp-<user>-<pid>` (:31), `NewToken()` 32 random bytes hex (:34), `All()`, `For(folder, listings)` (:96, nearest-containing workspace), `Remove`. Rewritten when workspace folders change (polled every `MCP_FOLDERS_EVERY = 1.0` s in `UpdateMcp`). |
| `McpBridge.cs` / `.Launch.cs` | The stdio bridge, section 2.4. |

Security: off by default. Enabled by user setting `mcp_server` (never workspace-scoped) or `--window --mcp`, and auto-started when an Agents-panel chat starts (`StartAgent` calls `StartMcp`). Token-gated, current-user-only pipe, local machine only.

### 5.2 Schema helpers (`Hex.Core/Mcp/McpSchema.cs`)
Builders return `JsonObject` nodes: `String(desc)` :13, `Number` :15, `Integer` :17, `Flag` (boolean) :19, `OneOf(desc, choices...)` :21 (string enum), `Any(desc)` :25 (no type), `Object(IEnumerable<(name, schema)> props, params required)` :28 (always `additionalProperties:false`), `Empty()` :41, `Check(schema, args)` :47 (missing required; unknown property; kind mismatch for string/number/integer/boolean/object/array; enum membership; returns a message a model can act on or null). `McpArguments` (:92) extension methods on the call's `JsonObject`: `String(name)` :94, `Number` :99, `Integer` :106 (clamped cast), `Flag(name, fallback = false)` :108. A `JsonNode` may have only one parent, so never reuse one schema node in two tools (see `Max()` local function in `McpHarness.cs`).

### 5.3 Wiring in Dawn (`Hex.Dawn/Agents/DawnScene.Mcp.cs`)
- `BuildMcp` (:38, adds the robot status item), `McpWanted` (:54), `ApplyMcp` (:57), `StartMcp` (:69; creates `HexMcpTools`, token, `McpPipeHost(McpDiscovery.PipeOf(pid), token, MakeMcpServer)`, writes the discovery file; refuses when `!HexHost.Pipes` or another scene owns it via static `mcpOwner` :17), `StopMcp` (:115), `ForgetMcpFile` (:133, process-exit cleanup), `MakeMcpServer(tag)` (:136; numeric tag wraps tools in `HexMcpCaller`; instructions = localisation `mcp.instructions` + `HexAgentProfiles.InstructionsFor(client, usual)`; resources = `HarnessResources()`), `QueueMcp` (:152), `UpdateMcp(dt)` (:166; also runs `UpdateAgent`, `UpdateHarness`, `UpdateAgentsPanel`, robot status, discovery refresh).
- Threading: pipe threads call `HexMcpTools.CallAs` (:337) -> `HexMcpCall` (:255; wraps an `IEnumerator<McpToolResult?>`, a `TaskCompletionSource`, a deadline) -> `QueueMcp` enqueues on `mcpCalls`, wakes the window loop, brings Dawn forward if Dusk is showing. The editor thread `RunMcpCalls` (:221) steps up to 8 calls per tick; only one `mcpCurrent` at a time. `yield return null` = not yet, resume next tick (`NoteChange()` so the next tick is prompt); `yield return result` = done; running out without a result gives "said nothing"; exceptions become "went wrong". Timeout: tool's `Timeout` (default 30 s), or the call's `timeout` argument + 5 s (clamped 0.1..600). Cancellation is checked each step. A tool is therefore a coroutine on the UI thread.
- `HexMcpTool(Meta, Run, Timeout = 30, RunAway)` (:301): `Run` = coroutine; `RunAway : Func<JsonObject, CancellationToken, Task<McpToolResult>>` runs off the editor thread (harness tools via `Harnessed(...)`, `hex_agent_wait`, `hex_selftest`, `hex_harness_stats`). A long wait inside a coroutine blocks other editor-thread tools, which is why waits that do not touch the editor are RunAway.
- `HexMcpTools` (:319) indexes `McpToolList()` by name (:329). `HexMcpCaller` (:309, `CALLER = "_hex_caller"` :311): for tools whose name starts `hex_agent` called from a chat bridge, `CallAs` removes any client-supplied `_hex_caller` and injects the real chat id after schema validation (so `additionalProperties:false` does not reject it, and a client cannot forge it).
- `McpClients` (:~247), `McpTools` (:248) expose the server to the self test.

### 5.4 Tool catalogue (60 tools when written, more since; registry `DawnScene.McpWorkbench.cs:87` `McpToolList()`, order Screen, Editor, Workbench, Notebook, Agent, Agents, Harness, Terminal, Debug). Files are `Hex.Dawn/Agents/DawnScene.Mcp<Name>.cs`; line = the `new McpTool("hex_...")` declaration.
| Tool | Purpose | File:line |
| --- | --- | --- |
| `hex_screenshot` | PNG of the window or a region, optionally scaled | McpScreen.cs:29 |
| `hex_ui_tree` | Components, tree rows, status items, menus with ids and bounds | McpScreen.cs:39 (tree walk in `McpUiTree.cs`) |
| `hex_click` | Click by id / visible text / x,y; left, right, double | McpScreen.cs:50 |
| `hex_type` | Type text into whatever has the keyboard | McpScreen.cs:57 |
| `hex_key` | Key or chord(s) like `Ctrl+K Ctrl+S` | McpScreen.cs:62 |
| `hex_scroll` | Mouse wheel over a target (`McpScrolled.cs` finds scroll owners) | McpScreen.cs:67 |
| `hex_hover` | Rest the pointer, report what is under it + tooltip | McpScreen.cs:72 |
| `hex_open` | Open a file at line/col, make it current | McpEditor.cs:17 |
| `hex_documents` | Open tabs, current, modified | McpEditor.cs:25 |
| `hex_editor` | Current tab's text (or lines), path, language, caret, selection | McpEditor.cs:29 |
| `hex_set_selection` | Move caret / select a range | McpEditor.cs:37 |
| `hex_problems` | Problems panel contents | McpEditor.cs:46 |
| `hex_outline` | Outline of current file | McpEditor.cs:53 |
| `hex_find_in_files` | Text/regex search in workspace folders (unranked, file order) | McpEditor.cs:57 |
| `hex_definition` | Go to definition (HIDL or language server) | McpEditor.cs:68 |
| `hex_symbol_info` | Hover text for the name at a place | McpEditor.cs:75 |
| `hex_references` | Find references via language server | McpEditor.cs:82 |
| `hex_commands` | Palette commands that can run now | McpWorkbench.cs:17 |
| `hex_run_command` | Run a palette command by name | McpWorkbench.cs:22 |
| `hex_terminal_read` | Text of the panel terminal | McpWorkbench.cs:27 |
| `hex_terminal_send` | Send a line to the panel terminal, return output after a moment | McpWorkbench.cs:35 |
| `hex_run_task` | Run/list `tasks.hor` tasks | McpWorkbench.cs:44 |
| `hex_task_output` | Output/state of a task's terminal tab | McpWorkbench.cs:49 |
| `hex_settings_get` / `hex_settings_set` | Read / change a setting by key | McpWorkbench.cs:56 / :63 |
| `hex_wait_for` | idle, file_open, dialog, no_dialog, text, text_gone, status, problems, task_done (`timeout` default 10 s) | McpWorkbench.cs:70 |
| `hex_selftest` | Self-test suites in a separate Hex (RunAway, `SelfTestRun` :379) | McpWorkbench.cs:78 |
| `hex_notebook` | Read notebook cells and outputs | McpNotebook.cs:20 |
| `hex_notebook_edit` | set/insert/delete/move cells | McpNotebook.cs:28 |
| `hex_notebook_run` | Run cells in the kernel, wait | McpNotebook.cs:38 |
| `hex_notebook_kernel` | Interrupt / restart / shut down | McpNotebook.cs:46 |
| `hex_notebook_new` | Create + open a notebook | McpNotebookFiles.cs:14 |
| `hex_agent_hint` / `hex_agent_hint_remove` | Dismissible inline hint at a position (max 50/document, never saved) | McpAgent.cs:10 / :13 |
| `hex_agent_list` | Agent systems, models, chats, who you are | McpAgents.cs:31 |
| `hex_agent_spawn` | Start a background agent, returns id | McpAgents.cs:34 |
| `hex_agent_wait` | Wait for agents (RunAway; default 300 s, max 600; `mode:any`) | McpAgents.cs:43 |
| `hex_agent_read` / `hex_agent_send` / `hex_agent_stop` | Last reply or full chat / new turn or steer / stop with children | McpAgents.cs:50 / :56 / :59 |
| `hex_map` | Folder tree with languages and main types | McpHarness.cs:48 |
| `hex_files` | Files by part of name | McpHarness.cs:55 |
| `hex_symbols` | Declarations by name, ranked | McpHarness.cs:62 |
| `hex_search` | Ranked text/regex search | McpHarness.cs:71 |
| `hex_refs` | Whole-word uses outside comments/strings | McpHarness.cs:84 |
| `hex_read` | One declaration or a line range | McpHarness.cs:92 |
| `hex_skim` | Files' size, language, summary, outline | McpHarness.cs:102 |
| `hex_changes` | Files changed since an index generation | McpHarness.cs:109 |
| `hex_harness_stats` | Calls and chars vs the plain way | McpHarness.cs:114 |
| `hex_calls` | Callers (in) or callees (out) of a name as a tree to a depth, library callees folded; LSP callHierarchy, falls back to `hex_refs` | McpCode.cs (CodeTools) |
| `hex_implementations` | Implementations/overrides/derived types; LSP implementation, then typeHierarchy subtypes | McpCode.cs |
| `hex_types` | Bases (up) and derived (down) as a tree; LSP typeHierarchy | McpCode.cs |
| `hex_rename` | Rename by name across files, all or nothing, `dry_run`; LSP rename, written to disk (or editor for unsaved tabs) | McpCode.cs |
| `hex_fix` | Diagnostics + code actions for a line, `apply` by number/title; LSP codeAction (+resolve, commands) | McpCode.cs |
| `hex_diagnostics` | What servers say is wrong with files now, no build, opening them if needed | McpCode.cs |
| `hex_edit_symbol` | Replace or delete a whole declaration found by name (index `Extent`), errors after | McpCode.cs |
| `hex_projects` | Projects and uses/used-by from .csproj, package.json, Cargo.toml, go.mod, pyproject.toml, CMakeLists.txt | McpCode.cs, `HexCodeIndex.Projects.cs` |

The language-server tools take `symbol` (Name or Container.Name, found by `HexCodeIndex.Locate`, interface members without bodies too) or `path`+`line`. A file needn't be open: `HexLanguages.AgentQueries.cs` (`AgentServer`) opens it in its server with no tab, re-sends it when the disk changes, lets go after 180 s idle, and counts each publishDiagnostics per path (`AgentDiagnostics`) so `hex_edit`, `hex_write` and `hex_edit_symbol` (`check`, true by default) end with the errors the running server says after the change (a server not yet running is not started for that).
| `hex_terminal` | list / new / select / rename / close / interrupt terminal tabs | McpTerminal.cs:18 |
| `hex_terminal_run` | Run a command in a tab, wait, return output + exit code | McpTerminal.cs:28 |
| `hex_debug_breakpoint` / `_breakpoints` | Set/clear/list breakpoints (file need not be open) | McpDebug.cs:21 / :30 |
| `hex_debug_start` / `_step` / `_stop` | F5 build+start (waits), continue/over/in/out/pause, stop | McpDebug.cs:34 / :41 / :48 |
| `hex_debug_state` / `_variables` / `_evaluate` | State+stack, variables by scope, evaluate in a frame | McpDebug.cs:52 / :58 / :73 |
| `hex_debug_screenshot` | PNG of the debuggee's own window (Windows only) | McpDebug.cs:66 |

Support types: `HexAgentPointer.cs` (the synthetic pointer, click = move/press/release over ticks; real mouse movement takes the pointer back), `HexWindowCapture.cs` / `HexPictures.cs` (capture, PNG, scaling). Resource: `hex://harness/changes` (`HARNESS_CHANGES`, `McpHarness.cs:14`; list/read/subscribe at :142-146, `Watch()` while subscribed). Conventions: lines/columns are 1-based in tool arguments and 0-based inside; `McpPath(path)` (`McpEditor.cs:91`) resolves relative to the first workspace folder; `McpWhere(doc)` (:99) names a document.

### 5.5 Worked example: add a tool (modelled on `hex_agent_hint_remove` and `hex_documents`)
Real small tool, `Hex.Dawn/Agents/DawnScene.McpAgent.cs:13` and its body (:~46):
```csharp
// 1. the group method returns the tools; a new file is `internal sealed partial class DawnScene` in namespace Hex.Dawn
private IEnumerable<HexMcpTool> AgentTools() =>
[
    new(new McpTool("hex_agent_hint_remove", "Remove a code hint",
        "Remove a hint by id from the current or named open file.",
        McpSchema.Object([("id", McpSchema.String("The hint id.")), ("path", McpSchema.String("An open file, current by default."))], "id")), AgentHintRemove)
];

// 2. the body is a coroutine on the editor thread
private IEnumerable<McpToolResult?> AgentHintRemove(JsonObject args)
{
    var target = AgentHintTarget(args);                       // helper: path -> HexDocument via McpPath, else current
    if (target is null) { yield return McpToolResult.Error("The file is not open."); yield break; }
    target.Editor.AgentHints = [.. target.Editor.AgentHints.Where(hint => hint.Id != args.String("id"))];
    yield return McpToolResult.Text("Hint removed.");
}
```
Steps: (a) pick/make the group file; (b) `new(new McpTool(name, title, description-for-a-model, schema, ReadOnly: true|false), Method, Timeout: 20.0f)`; read-only no-arg tools use `McpSchema.Empty()` as `hex_documents` does (`McpEditor.cs:25`, body :132); (c) read args with `args.String/Integer/Number/Flag`; (d) `yield return null;` to wait a tick (after opening a file, waiting for a dialog); finish with `McpToolResult.Text(...)`/`.Error(...)`/`.Image(png, caption)` and `yield break` after errors; (e) register by adding `.. FooTools()` to `McpToolList()` (`DawnScene.McpWorkbench.cs:87`); a tool missing from that list does not exist; (f) off-editor work: `RunAway:` instead of `Run` (harness tools use `Harnessed(meta, (index, args, budget) => HexAnswer)` at `McpHarness.cs:129`, which also applies the client's budget; note `Run` is passed `null` there); (g) a tool whose name starts `hex_agent` can read the calling chat via `_hex_caller` (`HexMcpCaller.CALLER`); (h) schema nodes per tool must be fresh objects; (i) tests: `Hex.Tests/McpTests.cs:14` (protocol with a `StandIn : IMcpTools`), `HexAgentSystemsTests.cs`, `HexHarnessTests.cs`, and the in-editor suite `Hex.Dawn/Testing/DawnScene.SelfTest.Mcp.cs` (calls tools like a client; run `Hex --selftest mcp --exit`); (j) user-visible additions: update `docs/agents.md` and the `mcp.instructions` localisation text (`Hex.Dawn/Assets/hex/localisation/en-gb.hor`), and the per-client `instructions` in `hex-extensions/claude_code` / `codex` if the tool should be preferred.

### 5.6 A call end to end (handshake, transport, answer)
1. Client (Claude Code etc.) starts `Hex.exe --mcp` over stdio. `McpBridge.Run` reads `<UserFolder>/mcp/*.json` (`McpDiscovery.All`), picks a listing for the folder (`Select`), else launches Hex under the mutex and polls for the file.
2. Bridge opens `NamedPipeClientStream(".", listing.Pipe, InOut, Asynchronous | CurrentUserOnly)`, 5 s connect, then `McpGate.Knock(pipe, token, chat)` writes `HEX-MCP <token> [chatId]\n` and reads `OK`/`NO`. Everything after that line is raw MCP, byte-pumped both ways by `Pump` (so the bridge does not parse JSON-RPC; the Hex side does).
3. Server side: `McpPipeHost.Listen` (thread "mcp pipe") accepts, gives the client 5 s (`ADMIT_MS`) to pass `McpGate.Admit` (constant-time token compare; tag <= 64 chars), calls `MakeMcpServer(tag)` and wraps the stream in an `McpSession` (a reader loop; one Task per request, writes under a lock).
4. `initialize` -> `McpServer.Initialize` negotiates a version from `McpProtocol.Versions`, stores `Client` (name + version, used by `CallingClient` and harness profiles), returns capabilities (`tools`, plus `resources` when `Resources` is set) and `instructions` (from `InstructionsFor(Client)` when set). `notifications/initialized` is accepted silently.
5. `tools/list` -> every `McpTool.Describe(negotiatedVersion)`. `tools/call` -> `CallTool` (:179): unknown tool = protocol error, `McpSchema.Check` failure = tool error with `isError`, otherwise `tools.Call(name, args, cts.Token)` (the cancellation source is stored per request id for `notifications/cancelled`). Result = `McpToolResult.ToJson()` = `{content:[{type:"text"|"image",...}], isError}`.
6. Resources: the list is a single entry, `hex://harness/changes` (`McpHarness.cs:142`). `resources/subscribe` calls `McpResources.Subscribe(uri, onChange)`, which `Watch()`es the index and sends `notifications/resources/updated` through `McpServer.Notify`.
7. On pipe close `McpSession` waits up to 30 s for in-flight tasks, the host removes the client from `Clients` (status bar count) and the editor's `mcpChanged` flag updates the robot item.

### 5.7 Argument cheat sheet (names as in the schemas; check the schema in the file for descriptions and defaults)
- Screen: `hex_screenshot` x,y,width,height,scale; `hex_ui_tree` interactive,find,root,max_depth,layouts,hidden; `hex_click` id|text|x,y + button/double; `hex_wait_for` condition,value,timeout.
- Editor: `hex_open` path,line,column; `hex_editor` start_line,end_line,numbers; `hex_problems` path,errors_only; `hex_find_in_files` query,regex,match_case,whole_word,folder,most; `hex_symbol_info`/`hex_references`/`hex_definition` line,column (+ optional path).
- Workbench: `hex_commands` find; `hex_run_command` name; `hex_terminal_read` tab,lines,scrollback; `hex_task_output` name,lines; `hex_settings_get` key,find; `hex_selftest` suites,timeout.
- Harness (all accept `max_chars`): `hex_map` under,depth; `hex_files` query,most; `hex_symbols` query,kind,path,most; `hex_search` query,regex,match_case,whole_word,glob,context,most; `hex_refs` name,glob,most; `hex_read` path,symbol,start_line,end_line,numbers; `hex_skim` paths,depth; `hex_changes` since.
- Agents: `hex_agent_spawn` prompt,name,agent,model,options; `hex_agent_wait` ids,mode,timeout; `hex_agent_read` id,full,most (default 8000, clamp 500..60000); `hex_agent_send` id,text; `hex_agent_stop` id.
- Debug: `hex_debug_state` frames; `hex_debug_variables` frame,depth,filter; `hex_debug_screenshot` scale,raise; `hex_debug_start` project,wait_seconds; `hex_debug_step` action,wait_seconds.
- Notebook: `hex_notebook` path,cells,outputs (every notebook tool takes `path` and opens the notebook first).

### 5.8 Debugging the MCP server itself
- Protocol-level: `Hex.Tests/McpTests.cs` drives `McpServer`/`McpSession`/`McpPipeHost` with a `StandIn : IMcpTools` (`echo`, `boom`, `slow`) and a real pipe; extend it for framing, batches, cancellation, version negotiation and the gate.
- Editor-level: `Hex --selftest mcp --exit` (`Hex.Dawn/Testing/DawnScene.SelfTest.cs:430` registers suite `mcp`; the body is `DawnScene.SelfTest.Mcp.cs`) connects a client to the pipe with the token and calls screenshot, ui tree, click, type, a command, problems and `hex_wait_for`. Self-test mode never starts the server by setting (`McpWanted` excludes `SelfTest`), the suite starts it itself.
- Live: a Hex with the setting on writes `<UserFolder>/mcp/<pid>.json`; `Hex --mcp-connect --folder X` gives a stdio endpoint you can pipe JSON-RPC lines into. Bridge diagnostics are on stderr. A stale file for a dead pid is cleaned by `McpDiscovery.All`/`Running(pid)`.
- Symptoms: "no matching Hex MCP server" = `--mcp-connect` found no listing for that folder (the editor must be open on it with the server on); a tool "timed out after N seconds" = a coroutine kept yielding null (see `HexMcpCall.Step`); "isn't an argument of this tool" = the schema lacks that property.

## 6. The harness (symbol index), `Hex.Core/Harness/`
Purpose: cheap, ranked code lookup so agents avoid grep and whole-file reads. Plan and validation: `docs/agent-harness-plan.md`, `docs/agent-harness-validation.md`.

### 6.1 Index internals
- `HexCodeIndex` (`HexCodeIndex.cs:60`; partials `.Search .Symbols .Skim .Read .Changes .Cache .Server .Answers .GoToAll`). Dawn creates it lazily in `DawnScene.Harness` (`McpHarness.cs:~32-40`): `new HexCodeIndex(() => harnessRoots, selfTest ? null : <UserFolder>/harness)`; `SaveCache()` hooked to `ProcessExit`. `harnessRoots` = workspace folders, refreshed by `McpFolders()`.
- `HexIndexSnapshot(generation, roots, files)` (:7) is immutable. `Current(force)` (:85) calls `Look()`. `Look(force)` (:98): locked; throttled by `StaleAfter` (2000 ms, :68) unless roots changed; enumerates `HexSearch.TextFilesUnder(roots)` (honours `files_exclude`, `search_exclude`, .gitignore); compares length + mtime to the previous snapshot; otherwise consults the disk cache; otherwise reads new files in `Parallel.ForEach` (:139, every core). Generation +1 when any file is added/removed/changed or server symbols arrive; `Changed` event (:74). Every answer ends `(index generation N)`; `HexChange(Generation, Relative, What)` (:49) feeds `hex_changes since=N` (`HexCodeIndex.Changes.cs`, `MOST_CHANGES = 5000`). `Watch()/Unwatch()` (`Changes.cs:84/91`) run a 2 s timer calling `Look()` while someone subscribed to the resource.
- `HexIndexedFile` (`HexIndexedFile.cs:46`): path, relative, length, mtime, `LineCount`, `Symbols` (`HexSymbol(Name, Kind, Container, Line, Column, Depth)` :7), `FromServer`, lazy `Lines`. `HexSymbol.Of(entries)` (:19) converts `OutlineEntry`s. `OutlineOf(path, text)` (:94) calls `HexOutline.Fresh` and returns `[]` on any exception. `LanguageOf(ext)` (:112) labels languages for Skim.
- Server symbols: Dawn asks the language server for each open, saved (not Modified) file every `HARNESS_SYMBOLS_EVERY = 3.0` s (`UpdateHarness`, `McpHarness.cs:17,~183-193`; `languages.AskSymbols`) and passes them to `TakeServerSymbols(path, length, written, symbols)` (`HexCodeIndex.Server.cs:19`); `FromServer(roots)` (:52) flattens file/namespace nodes and strips `(params)` from csharp-ls names; `WithServerSymbols` (`HexIndexedFile.cs:90`). Server symbols win over outline symbols until the file changes.
- Cache: `HexCodeIndex.Cache.cs`; file `index-<hash of roots>.bin`, magic `CACHE_MAGIC = "HEXIDX3"` (:13); stores path, length, mtime, lines, symbols, `FromServer` (no text). Saved at most every `CACHE_EVERY = 5000` ms (:16) and on process exit. `FromCache` counts hits. Cached symbols are keyed by length+mtime, so if you change an outliner you MUST bump the magic or old symbols stay until files change.
- Answers: `HexAnswer(Text, Plain, Failed)` (`HexBudget.cs:66`, `Wrong(why)` :68); `Ask(tool, asked, make)` (`Answers.cs:19`) memoises per (tool, args, generation), 64 entries. `HexBudget(most)` (:10; min 400, `ROOM_FOR_THE_END = 160`, `Line(...)` returns false when full, `Finish(howToGetMore)` adds "N more ... left out"). `HexHarnessStats` (:11) backs `hex_harness_stats` (`Plain` = what the grep-and-read-whole way would have cost).
- Queries: `Map` (`Skim.cs:71`), `Files` (:116), `Skim` (:13), `Changes` (:177), `Symbols(query, kind, under, most, budget)` (`Symbols.cs:88`; ranking exact, other case, prefix, capitals like `QM`, substring; `Container.Name`), `Search(query, regex, matchCase, wholeWord, glob, context, most, budget)` (`Search.cs:20`), `References` (:47; whole word, outside comments/strings, by text, no language server needed), `Read(path, symbol, start, end, numbers, budget)` (`Read.cs:15`). `GoToAll.cs` backs the palette's go-to-anything.
- Budgets and profiles: `HexAgentProfiles` (`HexAgentProfiles.cs:16`; `HexAgentProfile(Id, Name, Clients, Budget, Instructions, Extension)` :10; `USUAL_BUDGET = 8000` :19; `Set` :27, `Forget(extension)` :34, `For(client)` :41 matches the MCP client name by substring of `Clients`, `BudgetFor(client, asked)` :50 default 8000, clamped 400..200000, `InstructionsFor(client, usual)` :54 appends the profile's text to Hex's). Profiles are registered by extensions through `hex.agent({...})`.

### 6.2 How a harness tool call flows
Client `hex_symbols` -> `McpServer.CallTool` (schema check, sets `CallingClient`) -> `HexMcpTools.CallAs` -> `RunAway` of `Harnessed(...)` -> `Task.Run` on a thread-pool thread: `budget = HexAgentProfiles.BudgetFor(CallingClient, max_chars)`, `index.Symbols(...)` -> `HexAnswer` -> `McpToolResult` (plain text, failed -> error) -> stats recorded. No editor-thread time is used.

### 6.3 Outliners
`HexOutline` (`Outline/HexOutline.cs:19`, partial) is a static class with two entry points: `Outline(text, path?)` (:25; for the editor; keeps the last result in one static `Kept` slot, same string reference = cached) and `Fresh(text, path)` (`HexOutline.Fresh.cs:8`; nothing kept, safe on any thread, used by the harness). Both dispatch: extension `.hor` -> `OutlineOf(text)` (:44; the real HIDL lexer `Lexer.Tokenize(text, lenient: true)`, works on files that do not parse; caps 3000 entries); everything else -> `OutlineOfOther(text, extension)` (`HexOutline.Other.cs:27`), a line-by-line regex switch capped at 3000 entries: `.cs` (:40; `CsType`, `CsMember`, `NotNames` filter), `.d/.di` (:54; types, functions, fields, `typeDepth`), `.slang/.hlsl` (:86), `.py` (:96), `.js/.ts/.mjs/.jsx/.tsx` (:101), `.md/.markdown` (:112; headings, fenced blocks skipped), `.json` (:119), `.yaml/.yml` (:124). Unknown extensions yield no symbols (the file is still searchable). `OutlineEntry(Name, Line0, Column, Depth, Icon)` (:16; icon names like `symbol-class`, `symbol-method`, `symbol-property`, `symbol-field`, `symbol-interface`, `symbol-enum`, `symbol-struct`, `symbol-variable`).

### 6.4 Adding an outliner for a new language (say Rust `.rs`)
1. In `HexOutline.Other.cs` add `private static readonly Regex Rust = new(@"^(\s*)(?:pub(?:\([^)]*\))?\s+)?(fn|struct|enum|trait|impl|mod)\s+(\w+)", RegexOptions.Compiled);` next to the others (:11-22).
2. Add `case ".rs": if (Rust.Match(row) is { Success: true } m) entries.Add(new OutlineEntry(m.Groups[3].Value, line, m.Groups[3].Index, Depth(m.Groups[1].Value, 4), m.Groups[2].Value switch { "struct" => "symbol-struct", "enum" => "symbol-enum", "trait" => "symbol-interface", "fn" => "symbol-method", _ => "symbol-class" })); break;` inside the `switch (extension)` (:39). `Depth(indent, width)` (:135) turns leading whitespace into nesting. `line` is 0-based.
3. Label it in `HexIndexedFile.LanguageOf` (:112), else the language shows as the bare extension.
4. Bump `CACHE_MAGIC` (`HexCodeIndex.Cache.cs:13`, e.g. `HEXIDX4`) so existing caches do not keep the old empty symbol lists.
5. Is the file text and indexed at all? `HexSearch.TextFilesUnder` decides by extension/content sniffing and exclude settings; a language server entry in `language_servers.hor` with `extensions` improves symbols once files are open (server symbols override outlines).
6. Because `Outline()` shares `OutlineOfOther`, the editor's outline view and breadcrumbs pick it up too. Test in `Hex.Tests/HexHarnessTests.cs`; verify with `hex_skim` / `hex_symbols` on a `.rs` file.

## 7. Agents subsystem
Two meanings: (1) agents *driving Hex* over MCP (section 5); (2) agents *run by Hex* from the Agents panel.
- Agent systems are not hard-coded, they are extensions (`hex-extensions/claude_code`, `codex`) calling `hex.agent({ id, name, clients, budget, instructions, run: { program, arguments(prompt, chat), stdin, message(text, chat), on_event(chat, event), options, icon, install } })` (`Hex.Dawn/Extensions/HexExtensions.Agents.cs:32`). `HexAgentProfiles.Set` stores the profile; `agentRuns[id] = (extension, run)` stores the run object.
- UI: `Hex.Dawn/Agents/DawnScene.AgentsPanel*.cs` (Chats, Motion, Options, Rows, Run); the Agents side is its own layout `agents_view.hor` (see `dawn-app.md`). `DawnScene.Agent.cs` = "Explain/Change with Agent" (selection draft, <= 32000 chars).
- `StartAgent` (`AgentsPanel.Run.cs:83`): finds the program (`HexAgentProcess.Find` :33, PATHEXT/.cmd), starts MCP if off, calls the extension's `arguments`, then `HexAgentProcess.Start` (:112; redirected stdio UTF-8, `.cmd/.bat` via `cmd /d /s /c`, two reader threads into `Lines` ConcurrentQueue, stdin kept open when the run has `message`, for steering; `Send`, `CloseInput`, `Stop`). Working dir = first workspace folder. Run records live in `agentRuns` (`AgentsPanel.Run.cs:26`).
- Lines go to the extension's `on_event(chat, event)`; it reports through `HexAgentChatApi.Of(chat, folder, ...)` (`HexAgentChatApi.cs:16`): `id, folder, session, option, text, thinking, note, error, tool, tool_done, usage, taken, done`. `HexAgentChat` (`HexAgentChat.cs:47`) holds rows (`HexAgentEntry` :13; `HexAgentEntryKind` User/Text/Thinking/Tool/Note/Error; `HexAgentToolState`), session id, token/cost totals, max 2000 rows. Editor thread only, no locking. `HexJson` (:14) converts JSON <-> HIDL values (`json_parse` uses it).
- The agent reaches Hex via `hex.mcp_server(chat)` = `{command: Hex.exe, args: ["--mcp-connect", "--folder", firstFolder, "--agent-chat", id]}` (`HexExtensions.Agents.cs:50`).
- Agents starting agents (`McpAgents.cs`): `AGENTS_DEEPEST = 6`, `AGENTS_AT_ONCE = 12` (:16), wait default 300 s / max 600 (:19), reply cut at 6000 chars; `hex_agent_wait` is RunAway over `agentStatuses` published each tick (`PublishAgentStatuses`).
- Client-specific budget/instructions come from the profile whose `clients` substring-matches the MCP client name.

## 8. Extension model and the `hex.*` host API
### 8.1 Layout and discovery
- An extension is a folder with `extension.hor` (`HexExtension.FILE`, `HexExtension.cs:40`; id = folder name :46). Built-in: `Hex.Dawn/Assets/hex/extensions/` (the designer). User: `<exe>/extensions/` (`HexExtensions.OwnFolder`, `HexExtensions.Catalog.cs:64`; `OWN_EXTENSIONS`/`BUILT_IN_EXTENSIONS` consts `HexExtensions.cs:18`). Catalog: `hex-extensions/` at the repo root (git submodule, `CATALOG_SUBMODULE` :21; sub-folders claude_code, codex, emmet, hello_dolly, move_editor, pretty_json, snippets, text_tools, todo; README inside). No build step: copy the folder, bump `version`.
- Metadata: `let extension = { name, description, version, author, settings: { key: { title, about, kind: text|path|number|flag|choice, default, ... } } }`. `version` drives update cards, bump on every change. Extension settings live in the `extensions` section of the settings file.

### 8.2 Lifecycle (`Hex.Dawn/Extensions/HexExtensions.cs`)
1. Discovery: all folders found once before any file opens; built-ins whose `Enabled` load immediately (`HexExtensions.cs:~95`).
2. `StartAll()` (:103) after the editor is up and a file open: for each enabled one `Start(ext)` (:124): forgets old project templates/timers, `Load` (:113; runs `extension.hor` in its own HIDL runtime with the `hex` object from `HexOf`, `HexExtension.Load` :~88), then `Raise("on_start", hex)`. Failure -> `Failed(ext)` shows an error card (:~150) and the extension is off for the run.
3. Events: `Raise(name, args...)` (:~139) calls `name(hex, args...)` on every enabled, healthy extension that defines it. Hooks used: `on_start(hex)`, `on_open(hex, file)` (Documents.cs:212, Code.cs:278), `on_save(hex, file)` (Documents.cs:485), `on_change(hex, file)`, `on_tab`, `on_file_changed`, `on_setting(hex, key, value)` (`HexExtensions.Settings.cs:~28`). Palette commands: `Commands()` (:~160).
4. Limits: every call runs under a 1 s CancellationTokenSource (`HexExtension.cs:166`); past that or on an exception the extension is marked `Failed`. Saving any `.hor` in its folder reloads it. `Switch(ext, on)` toggles and persists `DisabledExtensions`.
5. `HexOf(ext)` (`HexExtensions.Api.cs:16`) builds the `hex` object once per extension from the groups below.

### 8.3 The `hex` object (assembled at `HexExtensions.Api.cs:19-23`; all in `Hex.Dawn/Extensions/`; user docs `docs/extensions.md`)
| Group (method, file:line) | Members |
| --- | --- |
| `Talking` Api.cs:33 | `title_note(text)`, `say(text)`, `notify(text, error?)`, `command(name, fn, key?)` (palette entry, optional key like `Ctrl+Shift+K`, replaces same-named) |
| `Screens` Api.cs:55 | `view({id,...})`, `panel(...)`, `window(...)` (layouts from the extension's folder, `IExtensionScreens`) |
| `Files` Api.cs:94 | `read`, `write`, `exists`, `find_up`, `load` / `try_load` (load a `.hor`), `document`, `file`, `sprite` |
| `Asking` Api.cs:111 | `setting(key)`, `set_setting(key, v)`, `pick_file`, `pick_folder` |
| `Sides` Api.cs:143 | `show_side`, `side_shown` |
| `Programs` Api.cs:159 | `start(program, args...)` (run a process, gated by `HexHost`) |
| `About` Api.cs:167 | `version` (Hex's), `name`, `folder` |
| `Prompting` More.cs:23 | `menu(...)`, `ask(...)`, `prompt(...)` |
| `Edits` Edits.cs:20 | `edit`, `insert`, `remove`, `rename`, `reorder`, `edits`, `undo_file`, `redo_file` (backed by `Hex.Core/Editing`) |
| `Problems` Problems.cs:16 | `problems(...)` (read/contribute diagnostics) |
| `Watching` Files.cs:24 | `watch`, `open`, `copy`, `copied` |
| `Timers` Timers.cs:22 | `every(seconds, fn)`, `after(seconds, fn)` (min `SHORTEST_TIMER`; `fn(hex)`) |
| `Caret` Caret.cs:18 | `caret`, `replace_text`, `selection`, `select`, `lines` |
| `Agents` Agents.cs:30 | `agent({...})`, `json_parse`, `temp` (folder), `mcp_server(chat)`, `agents()`, `workspace()` (folders), `executable` (Hex.exe), `home`, `json_set(file, keys, value)`; also `project_template` (Projects.cs:14) |
Function bodies use `Natives.Action/Function` over `args.Text/Number/Bool/Object/Callable(i)`; errors via `throw args.Wrong("...")`. To add a member: add a `("name", Natives.Function(...))` tuple to the fitting group (or a new group method) and, if new, include it in `HexOf` (Api.cs:19); document it in `docs/extensions.md`; add a case in `Hex.Dawn/Testing/DawnScene.SelfTest.Extensions.cs`. Feature-test with `hex.agent == null` guards (as `claude_code` does) when an API may be missing.

### 8.4 Walkthrough: `hex-extensions/hello_dolly/extension.hor` (v1.2.0)
`let extension = {...}` metadata; `welcome_greetings` list (a top-level `let`, usable from functions); `let greeting = { shown: "" }` mutable state in an object (top-level values are reloaded when the file is saved); `func greet(hex)` picks `math.pick(...)` different from the last and calls `hex.title_note(next)`; `func on_start(hex)` (called once the editor is up) greets, then `hex.every(60, greet)` (timer, `fn(hex)` signature fits `greet`) and `hex.command("Another greeting", greet)` (palette entry under the extension's name). Nothing is compiled.

### 8.5 The agent-system example: `hex-extensions/claude_code/extension.hor` (v1.5.0)
`hex.agent({ id, name, clients: ["claude-code"], budget, instructions, run: {...} })` (:54; guarded by `hex.agent == null` at :41). `run.arguments(prompt, chat)` builds `claude -p --input-format stream-json --output-format stream-json --replay-user-messages --verbose --mcp-config <temp>/claude-mcp-<chat>.json --permission-mode ... [--allowedTools] [--model] [--resume session]`, writing the config with `hex.json_set(config, ["mcpServers","hex"], hex.mcp_server(chat))`; `on_event(chat, event)` maps stream-json to `chat.text/thinking/tool/tool_done/usage/session/done/error`. Palette commands "Connect Claude Code to this folder" (writes `.mcp.json`) and "Teach Claude Code the Hex tools" (marked block `<!-- hex tools -->` in CLAUDE.md). `codex` does the same with `codex exec --json`, a `--mcp-connect` entry in the `~/.codex` TOML and AGENTS.md.

## 9. Conventions and gotchas
- Style: long conversational explanatory comments, constants UPPER_CASE, `internal` everywhere, partial classes by feature (`DawnScene.X.cs`, `HexCodeIndex.X.cs`).
- NativeAOT: use the `JsonNode` DOM only; avoid `System.Text.Json` serializer, `Activator`, reflection (IL2026/IL3050 are errors in Core). `Add<JsonObject>` would serialise; cast to `(JsonNode)` (see `McpServer.Handle`, `McpSession.Send`).
- Threading: the editor thread owns UI state; Core classes marked blocking (Git, DotnetProjects, harness) must run in the background. Kernels, agent processes, MCP pipes post to queues drained per tick. `Engine.WindowManager.Wake()` wakes an idle loop, `NoteChange()` requests another tick (an idle tick looping without it burned a core during `hex_wait_for`, see `RunMcpCalls`).
- A tool is a coroutine; never block inside one; one editor-thread call at a time.
- Schema nodes cannot be shared between tools. `Object` always forbids extra properties, so a new argument must be in the schema or the client gets "isn't an argument of this tool".
- `--mcp` is always a stdio bridge unless `--window` or `--selftest`; the bridge has no logger and writes only protocol to stdout. Never print to stdout in bridge code.
- Settings: `mcp_server` can never come from a workspace file. The setting file is HIDL, not JSON.
- Changing an outliner or the index format without bumping `CACHE_MAGIC` leaves stale symbols.
- Harness answers are memoised per generation; a test that edits files must call `Look(force: true)`/wait for a new generation.
- `HexHost` flags: Android has no processes/pipes; check `NoProcesses`/`Pipes` before spawning or listening.
- Hex self-builds rename locked files to `*.old`; run suites from the real `Hex/bin/...` so `hex-extensions` are beside the exe (see `testing-and-shipping.md`).
- Hex lives in a workspace with separate repos: `horizon/` (engine + Horizon.UI), `hex/`, `Fighter2D/`. Engine code (LSP client `Horizon.Tooling`, `TerminalFiles`, HIDL runtime) is under `horizon/`; Hex.Core references it by project.
- Do not drive or launch Hex while editing docs; tests go through `hex_selftest` or `Hex --selftest`.

## 10. Where to look for X
| Need | Look at |
| --- | --- |
| Process start / args | `Hex/Program.cs`, `Hex.Dawn/DawnProgram.cs`, `HexLaunch.cs`, `HexOptions.cs`, `Hex.Dusk/DuskProgram.cs` |
| Which mode (Dawn/Dusk), swap, single instance | `Hex.Shell/HexIdentity.cs`, `HexModes.cs`, `HexInstance.cs` |
| Capability flags per platform | `Hex.Core/HexHost.cs`, `Hex.Android/` |
| MCP protocol / framing | `Hex.Core/Mcp/McpServer.cs`, `McpSession.cs`, `McpProtocol.cs` |
| MCP auth / discovery / bridge | `McpGate` (`McpSession.cs:157`), `McpDiscovery.cs`, `McpPipeHost.cs`, `McpBridge*.cs` |
| Add or change an MCP tool | `Hex.Dawn/Agents/DawnScene.Mcp*.cs`, registry `DawnScene.McpWorkbench.cs:87` |
| Tool schema helpers | `Hex.Core/Mcp/McpSchema.cs` |
| MCP threading / timeouts | `DawnScene.Mcp.cs` (`RunMcpCalls` :221, `HexMcpCall` :255, `CallAs` :337) |
| Code index / search tools | `Hex.Core/Harness/HexCodeIndex*.cs`, `DawnScene.McpHarness.cs` |
| Outline per language | `Hex.Core/Outline/HexOutline*.cs`, `HexIndexedFile.LanguageOf` |
| Per-client budgets and instructions | `HexAgentProfiles.cs`, extensions' `hex.agent`, `mcp.instructions` in `localisation/en-gb.hor` |
| Agents panel / spawning CLIs | `Hex.Dawn/Agents/DawnScene.AgentsPanel*.cs`, `Hex.Core/Agents/*` |
| Extension API (`hex.*`) | `Hex.Dawn/Extensions/HexExtensions.*.cs`, `docs/extensions.md` |
| Extension load / hooks / failures | `HexExtensions.cs`, `HexExtension.cs` |
| Extension catalog sync | `Hex.Core/Extensions/HexExtensionCatalog.cs`, `HexExtensions.Catalog.cs`, `hex-extensions/README.md` |
| Settings keys / format / locations | `Hex.Core/Settings/HexSettings*.cs`, `TerminalFiles.UserFolder`, `Hex.Shell/Settings/EditorSettingsCatalog.cs` |
| Language servers | `Hex.Dawn/Assets/hex/language_servers.hor`, `HexServerCatalog.cs`, `Hex.Dawn/Languages/` |
| HIDL analysis / `--lsp` | `Hex.Core/Hidl/` |
| MSBuild / .NET / CMake projects | `Hex.Core/Projects/` |
| Tasks (`tasks.hor`) | `Hex.Core/Tasks/`, `Hex.Dawn/Tasks/`, repo `tasks.hor` |
| Notebooks / kernels | `Hex.Core/Notebooks/Kernel*.cs`, `ZmtpSocket.cs`, `NotebookFile.cs`, `Hex.Dawn/Notebooks/` |
| Git | `Hex.Core/Git/`, `Hex.Dawn/Git/` |
| Find in files, ignore rules | `Hex.Core/Search/` |
| Debugger | `Hex.Dawn/Debugging/`, `DawnScene.McpDebug.cs` |
| Editing HIDL by value path | `Hex.Core/Editing/` |
| Backups, local history, trash | `Hex.Core/Documents/HexBackups.cs`, `Hex.Core/Workspace/` |
| Self-test / unit tests | `testing-and-shipping.md`, `Hex.Tests/` (`McpTests.cs`, `HexHarnessTests.cs`, `HexAgentSystemsTests.cs`), `Hex.Dawn/Testing/DawnScene.SelfTest.Mcp.cs` |
| Build/run Hex itself | repo `tasks.hor`, `HexSelfBuild.cs` |

## 11. Recipes
**Add a CLI flag.** (1) Dawn: add `ARGUMENT_X` next to `DawnProgram.cs:11-28`, a field on `HexOptions` and fill it where `Mcp: args.Contains(ARGUMENT_MCP)` is built (~DawnProgram.cs:140); flags that take a value must also be listed in `HexLaunch.withValue`/`withValues` (`HexLaunch.cs:16-19`) or a bare-path parser will read the value as a file. (2) Dusk: constant in `DuskProgram.cs:13-25`. (3) Update the `HELP` text at the end of `Hex/Program.cs`. (4) If the flag must run before any window (like `--lsp`), handle it in `Program.Main` before `HexLog.Initialize` (:54) so no logger/console is created. (5) Document it in `dawn-app.md` section 2.

**Add a setting.** Field on `HexSettings`, row in `HexSettings.Table.cs` (copy `Flag("mcp_server", ...)` :83), catalogue entry in `Hex.Shell/Settings/EditorSettingsCatalog.cs` + localisation text, decide whether it may be workspace-scoped (add to `WorkspaceKeys`, `HexSettings.Workspace.cs:18`; never for anything that grants capability such as `mcp_server`), read it in Dawn through `settings.X`. Self-test suite `settings` exercises the catalogue.

**Add a language server entry.** Add a record to `Hex.Dawn/Assets/hex/language_servers.hor` under `servers` (command, args, language, extensions, about, install, update, repo, version); `HexServerCatalog` (`HexServerCatalog.cs:71`) parses it into `HexServerEntry` (:29); Dawn's `Hex.Dawn/Languages/HexLanguages*.cs` starts it for matching extensions and prompts to install. For symbols in the harness no more is needed (open, saved files are asked every 3 s); add an outliner (6.4) so unopened files have symbols too.

**Add a Core service used by Dawn.** Put the window-free logic in the matching `Hex.Core/<Folder>/`, keep it `internal`, free of reflection JSON, honour `HexHost` flags, unit test it in `Hex.Tests` without UI, then call it from a `DawnScene.<Feature>.cs` partial: blocking work on a background `Task` with the result posted back through the scene's queue (editor-thread-only state, see `dawn-app.md` section 8).

**Make an extension see something new.** Add the member to the right `hex` group (8.3), expose Core logic through it (not the other way round), keep each call under the 1 s cap (heavy work -> an `after`/background route through a Dawn helper), and extend `docs/extensions.md`.

**Add an agent system.** No Dawn code: create `hex-extensions/<id>/extension.hor` with `hex.agent({ id, name, clients: [<substring of the MCP client name>], budget, instructions, run: { program, arguments, on_event, options, ... } })`, use `hex.mcp_server(chat)` for the MCP config, map the CLI's event stream to `chat.text/thinking/tool/tool_done/usage/session/done/error`. `clients` also controls which MCP callers get this profile's budget and instructions (`HexAgentProfiles.For`).

## 12. Glossary
- **Dawn / Dusk**: the editor mode / the terminal mode of the single `Hex.exe`; swap in one window with Ctrl+Alt+Shift+D.
- **HIDL, `.hor`**: the engine's declarative language; layouts, settings, tasks, keys, localisation and extensions are all `.hor`.
- **Harness**: the symbol/search index (`HexCodeIndex`) plus its MCP tools; it answers in budgeted plain text and stamps an index generation.
- **Generation**: monotonically increasing index version; `hex_changes since=N` lists what moved.
- **Profile**: `HexAgentProfile`, registered by an extension through `hex.agent`, selected by MCP client name; sets answer budget and extra instructions.
- **Bridge**: the `Hex --mcp` / `--mcp-connect` stdio process between an MCP client and the pipe of a running Hex.
- **Listing**: `McpListing{Pid, Pipe, Token, Folders}` in `<UserFolder>/mcp/<pid>.json`; how a bridge finds the right editor.
- **Coroutine tool**: an `IEnumerable<McpToolResult?>` stepped once per editor tick; `null` = wait, a result = finished.
- **RunAway tool**: an MCP tool run off the editor thread as a `Task`.
- **Chat**: one conversation of the Agents panel (`HexAgentChat`); its numeric id is the tag a per-chat bridge presents at the pipe door.
- **Settings folder / UserFolder**: `TerminalFiles.UserFolder`, next to the exe for Hex; holds settings, keys, `mcp/`, `harness/`, plugins, sessions.
- **Self build**: building Hex over the exe that is running; locked files are renamed `*.old` and cleaned next start.
