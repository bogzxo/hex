# The Local AI window: a separate setup and configuration window, with simple and advanced modes and recommended setups

Follows `docs/local-ai-setup-plan.md`, which built the library, the picker, presets, roles and a first Local AI dialog. This plan turns that dialog into Hex's **own window for local AI**: separate from Settings, explaining clearly what everything does, with a **Simple** and an **Advanced** mode and **presets for the recommended models**. It is written for two implementers working in parallel (section 9), with the contracts they share in section 8.

## 0. What exists today, and what's wrong with it

- `Hex.Dawn/LocalAi/DawnScene.LocalAiWindow*.cs` is a fixed 820×560 **dialog** with a left nav (Overview, Models, Presets, Engine, Advanced) and a first run (Hardware → Engine → Models → Test). It works, and testers have passed most of it, but:
  - It's a modal-feeling dialog. It's cramped (rows wrapped by estimated widths, badges cut to "+N") and can't be resized or kept open beside the code.
  - It assumes knowledge. The labels are terse ("Weights", "Kind of model", "Repeat penalty", "min-p"), and nothing says what a setting changes, why you'd change it, or what's recommended.
  - Beginners and experts see the same thing. Every page shows every knob.
  - "Presets" are per-model sampling values only. There's no notion of a **setup**: a recommended combination of models, roles and behaviour, the thing a person actually chooses ("one fast model for everything" or "best completions plus a smart chat").
- Settings (`Hex.Dawn/Settings/DawnScene.SettingsPage.cs`) shows the pattern to copy for the container. `SettingsWindow` wraps a `WindowPanel`, which is movable and resizable, has title actions, sits on `chrome.Root` and is brought to the front when reopened.

## 1. Goals and non-goals

**Goals**
1. A **separate window**, "Local AI", opened from:
   - the palette ("Local AI…", "Local AI: Set Up…");
   - the Predictions status menu ("Local AI Settings…");
   - the AI submenu;
   - the picker's "Manage Models…";
   - the Settings AI section (a button row: "Open the Local AI window").

   It's a `WindowPanel` like Settings: movable, resizable (min 720×520, default 960×680, clamped to the screen), with position, size and the last page remembered for the session. It can stay open while you work. It's not modal.
2. **Simple mode**, the default for new users:
   - one page that says in plain words what's happening;
   - recommended **setups** to choose from;
   - on/off for each AI feature with a sentence about it;
   - nothing else.

   **Advanced mode** has everything: models, per-model presets, engine, prediction behaviour, privacy and diagnostics. A toggle in the title bar switches modes, and the choice is remembered.
3. **Clear explanations everywhere.** Every option has:
   - a plain title;
   - one line saying what it does;
   - a "?" that expands to say when to change it, what happens if you do, and the recommended value;
   - a "Recommended" mark on the recommended choice;
   - "Reset to recommended" per field and per page.

   A short glossary page in Advanced covers model, GGUF, quantisation, context, VRAM, offload, temperature, top-p, top-k, min-p, repeat penalty, thinking, FIM and model swapping.
4. **Recommended setups** (section 4): curated bundles of models, roles, per-model sampling and behaviour.
   - Each says who it's for, what it needs (VRAM and download size) and whether it fits this machine.
   - "Use this setup" downloads anything missing (with progress), applies everything, starts the server and runs a quick test.
   - The setup in use is shown. Changing anything it set marks it "Custom (based on X)", with "Return to X".
5. **Recommended presets per model** (section 5): for each catalog model, named sampling profiles ("Recommended", "Precise", "Creative" and, for thinking models, "Thinking off / on") with their source, from the model card. In Advanced, a profile is picked and then fine-tuned.

**Non-goals:**
- New engines (only llama.cpp and Ollama).
- Changing how predictions, AI Edit or the agent work.
- A real OS-level separate window: Hex has no multi-window support. A `WindowPanel` gives the same feel.
- Cloud providers.

