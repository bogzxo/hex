# Agents (MCP)

Hex can be driven by an AI agent over the Model Context Protocol, Claude Code or anything else that speaks it, and the
agent uses it the way you do. It looks at the window, clicks, types, presses keys and runs commands, and it reads the
code, the problems and the terminal without needing a screenshot for any of it. It's off as it comes. Switch on Agents
(MCP server) in the settings, under Editor, Run and debug, or start Hex with `--window --mcp` for the one run. Hex then puts its
server up on a pipe that only you can open, only from this machine, and only with the token it writes into
`settings/mcp` next to Hex.exe. A folder's own `.hex/settings.hor` can't switch it on, a repository you cloned doesn't
get to open that door. While an agent is connected the status bar says Agent on the right, and a click on it takes you
to the setting.

Claude Code is connected like this.

```
claude mcp add hex -- C:\path\to\Hex.exe --mcp
```

Started by a client like that, `Hex.exe --mcp` has no window. It finds the Hex that has the folder the client was started
in open (`--folder` says another), starts one on that folder with the server on when none has and waits for it to come
up, and then passes the client's stdin and stdout through to it. Every other client that starts its servers over stdio
is set up the same, the command is Hex.exe and the argument `--mcp`. A project can carry it for Claude Code in its
`.mcp.json` as well.

```json
{ "mcpServers": { "hex": { "command": "C:/path/to/Hex.exe", "args": ["--mcp"] } } }
```

`--mcp` always means a stdio bridge, including when a host probes it without redirected stdin. Use `--window --mcp`
to open the editor with its server enabled. Automatic launches are serialised across bridges, and discovery is checked
again after taking the launch lock, so simultaneous chats attach to the first editor instead of each opening a window.
With no explicit folder, a single running MCP editor is reused even when the host starts the bridge elsewhere.
Use `--folder` when several editors are open; an explicit folder never falls back to another workspace.

For a host such as Codex that starts one MCP connection per chat, use attach-only mode:

```toml
[mcp_servers.hex]
command = 'C:\path\to\Hex.exe'
args = ["--mcp-connect"]
```

Open Hex once and enable its MCP server before the host connects. `--mcp-connect` never launches an editor or briefly
opens a window; it reports a connection error on stderr if no matching server is available. Up to 32 simultaneous
connections are supported. The status count says MCP connections: three chats are three connections, not necessarily
three independent agents. Tool traffic stays on stdout and bridge diagnostics stay on stderr.

## No AI

Hex without any of its agents is **No AI**, under Editor, Run and debug in the settings, or `--no-ai` for the one run.
It takes Agentic, the Agents view and its robot, the Agents menu and its keys, the chats kept with a workspace, the
agents' row of the welcome page and the extensions that are agent systems (Claude Code, Codex, Antigravity, whose files
say who their agent is with `hex.agent`) out of Hex altogether. None of it is made as the editor starts rather than
made and hidden, so there's nothing of it to start up or to tick, and a workspace left in Agentic opens in Dawn. The
switch is read as Hex starts, so changing it puts up a card with a restart on it.

The MCP server isn't an agent of Hex's, it's the door for one outside it, so No AI leaves it to its own setting. On, it
has every tool for finding code and driving the editor as ever, and none of those about Hex's own agents: the
`hex_agent_*` tools, `hex_ask`, the `hex_plan_*` tools and `hex_harness_stats`. Switch Agents (MCP server) off as well
for a Hex nothing drives.

## Asking about code in Hex

Open **Agents** from the activity bar to start a chat with a provider. **Explain with Agent** and **Change with Agent**
prepare a draft from the selected code or the line at the caret. Review the draft and send it to the provider from
the sidebar. Use the history button to revisit chats during the editor session.

Once there's more than one chat, a list sits over the chat on screen. Each row says how its chat is doing: a spinner
while it works, a dot in the accent colour when it finished while you were looking at another, a cross when it failed,
a square when it was stopped. Its agent, model and time are at the right. Chats an agent started sit under that agent's
chat, a step in. Click a row to open it, use its cross to close it (along with the chats it started), and right-click
it to stop it, copy what it said last, or open the chat that started it. The list's head folds it away and counts the
chats that are working. While any are working, the activity bar's robot breathes, and its tooltip says how many.

Any working agent can be told more. Send stays beside Stop. Claude Code takes what you send on its stdin, a faded
bubble marked queued until it takes it in. An agent sitting in `hex_agent_wait` for the agents it started (which can
last minutes) gets it as an instruction in its mail instead, which wakes the wait at once, and Codex, which can't be
told more mid-step, finds it in its inbox or starts its next turn with it. Each chat keeps where it was scrolled to as
you go between them.

