# Local models in the Agents panel: plan

The Agents panel runs Claude Code, Codex and Antigravity, each a program of somebody else's that Hex starts and reads.
This adds a fourth agent system, **Local**, whose model runs on this machine: Hex itself holds the conversation, asks a
model server for the next step, runs the tools the model calls (Hex's own, the same ones the other agents use over
MCP) and shows it all in the same rows. The first two backends are **Ollama** and **AirLLM**, as the person asked
("do AirLLM and ollama"); any OpenAI-compatible server (llama.cpp's, LM Studio, vLLM, colibri) comes with them for
nothing. Every model's weights live on the data drive, D:.

Branch `feat/local-agents` in `hex` (worktree `hexwt-localagents`). Checked against `development` on 2026-11-21.
Nothing in here is built yet; this is the plan the orchestrator (chat #110) reads before the build starts.

## 1. What was learned

### 1.1 How the Agents panel runs an agent system today

- An agent system is an extension, not code in Hex. `hex-extensions/claude_code`, `codex` and `antigravity` each call
  `hex.agent({ id, name, clients, budget, instructions, run })` (`Hex.Dawn/Extensions/HexExtensions.Agents.cs:32`),
  which stores a profile (`HexAgentProfiles.Set`) and the `run` object (`agentRuns[id]`).
- `run` says `program`, `arguments(prompt, chat)`, `stdin`, `message(text, chat)` (a run that can be steered keeps
  its stdin open), `on_event(chat, event)`, `options` (the pickers under the box, `{ id, title, icon, choices }`, each
  kept in the extension's setting of that id) and `install`.
- `LaunchAgent` / `ContinueLaunch` (`Hex.Dawn/Agents/DawnScene.AgentsPanel.Run.cs:183`, `:208`) find the program
  (`HexAgentProcess.Find`, a whole path is taken as it is, `Hex.Core/Agents/HexAgentProcess.cs:55`), start Hex's MCP
  server if it is off, call `arguments`, start the process in the first workspace folder off the editor's thread
  (`HexAgentProcess.Start`) and hand every JSON line it prints to `on_event` on the editor's thread.
- What `on_event` calls on `chat` (`Hex.Core/Agents/HexAgentChatApi.cs`) is what goes on screen: `text`, `thinking`,
  `note`, `error`, `tool(id, name, input)`, `tool_done(id, ok, output)`, `usage({ input, output, cost, model })`,
  `session(id)` (handed back to `arguments` next turn, which is how a chat resumes), `models([{ id, name }])`,
  `approval(text, input, answered)` (a card with Allow and Deny, `answered(true|false)` called back), `question(...)`,
  `taken`, `done`. `chat` can also write a line to the agent's stdin.
- The agent reaches Hex's tools through `hex.mcp_server(chat)`: `Hex.exe --mcp-connect --folder <first> --agent-chat
  <id>`, the stdio bridge (`Hex.Core/Mcp/McpBridge.cs`) that finds this Hex's pipe in the discovery files, knocks with
  the token and the chat's id (`McpGate.Knock`, `Hex.Core/Mcp/McpSession.cs:215`) and passes JSON-RPC through. A bridge
  that says its chat gets `HexMcpCaller`'s treatment, so `hex_agent_*` calls are known to be that chat's.
- The tools: `tools/list` gives each `McpTool(Name, Title, Description, InputSchema, ReadOnly)`
  (`Hex.Core/Mcp/McpTool.cs`), with `annotations.readOnlyHint`. The input schemas are plain JSON Schema objects with
  `additionalProperties: false` (`McpSchema`), which is exactly what an OpenAI-style function tool's `parameters` is.
  So a tool list for a local model is `tools/list` mapped one to one, no second description of any tool.
- `HexAi.Off` (`Hex.Core/Agents/HexAi.cs`): with No AI on, agent systems' extensions, the panel and agent tools are
  never made.
- `hex.program_output(program, args, then)` runs a program off the editor's thread for at most 20 s and hands its
  output back; Codex and Antigravity use it to list their models.

### 1.2 Ollama (installed here, `0.40.16`, serving `http://localhost:11434`, store `D:\ollama\models`)

- `POST /api/chat` with `messages`, `tools` (OpenAI function shape), `stream: true`, `think`, `options.num_ctx`,
  `keep_alive`. Streaming chunks carry `message.thinking`, `message.content` and `message.tool_calls`; the client
  gathers all three and sends them back with the tool results (`role: "tool"`, `tool_name`, `content`) in the next
  request. The last chunk has `done_reason`, `prompt_eval_count`, `eval_count`, `eval_duration`, `load_duration`.
  https://docs.ollama.com/capabilities/tool-calling , https://docs.ollama.com/api/chat ,
  https://docs.ollama.com/capabilities/streaming
- `think` takes `true`, `false`, or a level for models that have them; `/api/show` says what a model supports.
- `/api/show` gives `capabilities` (`completion`, `insert`, `tools`, `thinking`, `vision`) and `model_info`'s
  `<arch>.context_length`. Probed here: `qwen2.5-coder:14b-base-q4_K_M` says `["completion", "insert"]` and 32768, so
  the two models present are completion models and can't be agents. `/api/tags` lists models, `/api/ps` the loaded
  ones with `size_vram` and `context_length`, `/api/pull` streams a download's progress, `/api/delete` removes one.
- **Context.** Ollama's default context is chosen by VRAM: under 24 GiB it is **4k**, and its own docs say agents and
  coding tools want at least 64000 (https://docs.ollama.com/context-length). Confirmed here: the loaded Zeta 2 runs
  with `context_length: 4096`. Hex must send `options.num_ctx` on every request, or a system prompt and a tool list
  alone fill the window and Ollama silently drops the start of the conversation.
- **OpenAI endpoint.** `/v1/chat/completions` works too, but a report from May 2026 says it hangs with `stream: true`
  and more than a handful of tools (https://github.com/NousResearch/hermes-agent/issues/25629); `num_ctx` can't be set
  through it either. So Hex talks to Ollama on `/api/chat`, and uses `/v1` only for servers that aren't Ollama.
- Closing the HTTP connection stops the generation (learned for edit prediction, same server).

### 1.3 AirLLM (https://github.com/lyogavin/airllm, Apache-2.0, LICENSE checked)

- A Python library, not a server. It splits a Hugging Face checkpoint into one file per layer on disk and runs a
  forward pass by loading one layer at a time onto the GPU, so VRAM needed is about one layer's weights: the README
  claims 70B in 4 GB, DeepSeek-V3 671B in about 12 GB, and for MoE models (since about v3, 2026/06-07) per-expert
  streaming that loads only the experts a token is routed to. Its table puts "Qwen3-30B / Mixtral (MoE)" at about
  1-3 GB of VRAM.
- Use: `pip install airllm`, then `AutoModel.from_pretrained(<HF id or local path>, compression="4bit"|"8bit",
  layer_shards_saving_path=..., delete_original=...)`, then `model.generate(input_ids, max_new_tokens, use_cache=True)`
  with the model's own tokenizer. `compression` is block-wise weight quantisation via bitsandbytes, said to be up to
  3x faster because the disk is the bottleneck.
- Weights: Hugging Face safetensors (bf16 or FP8), split again into layer shards (the split costs about the
  checkpoint's size again on disk; `delete_original` reclaims it). **Not GGUF**, so it can't share Ollama's files.
- No server and no OpenAI API in the project. A third-party wrapper exists (https://github.com/BretMcDanel/airllm-server,
  `/v1/chat/completions`, Linux-oriented, systemd, licence not checked); Hex shouldn't depend on it.
- No chat template handling or tool calling of its own: it is `generate` on token ids. Tool calling is feasible by
  rendering the prompt with the model's own chat template (`tokenizer.apply_chat_template(messages, tools=...)`, which
  Qwen3's templates support) and parsing the model's `<tool_call>{json}</tool_call>` blocks back out.
- **Speed is not published.** Every write-up says the same thing: it makes a big model possible, not fast. Each
  generated token is one pass over every layer, loaded from disk unless the OS cache holds them, and nothing is kept
  between requests, so every agent step prefills the whole conversation again. Estimate for this PC (unmeasured):
  Qwen3-Coder-30B-A3B with 4-bit compression is about 15-17 GB of shards, which fits the 32 GB RAM's file cache, so
  perhaps a second or a few per token; at bf16 (about 61 GB) every token reads from the NVMe, tens of seconds. To be
  measured in phase 4 and said plainly in the panel.
- **Windows:** the README covers Linux and macOS; nothing says Windows is supported or not. torch with CUDA runs on
  Windows; whether bitsandbytes' compression and AirLLM's file handling do is **not verified**. It is the riskiest part
  of this plan and is tried first in phase 4 before any UI is built for it.
- Sources: README (fetched 2026-11-21), https://pypi.org/project/airllm/ , https://huggingface.co/blog/lyogavin/airllm ,
  https://dev.to/umesh_malik/run-70b-llm-on-4gb-gpu-airllms-real-tradeoff-19oh

### 1.4 Colibrì (found, sure: https://github.com/JustVugg/colibri, Apache-2.0, LICENSE checked)

- "colibrì" with the accent is how the project writes its own name ("colibrì: tiny engine, immense model"), and the
  author writes Italian (`README.it.md`, the sample chat says "Ciao!"), which fits the person's spelling. Several forks
  carry the same name (uv-genai/colibri, tekintian/colibri, etchebest/colibri-v2); JustVugg's is upstream.
- A pure-C inference engine for very large mixture-of-experts models: dense weights in RAM, hot experts on the GPU
  (CUDA, Vulkan), warm ones in RAM, cold ones streamed from NVMe. The same idea as AirLLM, aimed at MoE and written for
  speed. Engines for GLM-5.2/5.3, DeepSeek V4 Flash, Kimi K3, MiMo, Qwen3.8-Flash-Next, Qwen3.6 (which also runs
  **Qwen3-Coder-30B-A3B**), OLMoE and more; it reads published GGUF files as they are (`coli import`).
- `coli serve` is an OpenAI- and Anthropic-compatible gateway (Python standard library only) on
  `http://127.0.0.1:8000/v1`, `/v1/models`, `/v1/chat/completions` with SSE streaming, usage, and OpenAI `tools` on most
  engines (OLMoE and Inkling refuse them with a 400). One generation at a time. `COLI_API_KEY` for a key.
- Windows: a prebuilt `colibri-<ver>-windows-x86_64-cuda.zip` with `coli_cuda.dll` for compute capability 8.0 and up
  (RTX 3060 is 8.6), or Vulkan; `START-HERE.bat --dir D:\...` puts models on another disk.
- Its own numbers: Qwen3.6-35B-A3B 30.0 tok/s with CUDA on an RTX 3070 8 GB; Qwen3-Coder-30B-A3B 8.5-9.6 tok/s on a CPU.
- Sources: README, `docs/api.md`, `docs/qwen36.md`, `docs/windows.md` (fetched 2026-11-21);
  https://justvugg.github.io/colibri/ ; https://pasqualepillitteri.it/en/news/7923/colibri-glm-5-2-744b-25gb-ram-en
- In this plan: the person narrowed the first build to AirLLM and Ollama. Colibrì needs no code of its own, it is the
  OpenAI-compatible backend pointed at `coli serve`; phase 5 adds it as a named preset and a test, if wanted.

### 1.5 Which MoE model to test with (RTX 3060 12 GB, 32 GB RAM, ~345 GB free on D:)

Sizes are Ollama's tags pages (https://ollama.com/library/qwen3-coder/tags , https://ollama.com/library/qwen3.6/tags ,
https://ollama.com/library/gpt-oss/tags), fetched 2026-11-21.

| Model | Active / total | Ollama tag, size | Tools | Thinking | Licence | AirLLM |
| --- | --- | --- | --- | --- | --- | --- |
| **Qwen3-Coder-30B-A3B-Instruct** | 3.3B / 30.5B | `qwen3-coder:30b` (= `30b-a3b-q4_K_M`), 19 GB; q8_0 32 GB; fp16 61 GB | yes | no | Apache-2.0 | Qwen3 MoE, in its supported table |
| Qwen3.6-35B-A3B | 3B / 35B | `qwen3.6:35b-a3b`, 23 GB; q8_0 39 GB | yes | yes | Apache-2.0 | 3.6 not named (3, 3.5, 3.8 are); unverified |
| gpt-oss-20b | 3.6B / 21B | `gpt-oss:20b`, 14 GB | yes | yes | Apache-2.0 | MXFP4 weights, not listed |

Others round the web for this size (Gemma 4 26B-A4B, about 16-19 GB) weren't checked closely.

(The person chose Mixtral 8x7B instead, see 1.6; this stays as the recommendation for a model that calls tools well.)

**Recommended: Qwen3-Coder-30B-A3B-Instruct**, because it is the one model all three runtimes run (Ollama, AirLLM's
Qwen3 MoE support, colibri's Qwen3.6 engine), it is trained for agentic coding and tool calls, and it fits: 19 GB at
Q4_K_M is about 10 GB on the GPU and the rest of the experts in RAM. Community numbers for 30B-A3B MoE models on a
3060 with experts offloaded to the CPU range 8-39 tok/s
(https://smeltcore.com/recipes/qwen3-6-35b-a3b-on-rtx-3060-38-9-tok-s-from-a-12gb-card-via-moe-expert-cpu-offload/ ,
https://specpicks.com/reviews/qwen-36-35b-a3b-rtx-3060-12gb-local-2026); expect 10-25 with Ollama here. Qwen3.6 is the
stronger second for Ollama alone (it thinks).

Downloads, all on D: (phase 2 and 4, not this turn):

```
ollama pull qwen3-coder:30b                       # 19 GB into D:\ollama\models (OLLAMA_MODELS)
# AirLLM, the same model as Hugging Face safetensors, ~61 GB, then ~15-17 GB of 4-bit layer shards:
#   HF_HOME=D:\models\huggingface, layer_shards_saving_path=D:\models\airllm\shards, delete_original=True
#   repo: Qwen/Qwen3-Coder-30B-A3B-Instruct
```

Each download is under 100 GB; both together peak at about 100 GB on D: while the shards are made, about 35 GB after
`delete_original`. AirLLM can't use the GGUF Ollama has; colibri could (`coli import` of Ollama's blob).

### 1.6 The model the person chose: Mixtral 8x7B, its smallest version

After reading the above the person chose AirLLM's own example row, "Qwen3-30B / Mixtral (MoE)", then "download only
the Mixtral 8x7B MoE model", then "the smallest version of that model". So the test model is
**`mixtral:8x7b-instruct-v0.1-q2_K`**, 17 GB, pulled through Ollama's API into `D:\ollama\models`.

- Mixtral-8x7B-Instruct-v0.1 (Mistral AI, Apache-2.0, not gated): 46.7B parameters, 12.9B active (2 of 8 experts a
  token), 32k context. Ollama's library marks `mixtral` as having tools; the model itself was not trained for function
  calling the way Qwen3 was, so expect weaker tool calls than 1.5's models (the fallback parser and schema errors
  matter more).
- Its sizes in Ollama (https://ollama.com/library/mixtral/tags): q2_K 17 GB, q3_K_M 23 GB, q4_0 26 GB, q4_K_M 28 GB,
  q8_0 50 GB, fp16 93 GB. q2_K is the smallest and the most damaged by quantisation.
- **AirLLM can't use it.** AirLLM reads Hugging Face safetensors, and the smallest of those for this model is the full
  bf16 checkpoint, 93.4 GB (`mistralai/Mixtral-8x7B-Instruct-v0.1`, 19 `model-*.safetensors`; its `consolidated.*.pt`
  files are the same weights again and are never downloaded). So the AirLLM half of the test needs either that
  download (under 100 GB, 4-bit shards about 25 GB after) or a pre-quantised Hugging Face copy, whose loading by
  AirLLM is unverified. Not downloaded; open question 6.
- What was done on this machine for AirLLM so far (all on D:, per section 7): a standalone Python 3.12.10
  (`D:\models\airllm\python`, NuGet's `python` package, no installer, nothing in the registry), a venv
  `D:\models\airllm\venv` with `huggingface_hub`, pip's cache and temp folder at `D:\models\airllm\pipcache` and
  `D:\models\airllm\tmp`. torch and airllm are not installed yet.

### 1.7 Measured with Mixtral 8x7B q2_K on this machine (2026-11-21)

Pulled through `POST /api/pull` into `D:\ollama\models` (17.3 GB). Probes are stdlib Python scripts run with the D:
Python (`probe_tools.py`, `probe_text_tools.py` in this chat's scratch folder), against Ollama 0.40.16, three tools
shaped like Hex's (`hex_search`, `hex_read`, `hex_edit`) and the question "Where is the class HexAgentProcess
declared? Find it with a tool."

- **Ollama refuses tools for this tag.** `/api/show` gives `capabilities: ["completion"]`, the template is the old
  `[INST] {{ .System }} {{ .Prompt }} [/INST]` with no tool section, and `/api/chat` with `tools` answers
  `400 {"error": "... does not support tools"}`. (The library page marks `mixtral` as having tools; that's the newer
  `mixtral:8x7b` tag's template, not the quantisation tags.) So **a model without native tools must not be left out**:
  the loop falls back to tools described in the system prompt and calls parsed from the text (`ToolCallText`), with
  `</tool_call>` as a stop sequence. Section 2's picker shows such a model with "tools by prompt" rather than hiding it.
- **The fallback works.** Step 1 answered exactly
  `<tool_call>{"name": "hex_search", "parameters": {"query": "HexAgentProcess", "glob": "1"}}</tool_call>`; step 2,
  given the result, answered "The class HexAgentProcess is declared in the file hex/Hex.Core/Agents/HexAgentProcess.cs".
  Two lessons for the parser: accept `parameters` as well as `arguments`, and check arguments against the schema and
  drop junk optional values (`glob: "1"`) with a note back to the model rather than run them.
- **Speed and memory:**

  | Context | Loaded size | On the GPU | Load | Prompt (warm) | Output |
  | --- | --- | --- | --- | --- | --- |
  | 8192 | 18.8 GB | 10.7 GB (57%) | 75 s | 135 tok/s | 7.2-7.4 tok/s |
  | 32768 | 22.0 GB | 10.7 GB (49%) | 55 s | 81 tok/s | 5.2-6.1 tok/s |

  A tool call of about 40 tokens takes 5-8 s, a turn of five tool calls and an answer about a minute. The 32k window
  costs 3.2 GB more (all of it in RAM, the GPU was already full) and about 20% of the speed.
- **Loading it pushed Zeta 2 (the edit prediction model, 5.5 GB) off the card**; afterwards only Mixtral was loaded
  and the card was at 11.4 of 12.3 GB. This is the swap section 2's "Keep loaded" and the VRAM warning are for.

## 2. What the person gets

- A **Local** chip in the Agents panel beside Claude Code and Codex. Its dot says ready (the server answers and the
  model is there), not running (Ollama isn't up, AirLLM's sidecar isn't started) or not set up.
- A **model picker** under the box listing the server's chat models by name, with size and where it runs ("19 GB,
  52% GPU") once loaded. Those whose `capabilities` has `tools` use native tool calls; chat models without it
  (Mixtral's quantised tags, 1.7) are marked "tools by prompt" and use the text fallback; completion-only models
  (Zeta 2, Qwen2.5-Coder base) are left out with a line saying why.
- The chat as with the other agents: what the model says as Markdown, its thinking dimmed, each tool call a row with
  its icon, input and output, approvals as cards with Allow and Deny.
- **Load state**: "Loading qwen3-coder:30b…" with a spinner while the server loads the model (Ollama's
  `load_duration`, AirLLM's shard split the first time, which can take many minutes and says so), then the turn.
- Under the chat the usual quiet line, plus **tokens a second** for local models (`eval_count / eval_duration`) and
  the **context used of the window** ("9.1k of 32k"), which the line already has room for.
- A palette command **Manage Local Models**: the models on the server, their sizes and the folder they're in, pull
  one by name with progress, delete one, and the AirLLM models with their shard folders.
- **Which models are loaded is the person's choice.** The model manager lists every model with a switch, "Keep
  loaded": the agent's model, the autocomplete model, both, or neither (loaded when asked, let go after
  `keep_alive`). Hex loads the kept ones when it starts (an empty `/api/generate` with `keep_alive: -1`), lets the
  others go (`keep_alive: 0`), and shows what is loaded now from `/api/ps`: size, how much of it is on the GPU
  ("17 GB, 58% GPU"), the context it was loaded with.
- **An automatic low-VRAM warning.** Before a model is loaded or kept, Hex adds up what it will need (the model's size
  from `/api/tags`, its KV cache for the chosen context from `/api/show`'s layer and head counts, the other kept
  models from `/api/ps`) against the card's free memory (`nvidia-smi --query-gpu=memory.total,memory.used` on NVIDIA,
  DXGI's adapter budget elsewhere on Windows, Metal's recommended working set on macOS). When it won't fit, a warning
  in the model manager, on the chip and as a note in the chat says by how much, what that costs ("about 7 GB will run
  on the CPU, expect a few tokens a second") and what would fit (a smaller context, a smaller quantisation, not
  keeping the autocomplete model loaded). It warns, it doesn't refuse. On this machine every candidate warns: the card
  is 12 GB and the desktop holds about 3.6 GB of it.
- **The agent's model for autocomplete too.** A switch in the AI settings, "Use the agent's model for edit
  predictions", makes edit prediction ask the agent's model (same server, same model, so one model in VRAM instead of
  two) with a chat-style fill-in prompt, in place of its own completion model. The trade, said in the setting's help:
  a 17-28 GB MoE model writes a prediction much slower than Zeta 2 (5.5 GB, fully on the GPU, about 50 tokens a second
  here), so predictions come later. See 3.8.
- Everything works offline. Nothing leaves the machine unless the URL is set to another one.

## 3. Architecture

### 3.1 The choice: Hex runs the loop, as a program of its own

Two ways were weighed.

1. **A new kind of run inside the editor**, no process: Dawn holds the loop, calls the model on a task, calls tools
   directly. Needs a second path through `AgentsPanel.Run.cs` (start, steer, stop, approvals, resume) for a run with
   no process, which is exactly the file Hex Link's work (#107) is also changing, and a hung HTTP read or a bug in the
   loop is inside the editor.
2. **`Hex.exe --local-agent`, a mode of Hex's own program**, started by an extension like any other agent system:
   it talks HTTP to the model server, speaks JSON lines on stdout, takes messages and approval answers on stdin, and
   reaches Hex's tools through the same pipe the MCP bridge uses. Hex's panel can't tell it from Claude Code.

**Recommended: 2.** It reuses every part of the panel as it is (finding, starting, stopping and killing a run,
steering, the rows, approvals, resume through `session`), needs no change to `AgentsPanel.Run.cs`, keeps a hung model
server out of the editor, is unit-testable without a window, and ships inside the AOT `Hex.exe` (no new binary). The
extension stays the place an agent system is described, as `docs/context/core-and-mcp.md` §7 says they are.

```
Agents panel ──starts──> Hex.exe --local-agent --folder F --agent-chat 12 --settings <json> [--resume S]
                            │  stdout: JSON lines (text, thinking, tool, tool_done, usage, approval, session, done)
                            │  stdin:  {"type":"message"} {"type":"approval","id":..,"allow":true} {"type":"stop"}
                            ├── HTTP ──> Ollama /api/chat   |  OpenAI-compatible /v1/chat/completions
                            │                                  (llama.cpp, LM Studio, vLLM, colibri, AirLLM sidecar)
                            └── named pipe (McpBridge.Resolve + McpGate.Knock, tag = chat id) ──> Hex's MCP tools
```

### 3.2 Pieces

```
Hex.Core/Ai/                         shared with edit prediction (see 3.7), no UI
  HexAiHttp                          the one static HttpClient (SocketsHttpHandler, ConnectTimeout 3 s, no overall timeout)
  HexAiEndpoint                      URL normalising (/api, /v1), IsLocal(host), the key from HexLinkSecrets
  HexAiError                         status + sentence (Unreachable, Unauthorized, NoSuchModel, TimedOut, BadAnswer,
                                     ContextFull, NoTools); never the key or the prompt in the sentence
  OllamaApi                          tags, show (capabilities, context length), ps, pull (progress), delete
Hex.Core/LocalAgent/
  LocalAgentOptions                  url, kind, model, context, keep_alive, think, tools set, approval mode, key
  ChatMessage / ToolCall / ToolSpec  the conversation, AOT-safe, written with Utf8JsonWriter
  IChatBackend                       IAsyncEnumerable<ChatDelta> Chat(messages, tools, options, ct)
    OllamaChatBackend                /api/chat, num_ctx, think, keep_alive, tool_calls whole per chunk
    OpenAiChatBackend                /v1/chat/completions SSE, tool_call deltas joined by index, reasoning_content
  ChatDelta                          Thinking | Text | ToolCall | Usage(prompt, output, seconds, load) | Done(reason)
  ToolCallText                       fallback parser: <tool_call>{..}</tool_call>, ```json {"name","arguments"}```
  LocalAgentTools                    tools/list -> ToolSpec (the chosen set), tools/call over the pipe, output cut
  LocalAgentContext                  token estimate, budget, trimming and compaction (section 5)
  LocalAgentLoop                     the turn: ask, stream rows out, run tools (approved), ask again, until no call
  LocalAgentStore                    the conversation file of a session in HexData.AgentTemp (resume)
  LocalAgentProtocol                 the stdout lines and stdin messages
Hex.Core/Mcp/McpClient               a small MCP client over a stream: initialize, tools/list, tools/call (new;
                                     today Hex is only ever the server or a byte pump)
Hex/Program.cs                       --local-agent dispatched beside IsBridgeMode
hex-extensions/local_models/
  extension.hor                      hex.agent({ id: "local", ... run }), options, models, approvals, commands
  airllm_server.py                   the AirLLM sidecar (section 4.3)
Hex.Dawn/Agents (small)              tokens a second and context in the turn line; nothing in Run.cs
```

### 3.3 The extension (`hex-extensions/local_models/extension.hor`)

- `hex.agent({ id: "local", name: "Local", clients: ["hex-local"], budget, instructions, run })`, guarded by
  `hex.agent == null` as the others are.
- `run.program`: `hex.executable`. `run.arguments(prompt, chat)`: `--local-agent --folder <first> --agent-chat <id>`,
  `--resume <session>` when the chat has one, and `--options <file>`: the chat's picks (model, approval mode, think)
  written to `hex.temp/local-<chat>.json` so the command line stays short and holds nothing secret.
- `run.message(text, chat)`: `{"type":"message","text":..}`, so the person can steer and a chat goes on in the same
  process between turns, the way Codex's app-server does.
- `run.on_event(chat, event)`: a direct mapping, the process already speaks in the panel's words
  (`text`, `thinking`, `tool`, `tool_done`, `usage`, `session`, `note`, `error`, `done`). For `approval` it calls
  `chat.approval(text, input, answered)` and `answered` writes `{"type":"approval","id":..,"allow":..}` back.
- `run.options`: Model (choices from `chat.models`, filled by `hex.program_output(hex.executable, ["--local-agent",
  "--models"])`, which prints the server's tool-capable models as JSON), Approvals (`ask`, `edits`, `all`), Think
  (`on`, `off`, for models that have it).
- Extension settings stay the non-secret ones; the URL, key and context are Hex's own settings (section 6) because the
  key has to be protected and a workspace mustn't be able to point an agent at another server.

### 3.4 The turn

1. Read the conversation (new, or the stored session), the system prompt and the tool list.
2. System prompt: the profile's `instructions`, `hex.harness_rules` and `plan_rules` as the other systems get them, the
   workspace folder, the date, and a short paragraph on how to call tools for small models (one call at a time, read
   before editing, paths relative to the folder).
3. Ask the backend. As deltas arrive: thinking and text go out as `thinking` and `text` lines in pieces (the panel
   joins them); tool calls are gathered.
4. No tool calls: the turn is over, `usage` then `done`, the conversation saved.
5. Tool calls: each one is checked (known tool, arguments parse as JSON and pass the schema; a bad one goes back to
   the model as the tool's error, which small models need often), approved (3.5), run over the pipe with a
   `max_chars` that fits the budget, its output cut to the budget and sent out as `tool`/`tool_done` lines, then added
   to the conversation as a `tool` message. Back to 3.
6. At most `local_agents_steps` model calls a turn (40 by default), so a model looping on the same call ends; the same
   call with the same arguments three times in a row is stopped with a note.
7. Stop (Escape in the panel): Hex kills the process as it does any agent's; the HTTP request goes with it, which stops
   the generation on the server. What was said so far is kept because the conversation is saved after every step.

### 3.5 Hex's tools and approvals

- **Which tools.** The full list is about a hundred tools and their schemas are some 30k tokens, more than a small
  context holds. The default set is the harness and editing ones a coding agent needs: `hex_map`, `hex_files`,
  `hex_search`, `hex_symbols`, `hex_refs`, `hex_skim`, `hex_read`, `hex_edit`, `hex_write`, `hex_diagnostics`,
  `hex_git_diff`, `hex_terminal_run`, `hex_plan_update`. A setting picks `core` (that set), `core+agents` (adds
  `hex_agent_*`) or `all`. The tool's descriptions come from `tools/list` unchanged.
- **Approvals.** The loop decides, before calling, from `readOnlyHint`:
  - read-only tools run at once;
  - `ask`: every tool that changes something (`hex_edit`, `hex_write`, `hex_terminal_run`, `hex_rename` ...) asks
    with an `approval` line and waits on stdin;
  - `edits`: file edits run, commands ask;
  - `all`: nothing asks.
  A command for `hex_terminal_run` that reads the workspace the long way gets the harness guard's answer
  (`HexHarnessGuard.ForCommand`, as `hex.harness_check` gives Codex) back as the tool's error, with the Hex tool to use.
  A denied call goes back to the model as "The person declined this", so it can try something else.
- Hex's own MCP side applies what it applies to every agent (the token, the chat's tag, `HexMcpCaller`, the tools'
  own limits), so a local model has no more reach than Claude Code has.

### 3.6 The protocol on stdout (one JSON object a line)

```
{"type":"session","id":"local-7f3c"}
{"type":"note","text":"Loading qwen3-coder:30b…"}
{"type":"thinking","text":"…"}            {"type":"text","text":"…"}
{"type":"tool","id":"c1","name":"hex_read","input":{…}}
{"type":"approval","id":"c2","text":"Edit Hex.Core/Foo.cs","input":{…}}
{"type":"tool_done","id":"c1","ok":true,"output":"…"}
{"type":"usage","input":9120,"output":412,"model":"qwen3-coder:30b","context":9532,"limit":32768,"seconds":21.4,"load":0}
{"type":"error","text":"Ollama isn't running at http://localhost:11434"}
{"type":"done"}
```

### 3.7 What is shared with edit prediction (`feat/edit-prediction`), not built twice

- **The HTTP client and errors.** Edit prediction's plan (§3.2) already makes one static `HttpClient` with a
  `SocketsHttpHandler`, per-request deadlines and an error-to-status mapping. Move those to `Hex.Core/Ai/` as
  `HexAiHttp` and `HexAiError` and use them from both. Whichever branch lands first makes them; the other rebases on it.
- **Ollama's model API.** `/api/tags`, `/api/show`, `/api/ps`, `/api/pull` are wanted by both (prediction lists
  completion models, agents list tool models): one `OllamaApi`.
- **The key.** `HexLinkSecrets.Protect` (`protected:<base64>`) and the secret settings row (`UIFormKind.Secret`) that
  prediction adds. The agent's key is its own setting (a different server may be in use), stored the same way.
- **The AI section of the settings.** Prediction adds a section "AI"; local agents' rows go in it under their own
  heading, not in a second section.
- **The URL rules.** Local vs remote (`localhost`, `127.0.0.0/8`, `::1`), the warning for plain `http` to another
  host, user scope only: one `HexAiEndpoint`.
- **Model folder.** One setting, `ai_models_folder` (section 7), used by both for anything Hex downloads itself.
- **Loaded models and VRAM.** One `HexAiModels` (what is kept loaded, what is loaded now, what a model needs, the
  card's free memory, the warning) serves the model manager, the agent chip and prediction's status item.

### 3.8 The agent's model for autocomplete

Edit prediction's providers ask a completion model with a raw prompt (`/api/generate`, `raw: true`, a fill-in-the-middle
or Zeta format). An instruct model like Mixtral has no fill-in-the-middle tokens, so the switch adds a format to
prediction's `IPredictionFormat` list, `chat`: the file's path, the lines before the caret, a marker, the lines after,
and one instruction ("Write only the code that goes at <CURSOR>"), sent to `/api/chat` with `num_predict` small and
`temperature` 0, the answer stripped of fences. When the switch is on, prediction's model setting is greyed and shows
the agent's model, its provider and URL are the agent's, and the agent and prediction share the one loaded model
(Ollama serves one request at a time per model by default, so a prediction waits while the agent generates; the status
item says "waiting for the agent"). Lives in `feat/edit-prediction`'s `Hex.Core/Prediction` as one more format and
provider choice, so it is built after that branch lands.

## 4. Backends

### 4.1 Ollama

`/api/chat`, `stream: true`, `options.num_ctx` (the setting, capped at the model's own `context_length`),
`keep_alive: "30m"` (setting), `think` when the model has `thinking` and the option is on, `tools` mapped from MCP.
Tool calls come whole in a chunk (`function.name`, `function.arguments` as an object); ids are made by Hex. Tool results
go back as `role: "tool"` with `tool_name`. The model list is `/api/tags` filtered by `/api/show`'s `capabilities`
containing `tools`. A model that isn't pulled: the error says so and offers the pull command in the panel. Ollama is
never started by Hex (the person's rule: never a second `ollama serve`); when it isn't running the chip says so.

### 4.2 OpenAI-compatible

`/v1/chat/completions`, SSE, `tools`, `tool_choice: "auto"`, `max_tokens`, `stream_options.include_usage`. Tool calls
arrive as deltas by `index` (id and name first, arguments in pieces) and are joined. `reasoning_content` (llama.cpp,
vLLM, colibri) is thinking. `Authorization: Bearer` when there's a key. Models from `/v1/models`. This covers llama.cpp's
`llama-server`, LM Studio, vLLM, colibri's `coli serve` and Hex's own AirLLM sidecar. When a server says nothing of
context, the setting is used as the window.

### 4.3 AirLLM: a sidecar Hex ships and starts

- `hex-extensions/local_models/airllm_server.py`, about 300 lines, standard library `http.server` plus `airllm`,
  `transformers` and `torch` (which the person installs, see 7.2). It serves `/v1/models` and
  `/v1/chat/completions` (SSE), bound to `127.0.0.1` on a free port, with a random bearer token Hex passes it on stdin,
  so nothing else on the machine can use it.
- The request's messages and tools are rendered with the model's own chat template
  (`tokenizer.apply_chat_template(messages, tools=tools, add_generation_prompt=True)`), generated with AirLLM, streamed
  out token by token (a `TextIteratorStreamer` if AirLLM's `generate` takes one; if not, as one piece at the end, which
  is the first thing phase 4 finds out), and `<tool_call>` blocks parsed into OpenAI `tool_calls`.
- Started by Hex when a chat with the AirLLM backend asks and nothing answers on its port, with `HF_HOME` and the
  shard folder under the models folder on D:, `compression` from the setting (`4bit` default), and kept running for
  `keep_alive`; stopped with Hex. One request at a time. It says its progress (downloading, splitting layers, loading)
  as `/health` lines Hex turns into the load state.
- The Python to use is a setting (`local_agents_airllm_python`); its venv is the person's (open question 2).
- What it can't do, said in the panel: it is slow (section 1.3), every step starts over (no cache between requests),
  and the context is what the setting says, kept small (8k by default for AirLLM).

### 4.4 Colibrì

The OpenAI-compatible backend with URL `http://127.0.0.1:8000/v1`. Phase 5 adds a preset with that URL, checks its
tools on Qwen3-Coder, and says in the model manager how to start it with `--dir D:\models\colibri`.

## 5. Context for small windows

- **The window** is the setting (`local_agents_context`, 32768 by default for Ollama, 8192 for AirLLM), capped by the
  model's own. 32k for Qwen3-Coder-30B-A3B costs about 3 GB of KV cache at f16 (48 layers, 4 KV heads of 128), which
  moves a few more experts to the CPU; the setting says so in its help.
- **Measured, not guessed.** Ollama returns `prompt_eval_count` each step; until the first answer, 3 bytes a token is
  the estimate, as prediction uses.
- **Tool output** is cut to fit: Hex's harness tools take `max_chars`, which the loop sets to the smaller of 6000 and
  a fifth of what's left; anything else is cut in the middle with a note saying how much was left out and how to ask
  for less (a line range, a symbol).
- **Old tool results go first.** Once the context passes 60% of the window, tool outputs older than the last four
  steps are replaced by one line ("hex_read Foo.cs:10-80, 2.1 KB, left out to save room").
- **Compaction.** Past 80%, the model is asked (no tools) to summarise the conversation so far in 300 words, files and
  decisions kept; the conversation becomes system, summary, the last user message and the last two steps. The panel
  gets a note, and the chat's `Compactions` count goes up as Claude Code's do.
- **The tool list costs too**: the core set is about 4k tokens; `all` warns when the window is under 64k.
- A request that is still too big gets `ContextFull` and a sentence saying to raise the window or start a new chat.

## 6. Settings

In the AI section of the settings window, user scope only (a workspace's `.hex/settings.hor` can't set any of them, as
prediction's URL and key can't be). Each with title, help and search words in `en-gb.hor` and a default in the catalog.

| Key | Kind | Default |
| --- | --- | --- |
| `local_agents_backend` | `ollama`, `openai`, `airllm` | `ollama` |
| `local_agents_api_url` | text | `http://localhost:11434` (`http://127.0.0.1:8000/v1` shown as a colibri example) |
| `local_agents_api_key` | secret, `protected:` | empty; env `HEX_LOCAL_AGENT_API_KEY` when empty |
| `local_agents_model` | text (the picker writes it) | empty, the first tool model when empty |
| `local_agents_context` | whole, 4096 to 262144 | 32768 (AirLLM 8192) |
| `local_agents_keep_alive` | text | `30m` |
| `local_agents_tools` | `core`, `core+agents`, `all` | `core` |
| `local_agents_approvals` | `ask`, `edits`, `all` | `ask` |
| `local_agents_steps` | whole, 5 to 200 | 40 |
| `local_agents_airllm_python` | path | empty (then `python` on PATH) |
| `local_agents_airllm_compression` | `none`, `8bit`, `4bit` | `4bit` |
| `ai_models_folder` | path | empty; set to `D:\models` on this machine |
| `ai_models_kept_loaded` | list of model names | empty (each loaded when asked) |
| `ai_vram_warning` | flag | on |
| `edit_predictions_use_agent_model` | flag | off |

The key is passed to `--local-agent` by neither argument nor file: the process reads the protected setting itself
(same user, same machine), so it is never in a command line, a temp file or a log.

## 7. Models on D:

### 7.1 Where each runtime keeps weights

| Runtime | Where | How Hex keeps it on D: |
| --- | --- | --- |
| Ollama | `OLLAMA_MODELS` = `D:\ollama\models` (already) | Hex pulls through Ollama's API, so the files land where Ollama keeps them. The model manager shows that folder (from `ollama`'s environment where it can be read, else says "Ollama's folder") and warns when it is on the system drive. |
| AirLLM | `HF_HOME` and `layer_shards_saving_path` | Set by Hex when it starts the sidecar: `<ai_models_folder>\huggingface` and `<ai_models_folder>\airllm\<model>`; nothing under `%USERPROFILE%\.cache`. |
| colibri | its `--dir` | Shown in the model manager's instructions: `START-HERE.bat --dir D:\models\colibri`. |
| Edit prediction | Ollama's store | Already D:. |

- `ai_models_folder` empty means Hex won't download for AirLLM and says why; it is not defaulted to D: in code (other
  machines have no D:), but the person's settings set it.
- Before a download Hex checks the free space of the target drive against the model's size and says both.

### 7.2 Everything for AirLLM on D: too, the Python runtime and CUDA included

The person's rule: all of it on D:, not only the weights. The layout, under `ai_models_folder` (`D:\models`):

```
D:\models\airllm\python      a standalone CPython 3.12 (NuGet's "python" package, unzipped: no installer, no registry)
D:\models\airllm\venv        torch (CUDA build; its wheels carry the CUDA runtime, cuBLAS and cuDNN DLLs, so no CUDA
                             toolkit is installed anywhere), airllm, transformers, bitsandbytes, huggingface_hub
D:\models\airllm\pipcache    PIP_CACHE_DIR
D:\models\airllm\tmp         TMP and TEMP while installing and running (pip and torch unpack gigabytes there)
D:\models\airllm\shards      layer_shards_saving_path
D:\models\huggingface        HF_HOME, the downloaded checkpoints
D:\ollama\models             Ollama's store (OLLAMA_MODELS, the person's)
```

Hex's model manager sets the runtime up itself on request ("Set up AirLLM"): downloads the Python package into that
folder, makes the venv, runs pip with `PIP_CACHE_DIR`, `TMP`, `TEMP` and `HF_HOME` pointed there, and starts the
sidecar with the same environment, so nothing lands under `%USERPROFILE%`, `%LOCALAPPDATA%` or `%TEMP%` on C:. The
only thing on C: is the NVIDIA driver, which is the system's. Set up by hand on this machine as far as the venv
(1.6), to prove the layout before it is code.

## 8. No AI and privacy

- **No AI** (`HexAi.Off`): the extension is an agent system, so it is never made; `Hex.exe --local-agent` also checks
  the setting and exits with "No AI is on" before any request; the AirLLM sidecar is never started; the model manager
  command doesn't exist.
- **What is sent:** the conversation, which holds code the tools read, to the URL in the settings. By default that is
  this machine. A URL that isn't local is shown on the chip's tooltip ("code goes to host:port") and plain `http` to
  another host is warned about in the settings, as for prediction.
- **The key** as 6 says; never in an error, a note or the agent log.
- **The sidecar** binds to loopback with a random token.
- **The conversation files** are in `HexData.AgentTemp`, where Agent Data measures and tidies the other systems'.
- **Logs** say what was asked of which server and how long it took, never the prompt.

## 9. Tests

**Unit (`Hex.Tests`, no network, no model):**
- `OllamaChatBackend` against an `HttpMessageHandler` of the test's: the body sent (`num_ctx`, `think`, `keep_alive`,
  tools mapped from an MCP schema), the stream read (thinking, content, a tool call, the final counts, a chunk split
  across reads), errors to their status (refused, 404 model, 400 "does not support tools"), the key in no message,
  cancelling closes the response.
- `OpenAiChatBackend`: SSE tool-call deltas joined by index (two calls in one answer, arguments in five pieces),
  `reasoning_content`, `[DONE]`, usage, a server that sends no usage.
- `ToolCallText`: Qwen's `<tool_call>`, fenced JSON, nothing, half a block.
- `LocalAgentLoop` with a scripted fake backend and a fake `IMcpTools`: a call, its result, the answer; an unknown tool
  and bad arguments go back as errors; approvals asked, allowed, denied (read from a fake stdin); `edits` mode; the
  step limit; the same call three times; stop mid-stream keeps what was saved.
- `LocalAgentContext`: old tool results dropped at 60%, compaction at 80%, `max_chars` given, a too-big request.
- `LocalAgentStore`: save, resume, a damaged file starts afresh with a note.
- `McpClient` against an in-memory `McpServer` with two tools: initialize, list, call, an error result.
- The extension: `arguments`, `on_event` mapping and approval answers, in the style of `HexAgentSystemsTests`.
- Settings: the rows, the key protected, a folder's file unable to set any of them, the goldens.
- The sidecar's parsing (`airllm_server.py`'s tool-call parser and SSE writer) with a fake model, run by a small
  Python test when Python is there, skipped when not.

**Self test `local_agent` (`Hex.Dawn/Testing`):** a loopback `TcpListener` scripted as Ollama (`/api/tags`, `/api/show`,
`/api/chat` streaming a thinking piece, a `hex_read` call, then an answer; another turn asking to edit). The suite picks
Local in the Agents panel, sends a message, sees the thinking, the tool row with its output, the answer, the usage line
with tokens a second; then the edit turn's approval card, Deny, the model's reply; Allow, the file changed; Stop in the
middle of a stream; a dead port's error. No model, runs anywhere the other suites do, from the real `hex/Hex/bin`.

**With real models (by hand, results in this file):**
- Ollama, `qwen3-coder:30b`: tokens a second at 8k, 32k and 64k context, VRAM and RAM used, and three tasks in this
  repository (find where a setting is read and explain it; add a doc comment to a method and build; fix a planted
  compile error using `hex_diagnostics`), each with how many steps, how long, and whether it got there.
- AirLLM, `Qwen/Qwen3-Coder-30B-A3B-Instruct` 4-bit: the first-time split's time and disk, seconds a token, the first of
  the three tasks. If it is too slow to finish a task in 30 minutes, that is the result, written down.
- Optional, colibri on the same model.

## 10. Phases and files

Files under `Hex.Dawn/Agents/` that Hex Link (#107) is also changing are marked ⚠; this plan avoids `AgentsPanel.Run.cs`,
`AgentsPanel.Chats.cs` and the fleet lists entirely.

**Phase 1, core (no UI).** `Hex.Core/Ai/*` (or taken from `feat/edit-prediction` if it has landed),
`Hex.Core/LocalAgent/*`, `Hex.Core/Mcp/McpClient.cs`, `Hex/Program.cs` (`--local-agent`, `--models`),
`Hex.Core/Mcp/McpBridge.cs` (make `Resolve` and `Open` usable from the agent, no change of behaviour), unit tests.
Done when the fake-server tests pass and `Hex.exe --local-agent` drives a real Ollama from a terminal with a
throwaway Hex window.

**Phase 2, the agent system.** `hex-extensions/local_models/extension.hor` (a new folder in the extension catalog
submodule, its own commit there and the pointer in hex), settings keys and rows (`Hex.Core/Settings/*`,
`Hex.Shell/Settings/*`, `en-gb.hor`), tokens a second and context in the usage line
(`Hex.Dawn/Agents/DawnScene.AgentsPanel.Turns.cs` ⚠ small), the self test, `docs/agents.md` and
`docs/context/core-and-mcp.md` §7. Pull `qwen3-coder:30b` and do the Ollama runs.

**Phase 3, models.** The model manager (a palette command and a window of the extension's, `hex.window`), Ollama pull
with progress, delete, free-space check, `ai_models_folder`, "Keep loaded" per model, the low-VRAM warning
(`Hex.Core/Ai/HexAiModels.cs`, `HexGpuMemory.cs`), "Set up AirLLM" on D:.

**Phase 3b, the agent's model for autocomplete**, once `feat/edit-prediction` has landed: the `chat` format and the
switch (`Hex.Core/Prediction/*`, the AI settings rows).

**Phase 4, AirLLM.** First a spike by hand: venv, `airllm` on Windows, the 30B-A3B model, streaming or not, seconds a
token. Then `airllm_server.py`, the sidecar's start and stop from the extension, its load state, its tests, the AirLLM
runs. If the spike shows AirLLM doesn't work on Windows, stop there and report, with WSL2 as the fallback to ask about.

**Phase 5 (if wanted), colibrì preset and test.**

## 11. Risks

- **AirLLM on Windows is unverified** and it may not stream; it is tried before anything is built on it.
- **AirLLM is too slow for an agent.** Very likely at bf16; plausible even at 4-bit. The result is measured and said in
  the panel rather than hidden; colibri is the faster engine of the same idea if the person wants one.
- **Small models call tools badly**: wrong arguments, made-up tools, loops. Schema errors go back to the model, the
  same-call limit stops loops, and the core tool set is small on purpose.
- **Context**: a big window moves experts off the GPU and slows everything; a small one compacts often. The setting's
  help says the trade, the usage line shows it.
- **VRAM shared with edit prediction**: Zeta 2 (5.5 GB) and a 19 GB agent model can't both be on a 12 GB card; Ollama
  swaps them, which takes seconds each way. Said in the model manager; open question 4.
- **Clashes**: Hex Link (#107) is changing networking and the Agents fleet lists; this plan only adds a line to the
  turn's usage and nothing in `Run.cs`, `Chats.cs` or the lists. Edit prediction adds the AI settings section and the
  HTTP client this reuses; the order of landing decides who makes `Hex.Core/Ai`.
- **The tool list grows** as Hex gains tools; `core` is a named list, not "everything read-only", so it doesn't grow by
  surprise.

## 12. Open questions for the person

1. **Colibrì**: is JustVugg/colibri (the C engine that streams MoE experts from NVMe) the one meant? You narrowed the
   build to AirLLM and Ollama; should colibri follow as phase 5, or not at all?
2. ~~AirLLM's Python~~ answered: everything on D:, the runtime and CUDA too (7.2).
3. ~~The test model~~ answered: Mixtral 8x7B, its smallest version, `mixtral:8x7b-instruct-v0.1-q2_K` (1.6).
6. **AirLLM's half of the test**: AirLLM can't read Ollama's GGUF. Download the full `mistralai/Mixtral-8x7B-Instruct-v0.1`
   safetensors (93.4 GB to `D:\models\huggingface`, about 25 GB of 4-bit shards after, the original deleted), try a
   smaller pre-quantised Hugging Face copy that AirLLM may not load, or leave AirLLM untested on a real model for now?
   And may torch and airllm (about 6 GB) be installed into `D:\models\airllm\venv` now?
4. ~~Edit prediction and agents at once~~ answered: the person chooses which models stay loaded, Hex warns about low
   VRAM, and prediction can use the agent's model (section 2, 3.8).
5. **Approvals by default**: `ask` (every edit and command asks) or `edits` (edits go through, commands ask, as Claude
   Code is set up here)?

## Status

- **Done:** research (Hex's agent systems and tools, Ollama, AirLLM, colibrì, the MoE models), this plan with the
  person's later answers (everything on D:, which models are loaded, the VRAM warning, the agent's model for
  autocomplete). `mixtral:8x7b-instruct-v0.1-q2_K` pulled into `D:\ollama\models` (17.3 GB) and measured (1.7): no
  native tools on that tag, tools by prompt work, 7 tok/s output at 8k. A standalone Python 3.12.10 and an empty venv
  on `D:\models\airllm`. A partial Qwen3-30B download (0.4 GB) and a C: venv from before the person's D: rule were
  deleted; nothing of this work is left on C: but the probe scripts in the scratch folder.
- **Next:** #110 reads this; on its word, phase 1 (core, the text-tools fallback included, and fake-server tests),
  then phase 2 with Mixtral, then the AirLLM spike once question 6 is answered.
- **Open questions:** section 12 (1, 5 and 6 still open).