## 2. Information architecture

```
Local AI window
 ├─ Title bar: "Local AI" · [Simple | Advanced] toggle · status chip (●Running Qwen3.5 9B) · (?) help · ✕
 ├─ Simple mode (no nav; one scrolling page in three sections)
 │   1. Status: one sentence + Start / Stop, with the problem and the fix when something's wrong
 │   2. Your setup: the active setup card, then "Change setup" opens the setup gallery (section 4)
 │   3. Features: four toggles, each with a one-line explanation and a "Try it" link
 └─ Advanced mode (left nav)
     ├─ Overview     status, the roles (Predictions / Chat, Edit & Agent) with Change…, the setup in use, Time It, log
     ├─ Setups       the same gallery as Simple, with full detail (every model, preset and setting each would set)
     ├─ Models       the library (exists), details, Download / Delete / Reveal / Add folder
     ├─ Model presets  per model: the profile picker (section 5) + each field, explained
     ├─ Predictions  invocation, eager or subtle, debounce, follow renames, edits elsewhere, kind override, disabled languages
     ├─ Engine       llama.cpp build, backend, folder, port, autostart, extra args; Ollama URL; which engine
     ├─ Privacy      what is never sent (globs), Link Chat answering, linked predictions serving
     ├─ Diagnostics  server log, router models and their state, graphics memory, Time It history, "Copy a report"
     └─ Glossary     terms, each a short paragraph
```

The **first run** is Simple mode's setup gallery with a short welcome on top ("Hex can run AI on this computer… pick a setup"). When nothing is set up, opening the window in either mode shows it. The four-step first run is retired. Its hardware detection and test are reused inside "Use this setup" (section 4.3).

## 3. Simple mode, in detail

**Status.** One sentence built from the state, for example:
- "Local AI is running **Qwen3.5 9B** on your graphics card. Code predictions appear when you ask (Ctrl+Alt+.). Chat, AI Edit and the Local agent use the same model."
- "Local AI isn't set up yet. Pick a setup below; it takes about 5 minutes on a fast connection."
- "The AI server stopped: *reason*. [Start again] [Show log]"
- "Downloading Zeta 2.1: 2.1 of 4.7 GB, about 3 minutes left. [Pause] [Cancel]"

It has a Start / Stop button. For an adopted server: "running outside Hex", with no Stop.

**Your setup.** The active setup's card (section 4.2), compact, with "Change setup…". If the settings no longer match it: "Custom (based on All-rounder) · [Return to All-rounder]". If there is none: the gallery inline.

**Features**, each a toggle plus one sentence plus "Try it":
1. **Code predictions:** "Suggests the next edit as you type, Tab accepts." Sub-choice: *As you type* or *Only when I ask (Ctrl+Alt+.)*.
2. **AI Edit:** "Select code, press Ctrl+I and say what to change." (Try it: opens a scratch sample with a selection.)
3. **Local agent:** "An agent in the Agents panel that runs on your own model." (Try it: opens the Agents panel with Local selected.)
4. **Answer in Link Chat:** "Linked devices can ask your model with @ai." Shown only when Link is on.

A disabled feature says why: "needs a chat model; your setup uses Zeta, which can't chat. [Change setup]".

## 4. Recommended setups

### 4.1 The catalog

Initial setups, in `Hex.Core/LocalAi/LocalAiSetups.cs`. VRAM figures come from the fit maths, with weights plus context at the setup's context.