Paste a picture into the box (a screenshot, an image copied in a browser) and it becomes a chip over the pickers, its
cross leaving it out again. Sent, it goes with the message, Claude Code getting it as an image block of the message and
Codex as an `--image` file, and every agent sees its path at the end of the message too. Extensions get them as
`chat.images()`, each `{ path, media_type, data }` with `data` in base64. Pasted pictures are kept in the
`agent_images` folder beside Hex's settings for a fortnight. The box wraps by how wide its letters really are, so a line
of prose runs to its edge.

The chats say what their agent system did along the way, each a quiet row. Its context compacted (by itself or asked,
and the tokens before and after), a call tried again (a run of retries is one row saying the latest), and coming close
to its plan's limit or going over it, with when that resets. Noon's Details column has a Limits section with the same,
and a bar of how much of the limit is used.

Under the chats, in the Agents view and at the foot of Noon's fleet, the Agent systems panel has a block for each
system Hex can run. Whether it's installed and its version, the models its chats are on, how many are working, the
chats, tokens and cost of this session, the fullest context of its chats with a bar, its compactions and retries, its
limit and when that resets, and what Hex's harness saved it, each line under the system's name beside an icon. The
panel's head folds every system to a line, folded in Dawn and open in Noon, and each system's own head folds it alone.
Its words are put right in place as things move, it's never made again under a click. What the harness saved is
counted across restarts, kept in `harness/stats.tsv` beside Hex's settings, so delete that file to start the count again.

## Slash commands

Type `/` in the composer to see commands for the selected provider. The list keeps every match and scrolls with
the wheel or its scrollbar. Up/Down selects a command, Tab or Enter completes it, and Escape dismisses the menu.
Enter after completion sends the command and its arguments. Descriptions appear on a second line where the provider
supplies them, with the full description in the tooltip. Metadata arriving while the menu is open refreshes it.

Hex handles `/help`, `/new`, `/clear`, `/resume`, `/stop` and `/status` locally. New and clear retain the previous
chat in history. Model, reasoning and permission options are also commands when a provider exposes the corresponding
picker: use `/model`, `/effort` or `/permissions` to choose, or supply a listed value. A chat's option overrides
apply to its next turn; a choice made before starting a chat changes the provider default.

| Provider | Native command support |
| --- | --- |
| Claude Code | Commands, skills, descriptions, argument hints and aliases from its SDK handshake. Local command output and errors are shown in the transcript. |
| Codex | `/compact` uses thread compaction; `/review [instructions]` starts an inline review (uncommitted changes by default); `/skills` refreshes the workspace skill catalog; `/mcp` lists MCP servers. Discovered enabled skills can be invoked as `/skill-name [arguments]` using structured skill input. |
| Antigravity | Built-ins are discovered through print-mode `/help`, skills through `/skills`. CLI-owned commands run separately in print mode; skills use the normal chat stream. |

Codex terminal-only commands are not sent to the model as ordinary prose. Its supported catalog is explicit;
`/skills` discovers custom skills before their first invocation. Claude loads the workspace command and skill
catalog in the background on startup and when switching workspaces. Antigravity has a small fallback catalog
if discovery is unavailable.

Commands are sent without the editor's file-context prefix. Pending inbox messages remain unread until an ordinary
prompt can carry them. Remove image attachments before running a listed command. Native commands wait until the
current turn finishes; a plain `/compact` can instead be queued through Hex's existing compaction mechanism.

Extensions advertise `run.commands` as `[{ name, about, hint }]`; `description` and `argumentHint` are accepted
as aliases for the metadata fields. A provider with no slash parser can set `run.strict_commands: true` to reject
unlisted commands in the composer. Each adapter remains responsible for translating its advertised native commands
into its actual protocol, and for displaying success or failure.

## Keeping agents on the harness

