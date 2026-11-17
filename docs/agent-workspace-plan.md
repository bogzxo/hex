# Agent workspace ("Noon"): design and implementation plan

Status: plan for the workspace; the agent groundwork of section 0 is built and this plan builds on it. Paths are relative to `hex/` unless they start with `horizon/`. Line numbers come from `docs/context/*.md` and the code as read for this plan; re-find with `hex_symbols` before editing. Items marked **verify** were not confirmed in code or against the CLI versions installed.

Look and feel were taken from the layouts (`Hex.Dawn/Assets/hex/*.hor`, `shell/*.hor`), the Solstice themes (`Hex.Shell/Assets/uix/flat/themes/solstice*.hor`), `Hex.Shell` (`HexLooks`, `HexHorizon`, `HexModeSwitch`), `docs/ux-revamp-plan.md` and the Agents panel code. No Hex MCP server was reachable while this was written, so no screenshots were taken; M1 starts with a screenshot pass to check the mockups against the real window (section 17.9).

Working name of the third tab: **Noon** (Dawn the editor, Noon the agents, Dusk the terminal). The name is an open question (section 16); everything below uses `Noon` / `noon_` as the identifier prefix.

## 0. Already built (commits `b65c6f0`, `cde07cc`, `hex-extensions` `4b2921e`)

Nothing in this section is proposed again. The rest of the plan uses these as they are.

| Built | Where | What the plan does with it |
| --- | --- | --- |
| **Harness-first steering.** `HexAgentProfiles.HARNESS_RULES`, one text for every agent system, exposed to extensions as `hex.harness_rules`. Claude Code gets it with `--append-system-prompt`, Codex with `-c developer_instructions='''...'''` (a TOML literal, so the text has no apostrophes). Claude's own `Explore` subagent is redefined through `--agents <temp>/claude-agents-<chat>.json` with the same rules (model haiku). Harness tool descriptions name the shell commands they replace. The shell stays available. Checked live: agents went harness first. | `Hex.Core/Harness/HexAgentProfiles.cs`, `Hex.Dawn/Extensions/HexExtensions.Agents.cs`, `Hex.Dawn/Agents/DawnScene.McpHarness.cs`, `hex-extensions/claude_code` 1.6.0, `codex` 1.5.0 | Hub rules (asking, showing, proposing, plans) are a second constant `HexAgentProfiles.HUB_RULES` / `hex.hub_rules` sent through the same three channels (section 9.3). Same no-apostrophe rule. |
| **Native subagents folded.** `chat.progress(tool_id, step)`; `HexAgentEntry.Progress` and `Steps`; `HexAgentChat.Progress(id, step)`; the row is "Subagent · <type>" with "step N · latest" (`SubagentOf`). Claude's stream carries `parent_tool_use_id`, read in `heard`; `step_of` shortens a tool call. | `Hex.Core/Agents/HexAgentChat.cs`, `HexAgentChatApi.cs`, `Hex.Dawn/Agents/DawnScene.AgentsPanel.Rows.cs`, `claude_code/extension.hor` | A native subagent is a leaf of the Fleet tree made from that tool entry (kind `Native`), with its step count and latest step. No separate transcript is kept for it (8.1). |
| **Mailbox and coordinator.** `HexAgentMail(Seq, From, FromName, Kind, Text, Directive)`; per chat `Mail`, `Post`, `MailRead`, `UnreadMail`, `MailSeen` (100 kept). Tools `hex_agent_message(text, to?, kind)` with kinds finding, question, answer, progress, blocked, instruction, and `hex_agent_inbox()`. `hex_agent_send` is an instruction. `Deliver` picks the route: a chat waiting in `hex_agent_wait` (`agentsWaiting`) is woken with it (the `messages` flag, on by default); a running steerable chat (Claude Code) takes it on stdin; a running chat that cannot be steered (Codex) finds it in its inbox; an unread instruction starts the next turn (`AfterAgentTurn`); unread mail is put in front of the next prompt (`UnreadPrompt` in `StartAgent`). Family only (`RootOf`). Both chats get a `$(mail)` note (`NoteText`). The spawn preamble and `HARNESS_RULES` tell agents to coordinate this way. Checked live: a Sonnet coordinator steered a Codex worker mid-run on a Claude scout's finding. | `Hex.Dawn/Agents/DawnScene.McpAgentMail.cs`, `DawnScene.McpAgents.cs`, `DawnScene.AgentsPanel.Run.cs`, `Hex.Core/Agents/HexAgentChat.cs`, `docs/agents.md` | The mail path is the one message bus of the workspace. The user becomes an endpoint of it (questions to the user, answers back), Hex itself a sender (notices), and Noon draws it: mail rows in threads, the family view, mail flow on the Board (sections 5, 6.8, 7.1, 7.9). |

Not built, still in this plan: the Noon face, the hub model and store, questions to the user, code cards, proposals, plans, approvals, touches and conflicts, checkpoints, worktrees, cross-feature integration.


## 1. Goals and non-goals

Goals
- A third tab in the title-bar mode selector, next to Dawn and Dusk, that opens a full-window workspace built around agents instead of files.
- One live model of every agent, subagent and external MCP client working on the project: tree, status, system, model, cost, tokens, time, plan, files touched, conflicts.
- Agents can ask the user structured questions, show code, propose edits and request approval through MCP tools; the user answers in place, per hunk, without losing the thread they were reading.
- Code is always drawn by Hex's own `CodeEditor` (highlighting, semantic colours, diff tints, lenses), never by a markdown code block.
- Everything is reachable through the MCP server and backed by the harness index, so agents spend fewer tokens and fewer turns than shelling out.
- No stutter: resize, scroll and streaming stay inside the frame budget with dozens of agents and long transcripts.
- It looks and behaves like the rest of Hex (section 17) and is tied into every other feature as tightly as they are tied to each other (section 18): agents are visible in the tree, tabs, editor, Problems, tests, debugger, Source Control, terminals and on the horizon line, and each of those can hand work to an agent.

Non-goals
- Replacing the side-bar Agents panel. It stays as the compact view of the same sessions (section 11).
- A new agent runtime. Agent systems stay extensions (`hex-extensions/claude_code`, `codex`) started through `hex.agent`.
- Remote or cloud agents, multi-user collaboration, Android (no processes or pipes there: `HexHost.NoProcesses`).
- Redoing what section 0 lists as built (harness-first steering, folded native subagents, the mailbox and coordinator tools).

## 2. What exists and what constrains the design

| Fact | Where | Consequence |
| --- | --- | --- |
| Dawn and Dusk are two `Scene`s swapped in one window; only the scene on screen is ticked. | `Hex.Shell/HexModes.cs`, `Hex.Dawn/Shell/DawnScene.Modes.cs`, `Hex.Dusk/Shell/DuskScene.Modes.cs` | A third scene would freeze the editor's tick. |
| All agent state (`agentChats`, `agentRuns`, MCP queue `mcpCalls`, `TakeAgentLines`) lives in `DawnScene` and is editor-thread only. | `Hex.Dawn/Agents/DawnScene.AgentsPanel*.cs`, `DawnScene.Mcp.cs` | Agents stop being read while Dusk is on screen; `QueueMcp` calls `HexModes.Bring(HexMode.Dawn)` for every tool call. |
| The mode selector is two buttons built by `HexModeSwitch.Add(bar, current, swap)`; both scenes build their own copy. | `Hex.Shell/HexModeSwitch.cs` | The third tab must be added in one shared place and behave the same from both scenes. |
| MCP tools are coroutines on the editor thread; `RunAway` tools run off it and read published snapshots (`agentStatuses`). | `DawnScene.Mcp.cs:301`, `DawnScene.McpAgents.cs` | Waiting tools (questions, proposals) must be `RunAway`. |
| A per-chat bridge presents its chat id at the pipe (`--agent-chat`); `HexMcpCaller` injects `_hex_caller` only for tools named `hex_agent*`. | `DawnScene.Mcp.cs:309`, `McpSession.cs` `McpGate` | Attribution for other tools needs the caller recorded for every call. |
| `HexAgentChat` holds rows, session id, tokens, cost, parent, depth; max 2000 rows; not persisted. | `Hex.Core/Agents/HexAgentChat.cs` | Sessions, events, plans and questions need a model and a store. |
| Transcript rows are built in code per entry and refitted on width change (`FollowWidth`); the stack is not virtualized. | `DawnScene.AgentsPanel.Rows.cs` | Reuse the row builders; add virtualization for the big view. |
| `CodeEditor` supports `ReadOnly`, `FitRows` (as tall as its code), `LineHighlights`, `LineChanges`, `LensesAbove` + `OnLensAbovePressed`, `PaintVersion` caching. | `horizon/Horizon.UI/Components/Controls/CodeEditor*.cs`; diff tab in `Hex.Dawn/Git/DawnScene.Diff.cs:132` | Inline code and per-hunk actions need no new text engine. |
| Diff model: `GitLineDiff.Diff` -> `GitHunk(OldStart, OldCount, NewStart, NewCount)`, `GitDiffText.Of(before, after)` (inline diff text + runs + word diffs). | `Hex.Core/Git/` | Proposals reuse it. |
| Git access is the `git` CLI through `GitProcess.Run(folder, args, input, patience, unattended)`; no environment parameter. | `Hex.Core/Git/GitProcess.cs:48` | Checkpoints with a private index need an `environment` overload. |
| Harness tools run off the editor thread with per-client budgets and memoised answers per generation. | `Hex.Core/Harness/`, `DawnScene.McpHarness.cs` | Per-agent read tracking and stats can hook `Harnessed(...)`. |
| Agent processes are started in the first workspace folder; `HexAgentProcess.Start(found, arguments, folder, stdin, environment, keepInput)` already takes a folder and environment. | `Hex.Core/Agents/HexAgentProcess.cs:112` | Worktree isolation is a different `folder`. |
| Limits: agents 2 deep, 6 started by agents at once. | `DawnScene.McpAgents.cs:16` | Kept; shown in the UI; made settings later. |
| Agents of one family (the chat somebody started and everything under it, `RootOf`) message each other; delivery depends on what the receiver is doing (`Deliver`). Mail is per chat, in memory, 100 kept; notes are the only trace in the UI. | `DawnScene.McpAgentMail.cs`, `HexAgentChat.Mail` | Noon needs a family-wide ordered log of mail for the views and the store; `Deliver` needs a user endpoint. |
| `agentsWaiting` says which chats sit in `hex_agent_wait`; `AgentStatus` carries the chat and its mail for off-thread reads. | `DawnScene.McpAgents.cs` | The session state "waiting for its agents" and the snapshot already exist in small; the hub snapshot grows from `AgentStatus`. |

## 3. Key decisions

### D1. Noon is a face of Dawn, not a third scene
`HexMode { Dawn, Dusk }` stays what a *scene/process* is (AppUserModelID, instance pipe, CLI). A new `HexFace { Dawn, Noon, Dusk }` is what the *selector* shows. Noon is a full-body view inside `DawnScene`'s compositor: the title bar, menu bar, status bar and toasts stay; `body` (activity bar, side bar, center, properties) is hidden and `noon_view` shown.

Reasons: the agents, the MCP coroutine queue, documents, language servers, skins and the keyboard are all owned by `DawnScene`; a second scene would need all of them shared or duplicated, and the editor would stop ticking while it is up. As a view, Dawn<->Noon is instant (no scene swap, no GC pass, no device work), `hex_ui_tree`/`hex_click` see Noon like any other part of the editor (so it can be tested through MCP), and code cards can use the same `PrepareEditor` policy as tabs.

From Dusk, picking Noon swaps the scene to Dawn and shows the Noon view in the same step (`HexModes.Show(HexFace.Noon)`).

### D2. A window-free hub in Hex.Core owns the model
`HexAgentHub` (new, `Hex.Core/Agents/Hub/`) owns sessions, the tree, events, questions, proposals, plans, approvals, touches, checkpoints and persistence. `HexAgentChat` becomes the transcript part of a session. Both the side panel and Noon render from the hub. Unit-testable without a window.

Threading follows the existing pattern: mutated on the editor thread only; other threads post `HexHubCommand`s on a `ConcurrentQueue` drained in the tick and read an immutable `HexHubSnapshot` republished when the hub's version moves (as `agentStatuses` is today). Waiters use `TaskCompletionSource` completed from the tick.

### D3. Agents keep working whatever is on screen
`HexModes.Update(float dt)` (called by the scene on screen, Dawn `DawnScene.cs:448`, Dusk `DuskScene.cs:255`) also runs registered background steps of the scene that is *not* on screen, on the same main thread. `DawnScene` registers one step: take agent lines, pump the hub, publish snapshots, run MCP calls that do not need the editor. `QueueMcp` brings Dawn forward only for tools flagged `NeedsEditor` (default true; hub tools false). **verify**: that the window loop wakes for agent output; `HexAgentProcess` reader threads do not call `Wake()` today, so a `Woken` callback is added.

### D4. Code is shown by reference
Agents point at code (`path`, `symbol`, lines) and Hex renders it. Agents do not paste code into chat to show it, and do not send whole files to propose a change. This is the main token saver of the UI and the reason every code surface is a `CodeEditor`.

