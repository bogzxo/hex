# Edit prediction: plan

Hex asks a model what comes next as the code is typed, the rest of the line or the edit that follows from the last
one, and shows it as faint text at the caret or as a small diff over the lines it would change. Tab takes it. Hex runs
no model itself: the user points it at a provider, an API URL, a key, a model name and what kind of server it is.

Branch `feat/edit-prediction` in `hex` and in the engine. Checked against the code on 2026-11-21; file and line
references are to `development` at `590eb42` (hex) and `68b7e11` (engine).

## 1. What was learned first

### 1.1 Zed

Zed's settings (the screenshot this was modelled on) are, per provider: API URL, API key (sent as
`Authorization: Bearer`), model, prompt format (`infer` works it out from the model's name) and max output tokens.
Providers are `zed` (their hosted Zeta), `ollama`, `open_ai_compatible_api`, `copilot`, `mercury`, `codestral`. Prompt
formats: `infer`, `zeta`, `zeta2`, `zeta2_1`, `code_llama`, `star_coder`, `deepseek_coder`, `qwen`, `code_gemma`,
`codestral`, `glm`, `sweep`. Tab accepts (in "eager" mode), Alt+L always accepts, there is accept next word and accept
next line, and a prediction that is not at the caret is shown as a diff with Tab first jumping to it.

Zed's source for the prompts is `crates/zeta_prompt/src/zeta_prompt.rs`; a copy is kept while this is built at
`data/agents/temp/scratch/predict/research/zeta_prompt.rs` with `sample.prompt` and `sample.output` from the model's
card.

### 1.2 Zeta 2's prompt (`zed-industries/zeta-2`, Zed's `V0211SeedCoder`)

Zeta 2 is Seed-Coder-8B-Base fine-tuned (8.25 B parameters, Apache-2.0, 32 768 tokens of context). It is a completion
model, not a chat model: the prompt is sent raw. Suffix first, then prefix, then the model writes the middle:

```
<[fim-suffix]>
{the file after the editable region}
<[fim-prefix]><filename>{related/file.cs}
{an excerpt of a related file}

<filename>edit_history
--- a/{path}
+++ b/{path}
@@ -604,7 +604,7 @@
 {context}
-{old}
+{new}

<filename>{path/of/this/file.cs}
{the file before the editable region}
<<<<<<< CURRENT
{the editable region, with <|user_cursor|> where the caret is}
=======
<[fim-middle]>
```

and it answers with the editable region rewritten, ending `>>>>>>> UPDATED\n`. Details that matter:

- A section that is empty is left out with its blank line. The suffix section always ends in a newline. Paths are
  written with forward slashes. An edit the user took from a prediction is preceded by `// User accepted prediction:`.
- Zed's budgets: about 350 tokens of editable region, 150 more of context round it, 4096 tokens of prompt, at most
  6 edit events, tokens estimated at 3 bytes each. Edit history is dropped oldest first, then related files.
- No stop tokens are set by Zed; `>>>>>>> UPDATED` is the natural end and Hex also sends it as a stop sequence.
- The answer often has `<|user_cursor|>` in it, where the caret should be after the edit. It is taken out of the text
  and kept as the place the caret goes.
- A file whose text contains one of the markers on a line of its own (`<<<<<<< CURRENT`, `=======`,
  `>>>>>>> UPDATED`) or any of the special tokens is not predicted for, the prompt would be ambiguous.
- Measured here: the whole editable region comes back, changed or not, so a region of 350 tokens costs 350 tokens of
  output. See 4.4 for stopping early.

Zeta 1 (`zeta`, Qwen2.5-Coder-7B) used `### Instruction` / `### User Edits` / `### User Excerpt` with
`<|editable_region_start|>`, `<|user_cursor_is_here|>` and `<|editable_region_end|>`. Zeta 2.1 uses numbered markers
inside the region and answers with only the span that changed. Both are formats to add after the first version, the
format is one class each (3.3).

### 1.3 Ollama

- `POST /api/generate` `{model, prompt, raw: true, stream: true, options: {num_predict, temperature, stop, num_ctx},
  keep_alive}`. Lines of JSON back, each `{response, done}`, the last with `done_reason`, `eval_count`,
  `prompt_eval_count`, `total_duration`, `load_duration`. `raw: true` stops Ollama wrapping the prompt in the model's
  template. An error is `{"error": "model 'x' not found"}` with a 4xx status.
- `POST /v1/completions` `{model, prompt, max_tokens, temperature, stop, stream}`, the OpenAI shape: server-sent
  events `data: {choices: [{text, finish_reason}]}` ending `data: [DONE]`. An error is
  `{"error": {"message", "type"}}`. The model's template is applied, which for the Zeta 2 GGUF is `{{ .Prompt }}`, so
  it comes out the same.
- Closing the connection stops the generation. That is how a stale request is cancelled.
- Ollama's default context is 4096 tokens whatever the model allows; Hex's prompts stay under that.
- Zeta 2 has no Ollama library entry. It is pulled from Hugging Face:
  `ollama pull hf.co/bartowski/zed-industries_zeta-2-GGUF:Q4_K_M` (5.2 GB on disk, 5.5 GB of VRAM).

Measured on this machine (RTX 3060 12 GB, Ollama 2026-11 on CUDA): 752 tokens of prompt, 256 of output, 5.1 s, about
50 tokens a second, prompt cached between requests (751 of 752 tokens reused). The first request after the model is
loaded took 36 s to load. On the CPU it was 5 tokens a second. So a prediction is only quick when the output is short:
see 4.4.

### 1.4 The second model

`qwen2.5-coder:14b-base-q4_K_M` (9.0 GB), Alibaba's Qwen2.5-Coder, Apache-2.0, the most pulled open code model on
Ollama and the one Zed's own Ollama example uses (at 7B). Nearly twice Zeta 2's parameters and a stronger general
code model; it is not trained on edit history, it fills in at the caret (`<|fim_prefix|>…<|fim_suffix|>…<|fim_middle|>`).
It is what the fill-in-the-middle family is tested with, as Zeta 2 is for the edit family. On 12 GB with the desktop
using 3.6 GB it only just fits, `7b-base` (4.7 GB) is the one to recommend for typing speed.

### 1.5 What other editors do

- **Debounce.** Copilot waits about 75 ms and adapts; Continue 350 ms; Zed 0 by default with its own fast model. A local
  8B model wants 150 to 300 ms so that a burst of typing is one request.
- **Cancel.** VS Code hands every provider a cancellation token cancelled on the next keystroke; everyone aborts the
  HTTP request, because a local server generates one thing at a time and a stale request holds up the fresh one.
- **Typing through.** If what is typed is what the ghost text says, the prediction is kept and shortened, no request.
  Copilot, Zed and Continue all do it; it is the largest saving there is.
- **Cache.** A small LRU keyed by the text before and after the caret, so backspace and retype, or undo, shows the
  old answer at once.
- **Context.** Prefix and suffix round the caret; recent edits (Zed, Cursor); excerpts of files recently looked at or
  edited (Continue, Copilot's neighbouring tabs); language-server definitions of what is near the caret (Zed).
- **Filters.** Nothing while the completion list is open or text is selected; nothing in the middle of a word unless
  the model is an edit model; an answer that only repeats what follows the caret is dropped; one line unless the caret
  is at the end of a line.
- **Accepting.** Tab for all of it, Ctrl+Right for the next word, Escape to dismiss; a prediction away from the caret
  is shown as a diff and Tab goes there first.

## 2. What the user gets

- Faint text after the caret as the typing pauses. Tab takes all of it, Ctrl+Right the next word, Escape or going on
  typing something else makes it go. Typing what it says keeps it.
- More than one line: the rest of the caret's line, then the other lines on rows of their own under it, the code below
  pushed down while it shows.
- An edit that is not an insertion at the caret (Zeta's next edit: the other three call sites of what was just
  renamed): what would go is struck through in the deletion colour, what would come is faint in the addition colour,
  on the lines it changes. Tab applies it as one undo step and puts the caret where the model said. When it is out of
  sight, the status bar item says which line and Tab goes there first.
- A status bar item for code files: predictions on, off for this language, off for this folder, waiting, and what went
  wrong in words (can't reach the server, the key was refused, no such model) with the last latency in its tooltip.
  Pressing it lists the switches and opens the settings.
- Off until switched on, and never for files that hold secrets.

## 3. Architecture

```
Hex.Core/Prediction/           no UI, no engine types, all of it unit tested
  PredictionOptions            what the settings say: provider, url, model, format, max tokens, debounce
  PredictionInput              path, language, the text, the caret's offset, edit events, related excerpts
  IPredictionFormat            Build(input, budget) -> PredictionPrompt;  Parse(prompt, output, complete) -> PredictionEdit[]
    Zeta2Format, FimFormat (qwen, code_llama, star_coder, deepseek_coder, code_gemma, codestral)
  PredictionFormats            by name, and Infer(model name)
  IPredictionProvider          IAsyncEnumerable<string> Complete(PredictionWireRequest, CancellationToken)
    OllamaProvider, OpenAiProvider, LinkProvider (HexPredictionRelay)
  PredictionException          a PredictionStatus and a sentence
  PredictionHistory            the last edits of each file as unified diffs
  PredictionDiff               the old region against the new one -> the fewest edits
  PredictionCache              LRU by what is before and after the caret
  PredictionPrivacy            may this file be sent at all
  PredictionEngine             debounce, cancel, cache, typing through: Edited(...) / Tick(now) / Take()
  HexPredictionRelay           the seam Hex Link serves and reaches predictions through

horizon/Horizon.UI/Components/Controls/CodeEditor.Prediction.cs
  UICodePrediction             the edits to show; SetPrediction / ClearPrediction / AcceptPrediction / AcceptPredictionWord

Hex.Dawn/Code/DawnScene.Prediction.cs      the glue: documents -> engine -> editor, keys, the status bar item
Hex.Core/Settings, Hex.Shell/Settings      the settings and their rows; Hex.Core/Link/HexLinkSecrets for the key
```

### 3.1 Threads

The editor's thread does three things only: notes that the text changed (a version number and a timestamp, no
allocation), every tick asks the engine whether a finished prediction is waiting, and hands the editor a prediction to
draw. Building the prompt (which joins the file's lines), the HTTP request, reading the stream and working out the
diff are a task off the thread. The text is taken for the task once the typing has paused, not on every keystroke:
`UITextDocument` keeps its lines as strings shared with its undo snapshots, so the snapshot is an array of references.

### 3.2 The provider abstraction

```csharp
public enum PredictionStatus { Ok, Off, Waiting, Unreachable, Unauthorized, NoSuchModel, TimedOut, BadAnswer, Refused }

public sealed record PredictionWireRequest(string Prompt, string? Suffix, int MaxTokens, string[] Stop, string Format);

public interface IPredictionProvider
{
    /// The text the model writes, in pieces as it arrives. Throws PredictionException. Cancelling closes the connection.
    IAsyncEnumerable<string> Complete(PredictionWireRequest request, CancellationToken cancel);
}
```

- `OllamaProvider(HttpClient, Uri api, string model)`: `/api/generate`, `raw: true`, `stream: true`,
  `keep_alive: "30m"`, `options.num_predict`, `options.temperature = 0`, `options.stop`. The URL the user gives is the
  server's (`http://localhost:11434`); `/api/generate` is added unless the path is already there.
- `OpenAiProvider(HttpClient, Uri completions, string model, string? key)`: `/v1/completions` with `stream: true`,
  `Authorization: Bearer` when there is a key. A URL ending `/v1` has `/completions` added. This is also what
  llama.cpp's server, LM Studio, vLLM, OpenRouter and Mistral's Codestral endpoint speak.
- `LinkProvider`: `HexPredictionRelay.Remote`, see 3.6.
- One `HttpClient` for all of it, static, made with a `SocketsHttpHandler` (`PooledConnectionLifetime` 5 minutes,
  `ConnectTimeout` 3 s) and no overall timeout; each request has its own deadline (15 s to the first piece, 60 s when
  the model is being loaded, which Ollama says). No `IHttpClientFactory`, Hex has no host or container.
- JSON is written with `Utf8JsonWriter` and read with `Utf8JsonReader`/`JsonDocument` a line at a time, the way the
  rest of Hex does it. Hex is published ahead of time and the tests run with reflection-based JSON switched off.
- Errors become a status and a sentence: connection refused, DNS, TLS -> `Unreachable`; 401/403 -> `Unauthorized`;
  404 or "model … not found" -> `NoSuchModel`; the deadline -> `TimedOut`; anything not JSON -> `BadAnswer`. The key and
  the prompt are never in the sentence or the log.

### 3.3 Prompts by model family

`IPredictionFormat` has a name, `Build` and `Parse`. Formats, chosen by the setting or inferred from the model's name
(`zeta-2`/`zeta2` -> `zeta2`; `qwen`, `codegemma`, `codellama`, `starcoder`, `deepseek`, `codestral` -> theirs;
anything else -> `qwen`'s, the commonest tokens):

| Format | Prompt | Answer |
| --- | --- | --- |
| `zeta2` | 1.2 | the editable region rewritten, diffed against the old one |
| `qwen`, `code_gemma` | `<|fim_prefix|>{before}<|fim_suffix|>{after}<|fim_middle|>` | inserted at the caret |
| `star_coder` | `<fim_prefix>{before}<fim_suffix>{after}<fim_middle>` | inserted at the caret |
| `code_llama` | `<PRE> {before} <SUF>{after} <MID>` | inserted at the caret, ` <EOT>` cut |
| `deepseek_coder` | `<｜fim▁begin｜>{before}<｜fim▁hole｜>{after}<｜fim▁end｜>` | inserted at the caret |
| `codestral` | `[SUFFIX]{after}[PREFIX]{before}` | inserted at the caret |

Fill-in-the-middle formats: `before` is the file's path as a comment line where the language has one, then up to
about 1500 tokens before the caret, cut at a line; `after` up to about 500 tokens. Stop sequences are the family's
end tokens and, when the caret is not at the end of its line, a newline. The answer is trimmed of what repeats the text
after the caret, and dropped when nothing is left.

Zeta 2: the editable region is the caret's line and the lines round it up to 350 tokens (cut at blank lines where
there are any, never mid line), context up to 150 tokens more each way, then the budget in 1.2. Related excerpts
(3.5) go in while they fit.

Tokens are estimated at 3 bytes each, as Zed does. Nothing here tokenises.

### 3.4 From the answer to edits

`PredictionDiff` turns old region and new region into edits: common lines at both ends are dropped, the lines between
are compared by line (Myers, the regions are a few dozen lines), and each changed run is narrowed to the characters
that differ. The result is a list of `PredictionEdit(int Start, int End, string Text)` in offsets of the text that was
sent, at most a handful.

- One edit that inserts at the caret (or replaces only what follows the caret on its line with text that starts the
  same) is **ghost text**.
- Anything else is a **diff**.
- No edit, or only whitespace at the end of lines, is nothing.
- An answer cut short by the token limit: the last, unfinished line is dropped, and the rest is taken as far as it
  goes, the lines after it assumed unchanged.

### 3.5 Context

- **Edit history.** `PredictionHistory` keeps, per file, the text as it was when the last event was closed. When a
  request is built, or the caret has moved more than 8 lines from the last edit, or a second has passed without typing,
  the text now is diffed against it into one unified-diff hunk with three lines of context and kept as an event; at
  most 6 events, at most 1500 bytes each. An accepted prediction is its own event, marked as Zed marks it. Files are
  forgotten as they close.
- **Related files.** The first version sends, while the budget allows, the 30 lines round the last caret of the two
  other code files most recently edited in this run, never one the privacy rules exclude. Definitions from the
  language server of the names near the caret are the next step, not this one.

### 3.6 Predictions through Hex Link

A Mac attached to a desktop over Hex Link can have the desktop's model predict for it. The prompt is built on the Mac
(it has the file, the edits and the caret), sent to the desktop, which passes it to its own provider and streams the
text back; the Mac parses and shows it as it would its own.

```csharp
public static class HexPredictionRelay
{
    /// Set by Hex Link while attached to a device that offers predictions, null otherwise.
    public static Func<PredictionWireRequest, CancellationToken, IAsyncEnumerable<string>>? Remote { get; set; }
    public static string? RemoteName { get; set; }

    /// Whether this Hex offers its predictions to paired devices: the setting, and a provider that is set up.
    public static bool Serving { get; }

    /// What Hex Link calls on the device that has the model. Throws PredictionException.
    public static IAsyncEnumerable<string> Serve(PredictionWireRequest request, CancellationToken cancel);
}
```

- Provider `link` in the settings on the Mac; the status bar says "from <device>". When no device is attached the
  status says so and nothing is sent anywhere else.
- `edit_predictions_share` on the desktop, off by default, user settings only. A device still needs Hex Link's own
  right to it. What the desktop is asked for is refused unless its own provider is a local or a remote server, never
  `link` again.
- Cancelling has to reach the desktop and close its request to the model. Hex Link owns the messages, the right and
  setting the two members; this side owns the seam and a test of it in one process.
- The desktop sees the prompt: code from the Mac's file. It goes over Hex Link's encrypted connection to a device the
  user paired, and the privacy rules of 6 are applied on the Mac before anything is built.

## 4. The life of a request

`PredictionEngine` is a class with no thread of its own and no clock of its own (`TimeProvider` is passed in), so its
tests are exact.

1. **Edited.** The document's `Edited` event (typing, not caret moves) calls `Edited(document, version, now)`. If a
   prediction is showing and the edit typed its next characters, it is shortened and kept (`typing through`); otherwise
   it is cleared, the request in flight is cancelled, and the debounce starts again.
2. **Tick.** Every tick `Tick(now)` is asked. When `edit_predictions_debounce` ms (200 by default) have passed with no
   edit, and the gates of 4.1 are open, one request starts: the editor's thread takes the lines (references), the
   caret and the version, and a task does the rest.
3. **Cache.** The task first looks in the cache (a hash of the 2000 characters before and the 500 after the caret,
   with the model and format): a hit is the answer, no request. 64 entries.
4. **Request.** Built by the format, sent by the provider with a `CancellationTokenSource` linked to the deadline. One
   request at a time: a new one cancels the old one first, and the provider's connection is closed with it.
5. **Stream.** Pieces are appended; see 4.4 for stopping early.
6. **Take.** The finished prediction is put where `Take()` finds it, with the version it was made for. The glue calls
   `Take()` every tick; a prediction whose version is not the document's, whose document is not the one on screen or
   whose caret has moved is thrown away. Otherwise the offsets become `UITextEdit`s and the editor is given them.
7. **Accepted or dismissed.** Accepting applies the edits as one undo step, records the event for the history, and
   asks again at once (no debounce), so the next edit comes while the hand is still on Tab. Escape dismisses and the
   same text is not offered again until something is typed.

### 4.1 When nothing is asked

Predictions off (4.2), the completion list or signature help open, text selected or more than one caret, the document
read-only, a diff or a picture, the file over 1 MB or excluded (6), the caret in the middle of a word with a
fill-in-the-middle format, the window not focused, or the last three requests failed (then one try every 30 s until
one works, so a server that is down is not hammered and the status stays readable).

### 4.2 Whether predictions are on

All of: `HexAi.Off` is false (No AI, the run's own truth); `edit_predictions` is on (user settings only, off by
default, so a cloned folder can't switch it on); the folder's settings have not set `edit_predictions_workspace` to
false; the language is not in `edit_predictions_disabled_languages`; the file is not excluded; a provider is set up
(a URL and a model, or a Hex Link device).

### 4.3 Latency

Measured per request and kept in the status item's tooltip: time to the first piece, time to done, tokens of prompt
and of output, and whether it came from the cache. `HEX_PREDICT_LOG=<file>` writes a line per request with those
numbers and the status (never the prompt, the answer or the key) for the benchmark and for bug reports.

### 4.4 Stopping early

An edit model writes the whole region again, 50 tokens a second here. Three things keep that short:

- `max tokens` is the smaller of the setting (256 by default) and what the region could need (its own tokens plus the
  setting's quarter).
- As the stream arrives, the lines that have come are compared with the old region. Once the answer has changed
  something and then written four consecutive lines the old region has at the right place after the change, the rest
  is taken as unchanged and the request is cancelled. A rename three lines into the region is done after seven lines,
  not forty.
- If the first lines that arrive equal the old region up to and past the caret's line by four lines with no change at
  all, there is no edit near the caret and it stops as "nothing".

Fill-in-the-middle answers stop at the family's stop tokens, at a newline when the caret is mid line, and otherwise at
a blank line after at least one line, or when the indentation falls below the caret line's (the block is finished).

## 5. The editor (engine)

A new partial, `CodeEditor.Prediction.cs`, in the shape of `CodeEditor.AgentHints.cs`:

```csharp
/// What a model predicts is typed next, shown before it is taken: edits of the text, none of them made yet.
public readonly record struct UICodePrediction(IReadOnlyList<UITextEdit> Edits, UITextPosition? CaretAfter = null);

public UICodePrediction? Prediction { get; }
public void SetPrediction(UICodePrediction prediction);
public void ClearPrediction();
public bool HasPrediction { get; }
public bool PredictionIsGhost { get; }          // one insertion at the caret
public int PredictionLine { get; }              // the first line it changes, for "Tab to go to line 40"
public bool AcceptPrediction();                 // one undo step (document.ApplyEdits), the caret where it says
public bool AcceptPredictionWord();             // ghost text only: the next word of it typed, the rest still shown
public Action<bool>? OnPredictionEnded;         // true when it was taken
public bool PredictionTakesTab { get; set; } = true;
```

- **Ghost text on the caret's line**: drawn after the caret in the line-number colour. When there is text after the
  caret on that line, that text is drawn moved along by the ghost's width (the row's draw is split at the caret's
  column the way `DrawWithInlays` splits at an inlay); the caret, hit testing and wrapping do not change, the document
  has not.
- **More lines**: rows that are not in the text, after the caret's line, `CodeRow` gaining a kind beside `Lens`
  (`CodeEditor.Rows.cs:23`), added in `EnsureRows` after the line's own rows, a version of the prediction in the rows'
  cache key, skipped wherever lens rows are (the row loops at `CodeEditor.cs:2872` and `:2984`, the gutter at
  `CodeEditor.Gutter.cs:139`, the minimap at `CodeEditor.Minimap.cs:155` and `:349`, `PositionAt`).
- **A diff**: for each edit, the range that goes has the deletion colour behind it and a line through it (boxes per
  row as `PaintRanges` makes them), and what comes is drawn faint in the addition colour after the range, on rows of
  its own when it has more lines. Colours are the ones the gutter's change marks use.
- **Keys**, in `OnTextInput` before the completion list's: Tab takes it when `PredictionTakesTab`, there is no
  selection and the completion list is not open (the list keeps Tab; a prediction under a list is still there when the
  list closes); Ctrl+Right takes a word of ghost text; a character typed that the ghost text starts with shortens it;
  any other edit ends it. Escape (`OnEscape`) ends it before the hover and the signature box.
- **Ending**: the document's `Changed` ends a prediction that was not shortened by it; so do moving the caret off its
  line, losing focus, and `SetText`.
- **Cost**: the lines of the prediction are split and measured once in `SetPrediction`; paint is indexed loops over
  what was made then, nothing allocated a frame, and nothing at all runs when there is no prediction but one null
  check.
- Hex also needs the settings page's text row to take a secret: `UIFormKind.Secret`, the form's `TextBox` with
  `Secret = true` (`TextBox.cs:174` already draws stars and won't copy).

Tab's order ends up: the completion list, a prediction, extensions (snippets, Emmet: `OnTab`), indent.

## 6. Privacy

- **Opt-in.** Off by default. Switching it on the first time says, once, what is sent and to where: the lines round
  the caret, recent edits and excerpts of recently edited files, to the URL in the settings.
- **What is sent.** Only what 3.3 and 3.5 say, only for the file being typed in, only while it is typed in. Nothing in
  the background, nothing for files only open.
- **Never sent.** A file matching `edit_predictions_exclude` (by default `**/.env`, `**/.env.*`, `**/*.pem`,
  `**/*.key`, `**/*.pfx`, `**/*.p12`, `**/id_rsa*`, `**/id_ed25519*`, `**/*secret*`, `**/*credential*`,
  `**/.npmrc`, `**/.pypirc`, `**/.netrc`, `**/*.kdbx`), a file outside the folders open (scratch and settings files
  included: Hex's own `settings.hor` is never sent), a file over 1 MB, and files `files_exclude` hides. Such a file is
  not in another file's related excerpts either. The patterns are `HexGlob`'s, the user can add to them, and a folder
  can add more but not take any away.
- **Per folder.** `edit_predictions_workspace: false` in a folder's `.hex/settings.hor` turns it off there. A folder
  can't turn it on, change the URL, the model or the key.
- **A remote URL.** When the URL is not this machine (not `localhost`, `127.0.0.0/8`, `::1`) the status item's tooltip
  says the code goes to that host, and plain `http` to a host that isn't local shows a warning in the settings.
- **The key.** Kept in the user settings as `edit_predictions_api_key`, the value protected by the system
  (`HexLinkSecrets.Protect`: DPAPI for the Windows user, a key in the macOS login Keychain, libsecret on Linux, a file
  only its owner can read where there is none) and written as `protected:<base64>`. A key typed into the file by hand
  as plain text is protected the next time the settings are saved. The settings row is a secret box. The environment
  variable `HEX_EDIT_PREDICTION_API_KEY` is used when the setting is empty. The key is sent only to the URL in the
  settings, never logged, never in an error, never over Hex Link, never in a workspace file.
- **No AI.** `--no-ai` and the No AI setting switch all of it off; there is no request and no status item.

## 7. Settings

A new section, **AI**, in the settings window (the agents' rows stay where they are). Keys, all in the editor section
of `settings.hor`:

| Key | Kind | Default | Scope |
| --- | --- | --- | --- |
| `edit_predictions` | flag | off | user |
| `edit_predictions_provider` | `ollama`, `openai`, `link` | `ollama` | user |
| `edit_predictions_api_url` | text | `http://localhost:11434` | user |
| `edit_predictions_api_key` | secret | empty | user |
| `edit_predictions_model` | text | empty (placeholder `hf.co/bartowski/zed-industries_zeta-2-GGUF:Q4_K_M`) | user |
| `edit_predictions_prompt_format` | `infer`, `zeta2`, `qwen`, `code_llama`, `star_coder`, `deepseek_coder`, `code_gemma`, `codestral` | `infer` | user |
| `edit_predictions_max_tokens` | whole, 16 to 2048 | 256 | user |
| `edit_predictions_debounce` | whole ms, 0 to 2000 | 200 | user |
| `edit_predictions_disabled_languages` | list of language ids | `plaintext`, `markdown` | user |
| `edit_predictions_exclude` | list of patterns | 6 | user, a folder adds |
| `edit_predictions_workspace` | flag | on | folder |
| `edit_predictions_share` | flag | off | user |

Each has its title, help, placeholder and search words in `en-gb.hor`, a default in `EditorSettingsCatalog.Usual`
(so the file's own checking knows the key), and the rows under `edit_predictions` depend on it. Changing the provider
to `openai` while the URL is still Ollama's default makes it `http://localhost:11434/v1/completions`, and back.

Commands (palette, rebindable): Toggle Edit Predictions; Toggle Edit Predictions for This Language; Predict Now
(Alt+\\); Accept Edit Prediction (Alt+L, Tab is the editor's own); Accept Next Word of Edit Prediction (Ctrl+Right,
the editor's own); Dismiss Edit Prediction; Edit Prediction Settings.

## 8. Performance

- Nothing per keystroke on the editor's thread but two fields written. No string is built there, no list, no task.
- The debounce means a burst of typing is one snapshot and one request.
- The snapshot is the lines' references; the join, the prompt, the hash for the cache, the HTTP and the diff are off
  the thread.
- One connection kept alive to the server; the model kept loaded (`keep_alive`).
- With no prediction showing the editor's paint has one null check more. With one, what is drawn was measured when it
  was set.
- The self test measures the frame time while a prediction is requested and shown against the same typing with
  predictions off.

## 9. Tests

**Unit (`Hex.Tests`, no network, no model):**
- Formats: Zeta 2's prompt for the model card's example reproduces `sample.prompt` to the byte; budgets drop history
  then related files; a file holding a marker is refused; every fill-in-the-middle family's tokens; inferring the
  format from a model's name.
- Answers: `sample.output` becomes the one insertion it is; the cursor marker is taken out and remembered; an answer
  cut short; an answer that changes nothing; trimming what repeats the text after the caret; CRLF files.
- `PredictionDiff`: insertion, deletion, replacement, two separate changes, whitespace only.
- Providers against a handler in the test (`HttpMessageHandler`): the bodies sent (raw, stream, stop, the bearer
  key), the streams read (lines of JSON, server-sent events, a piece split across reads), every error to its status,
  the key in no message, cancelling closes the response.
- `PredictionEngine` with a `TimeProvider` of the test's own: one request for a burst; a newer edit cancels the one in
  flight; a stale answer is never handed over; typing through keeps and shortens; the cache answers; three failures
  back off; accepting asks again at once; early stop.
- `PredictionHistory`: events coalesce, cap at six, forget closed files.
- `PredictionPrivacy`: the default patterns, a folder adding, files outside the folders.
- `HexPredictionRelay`: a request served and streamed in one process, cancelling reaches the provider, refused when
  not shared.
- Settings: the rows read and written, the key written protected and read back, a folder's file unable to switch
  predictions on or set the URL; the goldens.

**Engine (`Horizon.Tests`):** ghost text set, typed through, taken with Tab (the text, the caret, one undo), a word
taken, Escape; rows added for more lines and gone after; a diff applied as one undo step; the completion list keeping
Tab; nothing shown after an edit that isn't typing through.

**Self test `prediction` (`Hex.Dawn/Testing`)**: a small HTTP server in the test (a `TcpListener` on a loopback port
answering `/api/generate` and `/v1/completions` from a script, able to stall, to fail and to count what it was sent).
The suite types in a C# file, sees ghost text, takes it with Tab, sees a request cancelled when the typing goes on,
sees a diff for an edit away from the caret and takes it, switches the language off, opens an excluded `.env` and
sees nothing sent, points at a dead port and at a 401 and reads the status, and checks the frame time. No model, so it
runs anywhere the other suites do.

**With the models (by hand, recorded in the report):** Zeta 2 and Qwen2.5-Coder through Ollama's own API and its
`/v1`, in a build of this branch driven through its MCP pipe: predictions while typing in a C# file, Tab, cancelling,
a wrong URL and a wrong key, and the latency log.

## 10. Order of work

1. **Core** (`Hex.Core/Prediction`, its unit tests). **Engine** (`CodeEditor.Prediction.cs`, the secret form row, its
   tests). **Settings** (the keys, the section, the strings, the protected key). The three touch different files and
   go at once.
2. **Glue** (`DawnScene.Prediction.cs`, the keys and commands, the status bar item, the self test) on the three
   merged.
3. The models, end to end, the numbers; then Hex Link's side of 3.6 when its branch and this one meet.

Not in the first version: Zeta 1 and 2.1's formats, Zed's hosted service, Copilot, definitions from the language
server as context, several predictions to step through, predictions in notebooks' cells and in the terminal.

## Status

Kept current by the orchestrator (chat #110) at every phase change, so that another agent can take this over. The
copy on the branch (`hexwt-predict/docs/edit-prediction-plan.md`) is the one that is edited; `hex/docs/` has a copy.

**As of 2026-11-21 19:10, phase 1 running, nothing merged yet.**

Worker state at 19:10 (each was told to commit its work in progress and keep a `PREDICT-STATE.md` at the root of its
worktree with done / left / next step / branch / commit; read that first when taking one over):
- #115 engine (`<scratch>/predict/engine-ghost`, `feat/edit-prediction-ghost`): `CodeEditor.Prediction.cs` built to the
  contract of 5, `UIFormKind.Secret` added (FormPanel and SettingRow), 15 new tests green, the full engine suite was
  running. It reported: `Prediction` returns what is left of ghost text after typing through or a word taken;
  `SetPrediction` over a showing one replaces it with no `OnPredictionEnded`; typing the whole ghost ends it as taken;
  losing focus ends it on the next update.
- #114 core (`../hexwt-predict-core`): `Hex.Core/Prediction/` and `Hex.Tests/Goldens/Prediction/` being written,
  nothing committed at 19:10.
- #116 settings (`../hexwt-predict-settings`): rows, catalog, strings, goldens, `HexSettings.Prediction.cs`,
  `Hex.Core/Prediction/PredictionKey.cs` and `PredictionSettings.cs`, `PredictionSettingsTests.cs` being written,
  nothing committed at 19:10.

Done:
- Research and this plan. Commit `88e9687` on `feat/edit-prediction` (hex), which also carries
  `Hex.Core/Link/HexLinkSecrets*.cs` copied unchanged from `feat/hex-link` `1d1cb46` (same path, so the two branches
  merge clean; Hex Link owns those files).
- Ollama installed with winget (`%LOCALAPPDATA%\Programs\Ollama`, serves `http://localhost:11434`, started by its
  tray app; do not start a second `ollama serve` by hand, the one started during the install ran on the CPU).
  Models pulled: `hf.co/bartowski/zed-industries_zeta-2-GGUF:Q4_K_M` (5.5 GB VRAM, about 50 tokens/s) and
  `qwen2.5-coder:14b-base-q4_K_M` (9.5 GB VRAM, about 30 tokens/s). Only one fits the RTX 3060 at a time, Ollama swaps
  them (20 to 40 s to load).
- Baseline build of the worktree against the scratch engine copy is green.

Where things are:
- hex worktrees: `../hexwt-predict` (`feat/edit-prediction`, the branch everything merges into),
  `../hexwt-predict-core` (`feat/edit-prediction-core`), `../hexwt-predict-settings`
  (`feat/edit-prediction-settings`). `hex-extensions` in each is a detached worktree of the live one at `66022c3`.
- engine worktrees, in `hex/Hex/bin/Debug/net11.0/data/agents/temp/scratch/predict/`: `engine`
  (`feat/edit-prediction`, what hex builds against) and `engine-ghost` (`feat/edit-prediction-ghost`).
  `Horizon.HIDL` in each is a detached worktree of the live one at `bf0f436`.
- Build: `dotnet build Hex/Hex.csproj -p:HorizonRoot=<scratch>/predict/engine/ -p:HidlRoot=<scratch>/predict/engine/Horizon.HIDL/`.
- Scratch: `<scratch>/predict/research` (Zed's prompt source, the model card's sample), `<scratch>/predict/scripts`
  (`probe.py`, `fim.py`: latency probes against Ollama).

Running (Opus workers started through Hex by #110, briefs in their chats):
- #114 core: all of `Hex.Core/Prediction` but the two settings files, and its unit tests, including the
  `HexPredictionRelay` seam for Hex Link.
- #115 engine: `CodeEditor.Prediction.cs`, `UIFormKind.Secret`, engine tests.
- #116 settings: the twelve keys, the AI section, strings, `PredictionKey` and `PredictionSettings`.

Next:
1. Review each worker's result, merge core and settings into `feat/edit-prediction`, the ghost branch into the
   engine's `feat/edit-prediction`; flip the line marked `PREDICT-MERGE: UIFormKind.Secret`; build, all unit tests.
2. Phase 2, one Opus worker on the merged branches: `Hex.Dawn/Code/DawnScene.Prediction.cs` (the glue of 4), the
   commands and keys of 7, the status bar item, the self test `prediction` with its fake server (9).
3. End to end by the orchestrator in a build of the branch driven through its own MCP pipe
   (`HEX_REMOTE_PIPE=<unique>`, `HORIZON_AUDIO=off`, `HEX_LINK_KEYSTORE=file`, its own settings folder): Zeta 2 and
   Qwen through `/api/generate` and `/v1/completions`, Tab, cancelling, a bad URL and a bad key, the latency log
   (`HEX_PREDICT_LOG`), screenshots from its own window through MCP.
4. Tell #107 (Hex Link) the commit that has `HexPredictionRelay` and its loopback test; it builds the Link side
   (`HexLinkRights.Predict = 8`, `predict/start`, `predict/chunk`, `predict/done`, `predict/cancel`, the hello's
   `serves: ["predictions"]`) after its wiring lands. Whichever branch merges second carries the glue commit.
5. Report to the lead (#3).

Open questions: none that block. Which Hex Link device is preferred when several serve predictions is first
connected wins until a setting is asked for.
