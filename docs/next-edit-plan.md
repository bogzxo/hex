# Next edits, history-aware prediction and instruction editing

This plan follows on from `docs/edit-prediction-plan.md`. That design predicts one edit near the caret. This one adds four things: edits the model or Hex expects further away ("jumps"), a better edit history, a strategy chosen per model, and editing by instruction (Ctrl+I). The user runs one local model at a time on 12 GB of VRAM, through Ollama or a llama.cpp server that Hex manages. That model is either an edit model (Zeta 2 / 2.1, which can't chat) or an instruct model that can also fill in the middle (Qwen3.5 9B, Mellum 2.1). Everything below has to work with whichever one is loaded.

---

## Status

Implemented (phases 1 to 5 and 7): history that is recorded for every edit, jumps (a queue of suggestions away from the
caret, from the rename tracker and from far requests to the model), a strategy per model kind, instruction editing, and
the polish of phase 7: two settings, counters and a fuller status tooltip. Related plans: `docs/edit-prediction-plan.md`
(prediction near the caret) and `docs/ai-edit-plan.md` (AI Edit, Ctrl+I).

Keys:

| Key | What it does |
| --- | --- |
| Tab | Takes the prediction or jump shown; with one out of sight, goes to it first. |
| Alt+] / Alt+[ | The caret to the next / the previous jump. |
| Esc | Lets go of the shown prediction or jump (only that one). |
| Shift+Esc | Lets go of every jump in the file; a rename is not followed any more. |
| Alt+Shift+L | Takes every jump of the shown one's source together, as one undo step. |
| Ctrl+I | AI Edit: an instruction for the selection (see `docs/ai-edit-plan.md`). |

Settings: `edit_predictions_renames` (on) follows a rename to its other places with no model; `edit_predictions_far` (on)
asks the model for edits away from the caret. The status item's tooltip shows the model kind, the jumps waiting, whether
those two are on, and counters (predictions shown; jumps shown, taken, let go of; far requests asked and edits found),
kept in `PredictionLog`. A slot setting was left out: llama-server runs with `--parallel 1`.

---

## 1. What happens now, and where it falls short

The flow today:

1. `DawnScene.PredictionEdited` (`Hex.Dawn/Code/DawnScene.Prediction.cs:146`) tells `PredictionEngine.Edited` (`Hex.Core/Prediction/PredictionEngine.cs:96`). That call cancels whatever is in flight and restarts the debounce.
2. `PredictionEngine.Tick` (`:119`) waits for `DebounceMs`, takes a `PredictionSnapshot`, and starts `Run` (`:309`).
3. `Run` notes the text into `PredictionHistory` (`:318`), builds a `PredictionInput`, checks `PredictionCache`, builds the prompt with the format that `PredictionFormats.For` chose (`PredictionFormats.cs:58`), streams the answer, parses it into `PredictionEdit`s, and puts one `PredictionResult` into `ready`.
4. `UpdatePredictions` (`DawnScene.Prediction.cs:183`) takes the result. `ShowPrediction` (`:402`) gives it to the editor as a `UICodePrediction`, either ghost text or a small diff. Tab accepts it. `PredictionEnded` (`:439`) records an accepted prediction in history and asks for the next one at once.

Weaknesses, with where they come from:

- **Predictions only reach the caret.** Zeta's editable region is about 150 tokens round the caret, mostly below it (`Zeta2Format.cs:42`, `Ranges` at `:260`). An edit 40 lines away can't be predicted at all. Rename a variable on line 10 and nothing points at line 80.
- **A result is shown only where it was asked.** `ShowPrediction` drops the result and asks again when the caret has moved (`DawnScene.Prediction.cs:412`). There is no concept of "an edit waiting at another place".
- **Only one prediction exists at a time.** `ready` holds a single `PredictionResult` (`PredictionEngine.cs:33`). Nothing can be queued, so a rename with 6 remaining occurrences needs 6 round trips through the model.
- **History is only recorded when a prediction runs.** `History.Note` is called only from `Run` (`PredictionEngine.cs:318`). With `ShortcutOnly`, or while requests back off after failures, edits are never recorded. Typing during the debounce is folded into one diff, so the order of edits is lost.
- **One change spread over a file becomes one huge hunk, which is then dropped.** `PredictionHistory.Diffed` makes one hunk from the first changed line to the last (`PredictionHistory.cs:134`). Changes on lines 10 and 80 give a 70-line hunk. That is over `MOST_BYTES` = 1500 and `Add` drops it silently (`:123`), so the model never sees the very rename it should continue.
- **FIM models see no history and no other files.** `FimFormat.Build` (`FimFormat.cs:60`) uses only the text before and after the caret and a path comment. `input.Events` and `input.Related` are ignored, so a FIM model can't know that `count` became `total`. It also refuses inside a word (`InWord`, `:64`), which is exactly where a rename is typed.
- **Instruct models are treated as Qwen FIM.** `PredictionFormats.Infer` falls back to `FimFormat.Qwen` for any name it doesn't recognise (`PredictionFormats.cs:54`). Qwen3.5 9B instruct handles raw FIM tokens poorly or not at all. Hex already has a chat client, `OpenAiChat` (`Hex.Core/Prediction/OpenAiChat.cs`, used by `Hex.Dawn/Link/LinkAssistant.cs:240`) and `OllamaChat`, but prediction never uses them.
- **Dismissing stops everything.** `Dismissed` (`PredictionEngine.cs:199`) asks for nothing more until the next keystroke, even if other suggestions are still valid.
- **There is no instruction-based editing.** Ctrl+I is free in the default keymap (only the VS keymap uses `Ctrl+K Ctrl+I`, for hover, in `DawnScene.Keys.VisualStudio.cs:53`).

---

## 2. Next-edit suggestions beyond the caret ("jumps")

### 2.1 Concepts and types (Hex.Core/Prediction)

- `NextEdit` (new record): `Path`, `Anchor` (a `UITextDocument` tracked range, or an offset plus the version it belongs to, which the glue turns into a tracked range), `IReadOnlyList<PredictionEdit> Edits`, `Source` (`Model`, `Rename`, `Instruction`), `Reason` (short text such as "rename count → total"), `Confidence` (0..1), and `Version`.
- `NextEditQueue` (new, `Hex.Core/Prediction/NextEditQueue.cs`): an ordered list of `NextEdit`s for one document. Methods: `Add`, `Peek(caretOffset)` (the nearest pending edit after the caret, wrapping round to the top), `Take`, `Drop(NextEdit)`, `DropAll(source)`, `Rebase(IReadOnlyList<TextChange>)`, which shifts anchors after a document edit and drops any suggestion whose range was touched, and `Count`. It holds at most 32 entries. It is owned by the engine and used only on the editor thread.
- `PredictionResult` gains `IReadOnlyList<NextEdit> Elsewhere`. The near-caret edit is still `Edits`.

### 2.2 How they are found

**A. Predicted by the model (Zeta 2.1 and instruct models).**

- After a near-caret prediction is accepted, or a short pause follows an edit, the engine sends a second "far" request. Its region is centred on the **best candidate location** rather than the caret. Candidates come from `NextEditLocator.Candidates(text, history)` (new):
  1. lines that contain identifiers removed in the latest history event (see B);
  2. the lines that call or declare anything whose signature changed in the latest event (simple token match);
  3. Roslyn or LSP references to the symbol under the last edit, when a language server is running (`hex_refs`-style lookup through the existing code server, limited to 200 ms).
- At most 3 far regions are asked for per pause, one request at a time (one GPU), nearest first. Each uses `Zeta2Format.Build(input, maxTokens, regionStart, regionEnd, contextStart, contextEnd)`, the overload that already exists (`Zeta2Format.cs:74`), with the caret marker at the start of the candidate line. With Zeta 2.1 multi-region the chunk markers already let the model rewrite only the chunks it changes.
- An answer that changes nothing is a "no edit here" and costs only the time already spent. Answers that do change something become `NextEdit { Source = Model }`.

**B. Rename propagation, deterministic and without a model.**

- `RenameTracker` (new, `Hex.Core/Prediction/RenameTracker.cs`) looks at each history event as it is closed. When a hunk swaps one identifier token for another (same line otherwise, word boundaries both sides, the old name appearing somewhere else in the file), it records `Rename(oldName, newName, path, version)`.
- `RenameTracker.Remaining(text, rename)` finds the remaining whole-word occurrences of `oldName`. It skips strings and comments using the highlighter's token classes when the glue provides them, and otherwise a simple lexer for `"…"`, `'…'`, `//` and `/* */`. Each occurrence becomes a `NextEdit { Source = Rename, Confidence = 0.9 }` with a single `PredictionEdit`.
- If the language server offers a rename (`textDocument/prepareRename`), the status item offers "Rename all with language server (Shift+Tab)". Shift+Tab applies the whole set as one undo step. Hex never does this without the user asking.
- The rename is confirmed once the user accepts the second occurrence or types the second one by hand. Before that, only the nearest occurrence is shown, so a half-typed name doesn't light up the whole file.
- The tracker forgets a rename when the user types the old name back, when no occurrences remain, or after 5 minutes.

Model results and rename results are merged by range. When both cover the same span, the model's edit wins if it is a superset, such as a rename together with a changed argument. Otherwise the rename edit wins.

### 2.3 How they are shown (Hex.Dawn)

- **At the caret** (unchanged): ghost text or a small diff. Tab accepts it.
- **Elsewhere, visible on screen:** a faint highlight on the target span, plus a gutter arrow (`→` icon, theme colour `editor.prediction.jump`) on its line. Inline at the end of the caret's line goes a small hint: `Tab ↓ 42` or `Tab ↑ 12`, meaning direction and line number.
- **Elsewhere, off screen:** the same hint, plus a marker on the scrollbar.
- **Keys:**
  - `Tab` with nothing at the caret and a jump pending: move the caret to the next suggestion, scroll it into view, and show its diff there. A second `Tab` accepts it. Then the queue goes on to the next one, and the hint updates with the count, e.g. `Tab ↓ 57 (3 more)`.
  - `Alt+]` / `Alt+[`: next or previous suggestion without accepting anything.
  - `Esc`: dismiss the current suggestion only.
  - `Shift+Esc`: dismiss the whole queue.
  - `Alt+Shift+L` (not Ctrl+Shift+Tab, which is the terminal's Previous Tab): accept every remaining suggestion from the same source (for instance every rename occurrence) as one undo step.
- **Staleness:** every document edit calls `NextEditQueue.Rebase`. A suggestion whose span was edited, or whose old text no longer matches, is dropped. A version jump the glue can't map (reload, external change) clears the queue. Changing to a different document keeps that document's queue, at most 4 documents, oldest first out.
- `HexPredictionGlue` gains `JumpHint(caretLine, NextEdit) -> string` and `Anchored(NextEdit, UITextDocument) -> (start, end)?`. These are pure and can be tested.
- `CodeEditor` needs `SetJumpMarkers(IReadOnlyList<UIJumpMarker>)`, where `UIJumpMarker` holds a line, span, icon and tooltip, and `ScrollToRange`.

---

## 3. Edit-history context

- **Record every edit, not only those before a request.** `DawnScene.PredictionEdited` calls a new `PredictionHistory.NoteSoon(path, version, lines)` on every edit. History closes an event after 1 s idle, on a caret jump of more than `NEAR` lines, on a file switch, or on save. This runs off the editor thread with the lines captured, as `Run` does now. Recording happens with `ShortcutOnly` too.
- **Several hunks per event.** `Diffed` returns `IReadOnlyList<Hunk>` (Myers with `CONTEXT` = 3, as `PredictionDiff` already does) instead of one hunk spanning first to last. Large edits keep their real hunks. An event over budget is **trimmed**, keeping the hunks nearest the latest caret, not dropped.
- **Budgets.** `MOST_EVENTS` rises from 6 to 12. Events total at most about 1,200 tokens for Zeta (it keeps Zed's 4,096 prompt and fills what's left after the region and context, as now), about 600 tokens for FIM models, and about 1,500 tokens for instruct models. Newest events come first when trimming, and each event is at most about 400 tokens.
- **Order and recency.** Oldest first in the prompt, so the newest event sits nearest the region, as `Events` does now (`PredictionHistory.cs:100`). Other files' events come before the current file's. Events older than 10 minutes are dropped.
- **Accepted-prediction markers.** Keep `ACCEPTED` ("// User accepted prediction:") for Zeta. For chat and FIM prompts, mark each event `(accepted suggestion)` or `(typed)`. Rejected suggestions are recorded too, as `(rejected suggestion: …)`, max 2, and only for instruct models. That stops the model repeating them.
- **Rename summary line.** When `RenameTracker` holds an active rename, a one-line summary is added: `Renamed count → total in Foo.cs; 4 occurrences remain.` This goes in the chat prompt and in the FIM path comment.
- **Cross-file.** Events stay per path, and `Closed` still drops a file's events. A rename made in one file feeds candidates in other open files only through the language server references (2.2 A3). Hex never queues edits in files that aren't open.
- **Privacy.** Every path passes through the existing `PredictionAllowed` / privacy patterns before it goes into history (`DawnScene.Prediction.cs:342`).

---

## 4. Per-model strategy

### 4.1 Kinds

A new `PredictionModelKind { EditModel, Fim, Instruct, FimAndInstruct }` is decided by `PredictionModels.KindOf(options)`:

1. A setting, `edit_prediction.kind`, which defaults to `infer`.
2. The model name: `zeta` gives `EditModel`. Names containing `coder`, `starcoder`, `codellama`, `deepseek-coder`, `codestral` or `codegemma` give `Fim`. Names such as `qwen3`, `qwen3.5`, `mellum`, `instruct`, `-it` or `chat` give `FimAndInstruct` or `Instruct`.
3. A one-time probe, cached per model in Hex's state: Ollama `/api/show` (template has `.Suffix` gives FIM; capabilities include `completion`/`insert`), and for llama-server `/props` (`chat_template` present; whether the tokenizer has `<|fim_prefix|>`, from `/tokenize`). The probe result overrides the name.

`PredictionFormats.For` becomes `PredictionStrategy.For(options, kind)` and returns a **near** format and a **far** format:

| Kind | Near the caret | Elsewhere (jumps) | Ctrl+I |
|---|---|---|---|
| EditModel (Zeta 2 / 2.1) | `Zeta2Format` as now | Zeta region at the candidate line | Not available: "Zeta can only predict edits. Pick an instruct model for instructions." |
| Fim | `FimFormat` with history as a comment block | Rename tracker only | Not available (same message, naming the model) |
| FimAndInstruct (Qwen3.5, Mellum) | `FimFormat` with history | `ChatEditFormat` at the candidate line | `InstructEditFormat` |
| Instruct | `ChatEditFormat` at the caret | `ChatEditFormat` | `InstructEditFormat` |

### 4.2 FIM with history

`FimFormat.Build` adds, before the code prefix, a comment block in the file's own comment syntax (`Comment`, `FimFormat.cs:266`):

```
// Recent edits (oldest first):
// Foo.cs @@ -10,1 +10,1 @@
// -    int count = 0;
// +    int total = 0;
// Renamed count → total; 4 occurrences remain.
```

This is capped at 600 tokens. The `InWord` refusal stays for plain typing, but it is lifted when the caret sits inside an identifier that the rename tracker names. In that case the old word is removed from the prefix/suffix and the answer is checked against `newName`.

### 4.3 ChatEditFormat (instruct models, prediction)

`Hex.Core/Prediction/ChatEditFormat.cs` implements `IPredictionFormat`. Its request is a chat request: `PredictionRequest` gains `IReadOnlyList<ChatTurn>? Turns`, and the providers send it to `/v1/chat/completions` or Ollama `/api/chat`, streamed, with `temperature 0`, `max_tokens` = region size × 1.5, `think: false` (Qwen3 `/no_think`), and `ThinkFilter` still applied.

System message (fixed, so llama-server can reuse its cached prefix):

```
You predict the next edit a programmer will make. You see their recent edits and a region of the file with <|cursor|>.
Rewrite ONLY the region, applying the edit they most likely make next, consistent with their recent edits
(for example continuing a rename). If no edit is likely, return the region unchanged.
Reply with exactly:
<region>
...the region, every line, without <|cursor|>...
</region>
Nothing else.
```

The user message holds, in this order: related excerpts, recent edits (unified diffs, tagged), the rename summary, the file path and language, 30 lines above the region, `<region>` with the region and `<|cursor|>`, and 15 lines below. Parsing takes the text between the first `<region>` and `</region>`. A missing closing tag at the end of the stream counts as truncated, which means no edit. Code fences inside are stripped, as are lines that only contain the cursor marker. The result is diffed against the old region with `PredictionDiff.Edits`. If more than 60% of the lines changed, the answer is rejected as a rewrite, with a status saying so.

### 4.4 Latency and budget

- **Debounce:** near requests keep `DebounceMs`. Far requests wait for 600 ms idle or an accepted prediction, and never run while a near request is in flight. One request is in flight at a time, and a new edit cancels it (`Stop`, as now).
- **Order on one GPU:** near request, then far requests, nearest candidate first. A far result arriving after the document changed is put through `Rebase` and kept when its span is untouched, instead of being dropped.
- **Cache:** `PredictionCache.Key` takes the region text and the newest history event's hash as well as the caret context, because a region that is the same but has different history must not hit. Far results are cached by `(regionText, historyHash)`.
- **Prompt reuse for llama-server:** a stable order of fixed system message, then history, then file text, so each prompt shares its prefix with the last one. Send `cache_prompt: true` and `id_slot` 0 for prediction and slot 1 for Ctrl+I, when the server has two slots. Hex's managed server starts with `--parallel 2 --cache-reuse 256`, only when VRAM allows; otherwise use 1 slot, and Ctrl+I pre-empts prediction. Ollama has `keep_alive` already (`OllamaProvider`, 30 min).
- **Instruct models are slower**, so for `Instruct` kind the near region is 8 lines (not 150 tokens) and `max_tokens` is region×1.3. Status shows the time per request, as it does now.

---

## 5. Instruction-based editing (Ctrl+I)

### 5.1 UX states

`Idle` → `Prompting` → `Streaming` → `Reviewing` → (`Accepted` | `Rejected`) → `Idle`. `Failed` returns to `Prompting` with the text kept.

1. **Target:** the selection, extended to whole lines. With no selection, the innermost function, method or class member round the caret from the outline (the code server's document symbols; otherwise the indentation block), and failing that the current line. The target gets a light background.
2. **Ctrl+I** (command `edit.instruct`, also in the Command Palette and the editor context menu, key `UIKeyUse.BoundWhileTyping`) opens an inline box above the target: a one-line text field, a model name label, and hints `Enter to run · Esc to cancel · ↑ for previous`. It keeps the last 20 instructions per workspace.
   - With an `EditModel` or `Fim` kind model, the box opens disabled with: "*zeta-2.1* can't follow instructions. Pick an instruct model (Qwen3.5, Mellum) in Edit Prediction settings." plus a button that opens the settings.
3. **Enter:** the box turns into a progress line ("Rewriting 24 lines with qwen3.5:9b… 1.2 s") and streams. The target becomes read-only (typing elsewhere is allowed). Near-caret prediction pauses.
4. **Reviewing:** an inline diff over the target (removed lines red with strike, added lines green, the same renderer as the small-diff prediction, extended to many lines) and a bar with `Accept (Tab / Ctrl+Enter)`, `Reject (Esc)`, `Retry (Ctrl+R)`, `Edit instruction (Ctrl+I)`. The diff updates as lines stream in; lines not yet received are shown as old text, dimmed.
5. **Accept:** applied with `document.ApplyEdits` as **one undo step**, recorded in `PredictionHistory` as an accepted event tagged `instruction: "<text>"`. If the result renames something, `RenameTracker` is fed so jumps follow.
6. **Esc during streaming** cancels the request (same `CancellationTokenSource` pattern as the engine) and returns to Prompting. Esc again closes the box.
7. If the target's text changes during streaming, the request is cancelled and the message reads "The code changed while rewriting. Retry?"

### 5.2 Types

- `Hex.Core/Prediction/InstructEdit.cs`: `InstructRequest(Path, Language, Text, TargetStart, TargetEnd, Instruction, Events, Related, Diagnostics)`, `InstructEngine` (one in flight, `Run(request, IChatProvider, IProgress<InstructProgress>, ct)`), `InstructProgress(PartialText, Done, Error)`.
- `IChatProvider` with `IAsyncEnumerable<string> Stream(IReadOnlyList<ChatTurn>, ChatSettings, ct)`, implemented by `OpenAiChat` (add streaming SSE to it; it is `stream:false` today) and `OllamaChat` (`/api/chat` streamed). `LinkProvider` sends it over Hex Link the same way.
- `Hex.Dawn/Code/DawnScene.Instruct.cs` (state machine, keys, box) and `HexInstructGlue.cs` (target selection and turning parsed output into edits, kept pure for tests).

### 5.3 Prompt and output format

System:

~~~
You are a code editing tool. You rewrite the code between <edit> and </edit> to follow the instruction.
Keep everything not asked to change exactly the same, including indentation and comments.
Output ONLY the rewritten code, inside one fenced block:
```<language>
...
```
No explanations.
~~~

The user message holds: `File: <path> (<language>)`, the recent edits (≤1,500 tokens, tagged), language server diagnostics on the target lines (≤10), up to 60 lines above with `<edit>` and the target, `</edit>`, up to 30 lines below, then `Instruction: <text>`.

**Parsing** (`InstructEditFormat.Parse`, streaming-safe):

1. Drop thinking with `ThinkFilter`.
2. Take the first fenced block. If no fence appears within the first 200 characters, accept `<edit>…</edit>` instead. If neither, use the whole answer when it has at least 50% line overlap with the target, otherwise report an error.
3. Strip a repeated first or last context line the model copied from outside the target (compare with the lines just above and below).
4. Re-indent when the model dropped the target's common indentation.
5. Diff with `PredictionDiff.Edits` against the target.

**Errors**, shown in the bar with Retry: "The model answered without code." "The answer was cut off at max tokens (raise *edit_prediction.instruct_max_tokens*)." "Couldn't reach *url*." Plus the existing `PredictionException` messages.

`max_tokens` is the target's token count × 2 + 256, capped by a setting (default 4,096). Temperature is 0.2. Retry uses temperature 0.6 and a new seed.

---

## 6. Phases, tests and risks

Tests go in `hex/Hex.Tests` (xUnit, as `PredictionThinkingTests.cs`), with fake providers using `HttpClient(handler)` as there.

**Phase 1: history foundation**
- Record on every edit (`NoteSoon`), multi-hunk `Diffed`, trimming instead of dropping, 12 events, accepted/typed tags.
- Tests: rename at lines 10 and 80 gives two hunks, both kept; a 3 KB edit is trimmed, not dropped; events with ShortcutOnly; per-file order; `Closed`.

**Phase 2: RenameTracker and NextEditQueue (no model)**
- Detect renames, find remaining occurrences (skipping strings and comments), queue them, `Rebase` on edits, jump keys, gutter arrow, hint, Esc/Shift+Esc, Alt+Shift+L.
- Tests: detection (identifier swap yes, partial word no, typing back cancels); occurrences in C#/TS/Python fixtures; `Rebase` shifts and drops; `JumpHint` text; ordering wraps round; one undo step for accept-all.
- This gives the user the rename behaviour first.

**Phase 3: model kinds and strategy**
- `PredictionModelKind`, `KindOf` (name + `/api/show` + `/props` probe with a cache), `PredictionStrategy.For`, setting `edit_prediction.kind`. Status item shows the kind.
- Tests: name table; probe parsing from recorded JSON; settings override.

**Phase 4: FIM with history, ChatEditFormat and chat streaming**
- History comment in FIM, the rename-aware InWord exception, `PredictionRequest.Turns`, streamed `/v1/chat/completions` and `/api/chat`, `ChatEditFormat` build and parse.
- Tests: prompt snapshots per format; parse `<region>` with fences, think blocks, truncation, rewrite rejection; SSE chat streaming with a fake server; cancellation closes the stream.

**Phase 5: far (jump) predictions from the model**
- `NextEditLocator`, far requests with Zeta regions at candidates and `ChatEditFormat`, merging with renames, cache keys with the history hash, `cache_prompt`/`id_slot`.
- Tests: candidates from history; far request scheduled only when idle and never alongside a near one; merge rules; a stale far result is rebased or dropped; the cache misses when history differs.

**Phase 6: Ctrl+I instruction editing**
- `InstructEngine`, `InstructEditFormat`, `DawnScene.Instruct.cs`, the box, the multi-line inline diff, Accept/Reject/Retry, one undo step, the history event, the disabled state for Zeta/FIM, Hex Link pass-through.
- Tests: target selection (selection, function, line); prompt snapshot; parse fenced, tagged, bare, copied context lines, re-indent; cancel mid-stream; edit during stream cancels; accept is one undo; UI state transitions through `hex_selftest`-style scene tests.

**Phase 7: polish**
- Settings UI (kind, instruct max tokens, rename propagation on/off), docs (`edit-prediction-plan.md` cross-links), status tooltips, telemetry counters (`predictionsShown`, jumps accepted or dismissed) in `PredictionLog`.

### Risks

- **One GPU:** far requests and Ctrl+I compete with near prediction. Strict priority (Ctrl+I > near > far) and cancellation are required. Slot reuse needs `--parallel 2`, which costs KV memory on 12 GB. Fall back to one slot.
- **Instruct models rewriting too much or chatting:** handled by a strict output format, temperature 0, the rewrite-ratio guard and the fence parser. Measure with a small fixture set per model before turning far chat predictions on by default.
- **Rename false positives** (shadowed locals, same name in another scope): only suggest, never apply without Tab. Prefer language server references when available, and skip strings and comments.
- **Probe differences** between Ollama versions and llama-server builds: name-based inference is the fallback, and the setting overrides.
- **Prompt drift from Zed's Zeta formats** when the region moves away from the caret. Keep the caret marker at the candidate line's start. Test against Zeta 2.1 on real renames.
- **Editor work** (multi-line inline diff, jump markers, scrollbar marks) is the biggest UI piece and should share the existing prediction renderer rather than add a second one.