### D5. Waits are bounded and resumable
MCP clients time out tool calls (Codex is given 660 s in `codex/extension.hor`; Claude Code's limit is an environment setting, **verify**). Every waiting tool waits at most `timeout` (default 240 s, max 600 s), then returns "still pending, id N" with the matching `*_wait` tool to resume. A pending question never dies with its call: it stays in the inbox and its answer is delivered on the next wait, or as a new turn when the agent has stopped (section 7.1).

## 4. Architecture

```
            Hex.Shell                        Hex.Core                               Hex.Dawn
  HexFace, HexModes.Show/Face     Agents/Hub/HexAgentHub (model)          Noon/DawnScene.Noon*.cs (view)
  HexModeSwitch (3 buttons)       Agents/Hub/HexHubStore (jsonl)          Noon/HexCodeCard*.cs (CodeEditor cards)
  HexModes background steps       Agents/Hub/HexProposal, HexQuestion     Agents/HexAgentRows*.cs (shared rows)
                                  Agents/Hub/HexCheckpoints (git)         Agents/DawnScene.McpHub*.cs (MCP tools)
                                  Agents/Hub/HexTouches (conflicts)       Agents/DawnScene.AgentsPanel*.cs (side panel)
                                  Harness/HexCodeIndex.Derive (worktree)  Extensions/HexExtensions.Agents.cs (chat API)
            horizon/Horizon.UI
  Containers/VirtualStack.cs (new), CodeEditor pooling hooks
```

### 4.1 Files to add

Hex.Core (`namespace Hex.Core`, all `internal`, JsonNode only, no reflection)
- `Hex.Core/Agents/Hub/HexAgentHub.cs` - sessions, tree, version, command queue, snapshot publishing.
- `Hex.Core/Agents/Hub/HexAgentHub.Events.cs` - event log, ring buffer per session, subscribers.
- `Hex.Core/Agents/Hub/HexAgentSession.cs` - session record wrapping `HexAgentChat`.
- `Hex.Core/Agents/Hub/HexQuestion.cs`, `HexQuestionQueue.cs` - questions, routing, answers, remembered answers.
- `Hex.Core/Agents/Hub/HexProposal.cs`, `HexProposalHunks.cs` - proposed edits, hunk split, partial apply, staleness.
- `Hex.Core/Agents/Hub/HexPlan.cs` - plan items and merge rules.
- `Hex.Core/Agents/Hub/HexApproval.cs`, `HexApprovalRules.cs` - approval requests, allow/deny rules.
- `Hex.Core/Agents/Hub/HexTouches.cs` - who read/wrote what, conflict detection.
- `Hex.Core/Agents/Hub/HexCheckpoints.cs` - git tree snapshots, restore, per-turn diff.
- `Hex.Core/Agents/Hub/HexWorktrees.cs` - create/remove/merge agent worktrees.
- `Hex.Core/Agents/Hub/HexHubStore.cs` - append-only session files, summaries, load.
- `Hex.Core/Agents/Hub/HexSessionSearch.cs` - search across stored sessions.
- `Hex.Core/Agents/Hub/HexHubSnapshot.cs` - immutable view for off-thread tools.
- `Hex.Core/Agents/Hub/HexBrief.cs` - compact summary of a session for other agents.
- `Hex.Core/Agents/Hub/HexMailLog.cs` - every `HexAgentMail` of a family in order with its receiver, for the family view, the Board and the store; endpoints `USER` and `HEX`.
- `Hex.Core/Harness/HexCodeIndex.Derive.cs` - index for a worktree sharing unchanged files with the main one.
- `Hex.Core/Harness/HexReadMemory.cs` - per-caller record of what was read at which generation.

Hex.Shell
- `Hex.Shell/HexFace.cs` - `enum HexFace { Dawn, Noon, Dusk }`, icon and localisation key per face.

Hex.Dawn
- `Hex.Dawn/Noon/DawnScene.Noon.cs` - build, show/hide, focus, tick, layout state.
- `Hex.Dawn/Noon/DawnScene.Noon.Fleet.cs` - the agent tree column.
- `Hex.Dawn/Noon/DawnScene.Noon.Thread.cs` - transcript of the selected session (virtualized).
- `Hex.Dawn/Noon/DawnScene.Noon.Inbox.cs` - questions, approvals, unread across agents.
- `Hex.Dawn/Noon/DawnScene.Noon.Review.cs` - proposals and changes, per hunk.
- `Hex.Dawn/Noon/DawnScene.Noon.Board.cs` - lanes/timeline of all agents, conflicts.
- `Hex.Dawn/Noon/DawnScene.Noon.Context.cs` - right column: plan, files, usage, details.
- `Hex.Dawn/Noon/DawnScene.Noon.Compose.cs` - composer, options, attachments, new-agent form.
- `Hex.Dawn/Noon/DawnScene.Noon.Search.cs` - session search and log view.
- `Hex.Dawn/Noon/DawnScene.Noon.Keys.cs` - commands and key context.
- `Hex.Dawn/Noon/DawnScene.Noon.Notify.cs` - toasts, title-bar badge, taskbar flash.
- `Hex.Dawn/Noon/HexCodeCard.cs`, `HexCodeCardPool.cs`, `HexDiffCard.cs` - CodeEditor-backed cards.
- `Hex.Dawn/Agents/HexAgentRows.cs` (+ `.Cards.cs`, `.Tools.cs`) - row builders moved out of `DawnScene.AgentsPanel.Rows.cs` behind `IAgentRowHost` (width, skin, narrow, link opener, pooled code cards) so both surfaces share them.
- `Hex.Dawn/Agents/DawnScene.McpHub.cs` (ask, show, notify), `.McpHub.Proposals.cs`, `.McpHub.Plans.cs`, `.McpHub.Approvals.cs`, `.McpHub.Sessions.cs` (tree, brief, search, checkpoints, claims).
- `Hex.Dawn/Assets/hex/noon.hor`, `noon_question.hor`, `noon_agent_row.hor`.
- `Hex.Dawn/Noon/DawnScene.NoonMarks.cs` - agent marks on the tree, tabs, gutter, status bar and horizon (section 18).
- `Hex.Dawn/Noon/DawnScene.NoonInline.cs` - proposals as inline diffs in the file's own editor tab, with lenses (18.2).
- `Hex.Dawn/Noon/DawnScene.NoonHandoffs.cs` - `NoonHandoff` and the `IAgents` implementation behind every "with Agent" entry (section 18).
- `Hex.Dawn/Agents/DawnScene.McpTests.cs`, `DawnScene.McpGit.cs`, `DawnScene.McpHistory.cs` - tools of section 7.11.
- `Hex.Shell/Assets/uix/flat/themes/solstice_noon.hor`, `solstice_day_noon.hor` - Noon's accent over the same ground (17.2).
- `Hex.Dawn/Testing/DawnScene.SelfTest.Noon*.cs` (section 13).

Engine (`horizon/`)
- `horizon/Horizon.UI/Components/Containers/VirtualStack.cs` - vertical list that realizes only rows near the viewport (section 12).
- `horizon/Horizon.Tests` (or the UI test project that exists, **verify**) - `VirtualStackTests`.

### 4.2 Files to change

| File | Change |
| --- | --- |
| `Hex.Shell/HexModeSwitch.cs` | `Add(TitleBar, HexFace current, Action<HexFace> pick)` builds three buttons; `SetScale`/`Dress` unchanged (they walk the row); new `Badge(row, HexFace.Noon, count)` for pending questions. |
| `Hex.Shell/HexModes.cs` | `Face` (current face), `Show(HexFace)` (swap scene if needed, remember the face wanted), `FaceWanted` read by Dawn in `OnReturned`; `Update(float dt)` runs background steps; `InBackground(HexMode, Action<float>)`. `Bring(HexMode)` keeps working. |
| `Hex.Dawn/Shell/DawnScene.Modes.cs` | `BuildModes` uses faces; `ShowFace(HexFace)`; `OnReturned` applies `HexModes.FaceWanted`; `LetGo` also when leaving to Noon is NOT called (same compositor), only focus is moved. |
| `Hex.Dusk/Shell/DuskScene.Modes.cs` | Selector with three faces; Noon -> `HexModes.Show(HexFace.Noon)`. |
| `Hex.Dawn/Shell/DawnScene.cs` | ctor: `BuildNoon()` after `BuildAgentsPanel` and before `commands.Build(menu)` (`:248`); `DeclareTicks`: `panelTicks.Add("noon", UpdateNoon)` after `mcp`. |
| `Hex.Dawn/Shell/DawnScene.Chrome.cs` | `FitChrome` sizes `noon_view` to the body; scale follows `SetScale`. |
| `Hex.Dawn/Assets/hex/editor.hor` | Include `noon.hor` under `root` after `body`; add layer `noon` to `compositor.hidden_layers([...])`. |
| `Hex.Dawn/Agents/DawnScene.AgentsPanel*.cs` | `agentChats` -> `hub.Sessions`; rows through `HexAgentRows`; "Open in Noon" button and menu entry; question/approval cards in narrow form. |
| `Hex.Dawn/Agents/DawnScene.AgentsPanel.Run.cs` | `StartAgent` takes the session's folder (worktree) and environment; records turn checkpoints; `HexAgentProcess` wake callback. |
| `Hex.Dawn/Agents/DawnScene.Mcp.cs` | `HexMcpTool` gains `NeedsEditor = true` and `TakesCaller = false`; `HexMcpTools.CallAs` records the calling chat for every call (`McpServer.CallingChat` AsyncLocal) and injects `_hex_caller` when `TakesCaller` or the name starts `hex_agent`; `QueueMcp` brings Dawn only when `NeedsEditor`; untagged connections get an external session id. |
| `Hex.Dawn/Agents/DawnScene.McpAgents.cs` | `hex_agent_list` gains `tree`; `hex_agent_spawn` gains `isolation`, `context`, `claims`, `budget`, `role`; `AgentStatus` is replaced by `HexHubSnapshot` rows (it already carries chat and mail); `hex_agent_wait` keeps waking on mail, which now includes answers from the user and notices from Hex. |
| `Hex.Dawn/Agents/DawnScene.McpAgentMail.cs` | `Deliver` takes a receiver that may be the user (`HexMailLog.USER`); mail is also appended to `HexMailLog`; `hex_agent_message` gains `re` (the message or question answered) and, for a chat nobody started, defaults `to` to the user; `MAIL_KINDS` gains `notice` (from Hex only). |
| `Hex.Dawn/Agents/DawnScene.McpHarness.cs` | `Harnessed(...)` records reads in `HexTouches` and `HexReadMemory`; resolves paths against the caller's root (worktree); appends unread notices (section 7.9). |
| `Hex.Dawn/Agents/DawnScene.McpWorkbench.cs` | `McpToolList()` adds `.. HubTools()`. |
| `Hex.Core/Agents/HexAgentChat.cs` | Entry kinds `Question, Proposal, Code, Plan, Approval, Mail`; `HexAgentEntry.Ref` (id of the hub object); `HexAgentMail` gains `To` and `Re`; usage split into input/cached/output; `ContextUsed`/`ContextLimit`. `Progress`, `Steps`, `Mail`, `Post`, `MailRead`, `UnreadMail` exist. |
| `Hex.Core/Agents/HexAgentChatApi.cs` | New chat members for extensions (section 9.1). |
| `Hex.Core/Agents/HexAgentProcess.cs` | `Woken` callback from reader threads; nothing else. |
| `Hex.Core/Mcp/McpServer.cs` | `CallingChat` AsyncLocal beside `CallingClient`; session id for untagged clients. |
| `Hex.Core/Git/GitProcess.cs` | `Run(..., IReadOnlyDictionary<string,string>? environment)` overload. |
| `Hex.Core/Git/GitRepository.cs` | `WriteTree(indexFile)`, `RestoreFrom(tree, paths)`, `WorktreeAdd/Remove/List`. |
| `Hex.Core/Settings/HexSettings.cs`, `.Table.cs`, `Hex.Shell/Settings/EditorSettingsCatalog.cs` | Settings of section 10.6 (user-only where they grant capability). |
| `Hex.Dawn/Assets/hex/localisation/en-gb.hor` | `noon.*`, `modes.*` for the third face, `mcp.instructions` extended (section 9.3). |
| `hex-extensions/claude_code/extension.hor`, `codex/extension.hor` | Section 9; bump `version`. |
| `Hex.Dawn/Context/DawnContext.cs`, `DawnScene.Context.cs` | `IAgents` (`Handoff`, `MarksFor`, `Changed`) for parts (18.17). |
| `Hex.Dawn/Git/DawnScene.Git.cs`, `Shell/DawnScene.Tabs.cs`, `Workspace/DawnScene.ExplorerFiles.cs` | Agent badge beside the git letter on tree rows and tabs (18.1). |
| `Hex.Dawn/Shell/DawnScene.Horizon.cs`, `StatusBar.cs`, `Hex.Shell/Settings/HexLooks.cs` | Horizon in Noon, status item "Agents 3 · ? 1", `noon_theme` of a look (17.2, 17.3, 18.12). |
| `Hex.Dawn/Shell/DawnScene.Panel.cs`, `Tests/HexTestExplorer*.cs`, `Debugging/HexDebugging*.cs`, `Git/HexGit*.cs`, `Code/HexCodeEditing.Hints.cs`, `Languages/HexLanguages.Fixes.cs`, `Shell/DawnScene.Terminal.cs`, `Code/DawnScene.SearchView.cs`, `Notebooks/DawnScene.NotebookCells.cs`, `Workspace/HexExplorer*.cs` | "With Agent" menu entries and lenses, each a few lines calling `IAgents.Handoff` (section 18). |
| `Hex.Dawn/Shell/DawnScene.Welcome.cs`, `Assets/hex/welcome.hor` | Agents row on the Welcome page (18.15). |
| `Hex.Dawn/Extensions/HexExtensions.Api.cs` | `hex.noon` group (18.14). |
| `Hex.Core/Workspace/HexLocalHistory.cs` | Optional source of a kept version (18.8). |
| `docs/agents.md`, `docs/extensions.md`, `docs/context/*.md` | New tools, chat API, the Noon map. |

## 5. Data model

All ids are small integers unique per hub run and stable in the store (`s12`, `q7`, `p3`, `a4`, `c9` in tool text). Times are UTC ticks in the store, seconds in tool text.

```csharp
internal enum HexSessionKind { Panel, Spawned, Native, External }   // user chat, hex_agent_spawn, a CLI's own subagent, an MCP client Hex did not start
internal enum HexSessionState { New, Running, WaitingUser, WaitingAgent, Done, Failed, Stopped }

internal sealed class HexAgentSession
{
    public int Id;                       // same number as HexAgentChat.Id for Panel/Spawned
    public HexSessionKind Kind;
    public int? Parent; public int Depth;
    public string System;                // profile id: "claude_code", "codex"
    public string? Provider, Model;      // provider reported by the extension (anthropic, openai, ...)
    public string Name, Role;            // "scout", "reviewer", free text
    public HexAgentChat Chat;            // transcript rows (null for External until it says something)
    public string Root;                  // folder it runs in: workspace folder or its worktree
    public string? Worktree, Branch;     // set when isolated
    public HexUsage Usage;               // Input, CachedRead, CachedWrite, Output, Cost, ContextUsed, ContextLimit
    public HexBudgetLimit? Limit;        // Cost, Tokens, Seconds; reaching one raises an approval
    public double ActiveSeconds, WallSeconds; public long Started, LastActivity;
    public HexPlan? Plan;
    public bool Background;              // does not raise "finished" toasts, only the badge
    public bool Pinned, Archived;
    public int Version;                  // bumps on any change; views redraw by it
}

internal sealed record HexHubEvent(long Seq, long At, int Session, HexEventKind Kind, int Ref, string Text);
internal enum HexEventKind { Started, Turn, TurnDone, Text, Tool, ToolDone, Question, Answer, Proposal, Verdict, Plan,
    Approval, Decision, Touch, Conflict, Checkpoint, Restore, Spawn, Stop, Error, Notify, Usage }

internal sealed class HexQuestion
{
    public int Id, Session; public string Title;
    public IReadOnlyList<HexQuestionPart> Parts;        // 1..6
    public IReadOnlyList<HexCodeRef> Context;           // code shown with the question
    public HexRoute Route;                              // User, Parent, Auto
    public bool Blocking;                               // the agent is waiting on it
    public string? RememberKey;                         // answers reused for the same key in this workspace
    public HexQuestionState State;                      // Pending, Answered, Dismissed, Expired, Withdrawn
    public IReadOnlyList<HexAnswer>? Answers; public string? AnsweredBy;  // "user", "#3", "remembered"
    public long Asked, Answered;
}
internal sealed record HexQuestionPart(string Id, string Text, HexQuestionKind Kind,   // Choice, Multi, Text, Confirm
    IReadOnlyList<HexOption> Options, bool AllowOther, string? Default);
internal sealed record HexOption(string Id, string Label, string? About, HexCodeRef? Preview, bool Recommended);
internal sealed record HexAnswer(string Part, IReadOnlyList<string> Picked, string? Text);

internal sealed record HexCodeRef(string Path, string? Symbol, int StartLine, int EndLine, string? Note, long Generation);

internal sealed class HexProposal
{
    public int Id, Session; public string Title, Rationale;
    public IReadOnlyList<HexProposedFile> Files; public HexProposalState State;   // Pending, Partly, Applied, Rejected, Withdrawn, Stale
    public HexApplyMode Mode;                                                     // Review, Auto
    public int? Checkpoint;                                                       // taken before the first hunk is applied
}
internal sealed class HexProposedFile
{
    public string Path; public HexFileChange Change;       // Edit, Create, Delete, Rename
    public string BaseHash; public string Before, After;   // Before = text the proposal was computed against
    public List<HexHunk> Hunks; public string? RenameTo;
}
internal sealed class HexHunk
{
    public int Id; public GitHunk Lines;                   // from GitLineDiff.Diff(before, after)
    public HexVerdict Verdict;                             // Pending, Accepted, Rejected, Edited
    public string? Comment, EditedText;                    // the user's note or their own version of the new lines
}

internal sealed class HexPlan { public string Title; public List<HexPlanItem> Items; public int Version; }
internal sealed record HexPlanItem(string Id, string Text, HexPlanState State, string? Note, int? Session);  // Pending, Doing, Done, Blocked, Dropped; Session = subagent it was given to

internal sealed class HexApproval
{
    public int Id, Session; public string Action, Detail;  // "run", "write", "network", "delete", "restore", "budget", "merge", or a CLI tool name
    public string? Command; public IReadOnlyList<string> Paths; public HexRisk Risk;
    public HexDecision Decision; public string? Rule;      // Pending, Allow, AllowAlways, Deny, DenyAlways; the rule it made or matched
}

internal sealed record HexTouch(int Session, string Path, HexTouchKind Kind, int StartLine, int EndLine, long At, long Generation);  // Read, Write, Claim, Propose
internal sealed record HexConflict(string Path, int First, int Second, HexConflictKind Kind, long At);  // BothWrite, StaleRead, ClaimHeld, OverlapProposal

internal sealed record HexCheckpoint(int Id, int Session, string Root, string Tree, string Label, long At, HexCheckpointWhy Why);  // TurnStart, BeforeApply, Manual, BeforeRestore
```

Mail (exists as `HexAgentMail(Seq, From, FromName, Kind, Text, Directive)`, kept per receiving chat). The plan adds two fields and a log, nothing else:

```csharp
internal sealed record HexAgentMail(long Seq, int? From, string FromName, string Kind, string Text, bool Directive)
{
    public int To { get; init; }          // a chat id, HexMailLog.USER (0) or a family broadcast (-1, later)
    public long? Re { get; init; }        // the message this answers, or a question id through HexQuestion.Mail
}
internal sealed class HexMailLog            // one per family (RootOf), editor thread, republished in HexHubSnapshot
{
    public const int USER = 0, HEX = -2;    // endpoints that are not chats: the user, and Hex itself (notices)
    public IReadOnlyList<HexAgentMail> All; // in Seq order, capped at 2000, older ones in the store
    public IEnumerable<HexAgentMail> Between(int a, int b);
    public (int From, int To, int Count, long Last)[] Flows(long since);   // for the Board's arrows
}
```
- A question to the user is a `HexQuestion` plus one mail of kind `question` with `To = USER`; the user's answer is one mail of kind `answer` from `USER` with `Re` set, handed to the existing `Deliver`. So an answer reaches a waiting coordinator through its `hex_agent_wait`, a running Claude Code on its stdin, a running Codex in its inbox, and a finished agent as its next turn, with no new delivery code. `Deliver` treats an answer to a blocking question as a directive (it starts a finished agent).
- What the user types to a session is mail of kind `instruction` from `USER` (the composer's existing `SendToAgent`/`SteerAgent` path, now also logged), so the family log is the whole conversation between everyone, the user included.
- Hex's own notes to an agent (a file it read changed, a proposal was decided, budget) are mail of kind `notice` from `HEX` (7.9).
- `HexSessionState.WaitingAgent` is `agentsWaiting`; `HexSessionKind.Native` is a tool entry for which `SubagentOf` is not null.
- Coordinator: a session with at least one live child. It is a role read from the tree, not a type.

Store (section 10.4): `<UserFolder>/agents/<hash of workspace roots>/` with `sessions/<id>.jsonl` (one `HexHubEvent` or row per line), `index.json` (one summary per session for the list and for search ranking), `rules.hor` (approval rules and remembered answers, user-only), `layout.hor` (Noon layout). Nothing is written in self-test (`kept == false`).

## 6. UI

### 6.1 Selector

```
[ icon Hex ]  File Edit ... Help                         [ <> Dawn | (o) Noon 2 | >_ Dusk ]   _ [] x
```
- Three `button_flat` buttons in one row, same styling rules as today (`HexModeSwitch.Dress`: the active one filled with the accent in flush looks). Icons: `code`, `robot`, `terminal`. The badge on Noon is the count of blocking questions and approvals; it pulses once when it goes up (respects `settings.UiAnimation`).
- Keys: `Ctrl+Alt+Shift+D` keeps toggling Dawn<->Dusk. New `Ctrl+Alt+Shift+A` goes to Noon, and from Noon back to the face that was up before. `HexModes.Show` is also a palette command per face ("Show Dawn", "Show Noon", "Show Dusk") and a View menu group.
- Self-test, hosted (Android) and "mode started on its own" keep working: with no `HexModes.Host`, Dawn still has Dawn and Noon faces; Dusk as a program of its own shows Noon greyed with a tooltip, or starts Hex on its folder with `--face noon` (new flag, section 10.7).

### 6.2 Layout

```
+--------------------------------------------------------------------------------------------------------------+
| title bar, menu                                                            [Dawn | Noon 2 | Dusk]     _ [] x |
+----------------------+---------------------------------------------------------------+-----------------------+
| FLEET          + new | [Thread] [Inbox 2] [Review 5] [Board] [Search]     scout #7 > |  PLAN  4/9            |
| filter: all v  /find |---------------------------------------------------------------|  v 1 Map the parser   |
|----------------------|  you                                    Refactor the lexer ...|  v 2 Find callers     |
| v * #3 Refactor lexer|                                                               |  > 3 Split Tokenize   |
|   claude · fable     |  #3 claude                                                    |  o 4 Update tests     |
|   12m · $0.84 · 4/9  |  I'll split Tokenize in two. First the callers:               |  o 5 ...              |
|   ? 1 question       |  +- Lexer.cs  Tokenize  (hex_show) ----------------- open -+ |-----------------------|
|   v ~ #7 scout       |  | 118  public IReadOnlyList<Token> Tokenize(string text,  | |  FILES  6             |
|       codex · gpt-5  |  | 119      bool lenient = false)                          | |  M Lexer.cs    #3     |
|       2m · 31k tok   |  | 120  {                                                  | |  M Tokens.cs   #3 #8 !|
|   v . #8 tests       |  +---------------------------------------------------------+ |  R Parser.cs   #7     |
|       claude · sonnet|  > 3 tools · hex_symbols, hex_refs, hex_read          1.2s  |  A LexerTests  #8     |
|       worktree agent8|  +- ? Question from #3 ------------------------- blocking -+ |-----------------------|
| . #2 Fix the build   |  | Keep the old Tokenize overload?                         | |  USAGE                |
|   codex · done 1h    |  | (1) Keep, mark obsolete  recommended                    | |  in 412k  cached 88%  |
| x #1 Docs pass       |  | (2) Remove, fix 14 callers        [show callers]        | |  out 18k  $0.84       |
|   failed             |  | (3) Other...                                            | |  context 61% ======-- |
|----------------------|  |                                   [Skip]  [Answer  ^Enter]| |  harness saved 1.4M ch|
| totals 3 running     |  +---------------------------------------------------------+ |-----------------------|
| $1.92 · 1.1M tok     |---------------------------------------------------------------|  DETAILS              |
| ! 1 conflict         |  [ Tell #3 more...                                          ] |  session a1b2 · root  |
|                      |  claude v  fable v  edits v  @file  worktree o   [Stop][Send]|  checkpoints 4  [undo]|
+----------------------+---------------------------------------------------------------+-----------------------+
| status bar: branch, problems, Agent 4 connections, ...                                                       |
+--------------------------------------------------------------------------------------------------------------+
```
- Three columns with `HexSplitter` grips: Fleet (240 default, 180..420), Stage (fills), Context (280 default, 220..480, collapsible with `Ctrl+Alt+B`). Below 1000 px wide Context folds to icons; below 760 px Fleet becomes a dropdown at the top of Stage.
- Stage has five tabs. Thread and Review keep their own scroll position per session; switching session or tab never resets a composer draft (drafts are per session).
- The composer is the side panel's card (same pickers from the extension's `run.options`) plus: `@` to attach a file/symbol through the harness (`hex_files`/`hex_symbols` ranking, inserted as a `HexCodeRef`, not as text), worktree toggle, background toggle, and with no session selected the new-agent form (system, model, role, prompt).

### 6.3 HIDL sketch (`Assets/hex/noon.hor`)

Component kinds and property names follow `agents_view.hor`; names are the contract with `layout.Get<T>`. Splitters, fills and widths are set in `FitNoon`.

```
// Noon, the agents' workspace, a face of the editor's window. The fleet at the left, the stage in the middle with the
// thread, inbox, review, board and search over one composer, and what the picked agent is about at the right.
#define layout

let noon_view = compositor.panel({ fill: "both", visible: false, layer: "noon", padding: vec(6, 4, 6, 6) });

// The fleet, every agent and the agents it started, and the project's totals under them
let noon_fleet = compositor.panel({ parent: noon_view, anchor: "left", pivot: "left", fill: "vertical", size: vec(240, 0) });
let noon_fleet_head = compositor.panel({ parent: noon_fleet, anchor: "top", pivot: "top", fill: "horizontal", size: vec(0, 32) });
let noon_fleet_title = compositor.label({ parent: noon_fleet_head, anchor: "left", pivot: "left", text: "@noon.fleet", text_scale: 0.13, dim: true });
let noon_fleet_filter = compositor.button({ parent: noon_fleet_head, anchor: "right", pivot: "right", pos: vec(-34, 0), size: vec(32, 32), icon: "filter", style: "button_flat", tooltip: "@noon.filter_tip" });
let noon_new = compositor.button({ parent: noon_fleet_head, anchor: "right", pivot: "right", size: vec(32, 32), icon: "add", style: "button_flat", tooltip: "@noon.new_agent" });
let noon_fleet_find = compositor.textbox({ parent: noon_fleet, anchor: "top", pivot: "top", pos: vec(0, -34), fill: "horizontal", size: vec(0, 28), placeholder: "@noon.find", text_scale: 0.13 });
let noon_fleet_scroll = compositor.scroll({ parent: noon_fleet, fill: "both", padding: vec(0, 66, 0, 58) });
let noon_fleet_rows = compositor.stack({ parent: noon_fleet_scroll, fill: "horizontal", spacing: 1 });
let noon_totals = compositor.panel({ parent: noon_fleet, anchor: "bottom", pivot: "bottom", fill: "horizontal", size: vec(0, 54), padding: vec(6, 4, 6, 4) });
let noon_totals_text = compositor.label({ parent: noon_totals, anchor: "top_left", pivot: "top_left", text: "", text_scale: 0.115, dim: true });
let noon_conflicts = compositor.button({ parent: noon_totals, anchor: "bottom_left", pivot: "bottom_left", size: vec(0, 22), style: "button_flat", icon: "warning", visible: false });

// The stage, its tabs over whichever of them is up, and the composer under all of them
let noon_stage = compositor.panel({ parent: noon_view, fill: "both" });
let noon_tabs = compositor.stack({ parent: noon_stage, anchor: "top_left", pivot: "top_left", direction: "horizontal", spacing: 2, size: vec(0, 32) });
let noon_tab_thread = compositor.button({ parent: noon_tabs, style: "button_flat", icon: "comment-discussion", text: "@noon.thread" });
let noon_tab_inbox = compositor.button({ parent: noon_tabs, style: "button_flat", icon: "inbox", text: "@noon.inbox" });
let noon_tab_review = compositor.button({ parent: noon_tabs, style: "button_flat", icon: "diff", text: "@noon.review" });
let noon_tab_board = compositor.button({ parent: noon_tabs, style: "button_flat", icon: "graph", text: "@noon.board" });
let noon_tab_search = compositor.button({ parent: noon_tabs, style: "button_flat", icon: "search", text: "@noon.search" });
let noon_crumbs = compositor.label({ parent: noon_stage, anchor: "top_right", pivot: "top_right", text: "", text_scale: 0.115, dim: true });

let noon_thread = compositor.panel({ parent: noon_stage, fill: "both", padding: vec(0, 36, 0, 112), clip: true });           // holds the VirtualStack made in code
let noon_inbox = compositor.scroll({ parent: noon_stage, fill: "both", padding: vec(0, 36, 0, 8), visible: false });
let noon_inbox_rows = compositor.stack({ parent: noon_inbox, fill: "horizontal", spacing: 8 });
let noon_review = compositor.panel({ parent: noon_stage, fill: "both", padding: vec(0, 36, 0, 8), visible: false });
let noon_review_files = compositor.scroll({ parent: noon_review, anchor: "left", pivot: "left", fill: "vertical", size: vec(260, 0) });
let noon_review_rows = compositor.stack({ parent: noon_review_files, fill: "horizontal", spacing: 1 });
let noon_review_diff = compositor.panel({ parent: noon_review, fill: "both", padding: vec(264, 0, 0, 40), clip: true });     // one CodeEditor made in code
let noon_review_bar = compositor.stack({ parent: noon_review, anchor: "bottom_right", pivot: "bottom_right", direction: "horizontal", spacing: 6, size: vec(0, 34) });
let noon_board = compositor.panel({ parent: noon_stage, fill: "both", padding: vec(0, 36, 0, 8), visible: false, clip: true }); // lanes drawn by a DrawingPanel made in code
let noon_search = compositor.panel({ parent: noon_stage, fill: "both", padding: vec(0, 36, 0, 8), visible: false });
let noon_search_box = compositor.textbox({ parent: noon_search, anchor: "top", pivot: "top", fill: "horizontal", size: vec(0, 30), placeholder: "@noon.search_all" });
let noon_search_scroll = compositor.scroll({ parent: noon_search, fill: "both", padding: vec(0, 34, 0, 0) });
let noon_search_rows = compositor.stack({ parent: noon_search_scroll, fill: "horizontal", spacing: 2 });

let noon_compose = compositor.panel({ parent: noon_stage, anchor: "bottom", pivot: "bottom", fill: "horizontal", size: vec(0, 104), padding: vec(6, 6, 6, 6) });
let noon_input = compositor.textbox({ parent: noon_compose, anchor: "top", pivot: "top", fill: "horizontal", size: vec(0, 48), padding: vec(6, 4, 6, 4), text_scale: 0.15, placeholder: "@noon.input", max_length: 20000 });
let noon_options = compositor.stack({ parent: noon_compose, anchor: "bottom_left", pivot: "bottom_left", direction: "horizontal", spacing: 0 });
let noon_send = compositor.button({ parent: noon_compose, anchor: "bottom_right", pivot: "bottom_right", size: vec(32, 32), icon: "arrow-up", tooltip: "@noon.send" });
let noon_stop = compositor.button({ parent: noon_compose, anchor: "bottom_right", pivot: "bottom_right", pos: vec(-38, 0), size: vec(32, 32), icon: "debug-stop", tooltip: "@noon.stop", visible: false });

// What the picked agent is about, its plan, the files it touched, what it used and where it runs
let noon_context = compositor.scroll({ parent: noon_view, anchor: "right", pivot: "right", fill: "vertical", size: vec(280, 0) });
let noon_context_rows = compositor.stack({ parent: noon_context, fill: "horizontal", spacing: 10 });
```

### 6.4 Fleet (left)
- One row per session, children indented one step (`CHAT_STEP`), collapsible per parent. Row: state glyph, `#id name`, second line `system · model`, third line `time · cost or tokens · plan n/m`, badges: `?` pending question, `!` conflict, `wt` worktree, `bg` background, unread dot.
- State glyphs reuse the side panel's: spinner running, filled dot unread, cross failed, square stopped; new: `?` waiting for the user (accent), hourglass waiting for another agent.
- Sorting: sessions needing the user first, then running, then by last activity. Filters: all, needs me, running, done, failed, archived; by system; text find over name, prompt, files touched.
- Row menu: open, open in side panel, stop (with children), retry, tell more, brief (copy), copy last reply, show changes (Review filtered to it), restore to before this turn, merge worktree, archive, close. Multi-select (Ctrl/Shift click) for stop/archive/close.
- Totals strip: running count, total cost and tokens of this project today, harness savings, conflicts button (opens Board filtered to conflicts). Limits `2 deep / 6 at once` shown when reached.
- External sessions (MCP clients Hex did not start, such as a Claude Code in a terminal connected with `--mcp`) are listed under "Connected" with their client name; they have no transcript but do have plan, questions, proposals, touches.

- Families: each chat somebody started is a family root (`RootOf`); its tree is one block with the family's totals on the root row (agents, cost, tokens, unread mail). A root with live children shows the coordinator glyph (`organization`) and its state reads "coordinating 3" while it sits in `hex_agent_wait` (`agentsWaiting`), not "running".
- Native subagents (a CLI's own, section 0) are leaf rows under their chat, made from the tool entry: `Subagent · Explore`, "step 12 · hex_search Tokenize", spinner until the tool is done. They have no menu beyond "Show in thread".
- Mail on rows: an envelope with the unread count for mail the agent has not read; a row flashes its envelope once when mail arrives (motion setting respected). Hovering a row dims every row outside its family and draws thin lines to the rows it exchanged mail with in the last minute.

### 6.5 Thread
Rows come from `HexAgentRows` (shared with the side panel) plus new cards. Consecutive tool rows of one turn fold into a group ("3 tools · names · time"), the way a native subagent already folds into one row with its latest step.

| Card | Content | Actions |
| --- | --- | --- |
| Code (`hex_show`, tool results that are code) | Header `path  symbol  lines`, `CodeEditor` read-only, `FitRows` 14 (expands to 40, then scrolls inside). Stale marker when the file changed since (`generation`). | Open in Dawn at the line (switches face), copy, pin to Context. |
| Diff (proposal, or an agent's Edit/Write tool call) | Inline diff via `GitDiffText`, tints via `LineHighlights`/`LineChanges`, word diffs. Per hunk a lens above: `Accept · Reject · Edit · Comment` (`UICodeLensAbove`). | Per hunk; file level Accept all / Reject all; "Review all" opens Review. |
| Question | Title, parts, options with keys 1..9, "Other" text box, optional code previews per option (shown on focus/hover in a code card beside it), who asks with its breadcrumb. | Answer (`Ctrl+Enter`), Skip, "Let #parent decide", Remember. |
| Approval | Action, command or paths in a code card, risk colour, matching rule if any. | Allow once (`Y`), Always (`A`), Deny (`N`), Deny with reason. |
| Plan | The session's plan, changed items highlighted for a moment; one card per session, updated in place, not one per update. | Click an item given to a subagent to jump to it. |
| Subagent (native, exists) | "Subagent · <type>", "step N · latest" (`SubagentOf`, `Progress`, `Steps`). In Noon the row expands to the last 20 steps kept on the entry (new: a small ring of step texts). | Expand. |
| Spawned agent | `#7 scout · codex · running 2m · 31k tok`, its last line; when done its brief. | Open, stop, tell more. |
| Mail | Replaces the `$(mail)` note row when the hub is there: direction arrow, the other side (`#7 "scout"`, you, Hex), kind as a small tag (finding, question, answer, progress, blocked, instruction, notice), the text, and how it was delivered (taken in, woke the wait, inbox, started a turn). A question shows its answer under it when it comes. | Jump to the other chat at the matching row; reply (a mail from the user); for `blocked` and `question` from a child, "Answer for #3". |
| Tool | As today (icon, name, preview, time, chevron). Output that is code, a diff, a file list or harness text gets a proper renderer: `path:line` lines are links; file reads become code cards on expand. | Expand, copy, open. |
| Notice | Checkpoint taken/restored, conflict, budget reached, worktree made/merged. | Jump to the related view. |

Streaming: only the last text block is re-rendered while it grows; markdown is parsed per block, finished blocks are never touched again.

### 6.6 Inbox
All pending questions and approvals of every session, oldest blocking first, then unread finished turns. Each card is the same card as in the thread, with the asking agent's breadcrumb (`#3 Refactor lexer > #7 scout`) and a two-line reason taken from the agent's last text. Answering here does not change the selected session or any scroll position. After an answer, focus moves to the next card ("answer and next"). Drafted answers are kept on the question until sent.

```
+- Inbox  2 need you -----------------------------------------------------------------------------------------+
| ? #3 Refactor lexer > #7 scout · codex            blocking · asked 40s ago                                   |
|   Which test project should the new tests go in?                                                              |
|   (1) Hex.Tests  recommended    (2) a new Hex.Lexer.Tests    (3) Other...            [Let #3 decide] [Answer] |
|---------------------------------------------------------------------------------------------------------------|
| ! #8 tests · claude                                blocking · asked 5s ago                                    |
|   Run:  dotnet test Hex.Tests --filter Lexer         (rule: none)                                             |
|                                               [Deny  N]  [Always for dotnet test  A]  [Allow once  Y]         |
|---------------------------------------------------------------------------------------------------------------|
| . #2 Fix the build · codex   finished 1h ago · 3 files changed                         [Review] [Open] [Done] |
+---------------------------------------------------------------------------------------------------------------+
```

### 6.7 Review
```
+- Review ------------------------------------------------------------------------------------------------------+
| by: all v   pending 5 | applied 9   | Tokens.cs  ·  proposal p3 from #3  ·  2 of 3 hunks pending             |
|-----------------------+-------------------------------------------------------------------------------------|
| v #3 Refactor lexer   |   41   internal enum TokenKind                                                      |
|   M Lexer.cs     2/2 v|      Accept · Reject · Edit · Comment                          hunk 1 of 3  (J/K)   |
|   M Tokens.cs    1/3  | - 42       Ident, Number, Text,                                                     |
| v #8 tests            | + 42       Ident, Number, Text, Raw,                                                |
|   A LexerTests   0/1  |   43       Open, Close                                                              |
| ! Tokens.cs  #3 + #8  |      Accept · Reject · Edit · Comment                          hunk 2 of 3          |
|                       | - 57   public bool IsText => Kind == TokenKind.Text;                                |
|                       | + 57   public bool IsText => Kind is TokenKind.Text or TokenKind.Raw;               |
|-----------------------+-------------------------------------------------------------------------------------|
|                                    [Reject file]  [Accept file]      [Reject all]  [Accept all  Ctrl+Shift+Enter]|
+---------------------------------------------------------------------------------------------------------------+
```
- Left: files grouped by session, with change kind, hunk progress, conflict rows where two sessions touch one file. Two sources feed it: **proposals** (edits not yet on disk) and **changes** (what an agent already wrote, from the turn checkpoint diff; section 8.3). Both render the same way; verdict on a change is "keep" or "revert hunk".
- Right: one pooled `CodeEditor` showing `GitDiffText` for the selected file, read-only, with the file's highlighter and semantic colours when the file is open in Dawn. `Edit` on a hunk makes that hunk's added lines editable in a small `CodeEditor` under it (the user's version is what is applied; verdict `Edited`). `Comment` attaches a note sent back to the agent with the verdicts.
- Keys when the diff has focus: `J`/`K` or `Alt+F5`/`Shift+Alt+F5` next/previous hunk (same commands as `DawnScene.Diff.cs`), `A` accept, `R` reject, `E` edit, `C` comment, `Shift+A`/`Shift+R` whole file, `O` open in Dawn.
- Applying a hunk: checkpoint first (once per proposal), then through the open document when the file is open (an undo step in that editor, `IDocuments.Rewrite`) or `DocumentFile` atomic write otherwise. Later hunks are re-anchored with the same line arithmetic `HexGit.Hunks` uses for staging.
- Stale proposals (base hash no longer matches): hunks whose context still matches are re-anchored; the rest are marked stale and can only be rejected or sent back ("rebase this", one click, a message to the agent with the new text of the region).

### 6.8 Board and family view
Board: one horizontal lane per session on a shared time axis: turn spans, tool ticks coloured by kind (harness, editor, terminal, write, shell), waits on the user hatched, waits on agents (`hex_agent_wait`) dotted. **Mail flow** is drawn between lanes: an arrow per message from sender to receiver at its time, coloured by kind (instruction in the accent, blocked in the warning colour, the rest dim), bundled when dense; the user and Hex are lanes of their own at the top. Hovering an arrow shows the message, clicking opens it in the thread. Under the lanes, a file matrix: rows are files touched, columns sessions, cells `R`/`W`/`P`(proposed)/`C`(claimed); rows with a conflict are marked and sorted first. Drawn by one `DrawingPanel` from the hub snapshot (no component per mark), so 50 lanes cost one paint.

Family view (the coordinator's desk; the Thread tab shows it when a family root is selected and "Family" is toggled, `Alt+F`):
```
+- #3 Refactor lexer · coordinating 3 · sonnet -----------------------------------------------------------------+
| WORKERS                      | MAIL (whole family, newest last)                         | DECISIONS            |
| ~ #7 scout   codex           | 14:02 #7 -> #3  finding   Tokenize has 14 callers, 3 in  | q7 you: keep overload|
|   step: hex_refs Tokenize    |                           generated code (Lexer.g.cs:40) | p3 2 of 3 accepted   |
|   plan 2/3 · 31k tok         | 14:02 #3 -> #8  instruction  skip generated callers       | a4 dotnet test always|
| ~ #8 impl    codex           |                           (in its inbox, read 14:03)     |----------------------|
|   writing Tokens.cs          | 14:05 #8 -> #3  blocked   IsText is used by the designer | FINDINGS 5           |
|   plan 1/4 · 12k tok   ! 1   | 14:05 #3 -> you question  keep the old overload?  [Answer]| Tokenize callers ... |
| . #9 tests   claude · haiku  | 14:06 you -> #3 answer    keep, mark obsolete            | generated code rule  |
|   done · brief               | 14:06 #3 -> #8  answer    keep IsText, add IsRaw         |                      |
|------------------------------+-----------------------------------------------------------+----------------------|
| [ To: #3 v | instruction v ]  Tell the coordinator, or pick a worker...                          [Stop all][Send]|
+-----------------------------------------------------------------------------------------------------------------+
```
- Left: the family's members as cards (latest step or current file, plan progress, tokens, conflicts). Middle: `HexMailLog.All` for the family as one stream, filterable by kind and by member; this is the conversation between agents that today is scattered as notes across chats. Right: decisions the user made in this family (answers, verdicts, approvals) and the family's findings (mail of kind `finding`, newest first, searchable).
- The composer here sends mail as the user: to the coordinator by default (kind `instruction`, the existing send path), or to any member. Talking to a worker directly also posts a `notice` to the coordinator ("the user told #8: ..."), so the coordinator's picture stays whole.
- "Stop all" stops the root and with it everything under it (`StopAgent`, exists).


### 6.9 Context (right)
Sections, each collapsible, state kept per workspace: Plan (items with state glyphs, progress, who has it), Files (touched by this session and its children, kind, conflicts), Usage (input, cached share, output, cost, context meter, turn and total time, harness calls and characters saved for this caller), Pinned code (cards pinned from the thread), Details (system, model, provider, session id, root or worktree and branch, permission mode, checkpoints with restore), Log link.

### 6.10 Search and logs
- Search tab: query over all stored sessions of this workspace (prompts, replies, tool names and inputs, file paths, question texts). Results grouped by session with matched lines; Enter opens the session at that row. Filters: system, state, date, "touched file".
- Log: per session, the raw lines from the agent process (stdout JSON and stderr) and hub events, in a read-only `CodeEditor` with the JSON highlighter, tailing. Export transcript as Markdown or the raw `.jsonl`.

## 7. MCP tools

All registered in `DawnScene.McpHub*.cs`, added to `McpToolList()`. All hub tools are `NeedsEditor = false`, `TakesCaller = true`. Waiting tools are `RunAway`. Results are plain text like the rest of Hex's tools, short, with the next tool named. Schema nodes are fresh per tool (`McpSchema`). Lines are 1-based.

### 7.1 `hex_ask_user`
Ask the user one to six questions and get the answers.

Arguments
| Name | Type | Notes |
| --- | --- | --- |
| `questions` | array, required | Each `{ id?, text, kind?: "choice"|"multi"|"text"|"confirm", options?: [{ label, about?, recommended?, preview?: {path, symbol?, start_line?, end_line?} }], allow_other?: true, default? }`. `choice` by default when options are given, else `text`. 2..9 options. |
| `title` | string | Short heading for the card. |
| `context` | array | Code to show with the question: `{path, symbol?, start_line?, end_line?, note?}`. |
| `route` | `user` \| `parent` \| `auto` | `user` is the default for a chat somebody started. `parent`: the same as `hex_agent_message(kind: "question")` to the parent, which exists; this tool only adds the structure. `auto` (default for spawned agents): the parent first, the user when the parent passes it on or has not answered in 60 s. |
| `blocking` | boolean | Default true. False: the question is left in the inbox and the call returns at once. |
| `remember` | string | A key; an earlier answer under the same key in this workspace is returned without asking. |
| `timeout` | number | Seconds to wait, default 240, max 600. |

Result
```
q7 answered by user after 42s.
keep_overload: Keep, mark obsolete
test_project: Other: "put them next to LexerTests in Hex.Tests/Lexing"
```
or `q7 is still waiting for the user (asked 240s ago). hex_ask_wait(ids:[7]) waits again; carry on with other work if you can.`
or `q7 dismissed by the user: "decide yourself".`

Behaviour: the question is a `HexQuestion` and one mail of kind `question` to the user in the family's `HexMailLog`; the session's state becomes `WaitingUser`; a toast and the badge are raised (section 10.5). The answer is one mail of kind `answer` from the user handed to the existing `Deliver`, so every route is the built one: it wakes a `hex_agent_wait`, goes in on a steerable agent's stdin, waits in the inbox of one that cannot be steered, is put in front of the next prompt (`UnreadPrompt`), and starts the next turn of an agent that finished while it waited (`AfterAgentTurn`, the answer counted as a directive). The `hex_ask_user` call itself, when still open, returns the answer directly and marks that mail read. A chat nobody started that sends `hex_agent_message(kind: "question" | "blocked")` with no `to` reaches the user the same way, as a free-text question in the Inbox.

### 7.2 `hex_ask_wait`, `hex_ask_withdraw`, and answering for a child
- `hex_ask_wait(ids?, timeout?)`: wait for pending questions of the caller; same result lines. A coordinator does not need it: `hex_agent_wait` already wakes on mail, and the answer is mail.
- `hex_ask_withdraw(id)`: the asker no longer needs it.
- A parent answers its child's question with the tool that exists: `hex_agent_message(to: child, kind: "answer", re: 7, text)`; for a structured question the text may be option labels (`keep_overload: Keep, mark obsolete`), matched to the parts by Hex. Passing it up is `hex_agent_message(kind: "question", re: 7)` to its own parent, or to the user when it has none. No `hex_ask_answer` tool.


### 7.3 `hex_show`
Show code or text to the user in the caller's thread without spending tokens on it.

`hex_show(items: [{ path, symbol?, start_line?, end_line?, note? }] | { diff: { path, before_ref?, after_text? } } | { text, language? }, title?)` -> `Shown 2 cards (Lexer.cs Tokenize 118-164, Tokens.cs 40-58).` Symbols resolve through `HexCodeIndex.Read`. Unknown path or symbol is an error naming `hex_symbols`.

### 7.4 `hex_propose_edit`
Propose changes for review instead of writing files.

Arguments
| Name | Type | Notes |
| --- | --- | --- |
| `files` | array, required | Each `{ path, edits?: [{ old_text, new_text, occurrence? } \| { start_line, end_line, new_text }], content?, delete?, rename_to? }`. `edits` are anchored replacements against the current text (open document text when the file is open and modified). `content` only for new files. |
| `title`, `rationale` | string | Shown on the card. |
| `mode` | `review` \| `auto` | `auto` applies at once when the user's setting allows auto-accept for this session, else falls back to `review` and says so. |
| `wait` | boolean | Default true. |
| `timeout` | number | Default 240, max 600. |

Errors are specific and cheap to act on: `edit 2 of Tokens.cs: old_text not found (the file changed at generation 412; hex_read path:Tokens.cs start_line:40 end_line:60)`; `old_text matches 3 places, give occurrence or more context`.

Result
```
p3 decided after 95s: 3 accepted, 1 rejected, 1 edited by the user. Files written: Lexer.cs, Tokens.cs (index generation 418).
Lexer.cs  h1 accepted  h2 accepted
Tokens.cs h3 accepted  h4 rejected: "keep IsText as it is, add IsRaw"  h5 edited (the user's version is in the file; hex_read path:Tokens.cs start_line:55 end_line:60)
```
Companions: `hex_proposal_wait(ids?, timeout?)`, `hex_proposal_status(id)`, `hex_proposal_withdraw(id)`, `hex_proposal_update(id, files)` (replace pending hunks after feedback, keeps verdicts of unchanged hunks).

### 7.5 `hex_plan_update`, `hex_plan_read`
- `hex_plan_update(items: [{ id?, text, state?: "pending"|"doing"|"done"|"blocked"|"dropped", note?, agent?: <chat id> }], title?, merge?: true)`: with `merge` (default) items are matched by `id` (or exact text) and only given fields change; without it the list is replaced. Result one line: `Plan: 4 of 9 done, doing "Split Tokenize".`
- `hex_plan_read(id?)`: the caller's plan, or another session's (a child reading the parent's plan to see where its task fits). Budgeted text.

### 7.6 `hex_request_approval` and `hex_permission`
- `hex_request_approval(action, detail, command?, paths?, risk?: "low"|"medium"|"high", timeout?)` -> `a4 allowed once.` / `a4 allowed, and always for "dotnet test *" in this workspace.` / `a4 denied: "not on main"`. Rules are checked first, so a matching rule answers without a card.
- `hex_permission(tool_name, input, tool_use_id?)`: the shape Claude Code's `--permission-prompt-tool` calls (**verify** against the installed CLI). Returns one text block of JSON: `{"behavior":"allow","updatedInput":{...}}` or `{"behavior":"deny","message":"..."}`. Its schema is hand-built with `additionalProperties: true` (the `McpSchema.Object` builder forbids extras). It maps the CLI tool to an approval (`Bash` -> run + command, `Edit`/`Write` -> write + path, `WebFetch` -> network + url), consults rules, else raises an approval card. A long wait is bounded by `noon_permission_wait` (default 300 s) after which it denies with "no answer; ask again with hex_request_approval".

### 7.7 Fleet tools (extending what exists)
`hex_agent_list`, `hex_agent_spawn`, `hex_agent_wait` (wakes on mail, `messages` flag), `hex_agent_read`, `hex_agent_send` (an instruction), `hex_agent_stop`, `hex_agent_message`, `hex_agent_inbox` exist (section 0, `docs/agents.md`). Changes:
- `hex_agent_list(tree?: true)`: adds per chat tokens, plan progress, pending questions, files written, worktree, and native subagents as indented lines. Text stays one line per chat.
- `hex_agent_spawn(...)` new arguments: `isolation: "shared"|"worktree"`, `context: [{path, symbol?, start_line?, end_line?}]` (resolved by Hex and put in front of the prompt under a budget, so the child does not search again), `claims: [paths]`, `budget: { cost?, tokens?, seconds? }`, `role`, `background`.
- `hex_agent_wait`: no new argument. It wakes on mail already; questions routed to the caller, conflicts and decided proposals arrive as mail (kinds `question`, `notice`), so they wake it too.
- `hex_agent_message`: `re?: <seq or question id>`; `to` defaults to the user for a chat nobody started; kind `notice` is refused to agents.
- `hex_agent_inbox(peek?: true)`: read without marking read (for a coordinator checking between steps without losing the wake).
- `hex_agent_brief(id, most?)`: structured summary built by Hex with no model call: state, plan, answers the user gave, findings it sent, files written with line counts, proposals and verdicts, last reply (cut). Default 1500 characters. What a coordinator reads instead of `hex_agent_read full`.
- `hex_agent_findings(query?, most?)`: the family's mail of kind `finding`, newest first, ranked by the query, each with who found it and the index generation it was sent at (findings whose files changed since are marked stale). This is the shared memory of a family; no separate blackboard.
- `hex_session_search(query, most?, agent?, since?)`: ranked lines across stored sessions, `sN rowM: text`.


### 7.8 Files, claims, checkpoints
- `hex_claim(paths, intent: "read"|"write", note?, release?)`: advisory claim. Result lists holders: `Tokens.cs is being written by #8 (tests) since 40s. Claimed anyway; coordinate with hex_agent_send(8, ...) or work in a worktree.` Claims are released when the turn ends.
- `hex_touches(path?, agent?)`: who read, wrote, claimed or proposed what.
- `hex_checkpoint(label?)` -> `c9 taken.`; `hex_checkpoint_list(agent?)`; `hex_checkpoint_restore(id, paths?)` raises an approval (`restore`) unless the user allowed it.
- `hex_changes_by(agent?, since_checkpoint?)`: per-file line counts of what a session changed since a checkpoint, with `hex_show`-able refs.

### 7.9 Notices from Hex, on the mail path
Hex tells an agent things as mail of kind `notice` from `HexMailLog.HEX`, posted with `chat.Post` like any other mail, so the built routes carry them: they wake a `hex_agent_wait`, show in `hex_agent_inbox`, and go in front of the next prompt. Notices are never steered onto stdin (they would interrupt) and are never directives. Examples: `Tokens.cs changed since you read it (by #8, generation 417).`, `p3 decided, hex_proposal_wait.`, `2 new errors in Tokens.cs, hex_problems mine:true`, `stopped at Lexer.cs:131, breakpoint`, `budget 80% used.`
One addition to delivery, for agents that neither wait nor look in their inbox: `HexMcpTools.CallAs` appends up to three unread notices to the result of whatever tool a tagged caller calls next (`note: ...` lines) and marks them read. Off for clients whose profile sets `notices: false`. Notices to External sessions (no chat, no inbox) use only this route.

### 7.10 `hex_notify`
`hex_notify(text, level?: "info"|"done"|"warn")`: a toast and an Inbox line; it is mail of kind `progress` to the user, so it is in the family log too. `done` marks the session's work as ready for review. (Findings shared between agents are `hex_agent_message(kind: "finding")` and `hex_agent_findings`, 7.7; there is no separate note store.)


### 7.11 Tools that close gaps in other features (section 18)
| Tool | Arguments | Result |
| --- | --- | --- |
| `hex_tests` | `action: "list"`, `"run"` or `"results"`, `filter?`, `project?`, `failed_only?`, `wait_seconds?` | Budgeted text, one line per test: `Hex.Tests.LexerTests.Splits_raw FAILED 12ms  Lexer.cs:131  expected 3 actual 2`. Runs through the Test Explorer (`Hex.Dawn/Tests/HexTestExplorer*`), so the user sees the same run. The wait is RunAway on the explorer's published state. |
| `hex_git` | `action: "status"`, `"diff"`, `"log"`, `"blame"`, `"stage"`, `"unstage"`, `"commit"` or `"branch"`, `paths?`, `message?`, `staged?`, `most?` | Compact text from `HexGit`'s cached state (no process start for status). `stage`, `commit`, `branch` raise an approval unless a rule allows. Never pushes. |
| `hex_history` | `path`, `action: "list"`, `"read"` or `"diff"`, `version?` | Local history versions (`HexLocalHistory.Versions`) with who made each (you, #3) and diffs between them. |
| `hex_problems` (extend) | `since?: <generation>`, `mine?: true` | Only problems that appeared since a generation, or in files the caller wrote. |
| `hex_run_task` (extend) | `wait_seconds?`, `attach?: true` | A task already running is joined rather than started twice; the result ends with the problems `HexBuildProblems` parsed. |
| `hex_terminal_run` (extend) | `tab?: "agent"` | Default for tagged callers: the caller's own terminal tab (18.9). |
| `hex_debug_*` (extend) | - | `hex_debug_start` records the driving session; stops reach it as notices (18.5). |
| `hex_notebook_edit` (extend) | - | A proposal on the cell when `noon_review_edits` is `propose` (18.10). |
| `hex_open` (extend) | `reveal?: false` | A tagged caller opens files without taking the user's focus unless `reveal` (17.6). |

## 8. Mechanisms

### 8.1 Identity and attribution
- Tagged bridges (`--agent-chat N`) map to session `N`. Untagged connections get an External session per connection, named from `initialize`'s client name; closed with the connection, kept in the store.
- `McpServer.CallingChat` is set for every call. `Harnessed(...)` records `HexTouch(Read)` for `hex_read`, `hex_skim`, `hex_open`, `hex_editor`; write tools (`hex_propose_edit`, notebook edits, editor typing) record `Write`/`Propose`.
- Edits agents make with their own tools are attributed from the agent's event stream: the extension reports them (`chat.touched`, section 9.1), and the file watcher (`HexFolders.TakeFolderChanges`) confirms the write. A changed file no session claimed within 2 s is attributed to "you".
- Native subagents (a CLI's own Task tool) are already folded into their tool entry (`chat.progress`, section 0). The hub lists such an entry as a `Native` leaf of the tree and attributes what the extension reports for it (`chat.touched` with the parent tool id) to the owning session, marked "via subagent". Codex has none known.

### 8.2 Conflicts
`HexTouches` keeps, per path, the live sessions' touches. Rules, evaluated when a touch is added:
- `BothWrite`: two running sessions wrote or claimed the same path in the same root. Hard conflict: notice card in both threads, a notice (7.9) to both, Fleet `!`.
- `StaleRead`: a session read lines that another has since written. A notice to the reader only.
- `OverlapProposal`: two pending proposals have hunks overlapping in a file. Review shows them together; accepting one re-anchors or stales the other.
- Sessions in different roots (worktrees) never conflict until merge.

### 8.3 Checkpoints and undo
- A checkpoint is a git tree of the whole working tree taken with a private index, leaving the real index and HEAD alone: `GIT_INDEX_FILE=<tmp> git add -A` then `git write-tree`, kept alive by `refs/hex/checkpoints/<session>/<n>`. Stat-only for unchanged files, so it is fast after the first. Runs on a background thread (`InBackground`).
- Folders with no git get a shadow repository under `<UserFolder>/agents/<hash>/shadow.git` used with `--git-dir`/`--work-tree`, honouring `files_exclude` through an exclude file.
- Taken at: turn start of every top-level session (debounced: one per 2 s per root), before the first hunk of a proposal is applied, before a restore, on demand.
- "What did this turn change" = diff of the turn-start tree against the working tree, limited to paths the session touched. This feeds Review's changes and `hex_changes_by`.
- Restore: `git restore --source=<tree> --worktree -- <paths>` plus removal of files added since; open documents reload through the existing disk-change path (quiet reload as an undo step when unmodified). Scope: whole checkpoint, one session's files, one file, or one hunk (through Review's revert). A restore always takes a `BeforeRestore` checkpoint first, so undo can be undone.
- Unsaved editor text is not in the working tree: restore refuses for files modified in an editor and says which, unless the user picks "discard my unsaved changes".
- Retention: 50 checkpoints per session, 14 days, pruned by deleting refs.

### 8.4 Worktrees and isolation
- `isolation: "worktree"`: `git worktree add <repo>/.hex/worktrees/agent-<id> -b hex/agent-<id> HEAD` (`.hex/worktrees` added to `.git/info/exclude`). The agent process runs with that folder as cwd; its bridge keeps `--folder <main workspace>` (one editor, one pipe) and the hub resolves that session's relative paths against its root.
- Harness in a worktree: `HexCodeIndex.Derive(main, root, changed)` shares `HexIndexedFile`s of the main snapshot for files unchanged relative to the base commit (`git diff --name-only <base>` + untracked) and reads only the changed ones, so a worktree index costs the size of its diff. Answers say `(worktree agent-8, index generation N)`.
- Review shows a worktree session's changes as branch diff against its base. Finishing: "Bring into workspace" applies the diff to the main tree as a proposal (so it is reviewed per hunk and checkpointed), or "Merge branch" (`git merge --no-ff`, approval `merge`), or "Keep branch", or "Discard" (worktree and branch removed).
- No git: isolation falls back to `shared` with a notice; a copy-based scratch root is an open question.
- Dirty main tree at spawn: the worktree starts from HEAD; the user is told uncommitted changes are not in it, with the option "include my changes" (checkpoint tree used as the base commit of the branch).

### 8.5 Background agents
A background session does not toast on every turn, only raises the badge; it keeps running across face switches and with the window minimised (D3). On Hex exit with agents running the close question lists them (`HexModes.OnClose`): stop them, or (later, open question) leave them and reattach. Their turns are resumable anyway through the stored session id (`--resume`).

### 8.6 Budgets
`HexBudgetLimit` per session and per subtree. At 80% a notice; at 100% the hub stops forwarding new spawns for that subtree and raises an approval (`budget`): raise the limit, let this turn finish, or stop. Cost comes from the extension's `chat.usage`; where a system reports no cost (Codex today) tokens and seconds are the limits.

## 9. Agent systems (extensions)

### 9.1 Chat API additions (`HexAgentChatApi.Of`)
| Member | Meaning |
| --- | --- |
| `chat.touched(path, kind)` | The agent read or wrote a file with its own tool (`kind`: `"read"`, `"write"`). |
| `chat.edit(tool_id, path, old_text?, new_text?, content?)` | An edit tool call, so the thread shows a diff card instead of raw JSON. |
| `chat.plan(items)` | The agent's own todo list (`[{ text, state }]`), mapped to the session plan. |
| `chat.progress(tool_id, step)` | Exists. A step of a native subagent on its tool's row. The hub keeps the last 20 step texts per entry for the expanded row. |
| `chat.touched(path, kind, tool_id?)` | With `tool_id`, the touch was made by the native subagent of that tool. |
| `chat.usage({ input, cached_read, cached_write, output, cost, model, provider, context_used, context_limit })` | Extended; old keys keep working. |
| `chat.root` | The folder the session runs in (worktree or workspace). |
| `chat.option(id)` | Unchanged; new ids `isolation`, `background`. |
| `hex.agent({... run: { ..., environment(chat), capabilities: ["steer","permission_tool","plan","subagents","resume"] } })` | `environment` adds variables to the process; `capabilities` lets the UI hide what a system cannot do. |

Each addition is feature-tested by extensions (`chat.plan != null`) as `claude_code` already does for `hex.agent`.

### 9.2 Claude Code (`hex-extensions/claude_code/extension.hor`, 1.6.0 today)
Exists: `--append-system-prompt hex.harness_rules`, `--agents` with the Explore override, `parent_tool_use_id` -> `chat.progress`, steering by stream-json on stdin, `--mcp-config` per chat, `--resume`.
- `arguments_for`: the appended system prompt becomes `hex.harness_rules + "\n" + hex.hub_rules` (guarded `hex.hub_rules != null`); the Explore override's prompt gets the read-only part of the hub rules (show by reference, report findings with `hex_agent_message`). Add `--permission-prompt-tool mcp__hex__hex_permission` when the permission option is "Ask in Hex" (new choice, the default once M5 lands), `--disallowedTools AskUserQuestion` (questions go through `hex_ask_user`). cwd is `chat.root`.
- `heard`: `tool_use` named `TodoWrite` -> `chat.plan(input.todos)` (no row); `Edit`/`MultiEdit`/`Write`/`NotebookEdit` -> `chat.edit(...)` + `chat.touched(path, "write")`; `Read` -> `chat.touched(path, "read")`; the same inside the `parent_tool_use_id` branch with the parent id as `tool_id`, next to the `chat.progress` call that is there; `result.usage` cache fields passed separately; `system/init` gives model and (where present) the context limit.
- New options `isolation` (shared | worktree) and `background` in `options`.
- Environment: `MCP_TOOL_TIMEOUT` raised to cover the 600 s waits (**verify** the variable name for the installed version).
- Version bump.

### 9.3 Instructions
`HexAgentProfiles.HARNESS_RULES` exists and already ends with the coordinating lines (start cheaper agents, `hex_agent_wait` wakes on messages, `hex_agent_send` steers, `hex_agent_message` passes findings on). The plan adds a second constant beside it, `HexAgentProfiles.HUB_RULES`, exposed as `hex.hub_rules` in `HexExtensions.Agents.cs`, sent through the same channels as the harness rules (Claude's `--append-system-prompt` and `--agents`, Codex's `developer_instructions`). No apostrophes in it (Codex takes it as a TOML literal). Draft:

```
- asking the user, hex_ask_user with options, a recommended one and code refs, rather than guessing or asking in prose
- showing code, hex_show with a path and a symbol or lines, rather than pasting it
- keeping a plan, hex_plan_update as items start and finish
- changes the user reviews, hex_propose_edit with anchored old_text and new_text, then act on the verdicts
- starting agents, give them context refs, read hex_agent_brief rather than the whole chat
- messages of kind notice are Hex telling you something changed, act on them
```
The fourth line is included only when the session's "Review edits" setting is `propose` (10.6). The spawn preamble in `AgentSpawn` (which already explains messaging) gains one sentence naming `hex_ask_user` with `route: auto`. `mcp.instructions` (for clients Hex did not start) gets the same lines in prose.

### 9.4 Codex (`hex-extensions/codex/extension.hor`, 1.5.0 today)
Exists: `-c developer_instructions` with the harness rules, MCP config on the line, `tool_timeout_sec=660`. Codex is not steered: mail reaches it through its inbox and `UnreadPrompt`, and an unread instruction starts its next turn (section 0).
- `arguments_for`: developer instructions become harness rules + hub rules.
- `heard`: `item.started/completed` of type `todo_list` -> `chat.plan`; `file_change` -> `chat.edit`/`chat.touched`; `command_execution` stays a tool row; token usage with cached input where reported (**verify** item type names against the installed `codex exec --json`).
- Because Codex only sees mail between tool calls, notices appended to tool results (7.9) are its fastest route; answers from the user reach a Codex that is still inside `hex_ask_user` directly, and one that moved on at its next tool call or next turn.
- Approvals: `codex exec` has no interactive approval channel; the sandbox option stays the guard, and Hex-side approvals apply to Hex tools (`hex_terminal_run`, `hex_propose_edit`, restore, merge). Moving Codex to its app-server protocol for live steering and approvals is an open question.
- cwd `chat.root`. Version bump.


### 9.5 Any other MCP client
Nothing to install: connecting to Hex's server gives the hub tools; the client appears under "Connected" in the Fleet.

## 10. Interaction details

### 10.1 Sending and steering
Same rules as the side panel (`SendToAgent`, `SteerAgent`, queued bubbles). In Noon, `Enter` sends, `Shift+Enter` newline, `Ctrl+Enter` sends and keeps focus; `Esc` with an empty box moves focus to the thread. `@` opens a chooser (`UIChooser`) over harness files and symbols; a picked item becomes a chip and a `HexCodeRef` in the prompt (`[from path:line]` form the panel already uses), not pasted text.

### 10.2 Questions
- Options are picked with `1`..`9`, `Tab` moves between parts, `Ctrl+Enter` answers. `Other` opens a one-line box in place. A multi-part question can be answered partly and left; the draft stays.
- An option with a `preview` shows its code in a card beside the options when focused, through the card pool.
- "Let #parent decide" sends it up the tree (mail of kind `question` to the parent, 7.2). "Remember" stores the answer under the question's `remember` key in `rules.hor`.
- Nothing about answering steals the view: cards sit in the thread where they were asked and in the Inbox; a toast's button opens the Inbox with that card focused, and `Alt+Left` (the existing places history, `Places.cs`) goes back to where the user was.

### 10.3 Approvals and rules
Rule = `(action, pattern, scope, decision)`: pattern is a command glob (`dotnet test *`), a path glob, or a tool name; scope is session, workspace or user. Stored in `rules.hor` in the user folder (never in `.hex/`, same reasoning as `mcp_server`: a cloned repo must not grant itself permission). The settings window lists and edits them. High-risk actions (delete outside the workspace, `git push`, restore, merge) never get "always".

### 10.4 Persistence
- Sessions: appended as events arrive, flushed at most every 2 s and on turn end, off the editor thread through one writer queue. On open of a workspace the `index.json` is read; transcripts load lazily when a session is selected. Stored sessions are shown as Done/Failed/Stopped; "Continue" resumes with the stored agent session id.
- Layout (`layout.hor`): column widths, collapsed sections, stage tab, selected session, fleet filter, last face. The last face is also kept in the workspace state so Hex reopens in Noon when it was closed there (setting `noon_restore_face`, on by default).
- Caps: 200 sessions or 200 MB per workspace, oldest archived first removed; "Keep" pins a session.

### 10.5 Notifications
| Event | In Hex | Outside Hex |
| --- | --- | --- |
| Blocking question or approval | Noon badge, toast with "Answer" (sticky), Fleet `?`, side-panel robot badge | Taskbar flash when the window is not focused (setting) |
| Turn finished (not background) | Toast, unread dot | Flash only when the turn took over 30 s |
| Failed, budget reached, conflict | Toast (error/warn), notice card | Flash |
| Proposal ready | Toast with "Review" | - |
A "do not disturb" toggle in the totals strip holds toasts and flashes (badges still count).

### 10.6 Settings (`HexSettings`)
| Key | Kind | Default | Scope |
| --- | --- | --- | --- |
| `noon_restore_face` | flag | true | user |
| `noon_review_edits` | choice `propose`, `write_then_review`, `write_freely` | `write_then_review` | user or workspace (workspace may only tighten) |
| `noon_auto_accept` | flag | false | user only |
| `noon_checkpoints` | flag | true | user |
| `noon_notify_flash` | flag | true | user |
| `noon_agents_at_once`, `noon_agents_deepest` | number | 6, 2 | user only |
| `noon_question_wait`, `noon_permission_wait` | number (s) | 240, 300 | user |
| `noon_budget_cost`, `noon_budget_tokens` | number | 0 (none) | user or workspace (workspace may only lower) |

### 10.7 Keyboard (key context `noon`, added to `KeysWindow.When`)
| Key | Does |
| --- | --- |
| `Ctrl+Alt+Shift+A` | To Noon / back to the last face |
| `Ctrl+1` / `Ctrl+2` / `Ctrl+3` | Focus Fleet / Stage / Context |
| `Alt+1`..`Alt+5` | Stage tab Thread, Inbox, Review, Board, Search |
| `Alt+Up` / `Alt+Down` | Previous / next session in the Fleet order |
| `Alt+Shift+Up` | The session that started this one |
| `Ctrl+N` (in Noon) | New agent |
| `Ctrl+.` | Jump to the next thing that needs me (question, approval, unread) |
| `Ctrl+Shift+.` | Stop the selected session |
| `1`..`9`, `Ctrl+Enter`, `Y` / `A` / `N` | Inside a focused question / approval card |
| `J` `K` `A` `R` `E` `C` `O` | Inside Review's diff |
| `Ctrl+F` | Find in the thread (the editor's find bar over the virtual list) |
| `Ctrl+Shift+F` (in Noon) | Search tab |
| `Ctrl+Z` on a notice "applied p3" | Offers restore of its checkpoint |
All are commands added before `commands.Build(menu)` under `View/noon` and a new `Agents` menu (<= 20 rows), so they are rebindable and in the palette. Letter keys are `UIKeyUse.Bound` (not while typing). `--face noon|dawn|dusk` CLI flag opens in a face (add to `DawnProgram`, `HexOptions`, `HexLaunch`, the `HELP` text).

## 11. Coexistence with the side-bar Agents panel
- One model: both render `hub.Sessions`. Selection is separate (`agentChat` for the panel, `noonSession` for Noon) so the panel can follow one agent while Noon shows another.
- The panel stays the quick surface while editing: one thread, the chat list, the composer. It keeps what was just built: the folded "Subagent · <type>" row and the `$(mail)` notes (in the panel mail stays a one-line note; the Mail card of 6.5 is Noon's wide form of the same entry). It gains: question and approval cards in narrow form (same builders, `narrow` flag), a plan line under the meta line (`4/9 · doing "Split Tokenize"`), a diff summary row per edit ("3 hunks in Tokens.cs · Review") that opens Noon's Review on that file, an envelope count on chat rows with unread mail, and an "Open in Noon" button in its head (on a family root: "Open family").
- "Explain/Change with Agent" (`DawnScene.Agent.cs`) keeps targeting the panel; a setting or `Shift` on the command sends the draft to Noon's composer.
- From Noon, "Open in Dawn" on any code card switches face and opens the file; the panel then shows the same session if the user asked for it.
- The row builders (`RowOf`, `ToolRow`, `SubagentOf`, `NoteText`, `AgentMarkdown`, `Bubble`) move behind `IAgentRowHost` unchanged in M0; both recent commits are in `DawnScene.AgentsPanel.Rows.cs` already, so nothing is waiting to merge.


## 12. Performance

Budgets (Release, 1600x900 and 3840x2160, 120 Hz): frame <= 8.3 ms; Noon's own tick <= 1.0 ms with 20 sessions streaming and <= 0.2 ms idle; no allocation per tick when nothing changed; face switch <= 1 frame of work after the first build; first build of Noon <= 30 ms (built lazily on first show, not at startup).

Measures
1. **`VirtualStack`** (engine): a vertical container over an item source `(count, key(i), estimate(i, width), make(i), recycle(component))`. It realizes items intersecting the viewport plus one viewport of overscan each way, keeps measured heights per key and width bucket, estimates the rest (rows x line height from character counts), keeps the anchor item fixed when heights are corrected, supports `StickToEnd`, and recycles components by kind. Thread, Inbox, Search results and the Fleet (past 100 rows) use it.
2. **Code card pool**: `HexCodeCardPool` holds a small number of `CodeEditor`s (default 12). A card off screen is a placeholder of known height; coming into view it takes an editor from the pool and sets text, highlighter, tints. Highlight spans for a card's text are cached by `(path, hash)`. `CodeEditor.PaintVersion` already keeps unchanged editors from repainting.
3. **Resize**: only realized rows are refitted (`FollowWidth` pattern); wrap results cached per width bucket (8 px); while a splitter or the window is dragged, estimates are used for unrealized rows and corrected lazily. No work proportional to transcript length on resize.
4. **Streaming**: agent lines are capped per tick (`AGENTS_LINES_A_TICK = 200` exists); text deltas are coalesced and the last block re-laid at most 30 times a second; the stack is told only "last item grew".
5. **Redraw by version**: every view keeps the hub/session version it drew; the Fleet key is a 64-bit hash over session versions rather than a built string (today's `agentsListDrawn` builds a string every 0.1 s).
6. **Off the editor thread**: checkpoints, diffs for Review (`GitDiffText.Of`), store writes, search, worktree commands, brief building for large sessions. Results come back through `InBackground`.
7. **Board**: one `DrawingPanel`, geometry rebuilt only when the snapshot version or the size changes; time axis animation is a transform, not a rebuild.
8. **Memory**: transcripts of unselected stored sessions are not loaded; a live session keeps `MOST_ENTRIES = 2000` rows in memory and pages older rows from its `.jsonl` when scrolled to.
9. **Hidden face costs nothing**: `UpdateNoon` returns after the hub pump when `noon_view` is not visible; Dawn's canvas/code are not laid out while Noon is up (their layers are hidden).

Checks: a `noon-cost` self-test in the style of `typing` ("no stutter allowed"): 30 synthetic sessions, 5,000 rows, 200 code cards; times every tick while streaming, wheel scrolling, dragging both splitters and resizing the window; fails on any tick over 8 ms after warm-up, on any allocation in an idle tick, and reports p50/p99. `HEX_PROFILE` step names `noon.*` added to `HexProfiler`.

## 13. Testing

### 13.1 Unit tests (`Hex.Tests`, no window)
| File | Covers |
| --- | --- |
| `HexAgentHubTests.cs` | Session tree, states, versions, snapshot publishing, command queue order, external sessions. |
| `HexQuestionTests.cs` | Routing user/parent/auto, timeout then later answer, delivery as a new turn, remembered answers, withdraw, multi-part partial answers. |
| `HexProposalTests.cs` | Anchored edits (not found, ambiguous, occurrence), hunk split, accept subset in any order, edited hunk, CRLF and final-newline preservation, new/delete/rename, stale base re-anchor, overlapping proposals. |
| `HexPlanTests.cs` | Merge by id/text, replace, progress line. |
| `HexApprovalRulesTests.cs` | Glob matching, scope precedence, high-risk never "always", workspace cannot loosen. |
| `HexTouchesTests.cs` | BothWrite, StaleRead, worktree separation, release at turn end. |
| `HexCheckpointsTests.cs` | Temp git repo: tree without touching index/HEAD, untracked files, restore of paths, added-file removal, shadow repo for a folder with no git. Skips when git is absent (as the other git tests do). |
| `HexWorktreesTests.cs` | Add/remove, dirty base, derive index shares unchanged files. |
| `HexHubStoreTests.cs` | Append, reload, torn last line ignored, caps, no writes when not kept. |
| `HexSessionSearchTests.cs` | Ranking, filters, budget. |
| `HexBriefTests.cs` | Size cap, contents. |
| `McpTests.cs` (extend) | `CallingChat` for every tool, `_hex_caller` cannot be forged, `hex_permission` accepts unknown properties, notices appended once. |
| `HexAgentSystemsTests.cs` (extend) | Recorded stream-json and `codex exec --json` fixtures -> plan, edits, touches, native subagents, extended usage. |
| `HexFaceTests.cs` | `HexModes.Show` state machine: face wanted across a scene swap, back-to-last-face, background steps run for the hidden scene. |
| `HexMailLogTests.cs` | Family log order and cap, `Between`, `Flows`, user and Hex endpoints, `Re` links, an answer counted as a directive; extends the mail cases already in `HexAgentsPanelTests.cs`. |
| `HexAgentsPanelTests.cs` (extend) | Shared row builders at narrow and wide widths. |
| Engine: `VirtualStackTests` | Realized range, anchor stability on height correction, stick to end, recycle. |

### 13.2 Self-tests (`Hex.Dawn/Testing/DawnScene.SelfTest.Noon*.cs`, registered in `SelfTestSuites`)
All drive the workspace through the MCP server with `HexMcpTestClient`, as the `mcp` suite does, using two clients tagged as chats so no real agent CLI is needed. A stand-in agent system registered by the suite (program: a script file played back as agent output) covers the process path.

| Suite | Steps and checks |
| --- | --- |
| `noon` | Click the Noon button in the title bar; `hex_ui_tree` finds `noon_view`; Dawn's `body` hidden; back with the key; face restored after `OnReturned`; layout widths survive a face switch; `hex_screenshot` shot. |
| `noon-ask` | Tagged client calls `hex_ask_user` (two parts, options, preview); badge shows 1; card in thread and Inbox; click option 2 and Answer; the call returns the expected lines; a second question times out, is answered afterwards, `hex_ask_wait` returns it; parent-routed question answered by the other client with `hex_agent_message(kind: "answer", re:)`. |
| `noon-review` | `hex_propose_edit` on a file in `testFolder` with three hunks; accept 1, reject 2 with a comment, edit 3; file content equals the expectation; result text lists the verdicts; undo restores the checkpoint; stale proposal after an outside edit is marked. |
| `noon-code` | `hex_show` of a symbol renders a `CodeEditor` card with the file's highlighter; "Open in Dawn" switches face and lands on the line. |
| `noon-fleet` | Stand-in agent spawns two children (`hex_agent_spawn`); tree rows, states, tokens and plan progress shown; stop parent stops children; `hex_agent_brief` text. |
| `noon-plan` | `hex_plan_update` merge and replace; Context column and the side panel's plan line follow. |
| `noon-conflict` | Two clients write the same file; conflict notice, Fleet `!`, a notice on the next tool result of both. |
| `noon-approve` | `hex_request_approval` allow once, always (rule made, second call answered with no card), deny; `hex_permission` JSON shapes. |
| `noon-checkpoint` | Turn of the stand-in agent writes files; Review shows changes; revert a hunk; restore the turn; modified-in-editor file refuses. Needs git on the machine, quiet skip otherwise. |
| `noon-worktree` | Spawn with `isolation: worktree`; `hex_read` answers from the worktree; bring changes back as a proposal; discard removes the worktree. |
| `noon-store` | Sessions written to the suite's private home, reloaded by a rebuilt hub, search finds a row. |
| `noon-keys` | Every key of 10.7 does what it says; none fires while typing in the composer except those marked. |
| `noon-panel` | Side panel and Noon show the same session; a question answered in the panel disappears from the Inbox. |
| `noon-looks` | Noon in each built-in look at scales 1.0/1.5/2.0: no clipped label, focus ring on every focusable, flush panes in Solstice, selector and horizon in Noon's accent, theme back to Dawn's on leaving; shots with `HEX_SELFTEST_SHOTS`. |
| `noon-inline` | A pending proposal shows as an inline diff in the file's own tab; Accept/Reject lenses change the buffer as one undo step each; `Ctrl+Z` reopens the hunk; verdicts reach the waiting call. |
| `noon-marks` | A session writing a file marks its tree row and tab; the mark goes when the turn ends; the tooltip names the session. |
| `noon-handoff` | A Problems row, a failing test row, search results, an explorer selection and an editor selection each fill the composer with the right refs and no pasted code. |
| `noon-git` | Source Control groups changes by session; discard of one session's group leaves the user's changes; `hex_git commit` raises an approval. |
| `noon-terminal` | `hex_terminal_run` from a tagged client runs in that session's own named tab, listed in Noon's Details. |
| `noon-debug` | The session that started the debugger gets the stop as a notice; the debug bar says who drives; the user stepping takes over. |
| `noon-mail` | Two tagged clients of one family exchange `hex_agent_message`; Mail rows show in both threads with the delivery said; the family view lists them in order; a question to the user is answered in the Inbox and wakes the coordinator's `hex_agent_wait`; a notice is appended to the next tool result; the Board draws one arrow per message. |
| `noon-cost` | Section 12. Not in the default set if it proves slow (add to `AskedSuites` exclusions). |
| `dusk-noon` (`Hex.Dusk/Testing`) | From Dusk, the Noon button swaps to Dawn in the Noon face; a stand-in agent keeps producing rows while Dusk is on screen. |
Run from the real `Hex/bin/...` (extensions are found by walking up from the exe). Check names start with the suite name.

## 14. Token and time saving strategy

| Lever | How | Expected effect |
| --- | --- | --- |
| Code by reference | `hex_show`, question previews, `@` attachments are refs resolved by Hex | Agents stop echoing code they just read; the user still sees it highlighted. |
| Anchored proposals | `hex_propose_edit` takes `old_text`/`new_text`, returns verdict lines | No whole-file output; rejected hunks cost one line of feedback instead of a rewrite turn. |
| Harness first | Built (`HARNESS_RULES`, section 0). The plan adds per-caller stats in Context and Fleet | Regressions are seen; pressure to keep using it. |
| Read memory | `HexReadMemory`: a caller re-reading the same range at the same generation gets `unchanged since you read it (generation N)` unless `force` | Removes repeat reads, a large share of long sessions. |
| Context packs at spawn | `hex_agent_spawn context:` resolved by Hex under a budget | Children start with what the parent found; no second search. |
| Briefs | `hex_agent_brief` built by Hex, 1500 chars | Parents stop reading child transcripts. |
| Event waits | `hex_agent_wait` wakes on mail (built); answers, questions, decided proposals and conflicts are mail, so they wake it too; `hex_ask_wait`, `hex_proposal_wait` for agents with no children | One blocked call instead of polling turns. |
| Notices | Mail of kind `notice` from Hex, also appended to results already being returned (7.9) | Change awareness with no extra call. |
| Shared findings | `hex_agent_message(kind: finding)` (built) + `hex_agent_findings`, stale-marked by index generation | Sibling agents do not rediscover; a coordinator briefs a new worker from findings. |
| Coordinator pattern | Built: a stronger model coordinates cheaper workers and steers them on each other's findings. The family view makes it watchable and lets the user step in at the right agent | Expensive tokens go to judgement only; the user corrects one worker instead of restarting the family. |
| Remembered answers and rules | `remember` keys, approval rules | Repeat questions and approvals cost nothing. |
| Parent-routed questions | `route: auto` on the mail path; the parent answers with `hex_agent_message(kind: answer, re:)` | The user is interrupted less; the child is unblocked in seconds. |
| Plans from native todo tools | `chat.plan` mapping | Plans shown with zero agent tokens. |
| Model routing | Cost and tokens per subtree visible; `model` per spawn | Cheap models for scouting become the obvious choice. |
| Budgets | Section 8.6 | Runaway subtrees stop early. |
| Worktrees | Parallel writers without conflict turns | Wall time, and fewer "file changed under me" retries. |
| Shared terminal runs | `hex_terminal_run`/`hex_run_task` output is one place; a build already running is reported, not restarted (later) | Fewer duplicate builds. |
Measurement: extend `hex_harness_stats` with per-caller rows and the new counters (reads avoided, briefs served, questions answered by rule); add a scenario to `docs/benchmarks` comparing one task with and without the hub tools.

## 15. Milestones

Each milestone ends with its unit tests and self-test suites green, `docs/agents.md` updated for new tools, and no tick over budget in `noon-cost` once that suite exists (M3). Harness-first steering, folded native subagents and the mailbox with coordinator tools are done (section 0) and are not in any milestone.

### M0. Groundwork (no visible change)
Steps in order; each is a commit that leaves every existing suite green.

| Step | Files | Classes and members |
| --- | --- | --- |
| 0.1 Caller identity for every tool | `Hex.Core/Mcp/McpServer.cs`; `Hex.Dawn/Agents/DawnScene.Mcp.cs`; `Hex.Tests/McpTests.cs` | `McpServer.CallingChat` (`AsyncLocal<int?>`, set in `CallTool` beside `CallingClient` from the session's tag); `HexMcpTool` gains `bool NeedsEditor = true`, `bool TakesCaller = false`; `HexMcpTools.CallAs` sets `CallingChat` for every call and injects `HexMcpCaller.CALLER` when `TakesCaller` or the name starts `hex_agent` (today's rule kept); `DawnScene.QueueMcp` calls `HexModes.Bring` only when the tool `NeedsEditor`. |
| 0.2 Agents tick off screen | `Hex.Shell/HexModes.cs`; `Hex.Dawn/Shell/DawnScene.cs`, `DawnScene.Modes.cs`; `Hex.Dusk/Shell/DuskScene.cs`; `Hex.Core/Agents/HexAgentProcess.cs`; `Hex.Dawn/Agents/DawnScene.AgentsPanel.Run.cs` | `HexModes.Update(float dt)` (replaces `Update()`, both callers pass dt) runs `background[mode]` for the mode not on screen; `HexModes.InBackground(HexMode, Action<float>)`; `DawnScene.TickAgentsOffscreen(float)` = `TakeAgentLines` + `PublishAgentStatuses` + `RunMcpCalls` for calls whose tool is not `NeedsEditor`; `HexAgentProcess.Woken` (`Action?`, called by both reader threads after enqueueing), set in `StartAgent` to `Engine.WindowManager.Wake`. Mark `hex_agent_*`, `hex_agent_message`, `hex_agent_inbox` and the harness tools `NeedsEditor: false`. |
| 0.3 The hub wraps today's chats | new `Hex.Core/Agents/Hub/HexAgentHub.cs`, `HexAgentSession.cs`, `HexHubSnapshot.cs`, `HexAgentHub.Events.cs`; `Hex.Dawn/Agents/DawnScene.AgentsPanel.cs`, `.Chats.cs`, `.Run.cs`, `DawnScene.McpAgents.cs`, `DawnScene.McpAgentMail.cs`; new `Hex.Tests/HexAgentHubTests.cs` | `HexAgentHub { Sessions, Add(chat, kind), Remove, ById, RootOf, Children, Version, Pump(), Snapshot, Post(HexHubCommand) }`; `HexAgentSession` (section 5) holding the existing `HexAgentChat`; `HexHubSnapshot` rows replace `AgentStatus` (same fields plus plan, usage, waiting, native subagents from `SubagentOf`); `DawnScene.agentChats` becomes `hub.Sessions` (a thin `agentChats` view kept for the panel code), `ChatById` -> `hub.ById`, `RootOf` moves to the hub, `agentsWaiting` moves to the hub (`hub.Waiting`), `PublishAgentStatuses` -> `hub.Pump()`. Tool results unchanged byte for byte. |
| 0.4 The family mail log | new `Hex.Core/Agents/Hub/HexMailLog.cs`; `Hex.Core/Agents/HexAgentChat.cs`; `Hex.Dawn/Agents/DawnScene.McpAgentMail.cs`; new `Hex.Tests/HexMailLogTests.cs` | `HexAgentMail.To`, `.Re` (init properties, defaults keep callers compiling); `HexMailLog` (section 5) owned by the hub per family root; `Deliver(to, from, kind, text, directive)` also appends to the log and records the route taken (`HexMailRoute { Steered, WokeWait, Inbox, StartedTurn, Prompt }`) for the UI; `DawnScene.agentMailMade` moves to `hub.NextMailSeq()`. No user endpoint yet (M2). |
| 0.5 Shared row builders | new `Hex.Dawn/Agents/HexAgentRows.cs`, `HexAgentRows.Tools.cs`, `IAgentRowHost.cs`; `Hex.Dawn/Agents/DawnScene.AgentsPanel.Rows.cs`; `Hex.Tests/HexAgentsPanelTests.cs` | `interface IAgentRowHost { float Width; bool Narrow; UISkin? Skin; float Motion; string Said(key, ...); void OpenLink(string); void FollowWidth(Action<float>); void Toggle(HexAgentEntry) }`; `HexAgentRows.RowOf`, `ToolRow`, `Bubble`, `AgentMarkdown`, `Text`, and the statics `SubagentOf`, `NoteText`, `ToolPreview`, `ToolTitle`, `ToolIcon`, `OneLine`, `Lines` move there unchanged; `DawnScene` implements `IAgentRowHost` for the panel and keeps `DrawAgentRows`, `FitAgentRows`, `ShowTyping`, chips and welcome. Existing tests call the statics through `HexAgentRows`. |
| 0.6 `IAgents` for parts | `Hex.Dawn/Context/DawnContext.cs`, `DawnScene.Context.cs` | `interface IAgents { IReadOnlyList<HexAgentSession> Sessions; event Action Changed; void Show(int session); }` on `DawnContext.Agents` (grows `Handoff`, `MarksFor` in M3/M4). |
| 0.7 Proof | new `Hex.Dusk/Testing/DuskScene.SelfTest.Agents.cs`; `Hex.Dawn/Testing/DawnScene.SelfTest.Mcp.cs` | Suite `dusk-agents`: a stand-in agent system (a script played as agent output) keeps adding rows while Dusk is on screen, and a `hex_agent_message` between two tagged clients is delivered without Dawn coming forward. `mcp` suite: `CallingChat` seen by a non-agent tool. |

Progress: 0.1 to 0.4 are built. 0.5 moved the window-free statics into `HexAgentRows` (DawnScene keeps forwarders); `IAgentRowHost` and the instance builders (`RowOf`, `ToolRow`, `Bubble`, `AgentMarkdown`) move with 1.6, when Noon gets a thread of its own and there is a second host, since Noon shows the Agents view itself until then. 0.6 is built (`DawnContext.Agents`). 0.7 is a check in the `mcp` suite that takes the background step by hand (a call about agents answered behind, the next one that drives the editor left waiting), since the self test's Hex runs without Dusk and so can't put it on screen; a real `dusk-agents` suite needs the self test to host both modes.

Accept: `agents-layout`, `agents-markdown`, `mcp` and the agent unit tests pass unchanged; `hex_agent_*`, `hex_agent_message`, `hex_agent_inbox` results identical to before; `dusk-agents` passes; no new allocation in an idle tick (`garbage`).

### M1. The Noon face
| Step | Files | Classes and members |
| --- | --- | --- |
| 1.0 Screenshot pass (17.9) | none (shots kept beside the plan's validation notes) | Corrects sizes and scales used in 1.3. |
| 1.1 Faces and the selector | new `Hex.Shell/HexFace.cs`; `Hex.Shell/HexModeSwitch.cs`, `HexModes.cs`; `Hex.Dawn/Shell/DawnScene.Modes.cs`; `Hex.Dusk/Shell/DuskScene.Modes.cs`; `Hex.Dawn/Assets/hex/localisation/en-gb.hor` (`modes.*`, `dusk_modes.*`); new `Hex.Tests/HexFaceTests.cs` | `enum HexFace { Dawn, Noon, Dusk }`, `HexFaces.Icon(face)`, `HexFaces.Mode(face)`; `HexModeSwitch.Add(TitleBar, HexFace current, Action<HexFace> pick)` (three buttons), `HexModeSwitch.Badge(Button row, int count)`; `HexModes.Face`, `HexModes.Show(HexFace)`, `HexModes.FaceWanted`, `HexModes.LastFace`; `DawnScene.ShowFace(HexFace)`, `OnReturned` applies `FaceWanted`; `DuskScene.SwapMode` -> `PickFace(HexFace)`. |
| 1.2 Noon themes | new `Hex.Shell/Assets/uix/flat/themes/solstice_noon.hor`, `solstice_day_noon.hor`; `Hex.Shell/Settings/HexLooks.cs`; `Hex.Dawn/Settings/DawnScene.EditorSkin.cs` | `HexLooks.NOON_THEME` key and `NoonThemeOf(look)`; `DawnScene.UseFaceTheme(HexFace)` (theme swap, or the subtree accent override if the swap is over a frame). |
| 1.3 The view and its frame | new `Hex.Dawn/Assets/hex/noon.hor`, `noon_agent_row.hor`; `Hex.Dawn/Assets/hex/editor.hor` (include + hidden layer `noon`); new `Hex.Dawn/Noon/DawnScene.Noon.cs`; `Hex.Dawn/Shell/DawnScene.cs` (ctor `BuildNoon()` before `commands.Build`, `panelTicks.Add("noon", UpdateNoon)`), `DawnScene.Chrome.cs` (`FitChrome` -> `FitNoon`) | `DawnScene.BuildNoon`, `ShowNoon(bool)`, `UpdateNoon(float)`, `FitNoon()`, fields `noonView`, `noonSession`, `noonTab`, `noonDrawn`; splitters via `HexSplitter`; built lazily on first show. |
| 1.4 Engine list | new `horizon/Horizon.UI/Components/Containers/VirtualStack.cs` + tests | `VirtualStack { ItemCount, KeyOf, EstimateHeight, Make, Recycle, StickToEnd, ItemGrew(i), ScrollTo(i) }` (section 12). |
| 1.5 Fleet | new `Hex.Dawn/Noon/DawnScene.Noon.Fleet.cs` | `DrawNoonFleet()` from `HexHubSnapshot` by version hash: family blocks (`hub.RootOf`), coordinator glyph, native subagent leaves (`SubagentOf`), envelope counts (`UnreadMail`), "coordinating N" from `hub.Waiting`; `NoonFleetMenu(session)` reusing `ChatMenu` actions; filter and find; totals strip. |
| 1.6 Thread and composer | new `Hex.Dawn/Noon/DawnScene.Noon.Thread.cs`, `DawnScene.Noon.Compose.cs`; `Hex.Dawn/Agents/HexAgentRows.Cards.cs` | `NoonRowHost : IAgentRowHost`; the thread as a `VirtualStack` over `session.Chat.Entries` with `HexAgentRows.RowOf`; `HexAgentRows.MailRow(entry, route)` (the Mail card of 6.5, from the mail entry kind added here; the panel keeps `NoteText`); composer wired to the existing `SendToAgent`/`SteerAgent`/`StopAgent` with `noonSession` as target (those take the chat as a parameter instead of reading `agentChat`); options from `BuildAgentsOptions` generalised to a host panel. |
| 1.7 Family view | new `Hex.Dawn/Noon/DawnScene.Noon.Family.cs` | `DrawNoonFamily(root)`: member cards, the `HexMailLog.All` stream as a `VirtualStack` with kind and member filters, findings list (mail of kind `finding`); composer target picker (coordinator or a member). Decisions column is empty until M2/M3. |
| 1.8 Context, status, horizon, Welcome | new `Hex.Dawn/Noon/DawnScene.Noon.Context.cs`, `DawnScene.NoonMarks.cs`; `Hex.Dawn/Shell/DawnScene.Horizon.cs`, `StatusBar.cs`, `DawnScene.Welcome.cs`, `Assets/hex/welcome.hor` | `DrawNoonContext(session)` (Usage, Details, Mail summary; Plan and Files sections appear in M4); status item "Agents n · ? m"; horizon states for Noon (17.3); Welcome's Agents row. |
| 1.9 Commands, keys, flag, layout state | new `Hex.Dawn/Noon/DawnScene.Noon.Keys.cs`; `Hex.Dawn/Shell/DawnScene.Menus.cs` (`DeclareMenus`: `Agents`), `KeysWindow.When.cs` (context `noon`); `Hex.Dawn/DawnProgram.cs`, `HexOptions.cs`, `HexLaunch.cs`, `Hex/Program.cs` (`--face`, `HELP`); `Hex.Core/Workspace/HexWorkspace.cs` (last face, selected session) | `AddNoonCommands()` (navigation keys of 10.7 that need no later milestone), `NoonLayout` (widths, collapsed sections, tab) kept through `HexFiles.Kept("noon_layout.hor")`, nothing kept in self-test. |
| 1.10 Panel link | `Hex.Dawn/Agents/DawnScene.AgentsPanel.cs`, `.Chats.cs`, `Assets/hex/agents_view.hor` | "Open in Noon" / "Open family" button and `ChatMenu` entries; envelope count on chat rows. |
| 1.11 Suites | new `Hex.Dawn/Testing/DawnScene.SelfTest.Noon.cs`, `.NoonFleet.cs`, `.NoonKeys.cs`, `.NoonPanel.cs`, `.NoonLooks.cs`, `.NoonMail.cs`; `DawnScene.SelfTest.cs` (register); `Hex.Dusk/Testing` (`dusk-noon`) | Suites `noon`, `noon-fleet`, `noon-keys`, `noon-panel`, `noon-looks`, `noon-mail` (without its user-question step until M2), `dusk-noon`. |

Accept: those suites pass; switching faces is one frame; a session started in the panel is usable in Noon and the reverse; a coordinator family started with `hex_agent_spawn` shows as one block with its mail readable in the family view while it runs; shots in all seven built-in looks at 100/150/200% reviewed.

### M2 to M8
**M2. Questions on the mail path.** User endpoint in `HexMailLog` and `Deliver`; `HexQuestion`, `hex_ask_user`, `hex_ask_wait`, `hex_ask_withdraw`; `hex_agent_message` `re` and default `to` user for roots; answers as mail (woken waits, steering, inbox, next turn, all built routes); question cards in both surfaces; Inbox; badge, toasts, flash; remembered answers; `HUB_RULES` and `hex.hub_rules` in both extensions; `--disallowedTools AskUserQuestion` for Claude Code; Decisions column of the family view.
Accept: `noon-ask` and the full `noon-mail` pass; a real Claude Code and a real Codex chat each ask a two-part question and continue with the answer; a coordinator in `hex_agent_wait` is woken by the user's answer to its worker's escalated question (manual checks recorded in a validation doc).

**M3. Code cards and proposals.** `HexCodeCard`, pool, `hex_show`; `HexProposal`, `hex_propose_edit` family; diff cards; Review for proposals with per-hunk accept/reject/edit/comment; `chat.edit` so agents' own edits show as diffs; proposals as inline diffs in the file's own tab (18.2); agent marks on tree rows and tabs (18.1); verdicts delivered as notices; `noon-cost`.
Accept: `noon-code`, `noon-review`, `noon-inline`, `noon-marks`, `noon-cost` pass; a 3-hunk proposal can be decided with the keyboard only, in Noon or in the file's tab.

**M4. Plans, touches, conflicts, Board.** `hex_plan_update/read`, `chat.plan` mapping in both extensions, `HexTouches`, conflicts, notices (7.9) with the tool-result route, `hex_claim`, `hex_touches`, `hex_agent_findings`, Board with mail flow, Files and Plan sections; handoffs from Problems, tests, search, explorer, editor, palette (18.2-18.6, 18.11), `hex_tests`, extended `hex_problems`.
Accept: `noon-plan`, `noon-conflict`, `noon-handoff` pass; Claude Code's own todo list appears as a plan with no tool call from the agent; a failing test reaches an agent in one click with its message, stack and source as refs; the Board shows every message of a live family as an arrow.

**M5. Approvals and checkpoints.** Rules, `hex_request_approval`, `hex_permission` wired to Claude Code, checkpoints, changes in Review (write-then-review), restore, `hex_checkpoint*`, `hex_changes_by`, budgets; Source Control grouping and `hex_git`, local history attribution and `hex_history`, agent terminal tabs, debugger driver and stop notices (18.5, 18.7-18.9).
Accept: `noon-approve`, `noon-checkpoint`, `noon-git`, `noon-terminal`, `noon-debug` pass; with permission mode "Ask in Hex" a Claude Code shell command raises a card and runs only after Allow; a whole turn can be undone and redone.

**M6. Store, search, logs, background.** `HexHubStore` (sessions and the family mail log), lazy loading, Continue, Search tab, `hex_session_search`, Log view, export, background sessions, `hex_notify`, `hex_agent_brief`, read memory, spawn context packs, close question listing running agents.
Accept: `noon-store` passes; Hex restarted shows yesterday's sessions with their mail and continues one; search over 200 stored sessions answers in under 200 ms off the editor thread.

**M7. Worktrees.** `HexWorktrees`, `isolation: worktree`, `HexCodeIndex.Derive`, branch diff in Review, bring back / merge / discard; notebook proposals, task attach, `hex.noon` extension API, designer and values handoffs (18.10, 18.14, 18.16).
Accept: `noon-worktree` passes; two writers on the same file in separate worktrees raise no conflict until one is brought back.

**M8. Scale and polish.** 50-session soak, memory retention suite for Noon (as `document-retention`), narrow-width layouts, all looks, localisation review, settings page, docs (`docs/agents.md`, `docs/extensions.md`, a `docs/context/noon.md` map), benchmark scenario (one task: single agent, coordinator family, coordinator family with the hub tools).
Accept: `noon-cost` at 50 sessions within budget; `garbage` suite still clean with Noon open; docs describe every tool and chat member.

Order notes: M0 steps 0.1, 0.2 and 0.5 are independent of each other; 0.3 before 0.4. In M1, 1.1-1.3 and 1.4 can run in parallel, then 1.5-1.7. M2 and M3 are independent after M1; M4 needs M3's cards; M5 needs M3's Review; M7 needs M5's checkpoints.


## 16. Risks and open questions

Risks
| Risk | Mitigation |
| --- | --- |
| MCP client tool timeouts shorter than a human's answer. | Bounded waits + resumable `*_wait` + answers delivered as new turns (D5). |
| `--permission-prompt-tool` contract, `parent_tool_use_id`, `TodoWrite` shape, Codex item types differ by CLI version. | Fixtures recorded from the installed versions in `HexAgentSystemsTests`; every mapping feature-guarded; the UI hides what `capabilities` does not list. |
| Edits written by an agent's own tools race the checkpoint. | Checkpoint at turn start, not per edit; per-edit attribution is only for display. |
| Checkpoint cost on very large repositories or with huge untracked trees. | Private index is stat-only after the first; honour excludes; time it and skip with a notice past 2 s; setting to turn off. |
| `VirtualStack` height estimates cause scroll jumps. | Anchor on the first visible item; correct heights only outside the viewport or with anchor compensation; tests on anchor stability. |
| The mail path now carries questions, answers and notices as well as agent talk; a flood (a chatty worker, many notices) could bury an instruction. | Per-kind caps (notices coalesce per file and per proposal), instructions and answers are never dropped by the 100-message cap (drop oldest progress and notices first), the family view filters by kind. |
| Hub tools let an agent pester the user. | Rate limit (3 pending blocking questions per session, 10 per project), `route: auto` default for children, do-not-disturb. |
| Auto-accept and "always" rules widen what an agent can do. | User-only settings, never from `.hex/`; high-risk actions excluded; rules visible and editable. |
| Worktree paths confuse language servers and the main index. | Worktrees under `.hex/worktrees`, excluded from the main index and from `files_exclude` by default; no language server started for them (harness only). |
| Dusk's loop sleeps while only agents are active. | Reader-thread wake + background step; `dusk-noon` suite. |
| Session files hold code and prompts. | User folder only, per workspace hash, caps, "Forget this session", nothing in self-test. |

Open questions
1. Name and icon of the third tab (Noon? Zenith? Agents?) and its accent in flush looks (Dawn orange, Dusk violet; a midday teal is proposed in 17.2; not yellow, the theme keeps that for warnings).
2. Should Dusk standing alone (`--dusk` as its own program) show the Noon button at all, or only when hosted with Dawn?
3. Default for `noon_review_edits`: `write_then_review` (fast, relies on checkpoints) or `propose` (safer, slower)?
4. Codex: move to its app-server protocol for steering and approvals, or stay on `exec --json` with the sandbox as the guard?
5. Agents surviving a Hex restart: resume by session id only (planned), or keep processes alive under a small supervisor?
6. Isolation without git: copy-based scratch roots with a baseline, or shared only?
7. Should proposals from external MCP clients (not started by Hex) be allowed to auto-apply ever?
8. Limits `2 deep / 6 at once`: raise with worktrees available, and per system?
9. Is a merge view for conflicting proposals needed (three-way in the existing merge editor, `DawnScene.Conflicts.cs`), or is re-anchor/stale enough?
10. Does Android get a read-only Noon (no processes there), or none?
11. Where the menu entries live: a new top-level `Agents` menu, or `View/agent` extended (menus are capped at 20 rows)?
12. Cost for systems that report none: keep tokens only, or a price table per model in the extension?
13. Handoffs go to the side panel's composer by default (the user stays in Dawn) or to Noon?
14. A `tasks.hor` task kind that starts an agent session: wanted, and in what form?
15. Should extensions be able to add MCP tools through `hex.noon`?

## 17. UX principles: Noon as a native part of Hex

What Hex looks like, as built: one window with a custom title bar (40 px, icon, title, menu bar, the mode selector at the right), an activity bar of 38 px flat icon buttons with a left indicator, a side bar with a bold 0.15-scale title and 22 px flat head buttons, tabs and breadcrumbs over the code, a bottom panel (Problems, Output, Terminal), a status bar, toasts bottom right, and under the title bar the horizon, a 2 px line that fills in the accent while something runs and goes the error red when it failed. The default look is Solstice: a plum slate drawn flush (docked panes square and touching, floating things round with `popup_radius` 12, controls `corner_radius` 6, rows 28 px), solid surfaces, no glow or glide, and exactly one accent per mode that "says where you are and what is running and nothing else". Six other looks exist (Solstice Day, Graphite, Fluid Glass, Frosted Glass, Flat, Visual Studio) and Noon must be right in all of them. Every string is a localisation key; every action is a command with a rebindable key; context menus are `Menu` + `ContextMenu.Show`; choosers are `UIChooser`; settings are rows in the one settings window.

### 17.1 Rules
1. **Nothing new to learn.** A row in Noon is a row of the explorer: 28 px (`row_height`), icon, label, dim detail at the right, hover as `hover_color`, selection as `selection_color` with the left indicator, right-click for its menu, cross to close. Chat rows keep the side panel's 26 px form where they are the same list.
2. **Docked is square, floating is round.** Fleet, Stage and Context are panes: flush against each other in flush looks, separated by the 1 px `border_color` hairline and a `HexSplitter`, cards on a surface with the skin's radius in the other looks (use `HexLooks.Flush(skin)` as Dawn's panes do). Question cards, approval cards, menus, choosers and toasts float: `popup_radius`, the popup material, and glass when the look has it (`HexGlass`, `HexLooks.GlassOf`), never a custom blur.
3. **The accent means "here" and "running".** Noon has its own accent (17.2). It is used for the active selector button, the horizon while agents run, the selected row's indicator, the focused field's outline, the primary button and "this needs you". Never for decoration. States use the theme's own colours: `error_color` failed, `warning_color` conflict or stale, `success_color` accepted or done; diff tints are the ones `DawnScene.Diff.cs` computes for the diff tab.
4. **Tokens only.** No colour literals in `noon.hor` or the code. Surfaces are `background`/`surface`/`control_color`, text `text_color`/`dim_text_color`, read from `compositor.Skin` whenever the skin object changes (the side panel's `agentRowsFor.Skin` check), so theme previews, look changes and Solstice Day are followed live.
5. **Type and spacing from the shell.** Text scales already in use: 0.15 titles and inputs, 0.13 rows, 0.115 meta lines, 0.105 fine detail; spacing on the 4/8/12/16 scale of `docs/ux-revamp-plan.md`; pane padding 10 like `left` and `center`. Code is always the editor font at the editor's zoom (`PrepareEditor`), prose is the UI font.
6. **Still by default.** Solstice is Flat's stillness. Noon adds no idle animation: spinners are the existing `loading~spin` glyph and the breathing robot; nothing pulses, slides or fades unless `settings.UiAnimation > 0`, and durations go through `HexMotion.Duration`. Rows appear in place (the side panel's `agentsStill` rule: reopening a view never replays entrances). Hover never moves or grows a control.
7. **Focus is always visible and always returns.** One focus ring style (the skin's). `Esc` backs out one level (popup, card field, find bar) and ends at the composer. Leaving Noon and coming back restores focus (`HexModeSwitch.RestoreFocus`); answering from a toast returns focus to where it was.
8. **Status in the status bar, progress on the horizon, cards for what needs a decision.** Routine things ("#7 finished", "checkpoint taken") are `Say` (status bar, 10 s, logged to Output). Toasts are for what needs the user or failed, with one direct action; repeated failures of one session collapse into one toast. Long work (checkpoint, worktree, search, store load) is a work card through `IWork.Start` and shows on the horizon like a build.
9. **Words like Hex's.** Short plain sentences in the status bar ("#3 asks which test project"), Title Case only for commands and menu entries, the verbs the rest of Hex uses (Open, Close, Stop, Discard, Stage, Keep). Numbers are exact; ids are always `#n`.
10. **Everything is a command.** Each action in Noon exists in the palette under an `Agents:` prefix, appears in the Shortcuts window with a `noon` context, and can be run by `hex_run_command`. No action is reachable only by pointer.
11. **Empty states do work.** No sessions: the Stage shows the Welcome page's form (left-aligned 760 px column, 28 spacing, the same action buttons as `WelcomeAction`): New agent, Continue last session, Connect an agent system (opens Extensions on the catalogue, as `BrowseAgentExtensions` does), What agents can do here (the docs). No agent system installed: the install card the side panel shows.
12. **Density is the editor's.** Noon is a tool surface, not a chat app: no avatars, no bubble wider than `AGENTS_BUBBLE`, tool calls folded by default, code and diffs given the width.

### 17.2 Accent, theme and looks
- New themes `solstice_noon.hor` and `solstice_day_noon.hor`: the same ground as `solstice.hor`, differing only in `accent_color`, `highlight_color`, `selection_color` (as `solstice_dusk.hor` does). Proposed: a midday teal, `accent_color: vec(0.290, 0.686, 0.706, 1)`, `highlight_color: vec(0.392, 0.765, 0.784, 1)`, `selection_color` the accent at 0.28. Not yellow or gold: the theme keeps yellow for warnings. To be tuned on screen against the plum ground and against `syntax.type`.
- Applying it: showing the Noon face swaps the compositor's theme to the look's Noon theme, and back on leaving, the way Dawn and Dusk differ today. **verify** that a theme swap is within one frame (`UseEditorSkin(..., save: false)` + `ColourChrome`); if not, the accent is overridden for the `noon_view` subtree, the selector and the horizon only.
- Non-Solstice looks have one accent for every mode; Noon follows them unchanged. `HexModeSwitch.Dress` already fills the active button with the accent only in flush looks.
- `HexLook` gains an optional `noon_theme` value so saved looks carry it; a saved look without it derives Noon's accent from the editor theme.
- Glass looks: only floating cards and popups are glass; the transcript surface stays opaque (text over blur is the cost `ux-revamp-plan.md` warns about). Code cards are always opaque.

### 17.3 Title bar and horizon
- Selector `Dawn | Noon | Dusk`, same button style, scale handling (`HexModeSwitch.SetScale`) and tooltips (`modes.active` / `modes.switch`). The badge is a small count after the label in the accent, no bubble.
- Title text in Noon: the workspace, then `Noon`, then the selected session when there is one, through the same `@window.title` mechanism.
- Horizon in Noon: runs (unknown share, 0.55 accent) while any session is working; fills by plan progress of the selected session when it has a plan (done/total); `Failed` red for 8 s when a session fails (`HORIZON_FAILED_MS`). In Dawn the horizon keeps meaning builds and background work; agents working show there through the status item and the robot, so the line never says two things at once.

### 17.4 Panes, heads and rows
- Each pane has the side bar's head: bold title at the left, 22 px flat icon buttons at the right with tooltips, at most four, the rest in a `...` menu.
- The Fleet is a `TreeView` when its features are enough (indent, badges via `row.Badge`/`BadgeColor` as git letters use, multi-select via `TreeView.Many`, columns via `TreeView.Columns` for model/cost/time), so it inherits keyboard navigation and the explorer's look. The three-line row of the mockup (6.2) is the comfortable density; a one-line density (name + right-aligned columns) is the default below 300 px. **verify** whether `UITreeItem` can carry a second line; if not, the tree is one-line only and the rich row is a row template (`noon_agent_row.hor`).
- Stage tabs are a `TabStrip` like the editor's tabs, with counts as badges, if `TabStrip` can be declared from HIDL (**verify**); the buttons sketched in 6.3 are the fallback.
- Context sections are `Collapsible`s with the Properties panel's row style (`shell/property_row.hor`).
- Splitters are `HexSplitter`, drag-to-collapse like the side bar (`Sides.cs`, `MIN_SIDE/MAX_SIDE`).

### 17.5 Popups, menus, choosers
- New agent, pick a session, attach a file or symbol, pick a model: `UIChooser` rows (icon, title, category, keycap), recent first, prefixes consistent with the palette (`@` symbols, `#` sessions, `>` commands).
- Context menus wherever a row or card is (right-click, `Shift+F10`, the menu key), built like `ChatMenu`.
- Confirmations are the existing ask dialog (`ask.hor`), only for destructive things with no undo (discard a worktree with unmerged work, forget a session). Everything undoable is done at once and undone with a checkpoint.
- Tooltips carry the detail a compact row drops, as chat rows do today.

### 17.6 Never take the wheel
- An agent never moves the user's caret, focused tab or face. A tagged caller's `hex_open` opens in the background unless `reveal: true`; a reveal while the user typed in the last 2 s becomes a status-bar offer ("#3 wants to show Lexer.cs:118 · Show").
- The synthetic pointer rules stay (`HexAgentPointer`: the real mouse takes the pointer back). A session using screen tools (`hex_click`, `hex_type`) shows "driving the window" in its row; any real input pauses it.
- Noon never opens by itself. What needs the user raises the badge and a toast; the user chooses when to go.

### 17.7 Sound and system
Taskbar flash only (setting). No sounds. No OS notification centre in the first version.

### 17.8 Accessibility
Every state has a glyph as well as a colour (spinner, `?`, cross, square, check). Keyboard reaches everything (rule 10). Text contrast checked in Solstice, Solstice Day and Graphite. Noon gives its components names and roles for `hex_ui_tree`, which is also what makes it testable; UI Automation exposure is whatever the shell has (the UX plan notes it is thin).

### 17.9 Checking against the real thing
First task of M1: with a Hex running `--window --mcp`, take `hex_screenshot`s of the title bar, each side bar view, the Agents panel, Welcome, the palette, a context menu, the settings window, a toast and a diff tab in Solstice, Solstice Day and Fluid Glass at 100/150/200%; put them beside the mockups of section 6; correct sizes, paddings and scales in `noon.hor` before any behaviour is built. Suite `noon-looks` keeps the shots.

## 18. Cross-feature integration

For each feature: what agents do with it (tools), how agent activity shows in it, and how it hands work to an agent. One helper does every handoff: `NoonHandoff(HexHandoff what)` with `HexHandoff(Title, Prompt, Refs: HexCodeRef[], Extra: string?, Target: session | new | ask)`. It fills the composer of the side panel (default, the user stays in Dawn) or of Noon (`Shift`, or a setting), for the last active session or a new one, with refs as chips, never pasted code. Agent marks come from `HexTouches` and are redrawn through the existing dirty flags (`treeDirty`, `tabsDirty`).

### 18.1 File tree and tabs
- Shows: a file a running session is writing has a robot badge beside the git letter (`row.Badge`/`BadgeColor`, `Git/DawnScene.Git.cs:104`), tooltip "being edited by #3 Refactor lexer"; files with pending proposals show `P`; conflicts take the warning colour. Folders show a dot when anything inside has a mark (as git does). Tabs carry the same badge; a tab of a file an agent changed since the user last saw it gets the unread dot. A worktree session's files are not marked in the main tree; its worktree appears as a root "agent-8 (worktree)" only when asked for (row menu "Show Worktree in Explorer").
- Agents use: `hex_files`, `hex_map`, `hex_skim`; create/rename/delete through proposals (`content`, `delete`, `rename_to`), so tree operations are reviewable and deletes go to `HexTrash`.
- Hands off: explorer context menu "Ask Agent About..." and "Change with Agent..." on files and folders (multi-select gives several refs); drag a file onto the composer to attach it.

### 18.2 Editor tabs, the code renderer and diffs
- Shows: a pending proposal for an open file is drawn **in that file's own tab** as an inline diff: removed lines put back above what replaces them with the diff tab's tints (`LineHighlights`, `LineChanges`), lenses above each hunk `Accept · Reject · Edit · #3 p3` (`LensesAbove`), gutter change bars, `Alt+F5`/`Shift+Alt+F5` between hunks. The buffer is unchanged until Accept; Accept is one undo step in that editor (`Ctrl+Z` reopens the hunk), Reject removes the overlay. A file with proposals shows a thin bar under the tabs: "3 proposed changes from #3 · Accept All · Reject All · Review in Noon". **verify**: whether `CodeEditor` can show removed lines that are not in the document; if not, the tab shows the read-only diff document (`OpenDiff`, kind `"agent"`) with the same lenses, as git diffs do today.
- Shows: where a running session reads or writes, a faint gutter marker in Noon's accent with the session on hover (from `HexTouch` ranges); `hex_agent_hint` hints keep working and gain the session id.
- Shows: "write then review" changes appear as ordinary change bars plus a lens at the top "Changed by #3 this turn · Review · Revert".
- Agents use: `hex_editor`, `hex_open`, `hex_set_selection`, `hex_symbol_info`, `hex_definition`, `hex_references`, `hex_outline`, `hex_propose_edit`, `hex_show`. An open, modified buffer is the truth for proposals, not the disk.
- Hands off: Explain/Change with Agent on a selection (`DawnScene.Agent.cs`, exists); new editor context-menu entries "Ask Agent About This" and "Add to Agent Context" (a ref chip, nothing sent); a "Fix with Agent" entry in the quick-fix list (`HexLanguages.Fixes`) carrying the diagnostic.
- Code cards in Noon and tabs in Dawn are the same `CodeEditor` under the same `PrepareEditor` policy, so font, zoom, wrap, whitespace, semantic colours and themes always match. In a card of a file with a language server, `Ctrl+Click`/`F12` goes to the definition in Dawn, hover and peek (`Alt+F12`) work in place.

### 18.3 Problems
- Shows: problems in files a session wrote this turn can be filtered "from #3's changes" (a chip in the Problems head); the count of new errors since the turn started shows on the session's row and as a notice in its thread ("2 new errors in Tokens.cs").
- Agents use: `hex_problems` with `since` and `mine`; a notice (7.9) after a write when new errors appear in what the caller wrote ("note: 2 new errors in Tokens.cs, hex_problems mine:true"), so the agent fixes them without a build and without being asked.
- Hands off: Problems row menu "Fix with Agent" (the selected rows: message, code, location as refs); head button "Fix All in File with Agent". `F8` navigation unchanged.

### 18.4 Test explorer
- Shows: a run a session started says "run by #8" in the explorer's head; the result is a test card in its thread (passed/failed/skipped, failing names as links into the explorer).
- Agents use: `hex_tests` (7.11) instead of `dotnet test` in a shell: structured failures (name, message, top frame as `path:line`), one run shared with the user, cancel through the explorer.
- Hands off: failing test row menu "Fix with Agent" (the test's source as a ref, the failure message, the stack's frames as refs, the output cut to a budget) and "Explain Failure"; head button "Fix Failing Tests with Agent" (failures grouped by file); "Write Tests for This with Agent" on a class in the explorer or a method in the editor. Run Until Failure offers its first failure to an agent from the result.

### 18.5 Debugger
- Shows: when a session drives the debugger (`hex_debug_start`), the debug bar says "driven by #3" with "Take Over"; a breakpoint an agent set has a robot overlay on its gutter mark (`CodeBreakpointMark`) and is listed under "Agent breakpoints" in the Debug view, removed when the session ends unless kept.
- Agents use: the `hex_debug_*` tools. A stop (breakpoint, exception, exit) reaches the driving session as a notice at once ("note: stopped at Lexer.cs:131, breakpoint, hex_debug_state"), so it does not poll. The user pressing F5/F10/F11 takes the wheel: the agent's next `hex_debug_step` is refused with "the user is stepping".
- Hands off: on any stop, the debug bar and the exception popup offer "Ask Agent Why" (stack frames as refs, the exception text, the top frame's locals under a budget from `HexDebugVariables`); Watch and data-tip menu "Ask Agent About This Value".

### 18.6 Search
- Agents use: `hex_search`, `hex_refs`, `hex_symbols` (harness, off-thread, ranked) rather than `hex_find_in_files`; hits the user should see are a `hex_show` with refs.
- Hands off: Search view head "Send Results to Agent" (hits as refs grouped by file, capped, with the query) and "Replace with Agent..." (the query and the intended change; the answer is a proposal reviewed per hunk, safer than Replace All for anything not literal).
- Noon's own session search is the Search stage tab; the palette's `#` prefix lists sessions.

### 18.7 Source Control and git
- Shows: the Source Control view can group changes "by #3", "by #8", "by you" under each repository (from checkpoints and touches; a toggle in the view head, off is today's list). An agent group has Review, Stage and Discard (discard reverts that session's hunks only). Blame for uncommitted lines names the session. Agent worktree branches are listed in the branches chooser under "Agents". Checkpoint refs (`refs/hex/checkpoints/*`) are hidden from history and never pushed.
- Agents use: `hex_git` (7.11) from `HexGit`'s cached state: status and diff with no process start. Staging, committing and branching are approvals; push is never offered to agents.
- Hands off: commit box button "Write Message with Agent" (the staged diff as refs; when the staged changes are one session's, its brief gives the first draft with no model call); change row menu "Explain This Change" and "Review with Agent" (a reviewer session on the diff); merge conflict lens "Resolve with Agent" (`DawnScene.Conflicts.cs`: both sides and the base as refs, the answer a proposal on the conflicted file).

### 18.8 Local history
- Shows: versions an agent wrote are labelled "#3 Refactor lexer" in the history list (`HexLocalHistory.Keep` gains an optional source); a version is kept before a proposal's first hunk is applied to a file, besides the checkpoint, so per-file undo works without git.
- Agents use: `hex_history` (7.11) to see what a file was before it or another session changed it.
- Hands off: history row menu "Explain What Changed" (two versions as a diff ref).

### 18.9 Terminal, Dusk and tasks
- Shows: each session that runs commands through Hex has its own terminal tab in the panel, named `#3 Refactor lexer` with the robot icon and reused for that session's commands; the user can interrupt it (Ctrl+C) or take the tab over. The same tabs are in Dusk (shared `TerminalHost`), grouped "Agents"; Noon's Details opens the tab in the panel or in Dusk. A command block carries the approval that allowed it. A CLI's own shell tool (Claude's Bash, Codex's command execution) runs outside Hex's terminals: those show as tool rows with output in a terminal-styled card, and the instructions prefer `hex_terminal_run` so output lands in a real tab.
- Agents use: `hex_terminal_run` (waits, exit code, output), `hex_terminal`, `hex_run_task`/`hex_task_output` with attach (a build already running is joined, not started twice); problems parsed by `HexBuildProblems` are appended to results, so no second call reads the output.
- Hands off: terminal context menu on a selection or a failed command "Explain with Agent" / "Fix with Agent" (the command, exit code, last lines under a budget, parsed problems as refs); a failed task's toast gets "Fix with Agent". A task kind that starts a session from `tasks.hor` is an open question.

### 18.10 Notebooks
- Shows: cells an agent changed or ran carry the robot mark; a pending cell edit is an inline diff in the cell's editor with the lenses of 18.2.
- Agents use: `hex_notebook*`; with review on, `hex_notebook_edit` becomes a proposal on the cell; outputs come back budgeted, images as captions unless asked for.
- Hands off: cell menu "Explain Output", "Fix Error with Agent" (the traceback and the cell as refs), "Continue Notebook with Agent".

### 18.11 Command palette and keybindings
- All Noon commands under `Agents:`; the `#` prefix lists sessions (state glyph, name, system, model) and opens one in Noon or the side panel, whichever face is up; recent sessions rank first (`UIRecentList`).
- "Ask Agent: <typed text>" is the last palette row for a query that matches no command.
- The keys of 10.7 are ordinary rebindable commands with the `noon` context; conflicts show in the Shortcuts window like any other.

### 18.12 Notifications, status bar, horizon
- Status bar: the robot item (MCP connections today) becomes "Agents 3 · ? 1" when sessions exist: working count and things needing the user; click opens Noon's Inbox, right-click lists sessions. Cached by tuple like the other items.
- Toasts and horizon: sections 10.5 and 17.3. Work cards for checkpoint, worktree, store load, session search.
- Output panel: one line per session event at `Say` level, so Output reads as a history without opening Noon.

### 18.13 Settings
- An "Agents" section in the settings window (rows of 10.6) with pages for approval rules (list, edit, delete), remembered answers, budgets, and the per-system settings the extensions already declare. Workspace scope is shown and limited as in 10.6.
- `hex_settings_get/set` reach them, except `noon_auto_accept`, limits and rules, refused to agents ("only the user can change this setting").

### 18.14 Extensions
- Agent systems stay extensions (section 9). A `hex.noon` group for any extension: `hex.noon.handoff({ title, prompt, refs })`, `hex.noon.sessions()`, `hex.noon.on_event(kind, fn)` (session done, proposal applied), `hex.noon.card(session, { title, layout })` (an extension's card in a thread, through `IExtensionScreens`). Extensions adding MCP tools is not in this plan.
- Catalogue extensions gain handoffs cheaply, for example `todo`: "Do This TODO with Agent" on a todo row.
- The Extensions window tags agent systems with a robot and lists their capabilities (9.1).

### 18.15 Welcome page and workspace
- Welcome (`welcome.hor`): under the recent files a row "Agents": New agent, Continue "<last session>", how many sessions need the user. Same left-aligned column and action style.
- Workspace state (`.hex-workspace`): last face and selected session, so reopening a folder returns to where the user was. Multi-root workspaces: a session's root is the folder it was started for (picked in the new-agent form when there are several) and its relative paths resolve there.
- Solution Explorer: project row menu "Ask Agent About This Project" (the project file and its outline as refs).

### 18.16 Designer and values
- Layout designer: "Change Layout with Agent" on selected components sends their generated code range as a ref; the answer is a proposal on the `.hor`, which the designer re-parses as it does for hand edits. `hex_screenshot` of the canvas lets the agent see the result.
- Values panel and HIDL console: "Explain This Value" on a row; a console error gets "Fix with Agent".

### 18.17 What integration costs
Each handoff is a menu entry plus one `NoonHandoff` call; each mark is a badge read from one dictionary (`HexTouches.MarksFor(path)`) updated when touches change, never per tick. Features do not reference Noon: they call a narrow `IAgents` interface added to `DawnContext` (`Handoff(HexHandoff)`, `MarksFor(path)`, `Changed` event), the same pattern as `IPanels` and `INotices`, so parts stay free of `DawnScene`.