| Id | Title | For | Predictions model | Chat / Edit / Agent model | Needs | Notes |
|----|-------|-----|-------------------|---------------------------|-------|-------|
| `allrounder` | **All-rounder** | Most people with 8–12 GB | Qwen3.5 9B | Qwen3.5 9B (same) | ~7 GB VRAM, 5.6 GB download | One model, no swapping, so everything answers at once. Thinking off for predictions, on by request in chat |
| `best-of-both` | **Best completions + smart chat** | 12 GB, people who type a lot | Zeta 2.1 | Mellum2.1 Thinking | ~9 GB VRAM for the larger, 12.2 GB download | Two models. Switching between predicting and chatting takes ~20 s while the other model loads |
| `reasoning` | **Thinking model for everything** | Harder edits and agent tasks | Mellum2.1 Thinking | Mellum2.1 Thinking | ~9 GB VRAM, 7.5 GB download | Slower answers that think first. Predictions use FIM without thinking |
| `light` | **Light** | 4–6 GB cards, laptops, or no GPU | Qwen2.5-Coder 1.5B | Qwen2.5-Coder 1.5B | ~2 GB VRAM or CPU, 1 GB download | Quick and small. Simple edits only. The agent is limited |
| `ollama-*` | **Use my Ollama models** | People who already run Ollama | best FIM-capable Ollama model | best chat+tools Ollama model | none (already there) | Shown only when Ollama answers. Models chosen by capability from `/api/show` |

Each setup also sets a **behaviour bundle**:
- prediction invocation (setups for typists: *as you type*; the others: *when asked*);
- eager mode;
- follow renames on, edits elsewhere on (off for `light`);
- the per-model **profile** (section 5) for each role;
- the context per model;
- thinking per role;
- autostart on.

### 4.2 The setup card

- **Title**, a one-line pitch, and a "Recommended for this computer" ribbon on the best fit, using the VRAM rules in 4.4.
- **What you get:** 2–3 bullets, for example "Predictions as you type", "Chat, AI Edit and agent in one model".
- **What it needs:**
  - VRAM, with a fit dot ("fits your 12 GB card");
  - download size, with "already downloaded" per file;
  - disk space, with a free-space check.
- **Trade-offs** in one line (for example "switching between predicting and chatting takes ~20 s").
- **Buttons:** **Use this setup** (primary), plus **Details** (Advanced shows the full list of every setting it changes).
- **Unavailable:** "Needs ~9 GB; your card has 6 GB." Greyed, but readable.

### 4.3 "Use this setup" flow

1. **Confirm.** If it replaces a custom configuration, show a short list of what changes: "Predictions model: Zeta 2.1 → Qwen3.5 9B; …". [Use it] [Cancel].
2. **Engine check.** If llama.cpp isn't installed, install the right build in place: the existing `LlamaCppBuilds` / `DownloadLlamaBuild` flow, with progress shown on the card.
3. **Downloads.** Download the missing model files, with progress, pause and cancel, through the existing `DownloadCatalogModel` work items. They can run while the window is closed, and the status bar shows the progress.
4. **Apply.** Set the roles, the per-model presets from profiles, and the behaviour bundle, all in **one settings write**, and record `local_ai_setup = <id>`.
5. **Start and test.** Start or restart the server and run Time It on each role's model. Show "Ready: predictions 96 tok/s, chat 40 tok/s".
6. **Done.** "You're set up. Try: press Ctrl+Alt+. in a code file · select code and press Ctrl+I · open the Agents panel → Local."

### 4.4 Recommending for this computer

- **VRAM 10 GB or more:** All-rounder recommended, with Best of both and Reasoning also fitting.
- **6–10 GB:** All-rounder if it fits at a reduced context, else Light.
- **Under 6 GB or no NVIDIA GPU:** Light (with the CPU noted).
- **Ollama answering with suitable models:** also show "Use my Ollama models".

The rules live in `LocalAiSetups.Recommend(hardware, library, ollamaModels)`.

### 4.5 Custom

`LocalAiSetups.Matches(settings, setup)` compares what the setup sets with the settings now. Any difference means "Custom (based on X)", with the differing items listed in Advanced → Overview.

## 5. Recommended presets per model (profiles)

`LocalModelPresets.Profiles(model)` returns named profiles for a model's family. Each is a full `LocalModelPreset` plus a title, a one-line description and a `Source` (the model card URL or "Hex default"). Initial profiles:

