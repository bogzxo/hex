# Context: Local AI, prediction, AI Edit, Hex Link, agents (Nov 2026 work)

This is a map for agents working on the subsystems added in Hex 3.4.7 and 3.5.0. Read it alongside `core-and-mcp.md`, `dawn-app.md` and `testing-and-shipping.md`. The design docs carry the detail. This page says where things live, how they connect, and what bit us.

## Design docs

| Area | Doc |
|---|---|
| Next-edit predictions (history, renames, far edits, model kinds, FIM and chat formats) | `docs/next-edit-plan.md` (its Status section lists the keys) |
| AI Edit (Ctrl+I, the AI context submenu) | `docs/ai-edit-plan.md` |
| Local agent (a local model as an agent system) | `docs/local-agent-plan.md` |
| Model library, picker, roles, presets | `docs/local-ai-setup-plan.md` |
| Local AI window (Simple/Advanced, setups, profiles, help text) | `docs/local-ai-window-plan.md` |
| Hex Link (sync, holds, merges, chat, ports, AI over Link, undo) | `docs/hex-link-plan.md` |
| macOS native work and its results log | `docs/macos-native-plan.md` |

## Local AI

- **llama.cpp: `Hex.Core/LlamaCpp`.**
  - `LlamaCppServer` starts, adopts, stops and measures the server. Starts are serialised by a generation counter.
  - Hex runs ONE router: `--models-preset hex-models.ini --models-max 1`, so one model is in VRAM and the router swaps models on request.
  - `LlamaCppServerConfig` is a record with value equality. A reference-equal list once restarted the server every frame and leaked 122 processes, so keep equality by value.
  - Adopting a running router works when one of its models has the same `--model` file, and Hex uses the router's alias for it (often "local").
- **Library: `Hex.Core/LocalAi`.**
  - `GgufReader` reads metadata only and keeps a template digest, not the whole template.
  - `LocalModelLibrary.Shared` scans the folders, Ollama and the catalog, caches GGUF metadata in `data/local-ai/gguf-cache.json`, and computes fit with `FitOf` and `FitDetail`.
  - `LocalModelCaps` holds the badges (`LocalModelBadges.Keys` is the single source).
  - `LocalModelPresets` / `LocalModelProfile` hold per-model sampling. The profile values cite their model cards.
  - `LocalAiSetups` holds the recommended setups: `Apply`, `ChangesOf` (the "Custom (based on X)" check) and `Recommend` by VRAM.
  - `LocalAiRoles` has two roles, Predict and Chat. Chat covers AI Edit, Link Chat @ai and the Local agent.
- **Settings: `local_ai_*`.** These are hidden from the Settings window; the window shows one row, "Open Local AI…". They're still readable through `hex_settings_get` and the file. Old `llamacpp_model`, `ai_edit_model` and similar keys are migrated and mirrored.
- **UI: `Hex.Dawn/LocalAi`.** `DawnScene.LocalAiWindow*.cs` is a WindowPanel with Simple and Advanced modes. `DawnScene.ModelPicker.cs` (`OpenModelPicker`, `PickModelFor`) is the one picker used everywhere.
- **Polling rule:**
  - VRAM is read with nvidia-smi at most every 30 s, and only while the window shows memory.
  - Router status is polled only while the window is open.
  - Nothing at all runs when the window is closed.

## Prediction and AI Edit

- **Core: `Hex.Core/Prediction`.**
  - The engine is `PredictionEngine`, and it must have zero allocations or timers when idle; a guard test enforces this.
  - `PredictionHistory` stores lines, packs inactive files with Brotli, and offers `NoteSoon` to debounce.
  - `RenameTracker`, `NextEditQueue`, `NextEditLocator` and `NextEditMerge` drive the jump suggestions.
  - Formats: Zeta2/2.1, FIM, `ChatEditFormat` and `InstructEditFormat`.
  - Streaming goes through `ChatStreams.Make` / `IChatStreamer`. `WireStream` is a pooled byte-line reader using Utf8JsonReader, and `ThinkSplitter` is the one think splitter.
  - Providers include ollama, openai/llamacpp and `linkai` (AI over Link).
- **Dawn: `Hex.Dawn/Code`.**
  - Predictions and next-edit: `DawnScene.Prediction*.cs`, `DawnScene.NextEdit.cs`, `HexNextEditGlue`.
  - AI Edit: `DawnScene.AiEdit.cs` with `HexAiEditGlue`. It handles multi-cursor, @file and @diagnostics, and places its card in a `CodeEditor.SetBlockSpace` gap, visible only over its own editor.
