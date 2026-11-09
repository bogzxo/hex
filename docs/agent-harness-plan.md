# The agent harness

What an agent pays for when it works on code is mostly finding things. It greps, gets a wall of absolute paths and
whole lines back, reads a two thousand line file to look at one method, greps again for who calls it, and every one of
those is a call and a few thousand tokens of context it never gets back. Hex already knows the workspace, what's in it,
what each file declares and where. The harness hands that to agents over the MCP server Hex already has, small and
ranked, so a question that took six calls and twenty thousand tokens takes two calls and two thousand.

The agent systems themselves (Claude Code, Codex, whatever comes next) aren't written into Hex. Each one is an
extension that tells the harness who it is and how it likes its answers, and does the setup for its own host.

## What Hex has that this stands on

Found while reading it, kept here so the next one doesn't go looking again.

- The server is `Hex.Core/Mcp`, one `McpServer` a client, over a pipe behind a token (`McpPipeHost`, `McpGate`), the
  bridge being `Hex.exe --mcp` or `--mcp-connect`. `McpServer` knows the client's name from its initialize
  (`claude-code`, `codex-mcp-client`), which is what a profile is picked by. `McpSession.Send` writes a message from any
  thread, which is all a notification needs.
- The tools are `Hex.Dawn/Agents`, a `HexMcpTool` each. Most run on the editor's thread a step a tick as an iterator,
  one that touches nothing of the editor's can be `RunAway` and run on the pipe's thread as a task. The harness's are all
  that kind.
- `hex_outline`, `hex_definition`, `hex_symbol_info` and `hex_references` all work on the file on screen at the caret,
  so an agent opens a file and moves the caret before it can ask anything, and paths come back whole.
  `hex_find_in_files` writes the whole path on every line.
- `HexOutline` is the outline, HIDL by its tokens and the rest by regular expressions a line at a time, depth by
  indent. It keeps the last outline in one static slot for the editor, so it isn't safe off the editor's thread as it
  is. `HexOutline.Fresh` is the same without the slot.
- `HexSearch.TextFilesUnder` is the files of the workspace the way Find in Files has them, with files_exclude,
  search_exclude, .gitignores and submodule checkouts left out. `HexSearch.ReadLines` won't read anything over 4 MB or
  with a zero byte in it. `HexSearch.InTextOrComment` tells a place in a line of C-like code that's in a string or a
  comment. `HexGlob` matches globs the way VS Code's settings do.
- Language servers are `HexLanguages`, one a document while it's open (`linters`). `documentSymbol` is asked already
  for code lenses (`AskDeclarations`), as `LanguageServerSymbol` with children, lines from 0.
- The terminal is Horizon's `TerminalPanel`, tabs (`TerminalTab`) of split panes (`TerminalView`) each with its
  profile (`TerminalProfile`, a name and a command). A view keeps the shell's command blocks when the shell says OSC
  133 marks, with exit codes, and guesses them otherwise, so a marker the agent's command echoes is the one sure way
  to know it's done.
- Notebooks are `HexNotebook` over a `.ipynb`, the tools reading and running the one on screen
  (`DawnScene.McpNotebook`), cells counted from 1 for an agent.
- A tool's schema is `JsonObject`s, and a node can only ever be in one. Two tools sharing a property's schema throws as
  the server starts and takes the whole server down with it, so every tool makes its own.
