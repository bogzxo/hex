# Hex 3.6.5 Dexamphetamine

Five more passes over Horizon and Hex. The numbers are measured against 3.6.0 on the same machine, see `docs/benchmarks.md`.

## macOS

- Making a new file no longer crashes Hex on a Mac, with or without a folder open, and a code font that can't be loaded or an error from watching the disk no longer takes it down either.

## Faster

- A frame takes a quarter to a third of the time to draw: 0.04 ms while typing (from 0.16), scrolling and in a notebook, and 0.085 ms dragging a window (from 0.23).
- Sitting idle, Hex uses half the CPU it did and about 22 MiB less private memory.
- Typing in a large C# file is steadier, 4.4 ms at the 95th percentile, from 5.5.
- The agents panel, the command palette's helpers and the embedded terminal are only loaded when first opened, and the terminal (Dusk) shows its first frame in 286 ms, from 302.
- Language servers' updates and HIDL scripts are quicker.

# Hex 3.6.0 Dexamphetamine

Built on Horizon 2.0.0, seven passes over the engine for speed and correctness. The numbers are measured against 3.5.2 on the same machine, see `docs/benchmarks.md`.

## Faster

- Hex draws its windows in about half the time: painting is 33 to 51% quicker idle, while typing, scrolling and dragging a window, and a notebook's 38%, with less sent to the GPU each frame.
- The editor's first frame comes up sooner (380 ms, from 389) and it uses about 9 MiB less memory sitting idle.
- Text and the glass panels draw faster.

## Code editor

- Fixes to the gutter, scrolling in popups, zooming a step at a time and multiple cursors.

## Terminal

- Fixes to how it reads what programs print (the VT parser) and to resizing.

## Fixes

- Focus goes where it should in the UI, and a reused draw list no longer shows what was drawn before.
- Language servers and debug adapters start and stop cleanly.
- Sound no longer works a thread while there's nothing to play.

# Hex 3.5.2 Dexamphetamine

## Linux

- A laptop's touchpad scrolls by the pixel, on Wayland and X11, with the same glide after a flick and the rubber band at the ends as on a Mac. Resting two fingers stops a glide, and horizontal scrolling goes the right way.
- Pinch to zoom the code, and swipe with three fingers to go between files.
- Under Wayland Hex uses the system's SDL, so its window is a Wayland one rather than going through XWayland. Without a system SDL it falls back to the one it brings.

# Hex 3.5.1 Dexamphetamine

## Engine

- Built on Horizon 1.5.1. Every Horizon library is now a package on nuget.org (bogzxo.Horizon.*): the engine, HIDL, the UI, the terminal, the editor tooling, the desktop, Android and iOS platforms, and the shader pack builder as a dotnet tool. Hex itself works as it did in 3.5.0.

# Hex 3.5.0 Dexamphetamine

## Scrolling and zoom

- The trackpad scrolls by the pixel on a Mac, with a glide after a flick that slows the way Safari's does and a rubber band at the ends that holds while your fingers are on it and springs back when they lift. Horizontal scrolling goes the right way.
- A scroller inside another (a code block in a chat) hands the scroll on once it reaches its end, and a bounce off one end no longer catches the next scroll as a stretch at the other.
- Pinch and Cmd+wheel zoom keep the middle of the code view in place, go smoothly with a gentle catch at every 25%, and wrapped code zooms in one piece. Line numbers stop growing at 150%.

## macOS

- In full screen the traffic lights stay on Hex's title bar, and the bar is as tall as the system's, which slides down over it.
- Shortcuts written Ctrl mean Command everywhere, and a drag copies with Option, as in Finder.

## Performance

- Hex uses far less CPU and power while agents work. When the screen changes with nobody touching it, it updates 24 times a second rather than 120, labels and buttons that haven't changed aren't drawn again, and spinners and pulses step 12 times a second and stop when Hex isn't the front window. Input, scrolling and animations still run at full speed.

# Hex 3.4.7

## macOS

- **On a Mac with Hex 3.4.6 or older, download the dmg once by hand.** Check for Updates in those versions looks for the portable zip, which isn't released any more, and says the asset is missing. From 3.4.7 on, Hex.app updates itself.

