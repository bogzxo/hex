# Agents (MCP)

Hex can be driven by an AI agent over the Model Context Protocol, Claude Code or anything else that speaks it, and the
agent uses it the way you do. It looks at the window, clicks, types, presses keys and runs commands, and it reads the
code, the problems and the terminal without needing a screenshot for any of it. It's off as it comes. Switch on Agents
(MCP server) in the settings, under Editor, Run and debug, or start Hex with `--mcp` for the one run. Hex then puts its
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

`Hex --mcp` typed at a prompt rather than started by a client is plain Hex with the server on.

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

Every call runs on Hex's own thread a tick at a time, so a click is a move, a press and a let go like a hand's, and an
idle Hex wakes for it at once. Points are window pixels, the same in a screenshot as in a click. Moving the real mouse
takes the pointer back from the agent. The server is `Hex.Core/Mcp` (the protocol, the pipe, the bridge) and
`Hex.Dawn/Agents` (the tools), `Hex --selftest mcp` tests it in the editor and `McpTests` without one.