Agents are told to use the harness in their system prompt, and still drift back to grep and cat as a long session
leaves the prompt far behind. So Claude Code runs with Hex as its PreToolUse hook (`Hex.exe --harness-hook`, given in a
settings file of the chat's own with `--settings`). Before Claude Code runs its shell, Grep or Glob, Hex looks at the
call, and a search or read of the workspace (`grep`, `rg`, `find`, `ls`, `cat`, `head`, `tail`, `sed -n`,
`Select-String`, `Get-Content` and the like as the first command of the line) is turned back with the Hex tool that does
it. A grep that filters a pipe, builds, tests, git and anything outside the workspace are left alone. Claude Code's
setting Use Hex's tools in the workspace picks how hard: always use Hex's tools (the default, Read, Edit and Write of
the workspace are turned back too), search and read with Hex's tools, suggest Hex's tools (the call runs with the Hex
tool said beside it) or don't interfere. Codex and Antigravity have no such hook and are told more firmly in their instructions.

An agent's bridge (`Hex.exe --mcp-connect`) outlives the Hex it talks to. When Hex restarts into a build of itself, the
bridge fails the calls that were cut off (so the agent makes them again), holds what the agent says meanwhile, waits up
to three minutes for a Hex to come back on the folder, shakes hands with it as the agent did and tells the agent the
tools may have changed.

## Agents starting agents

An agent run from the panel (or any client of Hex's server) can start others in the background, of any agent system
and model, to scout, research, review or take a part of the work while it carries on. They're chats of the panel under
the chat that started them, so they can be watched and talked to like any other.

- `hex_agent_list()` lists the agent systems that can be started, with the models and options they offer, and every
  chat with its id, name, state and which chat started it. It also says which chat you are.
- `hex_agent_spawn(prompt, name?, agent?, model?, options?, permissions?, approvals?)` starts one and returns its id
  at once. The prompt has to stand on its own, because the new agent doesn't see the conversation that started it.
  Its last message is what comes back. `permissions: { allow: [...], deny: [...] }` says what it may do without asking
  and may never do, in its agent system's own rules (Claude Code's `Bash(git status:*)` or `Edit` go to
  `--allowedTools` and `--disallowedTools`, Antigravity's `command(git status)` to its run's `--settings`, which in agy 1.5 is one whole command line, a prefix
  or a `*` among other words matching nothing, `command(*)` for any). Codex has no rules
  of the kind, its `sandbox` option says what it may do.
- What a spawned agent asks leave to do beyond its rules (Claude Code's and Codex's approvals) comes to the agent that started it
  as a message of kind question, which wakes its wait or starts its next turn, and `hex_agent_approve(id, allow,
  request?)` answers it. The card is in the agent's chat for the person as well, whoever answers first decides.
  `approvals: "person"` leaves them to the person alone. Only the agent that started it may answer for it.
  Antigravity can't ask headless, what it wasn't let do is a note at the end of its turn instead.
- `hex_agent_wait(ids?, mode?, timeout?)` waits until they finish and hands back what they said. By default it waits
  for all the running agents you started. `mode: "any"` returns when the first one finishes. A timeout reports which
  are still running. The wait happens off the editor's thread, so the agents waited for keep using Hex's tools in the
  meantime.
- `hex_agent_read(id, full?, most?)` gives a chat's last reply, or with `full` the whole chat.
- `hex_agent_send(id, text)` is an instruction. It starts another turn of a finished chat, and a running one takes it
  in as it works when it can be told more (Claude Code) or finds it in its inbox when it can't (Codex), starting its
  next turn with it if it hasn't read it by then.
- `hex_agent_message(text, to?, kind?)` tells another agent of the family something while both keep working, the
  parent by default. Its kind is a finding, a question, an answer, progress, blocked or an instruction. A chat started
  by another uses it to report what it finds as soon as it matters, ask, or say it's stuck.
- `hex_agent_inbox()` reads the messages not read yet, oldest first.
- `hex_agent_stop(id)` stops a chat and the chats it started.
- The models `hex_agent_list` offers come from each agent system: Claude Code's initialize, Codex's `model/list` and
  Antigravity's `agy models`, kept from one start to the next, so a new model shows without an extension being
  brought up to date.
- `hex_agent_close(id, clear?)` deletes the chat of an agent you started, and the chats it started, once its work is
  done. With `clear` the chat stays and is emptied instead, its rows, mail and session gone, so the next
  `hex_agent_send` starts it afresh. Only the agent that started it may close or clear it.

So one agent can coordinate others, a more capable model keeping the judgement while cheaper ones explore, implement and
test. `hex_agent_wait` returns as soon as a message comes for the agent waiting (`messages: false` waits on
regardless), so what one finds steers the rest straight away. A message to an agent that's waiting wakes its wait
rather than going in on its stdin, which it would only read once the wait was over. Only chats of one family talk, the
chat somebody started and everything under it. Each message is a line in both chats, to or from whom, its kind and how
it starts, and what a chat wasn't working to read goes in front of what it's next asked.

What Claude Code and Codex are told in their system prompt (`HexAgentProfiles.HARNESS_RULES`, `hex.harness_rules` to
an extension) says to find and read code through the harness before the shell, to run commands with
`hex_terminal_run`, and how to coordinate. The shell stays theirs for anything the harness doesn't do. Claude Code's
own Explore subagent is given the same rules (`--agents`), and what one of its subagents does is a single row, its
latest step and how many it's taken, in place of a row for each.

Two limits apply: agents can start agents only 2 deep, and at most 6 agents started by agents run at once. A chat's
bridge tells Hex which chat it is (`--agent-chat`, tagged at the pipe's door), which is how Hex knows whose agents are
whose. An extension passes the chat to `hex.mcp_server(chat)`, and reads the options a chat was started with through
`chat.option(id)`, which takes priority over its settings.

## Editor tools

`hex_agent_hint(id, text, line, column?, path?, version?)` displays an annotation anchored to the specified position in
an open editor. The source position is marked, with the hint and its × dismiss control on the same code row; hover
shows the full text. Reusing the id updates the hint. `hex_agent_hint_remove(id, path?)` removes it. Hints are separate
from diagnostics, language inlays and code lenses and disappear when the document changes. At most 50 hints are kept
per document. They are never inserted into source or saved to disk.

The tools, all named `hex_` something. `screenshot` (a PNG of the window or a part of it, scaled down if you like),
`ui_tree` (what's on screen with ids, the rows of trees, tabs, the status bar's items and the menus too), `click` (by
id, by the text on it or at a point, left, right or double), `type`, `key` (Ctrl+Shift+P and the like, chords one after
another), `scroll` and `hover`. `commands` and `run_command` for the command palette. `open`, `documents`, `editor`
(the current file's text, caret and selection) and `set_selection`. `problems`, `outline` and `find_in_files`.
`definition`, `symbol_info` and `references`, through HIDL or the file's language server. `terminal_read` and
`terminal_send` for the panel's terminal, `run_task` and `task_output` for the tasks. `settings_get` and
`settings_set`. `wait_for` (Hex idle, a file open, a dialog up or gone, text on screen or gone, a message, a file's
problems). And `selftest`, which runs suites of the self test in a Hex of its own and says what passed, never in the one
you're working in.

The harness is the tools for finding code without reading it, all of them off the editor's thread. `map` (the
folders as a tree with their languages and types), `files` (a file by part of its name), `symbols` (declarations by name,
ranked), `search` (text or a regex, ranked and grouped by file), `refs` (where a name is used, outside comments and
strings), `read` (one declaration out of a file by its name, or lines of it), `skim` (what files are, their summaries and
outlines) and `changes` (what changed since an answer), with `harness_stats` for what they saved. Every one takes
`max_chars`, and an agent system's extension says how big its answers are otherwise (Claude Code and Codex are in the
catalog). The index is kept between runs in the settings folder and takes what the language servers of open files say
they declare. A client can subscribe to `hex://harness/changes` and is told when the workspace changes.
`docs/agent-harness-plan.md` has how it works and `docs/agent-harness-validation.md` what it saves.

`terminal` lists the terminal's tabs and opens, selects, renames, closes or interrupts one, `terminal_run` runs a
command in one and waits until it's done, handing back what it printed and its exit code, and `terminal_read` and
`terminal_send` take a tab. The notebook tools take a `path` and open the notebook first, and `notebook_new` makes one.

The debugger is there too, what F9, F5, the steps and the Debug view are to somebody at Hex. `debug_breakpoint` puts a
breakpoint on a line of a file (a condition if you like, or `remove`) without the file being open, and
`debug_breakpoints` lists them. `debug_start` builds and starts a program under the debugger (a `project`, or what F5
would pick) and `debug_step` continues, steps over, in or out, or pauses. Both wait until it stops, ends or
`wait_seconds` pass and say where it is, why it stopped, the top of the stack and what it wrote since last asked, so
there's nothing to poll. `debug_state` says the same any time, `debug_variables` lists a frame's variables by scope
(objects opened `depth` levels down), `debug_evaluate` evaluates an expression in a frame the way the debug console does,
and `debug_stop` ends it and says its exit code. `debug_screenshot` is a PNG of the debugged program's own window,
drawn by itself while it runs, even behind other windows. Stopped at a breakpoint it can't draw itself, so the screen is
copied where it is, with `raise` bringing it to the front for a moment first. Windows only for now.

Every call runs on Hex's own thread a tick at a time, so a click is a move, a press and a let go like a hand's, and an
idle Hex wakes for it at once. Points are window pixels, the same in a screenshot as in a click. Moving the real mouse
takes the pointer back from the agent. The server is `Hex.Core/Mcp` (the protocol, the pipe, the bridge) and
`Hex.Dawn/Agents` (the tools), `Hex --selftest mcp` tests it in the editor and `McpTests` without one.

