# Hex 3.1.1 Amphetamine

C# completion keeps up with typing again.

- completion is asked before the colours, folds, hints and lenses Hex asks for after every change, which wait until typing stops; csharp-ls answers in turn, and on the engine's solution each completion waited 0.3 to 2 seconds behind them where 0.07 does
- a word the server had nothing for yet (csharp-ls still loading its solution) is asked again as it's typed on, rather than offering nothing until the next word
- a project file changing outside the folder csharp-ls runs in, in a build's output or in a hidden folder, no longer starts it again, which took the engine's solution half a minute to load each time
- a repository's checkpoint is found through a link in its path (macOS's /var), so restoring a turn's changes puts them back there

# Hex 3.1.0 Amphetamine

Hex 3.1 brings Agentic, a face of its own for agents beside Dawn and Dusk. Claude Code, Codex and Antigravity work side by side in a fleet, start and steer each other, and every change they make is checkpointed, so it can be reviewed hunk by hunk and put back. Hex also gets a nightly build of its development branch that installs beside the stable release.

## Agentic

- a face of its own, from the title bar or its key: the fleet of chats on the left, the chat in the middle, Details on the right, in a teal accent of its own in the Solstice looks
- tabs over the middle, Chat, Inbox, Changes and Plan, on Alt+1 to Alt+4
- the Inbox gathers what every agent waits on you for, its requests to Allow or Deny, its questions and the chats that finished while you were elsewhere; Ctrl+Alt+Y and Ctrl+Alt+N answer the request the keys say they're for
- the Plan shows the to-do list the agent keeps as it works, and an empty one offers to ask for a plan first
- each chat's card says its agent system, its model, its tokens and cost, what it's doing now, and a thin bar of how full its context is, green turning amber and red
- the fleet has a filter and puts itself away on Ctrl+Alt+B; the keys go round the fleet, up to the agent that started one and on to the next that wants looking at
- Hex starts in the face the workspace was left in, or the one --face names

## changes you can trust

- every turn of an agent gets a checkpoint of each git repository in the workspace as it begins, kept with the chat across restarts and pinned so git's tidying never takes it
- Changes lists what each turn wrote, to Diff against the checkpoint, Review in place hunk by hunk, keeping or undoing each on its own, or Restore whole
- undoing a hunk keeps the file's encoding and won't write over a change made since; Restore never deletes a file the checkpoint couldn't have had, an ignored .env or a nested repository's, and says why it left one alone
- Restore names files changed again since the turn and asks again if one changes while it waits

## agents that work together

- agents start agents of any system and model, give them their permission rules and answer what they ask leave to do
- a family of agents keeps one conversation of every message between them and you, in Details and in a window of its own filtered by kind and member, each agent's number opening its chat
- an agent closes or clears the chats it started
- Antigravity joins Claude Code and Codex, and each system's models come from the system itself, so new models show up without a Hex update
- each agent system's badge is in its own colour

## the agents' box

- @ lists the workspace's files and declarations that fit what's typed after it, Tab or a press putting the path in
- / lists the commands the agent system has
- pictures pasted in show as small ones of themselves, and pictures in a chat open full screen to zoom and drag
- the model the next turn runs on and how hard it thinks are pickers beside the box
- what you type for a working agent is held until it next speaks, and can be taken back, changed and sent again
- agents can ask you a question on a card with its options, ask leave for a tool, and say when they compacted their context

## harness

- what the harness saved is a window drawn like Horizon's performance overlay: the saving, tiles, a bar of each tool's part and a table of every tool and agent system
- agents reach for Hex's tools before the shell, and the guard turns a grep, find or cat of the workspace back to them

## Hex Setup and nightly builds

- Hex Setup installs the stable release or the development branch's nightly build, each in a folder of its own with its own entries and shortcuts, so both can be on a machine; the nightly one is Hex Dev, its icon grey with a yellow H
- each updates from its own kind of release, Hex Dev from the newest nightly
- Help has Open Hex Setup
- the development branch is built every night it moved and published as a nightly prerelease, the last seven kept

## performance

- a chat lays out and paints only the rows near what's in sight, so a long chat costs what a short one does, and a chat put on screen makes its rows as they come into sight
- Hex standing still with the agents on screen allocates next to nothing
- chats are written to the disk off the editor's thread, and a chat keeps its latest rows in memory, the rest read back a page at a time
- an idle Hex no longer gathers pipe handles from git, and a first launch of a new build no longer loads the shader compiler when the shipped shaders will do

## and

- Markdown files open in their preview
- a notebook cell's code and its output have thin outlines
- the wheel scrolling a list no longer snaps back when rows above it measure in
