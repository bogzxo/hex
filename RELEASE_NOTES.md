# Unreleased

## Hex Link

- Connect your machines from the Devices window. Copy an invite, paste it on the other machine, and approve what each device can do. Choose a viewer, collaborator, or build and test worker.
- Share a workspace or get a copy from another device, including its Git history and nested repositories. Hex keeps saved files in sync and can follow the owner's branch and commits. Progress, skipped paths, and conflicts appear in Devices.
- Open the same file to edit together, even before it is saved. See named carets and selections, follow someone through the file, and undo your own edits. Viewers can watch the shared buffer without changing it.
- Run an agent on another machine and answer its approval requests from your own. File claims show who is working on a path. Use a private worktree to build and test a change before publishing it back to the shared workspace.

# Hex 3.3.0 Amphetamine

Ctrl+. as a menu of everything that can be done to a line, an explorer you work in place, faces that switch without a flash, a C# language server that answers in under a second, and a pass over the whole of Hex and its engine to make it lighter.

## the editor

- Ctrl+. is a menu of actions for the line or what's selected: the language server's fixes first (the one it would pick at the top), its refactorings and what tidies the file, the editor's own (rename, format the selection, organise imports), and an agent to fix the problem, write what a TODO or a stub leaves or explain the code, started at once with the code attached
- a lightbulb in the gutter on the caret's line when the compiler or the server has a fix, a click opens the menu
- tabs can be pinned, a problem's hover is a card saying how bad it is, and the editor's bars, gutter and popups are tidied to one look
- the explorer is worked in place: type to find a file, New File and Rename name it on the row (Esc or a click away with nothing typed takes it back), drag to move and Ctrl to copy, the files you have open are marked and the one on screen is shown as you switch tabs
- folders in the explorer are in the text's colour with a dot for a change or a problem under them, a file with problems says how many, and a chain of single folders is one row
- Properties starts with the file's icon, name, place and git state, with buttons to open, copy its path, show it in the file explorer or open it with the system's program, and read-only values read as text
- the welcome page leads with one thing, Open Folder or Clone the first time and what you had open after, and Getting started is a checklist of six that says what's done
- the settings window says what each setting does in a line, the Language Servers window reads like it and takes the first click, and both fit the screen at 200%
- menus that don't fit the title bar at 200% fold into a … menu
- notebooks say what their cells are doing: a count, the time of the last run, queued and running with the seconds going, errors with a button to the line that failed, long output folded, and the kernel's state on the bar
- Dusk and the terminal page say when a shell ended and with what, a failed shell keeps its pane, tabs show busy, rung and failed, and find floats over the pane
- going between Dawn, Agentic and Dusk never flashes: a face is warmed when you point at its button and a short crossfade carries you over (none with UI animation at 0); the first Dusk of a run opens in about 30 ms instead of over a second
- every focusable thing shows a focus ring, Tab goes through the window, links, dim text and the accent's text meet 4.5:1 in every look, and nothing moves with UI animation at 0
- unsent text stays with its chat when you switch chats
- files and folders dragged onto Hex from outside open: files in tabs, a folder into the workspace, onto an explorer folder copied into it, onto Dusk's terminal pasted as quoted paths
- middle-click scrolling glides at slow speeds instead of stepping, and the open tab's title is always readable on its highlight
- a running notebook cell shows a spinner that turns into stop under the pointer

## C# and the language servers

- C# is checked by the compiler alone unless its analyzers are switched on: a file's first diagnostics take under a second where they took 5 to 43, and an idle csharp-ls uses nothing where it kept four cores busy
- csharp-ls is warmed in the background, so the first callers or types an agent asks for come in about a second where they took ten
- a language server is told only of the files it asked to watch, never of another repository's working copy, and a file open in it is never read again behind its back (an edit made twice, errors at lines that were fine)

## Agentic

