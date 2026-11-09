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

## Asking about code in Hex

Open **Agents** from the activity bar to start a chat with a provider. **Explain with Agent** and **Change with Agent**
prepare a draft from the selected code or the line at the caret. Review the draft and send it to the provider from
the sidebar. Use the history button to revisit chats during the editor session.

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

Every call runs on Hex's own thread a tick at a time, so a click is a move, a press and a let go like a hand's, and an
idle Hex wakes for it at once. Points are window pixels, the same in a screenshot as in a click. Moving the real mouse
takes the pointer back from the agent. The server is `Hex.Core/Mcp` (the protocol, the pipe, the bridge) and
`Hex.Dawn/Agents` (the tools), `Hex --selftest mcp` tests it in the editor and `McpTests` without one.

