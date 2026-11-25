# AI edits: right-click → AI → Edit, with a local reasoning model

The person asked for: select code, right-click, an **AI** submenu, **Edit…**, and a local instruct reasoning model doing
the edit, installed and set up for a 12 GB card; "prioritise user interaction and convenience". This plan builds on
`docs/next-edit-plan.md` section 5 (Ctrl+I), which it supersedes where they differ. See also
`docs/edit-prediction-plan.md` (prediction near the caret) and the Status section of `docs/next-edit-plan.md` (keys).

## 1. The model

Researched on Hugging Face, 2026-11-23, every model below fitting 12 GB:

| Model | Released | LiveCodeBench v6 | SWE-bench Verified | Note |
| --- | --- | --- | --- | --- |
| **Mellum2.1 12B-A2.5B Thinking** (JetBrains) | Oct 2026 | **82.0** | 47 | MoE, 2.5B active: fast. Apache-2.0 |
| Qwen3.5 9B (Qwen) | Feb 2026 | 75.4 | 50 | dense, slower. Apache-2.0 |
| Nemotron-Cascade-8B-Thinking (NVIDIA) | Dec 2025 | 71.4 | 38.5 (agentless) | NVIDIA open licence |
| Seed-Coder-8B-Reasoning (ByteDance) | Apr 2025 | below these | – | the one named; superseded, not offered |

**Mellum2.1 Thinking, Q4_K_M (8.07 GB)** is installed to `D:\models`: the best code reasoner that fits, and the fastest.
Q6_K (10.9 GB) would leave no room for the context a reasoning model needs. Seed-Coder-8B-Reasoning is dropped: the
person decided it's too far behind the others to offer.

**One model in the card's memory at a time.** The person's code model may be Zeta 2.1, which can't follow instructions.
AI edits then use the *edit model* (Mellum2.1 by default), swapped in when an edit is asked for and swapped back when
predictions resume. Never both at once:

- llama.cpp: when an edit model is set and differs from the code model, Hex's llama-server runs in router mode with
  `--models-max 1`, from a preset of the two (`[code]` loads at start, `[edit]` on demand). The router unloads one before
  loading the other. With 32 GB of RAM both files stay in the system's file cache, so a swap is a few seconds, not a disk read.
- Ollama: the edit model is another Ollama model name; Ollama swaps by itself when the card can't hold both.
- A code model that can chat (Qwen3.5, Mellum) is its own edit model: no swap at all. That's the recommended setup.

The UI says plainly when a swap will happen ("Mellum2.1 loads in a few seconds; predictions pause while it's in").

## 2. What the person does (interaction first)

### 2.1 Getting to it, the fewest steps

1. **Right-click** in code → **AI ▸** submenu (sparkle icon), at the top of the menu when there's a selection:
   - **Edit…** `Ctrl+I`: the inline box (2.2).
   - Quick actions, one click, no typing, each a fixed instruction:
     **Fix problems here** (shown only with diagnostics on the lines; the problems go in the prompt),
     **Add documentation comments**, **Simplify**, **Add error handling**, **Make it faster**.
   - **Recent**: the last five instructions run in this workspace, one click to run again.
   - A dim last line: the model that will do it, and "loads in a few seconds" when it isn't the one in memory.