| Family | Profile | Values (ctx / temp / top_p / top_k / min_p / repeat / thinking) | Description |
|---|---|---|---|
| Qwen3.5 | Recommended | 32768 / 0.7 / 0.8 / 20 / 0 / 1.0 / model | Qwen's non-thinking values from the card |
| Qwen3.5 | Thinking | 32768 / 0.6 / 0.95 / 20 / 0 / 1.0 / on | Qwen's thinking-mode values |
| Qwen3.5 | Precise (code) | 32768 / 0.2 / 0.9 / 20 / 0 / 1.0 / off | For edits that must not wander |
| Mellum2.1 Thinking | Recommended | 32768 / 0.6 / 0.95 / 40 / 0 / 1.0 / on | The card's values |
| Mellum2.1 Thinking | Fast | 16384 / 0.6 / 0.95 / 40 / 0 / 1.0 / off | No thinking, shorter context, quicker |
| Zeta 2.1 | Recommended | 8192 / 0 / 1 / 0 / 0 / 1.0 / off | An edit model, greedy |
| Qwen2.5-Coder 1.5B | Recommended | 16384 / 0.2 / 0.9 / 40 / 0.05 / 1.05 / off | Small model, low temperature |
| Unknown | Balanced | min(ctx, 16384) / 0.7 / 0.9 / 40 / 0.05 / 1.0 / model | Safe defaults |
| Unknown | Precise | the same with temp 0.2 | |

The implementer must check the Qwen3.5, Mellum and Qwen2.5-Coder values against their model cards on Hugging Face, adjust them if different, and record the URL in `Source`.

The preset stored per model gains a `Profile` id. The fields show the profile's values as placeholders, and any field changed by hand reads "(changed from Recommended)". A "Reset to <profile>" button restores them.

## 6. Explanations: content and presentation

- **Content** lives in en-gb.hor, in a new `local_ai_help` section: for each key, `title`, `short` (one line, at most about 90 characters) and `long` (2–4 sentences: when to change it, the effect, the recommended value).
- **Keys** cover every field in sections 3, 2 and 5:
  - the engine fields (backend, port, offload/"weights", extra args, autostart, folder);
  - the preset fields (context, temperature, top-p, top-k, min-p, repeat penalty, thinking);
  - the prediction behaviour fields (invocation, eager/subtle, debounce, renames, far, kind);
  - privacy (globs, Link Chat answering, prediction serving);
  - the roles (Predictions model, Chat/Edit/Agent model);
  - the glossary terms.
- **Plain words.** Replace jargon in titles:
  - "Weights" becomes "Where the model runs";
  - "Kind of model" becomes "How Hex talks to the predictions model";
  - "Repeat penalty" becomes "Avoid repeating itself";
  - "min-p", "top-k" and "top-p" keep their names in titles, but `short` explains them ("Ignore unlikely words: higher is more focused").
- **Presentation.** A small reusable row helper `LocalAiExplainedRow(key, control)` draws the title, the control at the right, the `short` below in a dim colour, and a "?" that toggles `long` underneath (one open at a time per page). A "Recommended" chip shows on the matching choice, or beside a number field whose value equals the recommended value.
- **Search.** The window has a search box in Advanced (title bar). It filters the rows of every page by title, short and long, and jumps to the page.

## 7. Behaviour and state

- **Settings:**
  - `local_ai_ui_mode` (simple | advanced): defaults to simple when nothing is set up, otherwise advanced if the user has changed advanced-only settings before. Migration: users with any non-default `llamacpp_*` or `local_ai_presets` get advanced.
  - `local_ai_setup` (a setup id, or "" for none, or custom).