- The Mac download is now Hex.app as a disk image (`Hex-<version>-macos-arm64.dmg` and `-macos-x64.dmg`, each also as a `.zip`), for Apple silicon and Intel. Hex Setup.app and the portable `Hex-osx-*.zip` are gone: the Setup app had no signature, so macOS called it damaged.
- Hex needs macOS 14 or newer (the site and README said 11).
- Hex isn't signed by an identified developer yet, so macOS stops it the first time it's opened. On macOS 15 and newer, open it once, then choose Open Anyway in System Settings > Privacy & Security. On macOS 14, Control-click Hex in Applications, choose Open, then Open. `xattr -dr com.apple.quarantine /Applications/Hex.app` does the same from Terminal.
- Hex in Hex.app updates itself: Check for Updates downloads the new app, puts it in place of the old one when Hex closes, opens it, and puts the old one back if that fails. A Hex that isn't in an app (built from source) opens the releases page instead.

## Discord

- Discord Rich Presence, off until you switch on Settings > Editor > Discord presence. It shows the file you are editing (name only, never a path), the workspace, a language picture, how long you have been in the workspace, and a Get Hex button. Files git ignores, files in a private Link worktree and patterns such as `**/.env*` and `**/secrets/**` are never named, and a folder's settings can only switch the sharing off. See `docs/discord.md`.

# Hex 3.4.6

## Hex Link

- A device that reconnects to the host no longer gets stuck flipping between "Offline from the session" and a claim or "Shared Session". When two devices dialled each other at the same moment, each refused the other's connection, so both links dropped and redialled in a loop. Both ends now keep the connection dialled by the device with the lower id.
- Each linked device's caret now also shows on the minimap and in the gutter, in the device's colour, so you can find someone whose caret is off screen. Hovering the gutter mark names the device.
- The file tree shows a dot in each linked device's colour on the file that device is working in.

## Dusk

- Input typed at a password, passphrase, PIN or verification-code prompt is never kept in history, recalled with Up and Down, or sent to other tabs. Lines already in `history.txt` from earlier versions aren't cleaned.
- Predictions work in the terminal. Turn on Terminal predictions under Settings > AI (edit predictions must be on). A suggestion shows dim after what you've typed: Tab or Right takes it, Escape dismisses it. Your recent commands go to the prediction model, except ones that look like they hold a secret. Nothing is suggested at a password prompt.

## Editor

- An edit prediction no longer draws over the inline git blame. The blame on that line is hidden while the prediction shows.
- The markdown preview shows the document as a page in the middle of the view, a readable width that narrows with the view. The wheel scrolls it from anywhere, the margins too.

## Agents

- The folder agents keep their data in can be changed under Settings > AI > Agents. Leave it empty for the default. Data already kept stays where it is. A folder's own settings and agents can't change it.

# Hex 3.4.5

## Hex Link

- File claims show on every device that keeps a folder in sync, also when a third device joins that never pinned the folder's authority. Claims follow the authority the synced devices agree on and pass through it between the others.
- A private worktree on a device whose folder came only from Sync Folder publishes to the folder's authority. Before, it said there was no authority to publish to.
- Publishing refuses when the authority's folder of the same name is a different repository, and says so. Before, it failed with a list of nested repositories that didn't match.
- Link Chat is a tab of the right side, next to Properties, instead of a column of its own. Each message shows a dot in its sender's colour.
- Each device can pick its own colour in Devices, under This device's details. Only that device can change it. Its caret, the dot beside the files it's editing, its badges and its Link Chat messages use that colour on every linked screen.
- Hovering or clicking the chips on a device's row in the agents list now highlights the row and opens or closes it, as the rest of the row does.
- An agent whose private worktree was discarded can still release its claims and talk to other agents.
- `hex_link_claims` has a `status` action that shows the claim folders, scopes and routes this device has, for finding out why claims don't reach another device.

# Hex 3.4.2

## Hex Link

