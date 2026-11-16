# Hex 3.0.0 Solstice

Hex3 is a project-health release with a new default look, a steadier base and fewer rough edges. It also brings agents that work together, an extension manager worth the name, and New Project.

## agents that start agents

- an agent in the Agents panel can start others in the background, of any agent system and model, to scout, research, review or take a part of the work while it carries on
- Claude Code can start Codex agents and Codex can start Claude Code ones, each on the model and options it is asked for
- the new tools are `hex_agent_list`, `hex_agent_spawn`, `hex_agent_wait` (all of them, or the first to finish, with a timeout), `hex_agent_read`, `hex_agent_send` and `hex_agent_stop`, for the panel's agents and any other client of Hex's MCP server
- agents started by agents are chats of the panel, nested under the chat that started them, so they can be watched, read and talked to like any other
- they go two deep at most and six run at once, and an agent that tries to go deeper is told so plainly
- stopping or closing a chat stops and closes the chats it started
- a spawn with a model the agent system does not list says so, an empty prompt is refused, and an outside client waiting with no ids waits for every running chat
- `hex_agent_send` to a chat that stopped or failed says it is going again

## the Agents panel

- once there is more than one chat, a list over the chat shows each one with a spinner while it works, a dot when it finished while you were elsewhere, a cross when it failed and a square when it was stopped, with its agent, model and time
- the list grows with the chats up to half the panel, and running chats show their time ticking
- right-click a chat to stop it, copy what it said last, or open the chat that started it
- the activity bar's robot breathes while agents work and its tooltip counts them
- Claude Code can be told more while it works, and what you send waits in a faded bubble marked queued until it takes it in
- dragging the side bar wider or narrower is smooth with a long chat open; rows are fitted to the width as it moves instead of being made again, so nothing stutters or jumps when the drag stops
- Claude Code offers Fable among its models

## debugging through agents

- agents can debug through the MCP server, with breakpoints, start, step, continue, pause and stop that wait until the program stops or ends and say where it is, why, the top of the stack and what it printed
- they can read variables, evaluate in a frame, and take a screenshot of the debugged program's own window

## Codex

- Codex sees Hex's tools even when the user's own Codex config has Hex's server switched off
- Codex's tool calls to Hex go through without an approval nobody is there to give
- its models and reasoning efforts come from Codex's own list, so new models show up without a Hex update
- warnings Codex prints no longer mark a finished turn as failed

## extensions

- a new Extensions window with Installed, Browse and Sources tabs and a search over all three, from the activity bar and from Settings
- extensions can be uninstalled, switched off and switched on again
- extensions can come from a GitHub repository, any git URL or a folder on the machine, as many sources as you like
- when two sources have the same extension the newer version wins, so a folder of extensions being worked on sits next to the published catalogue
- the Settings window has the same lists, their rows the full width of the window
- extensions have `hex.every` and `hex.after` for timers
- Hello Dolly shows a new line every minute
- Emmet expands on Ctrl+Alt+E, Ctrl+E being the start of Ctrl+E T for the Test Explorer

## New Project

- File, New Project (Ctrl+Shift+N) makes a C#, Horizon game, Python, Java, C++, Rust, Go, TypeScript or D project
- it says whether the language's server is installed, fills in a free name and searches the templates
- extensions add templates of their own with `hex.project_template`, as files, a folder or a function of the project
- a Horizon game builds against the engine on the machine or clones it, and opens on a scene that says Goodbye, sunshine!

## colour

- the colour dialog of a swatch has the new picker, with hex and RGBA to type and the file's other colours to take
- any `vec` of three or four numbers all between 0 and 1 gets a swatch and a picker wherever it is, a theme's nested colours included; one given to something named for a colour or a tint always gets one

## look