- finished agents settle into a Finished group by themselves after 30 seconds (Agents' settings say how long, or never), or when you pick Settle from a chat's menu, with those waiting on you and working at the top; the family conversation in Details shows the newest few and folds the rest, keeping the last 50
- Send now gets a message through at once, interrupting a tool that's still going, and an agent waiting on a task of its own says so with Wake now and Stop
- an agent's processes and everything they start are held together and end with it, and with Hex, on Windows, Linux and macOS, so nothing is left running behind a closed or restarted Hex
- Claude Code keeps to Hex's tools by default, agents can start agents six deep, and the agents' tools no longer wait behind another agent's long call
- an agent's edits are checked in under a second where they waited up to 30, hex_diagnostics on an unchanged file answers at once, and hex_screenshot sends a picture a model can use
- hex_agent_restart kills a stuck agent with what it had running and starts it again where it was, and a chat whose process outlived its turn says it's still running
- a tool group's time is the time it took, calls side by side counted once
- twelve agents started by agents can run at once

## faster and lighter

- terminal output three to four times faster, and a language server's messages read about ten times faster
- HIDL parses with a third of the garbage, and an agent's stream of output costs a sixth of what it did
- typing in the command palette or quick open allocates nothing per key, lists, trees and labels stop measuring and copying every frame
- the first frame no longer decodes a background picture twice, and git no longer walks the workspace every six seconds looking for repositories
- Persistent Process lets go of the window's swapchain and the system timer while asleep, and wakes in about 56 ms

# Hex 3.2.0 Amphetamine

Sharper text, a Hex that stays ready in the background, and agents that share their servers, keep their data in one place and get eight new tools.

## text

- letters are made at the size they're drawn instead of out of a distance field, their x height and baseline on whole pixels, so text at 100% and 125% is crisp where it was soft
- subpixel smoothing the way Windows' ClearType draws it, on a screen under 150% when Windows has ClearType on, grey on Mac, Linux, phones and Retina screens; Text smoothing in Settings picks auto, subpixel or grayscale
- text no longer steps at its edges while a window slides or fades in
- one interface size for the whole of Hex, the title bar and its menus included, on top of the screen's own scale; View, Interface Size and Settings pick it, and the code's zoom and the terminal's font go on top of it

## the editor

- Persistent Process in Settings (and Speed on the welcome page): closing the window puts Hex to sleep instead of quitting, a few MB of memory and no CPU, and opening Hex again shows a window in well under a second; File, Exit still quits
- Start with Windows starts Hex asleep when you sign in, so the first window is as quick as the rest
- Hex starts quicker, the first frame sooner and the work that can wait done a moment after
- a file with a line a fifth of a megabyte long (an agent's chat saved as JSON) opens and scrolls at once, coloured all along
- every right-click menu is grouped and short enough to read, what throws something away last and in the error colour, the Run menu split into the session, stepping and what's looked at
- a workspace whose .gitignore leaves out the repositories cloned into it has their code indexed again
- the About box shows Hex's icon again
- closing the window while it was idle no longer skips asking about unsaved changes
- the usage limits shown for Claude are the latest it said, and a limit that's been reached shows 100%
- keys that clashed (Properties and Agentic's fleet, Allow and Deny) only answer where they mean something
- many places do less work each frame, and work that blocked the window happens beside it

## Agentic

- the Plan tab is a plan to work from: what's being done now, what needs you, what's next, the steps done folded away, filters, a step's note, files and owner a click away, and steps you add, edit, reorder or drop reach the agent
- a chat's title is renamed in place on its card, from its menu, F2 or a double click
- Agent Data in the Agents menu and at the foot of Agentic says how much the chats, pictures and the agents' files take, and clears old chats, pictures nothing mentions and temporary files; it all lives in a data folder next to Hex
- Claude Code agents share Hex's servers instead of each starting its own: one Roslyn navigator for a workspace, no language servers of their own, a gigabyte less for every agent running
- new tools for agents, in any language with a language server: callers and callees a few levels deep, implementations, type hierarchies, a rename across every file at once, a line's fixes, a file's errors, a declaration replaced by name and the projects and what uses what; hex_edit and hex_write answer with the file's errors after
- an agent can show you pictures in the chat, and a tool's row opens or reveals the files it was given from its menu
- the thinking dots no longer make the chat jump
- Claude Code's chat carries on after a /compact instead of failing
- the setting that keeps Claude Code on Hex's tools says plainly what each choice does

# Hex 3.1.2 Amphetamine

Badges in Agentic, a settings window that finds what you changed, a welcome page that fills the window, and a lot that feels quicker.

## Agentic

- each agent's card says how it's doing on a badge, Working in blue with a beating dot, Failed in red, a finished chat you haven't seen in the accent
- the tabs' counts are badges, Inbox's in the accent, the Plan's green once every step is done, and the Plan's bar is green the whole way rather than going red at 100%
- Details counts each tool on a badge in the tool's colour
- / commands complete wherever the word is in the box, Tab takes only that word, Enter sends a command written out in full, and a message that starts with a slash is sent as a message
- the list of / commands walks with held arrow keys, Up goes up it rather than closing it, and it scrolls smoothly past its first page
- a picture opened full screen zooms round the pointer and keeps the part you dragged to in sight
- agents can read git's diff of a file and see which files other chats' plans are about to change

## the editor

- the settings window has All, Modified and Commonly used under its search, a press each; what sets a setting sits beside what it says, the key shows when you look at a row, and a changed setting has a dot
- the welcome page uses the whole window, three columns on a wide one, with Getting started for the settings nobody finds by looking
- spelling is checked as you type in comments, text, Markdown and page words, never code
- notification cards slide, fade and make room smoothly instead of jumping
- No AI in Settings or `--no-ai` leaves the agents out altogether

## languages

- the Language Servers window sets up each language a step at a time, and says every server is optional, set up only the ones you want
- a notebook's code cells are read by its language's server, so a missing import is underlined
- servers that wait to be asked what's wrong with a file (TypeScript 7's) are asked

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
