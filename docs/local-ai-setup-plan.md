# Local AI: setup window, a smart model selector, per-model presets (toward Zed parity)

This plan follows `docs/next-edit-plan.md`, `docs/ai-edit-plan.md` and `docs/local-agent-plan.md`. It changes how a user configures local AI in Hex. Today seven or more places name a model, each in its own way: free text, a four-model catalog, or Ollama tags. No GGUF metadata is read. Some of the 30+ `edit_predictions_*`/`llamacpp_*` settings duplicate each other. The 5-step llama.cpp dialog shows raw localisation keys in step 4.

**Goal:** one library of local models that Hex finds by itself, one model selector used everywhere, one Local AI window with a guided first run, and per-model presets.

**The constraint that shapes everything:** one GPU. Usually 12 GB, and one model fits in VRAM at a time. The llama.cpp router (`--models-max 1`) swaps between them.

## 1. Zed reference (what we copy)

These come from the scout report, from https://zed.dev/docs/ai/*:

- **Discovery:**
  - Local providers are discovered automatically: Ollama `/api/tags` and `/api/show`, LM Studio, and OpenAI-compatible servers.
  - The capabilities `supports_tools`, `supports_thinking` and `supports_images`, and the real context length, are read from the server.
  - A "No tools" badge appears where a model can't use tools.
- **Model selector:**
  - Grouped by provider, with favourites (a star) and a key that cycles favourites.
  - Searchable and fully keyboard-driven.
  - The same picker appears in the agent panel, the inline assistant and the edit-prediction menu.
- **Edit-prediction status menu:**
  - provider and model;
  - eager or subtle mode;
  - disable for this language;
  - "Configure Providers".
- **Accepting predictions:**
  - Tab accepts, plus a second key that always works (Alt+L).
  - Partial accept by word and by line.
  - Tab yields to the completion menu.
- **Inline assistant:** a model chip in the prompt box, and streaming replacement.

## 2. Contracts, shared by all implementers

All new Core types go in `Hex.Core/LocalAi/` (namespace `Hex.Core`), so Prediction's csproj lists stay untouched. Hex.Core compiles `LocalAi/*.cs` normally. Check the csproj and add the folder only if it uses explicit lists.

### 2.1 GGUF metadata (`GgufInfo.cs`, `GgufReader.cs`)

```csharp
internal sealed record GgufInfo(
    string Path, long Bytes,
    string? Architecture,        // general.architecture
    string? Name,                // general.name
    string? SizeLabel,           // general.size_label, e.g. "9B", "12B-A2.5B"
    long? Parameters,            // general.parameter_count when present
    int? FileType,               // general.file_type, mapped to a quant name by GgufReader.QuantName
    string? Quant,               // e.g. "Q4_K_M", from file_type or the file name
    int? ContextLength,          // <arch>.context_length
    int? Layers,                 // <arch>.block_count
    int? EmbeddingLength,        // <arch>.embedding_length
    int? HeadCountKv,            // <arch>.attention.head_count_kv (for a KV-cache estimate)
    int? ExpertCount,            // <arch>.expert_count (MoE)
    string? ChatTemplate,        // tokenizer.chat_template (kept, can be large)
    bool HasFimTokens,           // fim prefix/suffix/middle token ids or tokens present
    bool IsZeta);                // name, or a template/tokens recognisable as Zeta edit models

internal static class GgufReader
{
    // Reads only the header and the KV metadata. Skips the tensor data and large arrays (the tokens array is
    // scanned for the FIM tokens only). Returns null for a file that isn't GGUF (magic "GGUF", versions 2 and 3).
    public static GgufInfo? Read(string path);
}
```

### 2.2 Capabilities and roles (`LocalModelCaps.cs`)

```csharp
[Flags] internal enum LocalModelCaps { None = 0, Predict = 1, Fim = 2, EditModel = 4, Chat = 8, Tools = 16, Thinking = 32, Vision = 64 }

internal static class LocalModelCapsOf
{
    // Inferred from GGUF: a chat template gives Chat; a template that mentions tools / tool_call gives Tools;
    // <think> or enable_thinking gives Thinking; FIM tokens give Fim and Predict; Zeta gives EditModel and Predict, not Chat.
    public static LocalModelCaps From(GgufInfo info);
    // The same for Ollama, from /api/show "capabilities" (completion, tools, thinking, insert, vision)
    public static LocalModelCaps FromOllama(IReadOnlyList<string> capabilities, string name);
}

internal enum LocalAiRole { Predict, Chat }   // Chat covers AI Edit, Link Chat and the Local agent
```

### 2.3 The library (`LocalModelLibrary.cs`)