- **Window:** non-modal. Remembers its rect and page for the session. Reopening brings it to the front, at the requested page.
- **Refreshing:** redraw on state changes only (the existing `LocalAiSetupFlow.Signature`, extended with the mode, the setup id and the download progress, bucketed to whole percents). Never steal focus from a text box.
- **Errors:** every failure says what happened, why if known, and the next action. For example:
  - "Download failed: disk full on D: (1.2 GB free, 4.7 GB needed). [Choose another folder]";
  - "The server didn't start: CUDA out of memory. Try 'Where the model runs: automatic' or a smaller context. [Open Model presets]".
- **Keyboard:**
  - Ctrl+F focuses search (Advanced).
  - Alt+1..9 jumps to pages.
  - Esc closes the window when nothing is being edited.
  - Every control is tab-reachable, and hex_ui_tree sees them.
- **Self-test and agent access:** the window and its pages are reachable by hex_ui_tree ids; testers will use that.

## 8. Contracts, shared by both implementers

Core (Implementer 1 owns them). New files go in `Hex.Core/LocalAi/`:

```csharp
internal sealed record LocalAiSetupModel(string CatalogId, string ProfileId);   // a model and the profile it runs with

internal sealed record LocalAiBehaviour(
    string Invocation /* "typing" | "shortcut" */, string Mode /* "eager" | "subtle" */,
    bool FollowRenames, bool EditsElsewhere, bool Autostart);

internal sealed record LocalAiSetup(
    string Id, string TitleKey, string PitchKey, IReadOnlyList<string> GetKeys /* "what you get" bullets */,
    string TradeOffKey, LocalAiSetupModel Predict, LocalAiSetupModel Chat,
    double MinVramGb, LocalAiBehaviour Behaviour, bool Ollama = false);

internal sealed record LocalAiSetupFit(bool Fits, bool Tight, double NeedGb, double HaveGb, long DownloadBytes,
    IReadOnlyList<string> MissingCatalogIds, long FreeDiskBytes);

internal sealed record LocalAiSetupChange(string WhatKey, string From, string To);   // for the confirm list and the "Custom" diff

internal static class LocalAiSetups
{
    public static IReadOnlyList<LocalAiSetup> All { get; }                 // the catalog of section 4.1 (Ollama ones built at runtime)
    public static LocalAiSetup? Find(string id);
    public static LocalAiSetup? Recommend(long vramBytes, LocalModelLibrary library, IReadOnlyList<LocalModel> ollama);
    public static LocalAiSetupFit FitOf(LocalAiSetup setup, LocalModelLibrary library, long vramBytes);
    public static IReadOnlyList<LocalAiSetupChange> ChangesOf(LocalAiSetup setup, HexSettings settings, LocalModelLibrary library);  // empty = matches
    public static void Apply(LocalAiSetup setup, HexSettings settings, LocalModelLibrary library);  // one write; sets local_ai_setup; requires models on disk
    public static IReadOnlyList<LocalAiSetup> ForOllama(IReadOnlyList<LocalModel> ollama);            // "Use my Ollama models" when suitable
}

internal sealed record LocalModelProfile(string Id, string TitleKey, string DescriptionKey, string Source, LocalModelPreset Preset);

// additions to LocalModelPresets:
//   public static IReadOnlyList<LocalModelProfile> Profiles(LocalModel model);
//   public static LocalModelProfile ProfileOf(LocalModel model, HexSettings settings);   // the chosen profile (stored id), else the first
//   public static void UseProfile(HexSettings settings, string modelId, string profileId); // clears field overrides
//   LocalModelPreset gains no fields; the stored JSON gains "profile": "<id>"

internal static class LocalAiHelp
{
    // en-gb keys under local_ai_help: <key>.title, <key>.short, <key>.long; Recommended values per key where it has one
    public static IReadOnlyList<string> Keys { get; }      // every help key, for search and the tests that check each has its 3 strings
    public static string? RecommendedValue(string key, LocalModel? model, HexSettings settings);
}
```