- `HEX_HOME` moves the settings folder (and the MCP discovery files and the harness's cache with it), which is how a
  second Hex runs beside somebody's own without either seeing the other.
- csharp-ls answers documentSymbol with the file and its namespace around everything, which have to be walked through
  or a class's members end up two levels down.
- `Show(document)` set `browsing` to whether it was browsing and there's a workspace, and the setter, given false, puts
  the side on the outline. So opening any file while the side was on Search, Source Control or Agents flipped it to the
  outline. Now it only goes off the explorer when there's no workspace.
- A row of a chat measured by its scroll's width went round in circles, the bar came, the width changed, the rows were
  made again a different height and the bar went. The rows go by the side bar's width now, in steps.
- A tween's value tween reads where it starts from once, so a looping one goes from the same place every round, and a
  sequence's steps each start where the one before left off.
- The self test suites are named one word each (`mcp`, `notebook`, `terminal`, `extensions`), `--selftest x --exit`.
- Extensions are HIDL with a `hex` object (`HexExtensions.Api`), a group of members a partial. Built in ones are in
  `Assets/hex/extensions`, the rest come from the `hex-extensions` catalog. HIDL has no JSON of its own, so writing a
  host's JSON config from an extension needs Hex to do the JSON.

## What it is made of

### The index, `Hex.Core/Harness`

`HexCodeIndex` is every text file of the workspace's folders with what Hex knows of it, kept in memory and nothing of
the editor's in it, so it runs off the editor's thread and is tested on its own.

- The files are the ones Find in Files looks through (`HexSearch.TextFilesUnder`), so files_exclude, search_exclude and
  every .gitignore go for it too, and bin, obj and the submodule checkouts stay out.
- Each file keeps its text, its lines, its length and when it was written, and its symbols, the outline Hex draws
  (`HexOutline`, HIDL by its tokens and C#, D, the shaders, Python, JavaScript, TypeScript, Markdown, JSON and YAML by
  their shapes) with the container each one is in worked out from how deep it is.
- It's brought up to date when it's asked something and the last look is more than a couple of seconds old. Only a
  file whose length or time changed is read again, so a look at a warm index is a walk of the folders and a stat a
  file. A generation number goes up whenever anything changed.
- Answers are kept by what was asked and the generation they were made at, the last 64, so an agent asking the same
  thing twice (they do) gets it back for nothing.

On top of it, in partials by what they do.

- **Symbols.** Every declaration by name, looked up ranked. The exact name, then the name with different case, then a
  start of it, then the capitals of it (`QM` for `QueueMcp`), then anywhere in it. `Class.Member` narrows by container.
- **Search.** Text or a regular expression, ranked rather than in file order. A line that declares a symbol the query
  names comes first, then files by how much of the query is in their path and how many places they have, and the
  places are grouped under their file so a path is written once and not on every line. Lines are cut to a sensible
  width around what was found. A glob narrows it to some files.
- **References.** A name by the whole word, outside of comments and strings, the declarations marked as such and
  grouped by file. It is by text and says so, a language server's references are still `hex_references`, but it needs
  no file open and no caret and answers in a few milliseconds.
- **Reading a symbol.** The lines of one declaration, a method or a class, by its name in a file. Where it ends is found
  by its braces for C-like languages, by indent for Python and YAML, and by the next heading for Markdown. The doc
  comment above it comes with it. This is the one that saves the most, a method out of a big partial instead of the file.
- **Skimming.** What a file is without reading it, its size, the summary or leading comment it starts with, and its
  outline with lines. Several files at once in one call.
- **The map.** The folders of the workspace as a tree a few levels down, how many files of each language each has and
  the biggest types in it, for an agent that has just arrived.
- **Files.** A file by part of its name or path, ranked the way quick open ranks them.

### What comes back

Every answer is written for a model to read and pay for.

- Paths are relative to the workspace folder (with the folder's name in front when there are several), forward slashes,
  `path:line` so a client can go there.
- Every tool takes `max_chars` and stops before it, saying how much it left out and how to ask for the rest. Without one
  it goes by the profile of whoever is asking, and 8000 characters when nobody said.
- Nothing is repeated. A file's path heads its places once, a symbol's container is written once.

### The tools

All read only, all run off the editor's thread (`HexMcpTool.RunAway`) so they answer while the editor is busy and never
hold up a tick.

| Tool | What it does |
| --- | --- |
| `hex_map` | The workspace as a tree with languages and the main types of each folder |
| `hex_files` | Files by part of their name or path, ranked |
| `hex_symbols` | Declarations by name, ranked, `path:line kind Container.Name` |
| `hex_search` | Ranked text or regex search grouped by file, with a glob and context lines |
| `hex_refs` | Where a name is used, by the word outside comments and strings |
| `hex_read` | One symbol's lines out of a file by its name, or a range of lines |
| `hex_skim` | Size, summary and outline of one or several files |
| `hex_changes` | The files that came, changed or went since a generation |
| `hex_harness_stats` | What the harness was asked this run and what it handed back against reading the files whole |

`hex_find_in_files`, `hex_outline` and the language server's tools stay as they are, the harness doesn't replace
them, it goes first.

### Agent profiles, through extensions

An extension says it is an agent system with `hex.agent({ ... })`.

```
hex.agent({
    id: "claude_code",
    name: "Claude Code",
    clients: ["claude-code"],
    budget: 8000,
    instructions: "Find code with hex_symbols and hex_search, read it with hex_read ..."
});
```

`clients` are what the agent's MCP client calls itself in its initialize, a profile is the one whose name is in there.
The harness then answers that client with that budget, and the server's instructions for it have the profile's own
added to them. An extension can say several, and one that's switched off takes its profile with it.

It needs a little more of Hex to do its host's setup, so `hex` gets `hex.agent(profile)`, `hex.agents()` (the names
of the profiles there are), `hex.workspace()` (the folders open), `hex.executable` (where Hex.exe is), `hex.home` (the
user's home, for a host whose config lives there) and `hex.json_set(file, keys, value)`, a key of a JSON file set with
the rest of the file kept, since HIDL can't read or write JSON itself. What setting a host up means is the extension's
business.

- **Claude Code** (`hex-extensions/claude_code`) has Connect Claude Code to this folder, which writes the `hex` server
  into the workspace's `.mcp.json` (kept as it is otherwise), and Teach Claude Code the Hex tools, a short block in the
  folder's CLAUDE.md between markers it can find again.
- **Codex** (`hex-extensions/codex`) has Connect Codex, which adds `[mcp_servers.hex]` with `--mcp-connect` to
  `~/.codex/config.toml` after asking, and the same teaching block for AGENTS.md.

A new agent system is a new folder in the catalog, nothing of Hex changes.

### Where the server is touched

`McpServer` says which client a call came from (an `AsyncLocal` set around the call) and asks who it's made by for the
instructions for a client as it initializes. Both are a few lines, the tests' stand ins don't notice.

## How it's tested

- `Hex.Tests/HexHarnessTests.cs` and `HexAgentSystemsTests.cs`. The first builds a little workspace in a temp folder (C#, HIDL, Python, Markdown) and checks the
  index finds the files, the symbols rank right, search groups and ranks, references leave comments and strings out,
  a symbol is read to its end, the budget cuts honestly, a changed file is seen and the cache comes back.
- The profile registry matched by client name, and the server's per client instructions and calling client, against
  `McpServer` with a stand in.
- Hex driven for real. `Hex.exe --window --mcp --folder hex`, then a client over `--mcp-connect` doing what an agent
  does for a handful of real questions about Hex's own code, each answered twice, once the harness's way and once the
  plain way (grep and reading files whole), and the calls and characters of both counted. The numbers go in
  `docs/agent-harness-validation.md`.

## Keeping the index between runs

A cold index reads and outlines every file, a second or two over Hex's own repository and longer over the engine. So
the index is written to a file in Hex's cache folder (one per set of workspace folders, named by a hash of them) when
it changes, at most every few seconds and as Hex closes, and read back as it starts. What's kept is each file's path,
length, time and symbols, never its text. A file whose length and time still match is taken as it was and its text
only read the first time a search or a read needs it, so a warm start is a walk of the folders and nothing else. A file
that's gone or changed is dropped from it or done again, and a cache from another version of the format is thrown away.

## A language server's symbols

The shapes the outline goes by miss things, a C# method with no modifier in front, a local function, a property spread
over lines. A file that has a language server (csharp-ls, clangd, pyright and the rest Hex runs) gets the server's
symbols instead, asked as it's opened and saved (`documentSymbol`, which Hex asks already for code lenses) and handed to
the index for that file at that length and time. They're kept until the file changes and written to the cache with
the rest, so the server's answer outlives the server. Where there's no server the shapes are still it.

## Telling an agent what changed

An agent that read a file five minutes ago doesn't know somebody's typed into it since. The harness keeps a log of
what changed, which file, added, changed or gone, at which generation, and says it two ways.

- `hex_changes(since)` lists the files changed since a generation, every answer of the harness ending with the
  generation it was made at, so an agent asks for exactly what it missed and nothing more.
- The server says it can do resources and has one, `hex://harness/changes`. A client that subscribes to it gets
  `notifications/resources/updated` when the workspace changes, the index looking at the folders every couple of
  seconds while somebody's subscribed. Reading it is the same list as `hex_changes`.

## Driving all of Hex

The harness is for finding code, but an agent working in Hex needs the rest of it as well, and the terminal and the
notebooks had the least.

- The terminal had read and send for the focused tab only and no way to know a command had finished.
  `hex_terminal` lists the tabs (title, shell, running or not, which is focused), opens a new one with a profile and a
  folder, selects, renames, closes or interrupts one (Ctrl+C). `hex_terminal_run` runs a command in a tab and waits
  until it's done, by a marker the shell echoes with the exit code after it, and hands back only what that command
  printed and the code, cut in the middle to the budget when it's long, the start and the end being what matters.
  `hex_terminal_read` and `hex_terminal_send` take a tab too.
- The notebook tools only worked on the notebook on screen. They take a `path` now and open it first, which is a
  call less, and `hex_notebook_new` makes a notebook (an empty one with a kernel's language) and opens it.

## The Agents panel, and running agents from Hex

MCP alone can't start an agent's turn, the agent's host has to be asked something first, so a conversation started in
Hex went nowhere until somebody in a terminal told Claude to look. The Agents panel runs the agent itself. It's a page
of the panel under the editor, AGENTS, next to the terminal.

- On its left the agent systems there are (each extension that says how to run its agent), with a dot for whether it's
  ready, running or not installed, the MCP clients connected right now and the editor's Explain and Change requests
  waiting for one, and the chats of this run, New chat on top.
- On its right the chat. What you asked on a card of its own, what the agent said as Markdown, what it thought dimmed,
  and every tool it called as a row of its own with an icon by what kind of tool it is (a command, a file changed, a
  search, one of Hex's), what it was called with, a dot that's running, done or failed and how long it took. A row
  opens to say what it was given and what it said back, a file it names opens where it says. Under it the tokens, the
  cost when the agent says it, the model and how long the turn took.
- The box under it sends on Enter, an agent picked by the chips over it. A toggle puts the file on screen and its
  selection in front of what was asked, `@path:line` and the lines. Stop kills the agent's process and all it started.
  An empty chat has a few things to ask to start from.
- Its colours are the theme's (the skin's palette), so it looks right in every one of them.

How an agent runs is its extension's business, Hex only starts the program and reads it. `hex.agent` takes a `run`.

```
run: {
    program: "claude",
    arguments: (prompt, chat) => ["-p", "--output-format", "stream-json", "--verbose", ...],
    stdin: true,
    on_event: (chat, event) => { ... },
    install: "npm install -g @anthropic-ai/claude-code"
}
```

Hex finds the program on PATH (a .cmd or .bat through cmd, the way npm installs things on Windows), starts it in the
workspace's first folder with the prompt on its stdin, and hands every line it prints that's JSON to `on_event` on the
editor's thread, as HIDL values (`hex.json_parse` is the same for anything else). What the extension calls on `chat`
is what goes on screen. `chat.text(text)` and `chat.thinking(text)`, `chat.tool(id, name, input)` and
`chat.tool_done(id, ok, output)`, `chat.session(id)` (handed back to `arguments` next time so the chat goes on where it
was), `chat.usage({ input, output, cost, model })`, `chat.error(text)` and `chat.done()`. What it prints that isn't JSON
and what it says on stderr go in as notes, and its exit ends the turn whatever it said.

The agent gets Hex's own tools by being handed Hex's MCP server, `hex.mcp_server` is how to reach this Hex
(`{ command, args }`, `--mcp-connect` and the folder), and asking an agent from the panel puts the server up for it if
it wasn't, since somebody pressed Send.

- **Claude Code** (`hex-extensions/claude_code` 1.2) runs `claude -p` with `--output-format stream-json`, the hex server
  in an `--mcp-config` file it writes, `--resume` with the session it was told, a permission mode and the tools it may
  use from its settings (edits accepted and Hex's tools allowed as it comes).
- **Codex** (`hex-extensions/codex` 1.2) runs `codex exec --json` (or `codex exec resume` with the thread), the hex server
  given with `-c mcp_servers.hex...`, a sandbox from its settings, and turns its items (messages, reasoning, commands,
  file changes, MCP calls, searches, its todo list) into the same rows.

## Icons that move

Hex's icons are Codicons out of a distance field font, drawn as a quad each, anywhere a `$(name)` is written (a button's
icon is that too). They were all still, so a sync going on, a language server starting or an agent at work looked the
same as one that had stopped. Now an icon can say how it moves.

- `$(name~spin)` turns it round, `~pulse` swells and shrinks, `~breathe` fades down and back, `~bounce` hops,
  `~blink` goes and comes back, `~shake` jitters side to side, `~beat` beats twice and rests. A button's icon written
  `"sync~spin"` does the same, so does a tree row's or a tab's, since they all go through the same drawing.
- `$(spinner)` is a ring of dots chasing round, `$(dots)` three dots taking turns to hop and `$(pulse-dot)` a dot that
  breathes, drawn rather than read out of the font, in the colour of the text round them.
- A quad of the sprite batch is already a parallelogram (an origin and two axes), which italics shear, so turning an
  icon is its axes turned about its middle. One that's cut by a clip is drawn still rather than turned out of it.
- The motions are tweens of `Horizon.Core.Tweening`, one looping tween or sequence a motion (a dozen in all, not one an
  icon) on a context of their own, each writing a value every icon of that motion reads as it's drawn. So everything on
  screen moves in step and a hundred spinners cost what one does. The context is only ticked, by real time at the start
  of a paint, while a moving icon was drawn a moment ago, and a paint with one in it asks the window for frames a moment
  longer, so Hex sleeping between keys doesn't freeze one halfway. With motion off (`UIDrawList.Motion`, which the UI
  animation setting at 0 is) they're drawn still, a spinner as its ring.

Where Hex uses them. The status bar's work going on and git syncing spin, a game running pulses, the Agent item
breathes while an agent's call is running, the Agents view's tools spin while they run and its chats do while they're
going, a language server that's starting spins in its status and the notebook's kernel pulses while it's busy.

## The Agents view, as it ended up

It moved from a page of the panel to a view of the side bar of its own (the robot on the activity bar, Ctrl+Alt+A),
which is where a chat belongs. The agents are flat chips with a dot for ready, working or not installed, the chats and
who's connected over MCP are a menu behind the clock, and the turn's model, tokens, cost and time are a quiet line under
them. The box is a card at the bottom with the pickers inside it under the text, the file on screen, the model, and
whatever else the agent's extension offers (`run.options`, a list of `{ id, title, icon, choices }`, each kept in the
extension's setting of that id). What you asked is a raised bubble at the right, tools are flat rows like the explorer's
that open onto a thin rule with what they were given and said, and new rows slide up into place. Up and Down in the box
go through what was asked, Escape stops the agent, and the chats menu asks the last thing again, copies the last answer
or says what the harness saved.

## Still left out

- Nothing of the index is shared between two Hexes open on the same folders, each keeps its own, the cache file is
  written by whichever saved last.