```csharp
internal enum LocalModelSource { File, Ollama, LmStudio, Catalog /* recommended, not downloaded */ }

internal sealed record LocalModel(
    string Id,                   // stable: "file:<full path lowercased>" | "ollama:<name>" | "catalog:<catalog id>"
    string Name,                 // display: general.name or the catalog name or the file name without .gguf
    LocalModelSource Source,
    string? Path,                // the GGUF for File/LmStudio
    long Bytes,
    LocalModelCaps Caps,
    int? ContextMax,
    string? SizeLabel, string? Quant,
    string? CatalogId,           // when it matches a LlamaCppModels entry (by file name or id)
    LocalModelFit Fit,           // Fits / Tight / TooBig / Unknown for this GPU at the preset's context
    GgufInfo? Gguf);

internal enum LocalModelFit { Unknown, Fits, Tight, TooBig }

internal sealed class LocalModelLibrary
{
    // The folders scanned for *.gguf, recursively to depth 4. Skips mmproj-*.gguf, and parts of split files after the first.
    // Defaults: the llamacpp models folder (D:\models here), Hex's data llama.cpp/models, LM Studio's models folder
    // (~/.lmstudio/models, ~/.cache/lm-studio/models), and any folder the user adds (setting local_ai_model_folders).
    public IReadOnlyList<string> Folders { get; }
    public IReadOnlyList<LocalModel> Models { get; }        // the last scan, sorted: assigned roles first, then fit, then name
    public event Action? Changed;
    public Task Refresh(CancellationToken cancel = default); // background: files (cached by path+size+mtime in data/local-ai/gguf-cache.json), Ollama /api/tags+/api/show if reachable, catalog entries not on disk
    public LocalModel? Find(string id);
    public static long VramBytes();                          // nvidia-smi query, else 0 = unknown (cached)
    public static LocalModelFit FitOf(long modelBytes, GgufInfo? gguf, int context, long vram); // weights + KV estimate + 0.8 GB overhead
}
```

### 2.4 Presets (`LocalModelPreset.cs`)

```csharp
internal sealed record LocalModelPreset(
    int Context, double Temperature, double TopP, int TopK, double MinP, double RepeatPenalty,
    string Thinking /* "model" | "on" | "off" */, string Offload /* auto|gpu|experts|cpu */, string Extra);

internal static class LocalModelPresets
{
    // The built-in default by family: zeta (ctx 8192, temp 0); qwen3.5 (ctx 32768, temp 0.7, top_p 0.8, top_k 20, min_p 0);
    // mellum thinking (ctx 32768, temp 0.6, top_p 0.95); qwen2.5-coder (ctx 16384, temp 0.2). Otherwise a generic default
    // (ctx min(ContextMax, 16384), temp 0.7, top_p 0.9). Offload: experts for MoE that doesn't fit, else auto.
    public static LocalModelPreset Default(LocalModel model);
    // The user's overrides are kept in the settings as JSON in `local_ai_presets` ({ "<model id>": { ...fields } }). Missing fields come from Default.
    public static LocalModelPreset For(LocalModel model, HexSettings settings);
    public static void Save(HexSettings settings, string modelId, LocalModelPreset preset);
    public static void Reset(HexSettings settings, string modelId);
}
```

The router preset (`LlamaCppServerArgs.Preset`) writes **per-model** sections from these presets: `c`, `temp`, `top-p`, `top-k`, `min-p`, `repeat-penalty`, and the offload flags per section. The request-side sampling (`PredictionTuning`, AI Edit, the Local agent) reads the preset of the model it uses. The global `edit_predictions_temperature`/... settings become the fallback.

### 2.5 Roles (settings)

New settings:
- `local_ai_predict_model`: a LocalModel id, or "" for none.
- `local_ai_chat_model`: a LocalModel id, or "" for the same as predict when that can chat.
- `local_ai_engine`: `llamacpp` | `ollama`.
- `local_ai_model_folders`: extra folders.
- `local_ai_presets`: JSON.

Migration on load:
- From `llamacpp_model`/`edit_predictions_model` to `local_ai_predict_model`.
- From `ai_edit_model` to `local_ai_chat_model`.

The old keys stay readable, so nothing breaks, and are hidden from the settings UI.

**Resolution:** a single `LocalAiRoles.Resolve(settings, library)` returns the predict model, the chat model, and whether they are one model or two (a router swap). It is used by:
- predictions (`PredictionSettings`);
- AI Edit (`AiEditModels.Resolve`);
- the Local agent (`AiEditModels.ForAgent`);
- Link Chat (`MakeLinkAssistantOptions`);
- the llama.cpp server config (`HexSettings.LlamaCppConfig`: the code model is predict, the others are chat plus everything else in the library that can chat).

## 3. UI

### 3.1 The model selector (`Hex.Dawn/LocalAi/ModelPicker.cs`)

A popup, opened from anywhere with a role and an anchor:
- **Search box** at the top. Typing filters by name, family, size or quant.
- **Groups:**
  - "Assigned" (current predict and chat);
  - "On this computer" (Files, LM Studio);
  - "Ollama";
  - "Recommended for your GPU (download)" (Catalog), only the ones that fit.
- **Each row:**
  - a star (favourite);
  - the name;
  - the size label and quant (`9B · Q4_K_XL`);
  - **badges:** Predict, FIM, Chat, Tools, Thinking;
  - a fit dot (green fits, amber tight, red too big) with a tooltip ("7.5 GB weights + 1.1 GB context of 12 GB").
