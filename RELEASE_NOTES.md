# Hex 2.6.0

This release improves the agent workflow, makes Problems searchable, keeps the mode switch in one place, and updates Setup together with Hex.

## Agent workflow

- Right-click a line or selection to ask the connected MCP agent to explain or change it. Continue the conversation in View > Agent.
- Agents read requests with `hex_agent_requests`, reply with `hex_agent_reply`, and apply requested line changes with `hex_agent_edit`. Edits are one undo step, leave files unsaved, and reject stale or cancelled requests.
- Agents can add dismissible hints at source positions. Hints stay separate from diagnostics and disappear when the document changes.
- MCP connections attach consistently without transient editor windows. `--mcp-connect` attaches to an existing editor and never launches one. Simultaneous automatic launches are serialised; the status bar counts connections rather than assuming every chat is a different agent.
- The host must arrange inbox polling; an MCP connection alone cannot wake an idle agent.

## Editor and terminal

- Problems can be searched by message, diagnostic code or path, with coloured Error, Warning and Hint groups and counts.
- Dawn and Dusk stay beside the title-bar window controls in both modes.
- Hex keeps one `debug.log` beside the executable, resets it on each GUI launch and shares it safely between windows. MCP bridges do not reset it. Old numbered logs are removed when they are no longer locked by an older process.

## Installing and updating

- Windows Setup is a single executable containing its runtime and editable assets. Linux and macOS retain their setup archives.
- Help > Check for Updates downloads and verifies the release before asking Hex to close, then runs that release's own Setup instead of the old bundled installer.
- Setup re-verifies a private copy of the cached package and installs it without a second package download. The installed updater and uninstaller are replaced with the rest of Hex, and restored together if registration fails.
- Cancelling preparation or the unsaved-file prompt abandons the update. A failed process start leaves Hex open. Settings, extensions and personal files are preserved.
- Uninstall remains available offline and always asks for confirmation.

Windows UI and update tests run locally. Linux/macOS native packaging and execution are validated by their release runners; Windows tests alone do not establish those results.