- **Keys:**
  - Tab: jump or accept.
  - Alt+L: accept.
  - Ctrl+Right / Ctrl+Down: partial accept (a word or a line).
  - Alt+] / Alt+[: next or previous suggestion.
  - Esc / Shift+Esc: dismiss one or all.
  - Alt+Shift+L: accept all. It was Ctrl+Shift+Tab, which clashed with the terminal.
  - Ctrl+Alt+.: predict now.
  - Ctrl+I: AI Edit.
  - Ctrl+Alt+M: cycle favourite chat models.
- **Local agent.** It runs as `Hex.exe --local-agent`, dispatched before any desktop setup. The code is `Hex.Dawn/LocalAgent` plus `Hex.Core/Prediction/LocalAgent*.cs`. It supports profiles (write, ask, minimal), auto-compaction and `/compact`, recovers tool calls from text or reasoning, and its extension lives in `Assets/hex/extensions/local_models`.

## Hex Link

- **Core: `Hex.Core/Link`.**
  - Sync engine: `HexLinkSync*`.
  - Trees, watchers and caches: `HexLinkTree*`. These are shared per folder; holds split them.
  - Repository answers: `HexLinkWorkspaces` uses a stamp cache that reads `.git/HEAD` and refs, with no idle git spawns.
  - Docs CRDT, claims, holds, merge bases (an 8 MiB memory cap, and a disk store), ports (`HexLinkTunnels`/`HexLinkPorts`, the Ports right = 32) and AI over Link (`HexLinkAi*`, the Ai right = 64, with a loopback OpenAI endpoint for the local agent).
  - Conflict undo: `HexLinkResolutions`, `HexLinkSync.Undo`.
  - Path canonicalisation: `HexLinkTree.RealPath` resolves symlinked ancestors on Unix (/var → /private/var).
- **UI: `Hex.Dawn/Link`.**
  - Devices window, cards (Pause and Disconnect send a `link/bye`), pairing and invites (the default is "Work together", View + Edit; agent use is an opt-in tick).
  - Chat with code cards (Ctrl+K L), Look Here, @mentions and history.
  - Presence and live documents, Explorer marks, a status item with a right-click menu.
  - One merge editor for git and Link: marker-free regions with bars, and line endings preserved per line through `DocumentFile.WithLineEndings`.
- **Rule:** Link must cost nothing when it's off or no peer is linked (a guard test enforces this). Idle loops back off: live documents keep alive every 15 s, the tailnet is checked every 10 min, and the tree verify runs every 30 min while the watcher is healthy.

## Agents (the Agents panel and harness)

- **Review of agent edits, per hunk:** `Hex.Core/Agents/HexAgentReviews*.cs` keeps a deduplicated, Brotli-packed blob store under `reviews/`. `DawnScene.AgentsReview.cs` draws the review tab, and the turn-footer accordion and commands go with it.
- **Messaging:** `Hex.Dawn/Agents/DawnScene.McpAgentMail.cs` `Deliver`.
  - Any message to a done or stopped chat resumes it (`Hex.Core/Agents/Hub/HexMailResume.cs`: a 1 s batch with a ping-pong guard).
  - A chat waiting in hex_agent_wait wakes at once.
  - A chat inside another tool call reads it when that call ends, and the sender is told so.
- **Hover:** a card's hover uses `Button.PointerLit`, so the whole card lights.

## Performance rules (keep them)

- Nothing per frame while idle. Use version counters and dirty flags; no LINQ, no string building and no closures in Update or Tick paths.
- Use `WindowManager.RequestTickIn(seconds)` for timers/debounces (e.g. `HexFileWatch.TakeSettled`) and `WindowManager.NoteChange()` for simulation-thread UI changes instead of raw `RequestFrames()`.
- Anything animating without input must call `WindowManager.RequestSmooth()`. Otherwise it runs at the calm rate: 24 Hz focused, 10 Hz unfocused (`PaceCalmFrames`). Decorative motion (pulses, spinners) is capped at 12 Hz.
- Gate optional features with `HexModules.IsEnabled(HexModuleId.*)` (zero-alloc hot path; `HexAi.Off` automatically disables `agentic` and `local_ai`). See `modules-and-performance.md` for the full module architecture and `file:method` hot-path map.
- Use file watchers rather than polling; read the git stamp rather than spawning git; poll external processes only while their UI is visible.
- Guard tests live in `Hex.Tests/HexPerfBudgetTests.cs` (includes `HexModules.IsEnabled` and `HexAgentChat` streaming/tool budgets), `StreamAllocationTests.cs` and `HexLinkMemoryProbeTests.cs`. Allocation tests use per-thread counters, or run in `[Collection(Timed.NAME)]`.

## Working on Hex with agents (gotchas)

- Never use `hex_terminal_run` from an agent: it types into the user's terminal.
- Test live in a COPY of `Hex\bin\Debug\net11.0`, including `Assets\installer` and a warm shader cache, with its own `HEX_HOME` and `HEX_REMOTE_PIPE` and a distinct `link_port`.
- Parallel test runs lock `Hex.Tests\bin`. Use `dotnet test -o <scratch>` (about 14 localisation and analyzer tests then fail falsely, because they read `en-gb.hor` relative to the output folder).
- Never `git stash` or `git reset` the shared tree.
- Restore any user setting you change; testers must not leave the user's roles changed.
- Commits go to the `dev` branch of the separate `hex` and `horizon` repos, with no AI co-author lines.