- Chat with the people connected to you. Link Chat opens on the right when another device connects, with each person's name in their device's colour. Close it and reopen it with Link: Chat. A count shows messages that arrived while it was closed.
- Spy > and a device's name, in the command palette, jumps once to another person's file and caret. Use Follow to keep up with them.
- Add a `.hex-link-ignore` file at the root of a shared folder to keep build output and other local files from syncing. It uses `.gitignore` rules, and changes to it apply at once. When you turn on Link or share a folder that has no such file, Hex offers to create one for common build output.
- Files that are the same on both devices no longer show as conflicts when one copy has a UTF-8 BOM, when a file was only reformatted, or when a newer edit arrives while a sync is saving. A conflict over unsaved changes now clears once both saved files match.
- AI autocomplete over Hex Link works on the device without a model. The device with the model shares it whenever Share with paired devices is on, even with its own predictions switched off. When predictions can't come over Link, Hex says why: no device is connected, the device hasn't allowed this one to use AI completions, or it doesn't share its model.
- Private worktrees are safer. Editing `.hex-link-ignore` in a private copy never publishes ignored files as deletions. A publication that conflicted no longer raises a false conflict the next time. A file too large to publish is named. A private copy can be discarded while its authority is offline. Discarded copies and old snapshots are cleaned up.
- File claims ignore paths that `.hex-link-ignore` leaves out.
- Claims work in a folder kept in sync with Sync Folder, not only one shared or fetched with Get Workspace, and they show on both devices. Editing a file someone else has claimed, or one you're holding, says so. A claim keeps the time it was first made, and times are shown in your own zone.
- A held file that conflicts can be resolved without the merge editor: agents read the conflicting regions and keep their version, the other one or a merged text.
- Holding a file stops the other device's version from landing in it at once.
- A folder synced from another device publishes private worktrees through that device, as one fetched with Get Workspace does.
- Agents in a private worktree can name the shared folder by its path, read files before they're fetched, run terminal commands in their copy, and read the workspace's other folders.
- The device that joins a pairing starts with Collaborator chosen, as the inviting device does.
- Two copies of a folder that are already the same, one with Windows line endings and one with Mac or Linux ones, are matched on the first sync without anything being sent or rewritten.
- A device can let agents started from another one run without asking for anything. This is a separate choice from file edits, which stays the default, and Devices warns what it allows.
- An agent started by another agent takes the same permission level unless it's given its own.

## Editor

- AI autocomplete works in notebook cells, with the notebook's other cells as context. Code cells follow the kernel's language and Markdown cells the Markdown setting.
- `.hex-link-ignore` files are coloured and shown as ignore files, like `.gitignore`.
- A new Horizon game is made on the engine's NuGet packages, so nothing else needs to be on the machine to build it.
- Files written by an agent show up in search and symbols in the next answer, and a folder added to the workspace is indexed in full straight away.
- Rebuilding Hex itself is named so an agent can't mistake it for building its project, and on macOS it no longer replaces the files of the running Hex in place, which macOS answered by closing it.

## Known issues

- File claims made on one device don't yet show on the other in a folder kept in sync with Sync Folder, so the other side isn't warned. Hold changes to a file instead when two people work on it. Fixed in 3.4.5.
- Publishing a private worktree from a device whose folder came from Sync Folder can end with "A task was canceled." Publish from the device that owns the folder, or copy the change across. Fixed in 3.4.5.

# Hex 3.4.0 Modafinil

## Hex Link

- Connect your machines from the Devices window. Copy an invite, paste it on the other machine, and approve what each device can do. Choose a viewer, collaborator, or build and test worker.
- Share a workspace or get a copy from another device, including its Git history and nested repositories. Hex keeps saved files in sync and can follow the owner's branch and commits. Progress, skipped paths, and conflicts appear in Devices.
- Open the same file to edit together, even before it is saved. See named carets and selections, follow someone through the file, and undo your own edits. Viewers can watch the shared buffer without changing it.
- Run an agent on another machine and answer its approval requests from your own. File claims show who is working on a path. Use a private worktree to build and test a change before publishing it back to the shared workspace.
- Hold changes to a shared file while you work. Compare incoming edits, resolve conflicts in the merge editor and submit the result when ready.
- To connect over the internet, enable public connections in Devices. Hex asks before requesting a temporary port forward from your router. Public invites can use an IP address or a hostname.
- On the same network there is nothing to copy: open Invite a device on one machine, pick it under Nearby on the other, and check both screens show the same six digits. The Invite and Join windows lead through it in two steps.
- Chats running on your other devices are grouped by device in the chat list, running and finished, and each device folds away. Rename, stop, restart or delete the ones you started there.

## Editor settings

- AI autocomplete starts off. When enabled, choose a typing pause (100 ms by default) or a shortcut (`Ctrl+Alt+.` by default). When connected to Ollama, pick from its installed models.
- Git support can be switched off. Settings that require Git explain the dependency and become available again when you turn Git back on.
- Settings are regrouped, with AI and Hex Link in sections of their own. Searching for an old section's name still finds its settings.

## Also

- On macOS, Hex comes as an app in a disk image.
- A new C# project or Horizon game always has its project in its solution, so completions work the moment it opens. A solution with no projects in it no longer hides the project beside it.

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