- **Unsuitable models** for the role are shown greyed, with the reason ("can't chat", "no tools"), not hidden.
- **Keys:** Up/Down, Enter picks, Ctrl+Enter picks and opens its preset, Esc closes. A "Manage models…" footer opens the Local AI window.
- **Entry points:**
  - the status bar Predictions menu ("Model: X ▾");
  - the AI Edit box (the "Model: …" line becomes a clickable chip);
  - the Local agent's model option (the extension lists the library's chat models);
  - the settings rows `local_ai_predict_model` and `local_ai_chat_model`;
  - the Local AI window.

### 3.2 The Local AI window (`Hex.Dawn/LocalAi/DawnScene.LocalAiWindow*.cs`)

It replaces the 5-step llama.cpp dialog. The old command names open the new window. It is a dialog with a left nav:

1. **Overview.**
   - Engine status: llama-server running or stopped, loaded model, VRAM used of total, Start/Stop/Restart and the log.
   - The two role cards, Predictions and Chat/Edit/Agent, each with its model, badges and a "Change" button that opens the picker.
   - A one-line warning when the two models differ ("Two models: switching between them takes ~20 s").
   - A "Time it" button.
2. **Models.**
   - The library list as in the picker, with a details pane: path, architecture, context max, quant, caps, fit, and the template's first lines.
   - Actions: Set as Predictions, Set as Chat, Edit preset, Reveal in folder, Delete file (with confirm), Add folder…, Refresh, and Download for recommended entries, with progress (reuse `LlamaCppDownload`).
3. **Presets.** The selected model's preset fields with their built-in defaults shown as placeholders, Reset, and "Apply" (which restarts the server only when a server-side field changed).
4. **Engine.**
   - llama.cpp: build status and Install/Update (reuse `LlamaCppBuilds` + `LlamaCppPlatform`), port, autostart, extra args.
   - Ollama: URL and reachability.
   - The engine choice.
5. **Advanced.** Prediction behaviour (invocation, debounce, renames, far, kind override), privacy exclusions.

**Guided first run.** It opens when no model is assigned, or from "Set Up Local AI…". It is a 4-step flow inside the same window, with Back/Next:

1. Your hardware: GPU name and VRAM detected, RAM.
2. Engine: llama.cpp (recommended, installs in one click) or Ollama (shown as detected when it is).
3. Models:
   - "Found on this computer" (the scan), plus "Recommended for <VRAM> GB" with one pre-selected pick per role.
   - The default suggestion is one model that does both when it fits (Qwen3.5 9B), or Zeta for predictions plus a chat model.
   - The trade-off is explained in one sentence.
4. Test: start, load and Time It. Show tokens/s. Done.

**Fix as part of this:** the localisation keys (`llamacpp.offload_<x>` vs `llamacpp_offload_<x>`, scout finding).

### 3.3 The Predictions status menu (Zed's edit-prediction menu)

Clicking the status item opens a menu with:
- Model ▾ (the picker for Predict);
- Mode: Eager / Subtle;
- "Predict as you type" / "Only when asked" (invocation);
- Follow renames / Edits elsewhere toggles;
- Disable for <language>;
- "Local AI settings…".

## 4. Later rounds (Zed parity of the AI features themselves)

- Partial accept (Ctrl+Right accepts a word, Ctrl+Down accepts a line) and an always-works accept key (Alt+L).
- Subtle mode: show only when Alt is held.
- Tab yields to an open completion list.
- AI Edit: a model chip in the box, multi-cursor (one request per selection), and @file context.
- Local agent: Write/Ask/Minimal profiles mapped to its tool set, a token counter, and a per-hunk review of its edits.

## 5. Work packages (round 1, in parallel)

| WP | Owner files | Depends on |
|----|-------------|------------|
| LA-1 GGUF reader and caps | `Hex.Core/LocalAi/GgufInfo.cs`, `GgufReader.cs`, `LocalModelCaps.cs`, tests | contracts only |
| LA-2 Library, presets, roles, migration, router presets | `Hex.Core/LocalAi/LocalModelLibrary.cs`, `LocalModelPreset.cs`, `LocalAiRoles.cs`; settings files; `LlamaCppServerArgs.cs`; `PredictionSettings.cs`; `AiEditModels.cs`; tests | the LA-1 contracts (code against them, stub if needed) |
| LA-3 Model picker and status menu | `Hex.Dawn/LocalAi/ModelPicker*.cs`, the Predictions status menu in `DawnScene.PredictionItem.cs` | the LA-2 contracts |
| LA-4 Local AI window and guided flow | `Hex.Dawn/LocalAi/DawnScene.LocalAiWindow*.cs`, retiring `DawnScene.LlamaCppSetup.cs` behind the same commands, en-gb strings | the LA-2 contracts, LA-3's picker (open by method `OpenModelPicker(role, anchor, onPick)`) |

**Round 1 test:** Sonnet testers drive the UI through the first-run flow, the picker, the presets and role switching, and compare it against section 1. Findings become round 2.