2. **Ctrl+I** anywhere in code does the same as Edit…, and the palette has **AI: Edit Selection…** and each quick action.
3. **The target**: the selection grown to whole lines. With no selection, the block round the caret (by indentation:
   the lines of the caret's indentation and deeper, up to the line that opened them), and failing that the caret's line.
   The target gets a faint highlight the moment the box opens, so it's clear what will change.

### 2.2 The box

An inline panel right under the target's first line, over the code, the editor's width less the gutter, never a modal:

- One text field, focused, placeholder "Describe the change, Enter runs it". ↑/↓ go through the recent instructions.
- Under it, chips for the quick actions (click = run with that instruction), and the model's name.
- Enter runs. Esc closes. Clicking away closes it and keeps nothing.
- With no model that can follow instructions, the box says so and offers **Set Up a Model…** (llama.cpp's setup at its
  model step), instead of failing later.

### 2.3 While it works

- The box becomes a progress line in the same place: spinner, then **Loading Mellum2.1…** (when a swap happens),
  **Thinking… 3.1 s** (with the thinking tokens' count growing), then **Writing… 14 lines**. **Esc** or the ✕ cancels at
  once, the request dropped.
- The person can keep typing elsewhere in the file and use the editor; edit predictions pause until the edit is
  reviewed. If the target's own lines are changed meanwhile, the edit is cancelled: "The code changed while it was being
  rewritten. Retry?"
- "Show thinking" folds open what the model thought so far, read only, dim.

### 2.4 Reviewing

- The answer shows in place as the editor's prediction diff: what goes struck through on the deletion colour, what comes
  faint on the addition colour. Nothing in the document changes until it's taken.
- A bar where the box was: **Accept** `Tab` / `Ctrl+Enter` · **Reject** `Esc` · **Retry** `Ctrl+R` · **Refine…** `Ctrl+I`,
  and "+12 −7 lines".
- Accept applies it as one undo step (Ctrl+Z puts the code back), the caret at the end of the change, and goes into the
  edit history as an accepted prediction, so predictions know what was just done.
- Retry asks again with a little more temperature. Refine reopens the box with the instruction kept and the last answer
  given to the model as the conversation so far ("now also…").
- An answer that changes nothing says "The model left the code as it was." and keeps the box open.

### 2.5 Errors, each with its way out

| Problem | Said | Way out |
| --- | --- | --- |
| No instruct model | "Edits need a model that can follow instructions." | Set Up a Model… |
| Server not running | "llama.cpp isn't running." / Ollama unreachable | Start (llama.cpp) / Retry |
| Answer without code | "The model answered without code." | Retry, Show answer |
| Cut short | "The answer was cut off; it was too long for its token limit." | Retry, raise the limit in settings |
| Changed meanwhile | as 2.3 | Retry |

## 3. The prompt and the answer

System (the person's system prompt setting is added after it, when set):

```
You are a code editing tool inside the Hex editor. Rewrite the code between <edit> and </edit> to follow the
instruction. Change only what the instruction asks; keep everything else exactly as it is, including indentation,
comments and names. Answer with the rewritten code only, in one fenced code block, and nothing after it.
```

User message: `File: <path> (<language>)`, the problems on the target lines (at most 10), the recent edits (the edit
history's diffs, at most ~1,500 tokens, newest last), up to 60 lines before, `<edit>`, the target, `</edit>`, up to 30
lines after, `Instruction: <text>`. Refine adds the earlier instruction and answer as earlier turns.

Parsing (pure, streaming-safe, in `InstructEditFormat.Parse`): thinking dropped (`ThinkFilter`, and the server's
reasoning field); the first fenced block; else `<edit>…</edit>`; else the whole answer when most of its lines are the
target's; a first or last line copied from just outside the target dropped; the target's indentation put back when the
model dropped it; diffed with `PredictionDiff.Edits` against the target into edits for the editor.

Limits: max tokens = the target's tokens × 2 + 512, plus the thinking budget (2,048) for a model that thinks; temperature
0.2 (the person's tuning when they set one), 0.6 on Retry.

## 4. Code

Core (Hex.Core/Prediction, the runtime project):

- `InstructEdit.cs`: `InstructRequest(Path, Language, Text, TargetStart, TargetEnd, Instruction, Events, Problems, Earlier)`,
  `InstructEditFormat.Build(request, system) → ChatTurn list`, `InstructEditFormat.Parse(request, answer, complete) →
  InstructParsed(Replacement, Edits, Problem, Done)`, `InstructTarget.Around(text, selectionStart, selectionEnd, caret)`.
- Streaming chat: `ChatPiece(Text, Thinking)`; `OpenAiChat.Stream` (SSE, `delta.content` / `delta.reasoning_content`)
  and `OllamaChat.Stream` (NDJSON, `message.content` / `message.thinking`), both with the model named per request.
- llama.cpp: `LlamaCppServerConfig.EditModel/EditAlias`, router mode with `--models-max 1` and a preset of two.

Dawn:

- `Hex.Dawn/Code/DawnScene.AiEdit.cs`: the state machine (`Idle → Asking → Working → Reviewing`), the AI submenu (hooked
  into `AddCodeMenu`), Ctrl+I and the palette commands, the box and the bar (panels over the editor at its `CaretPoint`
  / the target's first line), the work (cancellable), the review through `CodeEditor.SetPrediction`, Accept/Reject/Retry/
  Refine, pausing edit predictions while it's going, the recent instructions (kept in the workspace's state).
- Settings: `ai_edit_model` (empty = the code model when it can follow instructions, else the catalog's edit model),
  `ai_edit_thinking` (on), `ai_edit_max_tokens` (4,096).

## 5. Phases

1. Core format, target and streaming, with unit tests (prompt shape, every parse path, re-indent, streaming of both APIs
   against a fake server).
2. The edit model: catalog (Mellum2.1, Seed-Coder-8B-Reasoning), settings, llama.cpp router with one model at a time.
3. The interaction: menu, Ctrl+I, box, progress, review, accept/reject/retry/refine, errors, predictions paused.
4. Quick actions and recent instructions.
5. Live test with Mellum2.1 on this machine; full suite.

## 6. Risks

- A reasoning model thinking for long: the progress line shows it's working, the thinking budget caps it, Esc cancels.
- A model rewriting more than asked: the prompt says not to, the diff shows exactly what changes, nothing is applied
  without Accept.
- Swap time between Zeta and the edit model: a few seconds from the file cache, said in the UI; avoided entirely by a
  code model that can chat.