- Solstice is the default look for new settings: flush panes on a plum slate, orange for Dawn and violet for Dusk
- Solstice Day is its light version; Graphite and the other looks are still under Appearance · Profiles
- a line under the title bar fills as far as a download, an install or an update check has got, runs full while a build or the tests do, and turns red when one fails
- C# keeps ReSharper's colours in Solstice
- the title bar says what is open, then Hex3 and the release's name, then an extension's note, parted by dots, the last two smaller and quieter
- New Project (File, Ctrl+Shift+N) makes a C#, Horizon game, Python, Java, C++, Rust, Go, TypeScript or D project, says whether its language server is installed, and lists the templates extensions add with `hex.project_template`
- Welcome starts at the left, shows the release by name and number, and lists what is new with a button to each thing

## the panel under the editor

- its titles are always there; the panel is open under them or folded down to them
- the title of the page that is showing folds it, the same title again opens it, another title turns the page
- Ctrl+J folds and opens it too, where it used to take the titles away with it
- it no longer folds and opens by itself as files with and without problems come up
- it comes folded on new settings

## the terminal

- its minimap comes up by itself once more than two screens of rows have gone off the top, and goes when the history is cleared
- its settings are rows of the settings window like every other, and the old form of settings.hor is gone

## editing

- three presses on a line of code select the line, and dragging on from there selects whole lines
- buttons give far less under the pointer and when pressed; New File on Welcome gives like the buttons beside it

## fixes

- resize handles sit exactly on the line between a side and the editor, and between the editor and the panel, one handle to a line
- the panel under the editor can be dragged taller and shorter again; it could not be until it had been folded and opened by hand
- after Save As, a rename or a move, the language server follows the file to its new path, so problems keep appearing
- switching between Dawn and Dusk no longer pauses for a blocking memory collection each time
- pressing F7 on the Welcome page no longer leaves the side bar hidden
- an outline could be shown for the wrong file when two were asked for at once
- extension folder watchers are released when an extension is removed and when Hex closes
- settings are written to a temporary file and moved into place, so an interrupted save cannot leave half a file
- Compare with Saved and local history report a file that cannot be read instead of failing
- a terminal tab that is not on screen no longer asks for a new frame on every tick
- Dusk no longer pauses for a blocking memory collection three seconds after it starts
- the Editor skin row no longer reads as changed on untouched settings
- the title bar says which build of Hex it is
- the Source Control side keeps a section closed while git keeps talking, which with several repositories was all the time
- the welcome page scrolls back to the top of the middle, What's new links every release's notes, and the panel's tabs have room round their titles
- wrapped text in a side bar being dragged no longer flashes between the old width and the new one
- a settings file with extension sources in it no longer shows a warning in Problems
- the agents layout self test drags the side by its splitter, the one handle there is

## language

- the interface text is British English throughout: Colour Theme, Initialise Repository, Organise Imports, catalogue, dialogue

- a setting this Hex does not know is written back as it was, so an older Hex no longer drops a newer one's settings

## for people building Hex

- every setting is one row of one table (`HexSettings.Rows`) that reads it, writes it and gives its range; adding one is its field and its row
- what the editor does each tick is a named, ordered list (`HexTicks`); `HEX_PROFILE` now times each step by name
- what Dawn's and Dusk's windows have in common is one class (`HexFrame`): the field of light, the lights and sparks, the popups' glass, the bars' dressing, the line under the title bar and the sizes a window is drawn at

- Hex builds on the engine's Vulkan 1.4 path, push descriptors and host image copies where the driver has them, with 1.3 the least it runs on
- every call an agent makes through Hex's server is tagged with its chat at the pipe's door (`--agent-chat`), which is how Hex knows whose agents are whose; `docs/agents.md` has the tools
- `docs/hex3-plan.md` has the review behind this release and the order of the remaining work
- 161 self-test checks that failed on `development` pass again; the suites no longer leave a changed layout open for the next one

**Full Changelog**: https://github.com/bogzxo/hex/compare/v2.9.1...v3.0.0
