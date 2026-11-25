# Local agent mode: the selected local model as an agent in the Agents panel

The person: "allow the user to use their locally selected model in the agentic mode, as Mellum2.1 should be capable
enough for agentic coding tasks" and "the Qwen3.5 9B model should also work in agentic workflows". This is phase 1 of
`docs/local-agent-models-plan.md` (whose architecture, section 3, it follows: Hex runs the loop as `Hex.exe
--local-agent`, an agent system like Claude Code, Hex's tools over the MCP pipe), rebuilt on what exists now: streaming
chat (`ChatStream.cs`, `OllamaChat`, `OpenAiChat`), Hex's own llama-server with its router (`LlamaCpp*`), and the model
resolver `AiEditModels`. AirLLM, Mixtral and the model manager of that plan are out of this phase.

## 1. What the person gets

- **Local** in the Agents panel's agent systems beside Claude Code, Codex and Antigravity, its chats like theirs: Markdown
  answers, thinking dimmed, each tool call a row, approvals as cards, Stop, steering between turns, resume.
- A **model** option per chat: every installed local model that can chat (llama.cpp: the catalog's chat models whose
  files are in the models folder, Mellum2.1 and Qwen3.5 9B on this machine; Ollama: the server's models whose
  `/api/show` capabilities include `tools`). Default: the code model when it can chat, else the edit model
  (`AiEditModels.Resolve`). One model in the card at a time: llama.cpp's router swaps (`--models-max 1`), Ollama swaps
  by itself; the first turn says "Loading Mellum2.1…".
- **Approvals** option: `edits` (default: file edits go through, commands ask), `ask` (everything that changes asks),
  `all` (nothing asks).
- Tools: a curated set of Hex's MCP tools sized for a small model's context (read, search, symbols, edit, write,
  terminal run, diagnostics, plan), not all hundred.
- Works offline; nothing leaves the machine.

## 2. How it runs

```
Agents panel ──starts──> Hex.exe --local-agent --folder F --agent-chat N --options <file> [--resume S]
                            stdout: JSON lines  {type: text|thinking|tool|tool_done|usage|approval|session|note|error|done, ...}
                            stdin:  JSON lines  {type: message, text} | {type: approval, id, allow} | {type: stop}
                            ├── HTTP ──> llama-server /v1/chat/completions (tools)  |  Ollama /api/chat (tools)
                            └── MCP client over the pipe McpBridge uses ──> Hex's MCP tools (tagged with chat N)
```

The turn (LocalAgentLoop): system prompt (Hex's agent rules for small models: one call at a time, read before editing,
paths relative to the folder, say when done) + conversation + tools → stream; text/thinking out as lines; tool calls
gathered; none → `usage`, `done`, saved; calls → each validated (known tool, JSON args), approved per the mode (approval
line, waits for stdin), run over MCP with an output budget, `tool`/`tool_done` lines, appended as `tool` messages → ask
again. At most 40 model calls a turn; the same call three times in a row stops with a note. Native tool calls when the
server gives them; the text fallback (`<tool_call>{"name":..,"arguments":..}</tool_call>`, Qwen/Hermes style, and a
fenced JSON object with name/arguments) when it doesn't. Context: an estimate of tokens; past 75% of the window the
oldest tool outputs are cut to a line, then the oldest turns summarised to one line each.

## 3. Work packages (parallel, each owns its files; contracts in the files named)

| WP | Owns | Contract |
| --- | --- | --- |
| LA1 tool chat | `Hex.Core/Prediction/LocalAgentChat.cs` (new), tests `Hex.Tests/LocalAgentChatTests.cs` | `AgentMessage`, `ToolSpec`, `ToolCall`, `AgentDelta`, `IAgentChat`, `AgentChats.Make`, `ToolCallText` |
| LA2 MCP client + tools | `Hex.Core/Prediction/LocalAgentMcp.cs` (new), tests `Hex.Tests/LocalAgentMcpTests.cs` | `McpClient`, `AgentToolResult`, `LocalAgentTools`, `LocalAgentPipe` |
| LA3 loop, protocol, store | `Hex.Core/Prediction/LocalAgentLoop.cs` (new), tests `Hex.Tests/LocalAgentLoopTests.cs` | `LocalAgentOptions`, `LocalAgentProtocol`, `IToolHost`, `LocalAgentLoop`, `LocalAgentStore` |
| LA4 process + extension | `Hex/Program.cs` (dispatch only), `Hex.Dawn/LocalAgent/LocalAgentProgram.cs` (new), `hex-extensions/local_models/extension.hor` (new) + its packaging like the other extensions | `LocalAgentProgram.Run(args)`, `--local-agent --models` JSON |
| LA5 models for agents | `Hex.Dawn/LlamaCpp/AiEditModels.cs` (add), `Hex.Core/LlamaCpp/LlamaCppServerArgs.cs` + `LlamaCppServer.cs` + `HexSettings.LlamaCpp.cs` (preset of every installed chat model), tests in `Hex.Tests/AiEditModelsTests.cs` / `LlamaCppTests.cs` | `AiEditModels.AgentModels(settings)`, `AiEditModels.ForAgent(settings, name)` |

Contracts are written in the files above as stubs (`throw new NotImplementedException("LAn")`) before the workers start;
each worker implements its own and codes against the others' as declared.

## 4. Tests

Each package unit-tests against fakes (FakeServer for HTTP, in-memory streams for MCP and stdio, a scripted IAgentChat
and IToolHost for the loop). Then live: Mellum2.1 and Qwen3.5 9B through llama.cpp, asked "find where X is declared and
add a comment above it" in a scratch folder, through the real process and pipe.