Settings (Implementer 1): `local_ai_ui_mode` and `local_ai_setup`, following the full pattern (HexSettings field and row, EditorSettingsCatalog row and Usual default, SettingsPage Get/Set, en-gb, goldens via HEX_RECORD_GOLDENS=1).

UI (Implementer 2) depends only on the above and on the existing:
- `LocalModelLibrary.Shared`, `LocalAiRoles`;
- `OpenModelPicker` / `PickModelFor`;
- `DownloadCatalogModel` / `DownloadLlamaBuild` / `MeasureLlama`;
- the server start and stop.

Implementer 1 writes the contract types as **compiling skeletons first** (within ~10 minutes) and messages Implementer 2.

## 9. Work split

**Implementer 1: "LW-1 Core: setups, profiles, help content"**
- Write the section 8 types and logic, with tests:
  - setups and their fits for 4/6/8/12/16 GB;
  - `Recommend`;
  - `ChangesOf` and `Apply` round trip;
  - Ollama setups from fake `/api/show` caps;
  - profiles per family;
  - `UseProfile` clearing overrides;
  - every help key has its three strings.
- Check the model-card sampling values (section 5) online and cite them.
- Write all the help content in en-gb (section 6): about 60 keys. Plain, specific, British English, matching the house tone in en-gb.hor.
- Add the two settings and their migration.
- Don't touch `Hex.Dawn/LocalAi/*` UI files except to fix compile breaks from contract changes, coordinating with LW-2.

**Implementer 2: "LW-2 UI: the window, simple/advanced, gallery, explained rows"**
- Convert the dialog to a `WindowPanel`-based window like `SettingsWindow`:
  - movable and resizable, with the title bar actions;
  - the mode toggle, a status chip, help, and search in Advanced.
- Simple mode page (section 3).
- The setup gallery and card (4.2), and the "Use this setup" flow (4.3), with progress and the confirm list.
- Advanced nav and pages (section 2): reorganise the existing Overview, Models, Presets ("Model presets" with the profile picker), Engine and Advanced (split into Predictions, Privacy and Diagnostics); add Setups and Glossary.
- `LocalAiExplainedRow`, applied to every row in both modes.
- Retire the four-step first run; the gallery is the first run.
- Update every entry point to open the window, plus a "Local AI" button row in Settings → AI.
- Until LW-1's skeletons land, read the existing window code, `SettingsWindow` / `WindowPanel`, and plan the layout.

**Both:**
- Don't commit, and don't restart Hex.
- Use full paths and Hex's MCP tools.
- Small, targeted edits to en-gb.hor: LW-1 in `local_ai_help` and `local_ai_setups`, LW-2 in `local_ai`.
- Run the full suite and hex_dev_build.

## 10. Acceptance (what the testers will check)

1. **The window.** It opens from every entry point, is movable and resizable, stays open while editing, and reopens at the same page.
2. **The first run.** A fresh profile sees the welcome plus the gallery in Simple mode, with the right recommendation for 12 GB (All-rounder). "Use this setup" on a machine with the files present skips downloads, applies, starts and tests in under a minute, and ends on the "Try" hints.
3. **Simple mode.** Exactly the status, setup and features sections. Every toggle has its sentence, and the disabled states say why.
4. **Advanced mode.**
   - Every page from section 2 exists.
   - Every row has a title, a short line and "?" with a long explanation.
   - Recommended chips appear on the recommended choices.
   - Search finds "temperature" and jumps to Model presets.
5. **Profiles.** Picking "Precise" for Qwen3.5 changes the fields. Editing one marks it changed. Reset restores it.
6. **Custom detection.** Changing the chat model after using All-rounder shows "Custom (based on All-rounder)" and "Return to All-rounder" restores it.
7. **Wording.** No raw localisation keys, no jargon in titles, and every error says the next action.
8. **Nothing regressed:** the picker, the status menu, AI Edit, predictions, the Local agent and the server lifecycle (still one router, one model in VRAM).
