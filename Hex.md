 a# Hex to-do

The list of what's asked for and not done yet, kept across sessions, agents and agent systems. Whoever works on Hex
reads it first, ticks what they finish (with the commit), and adds what they're asked for. Newest asks at the end of
their section. Plans in depth are in docs/agent-workspace-plan.md, this is what's on the go.

## Decided

- The agents' face is called Agentic (Noon in the code).
- Agents' edits are written first and reviewed after.
- Agents have their own menu on the bar.
- An agent system that reports no cost keeps a token count.
- Work handed to an agent stays in the face you're in.
- Agents in this workspace use Hex's tools (hex_read, hex_edit, hex_write, hex_search, hex_terminal_run), never their own
  Read, Edit, Write, grep or cat. Claude Code's guard enforces it (strict).

## Agents panel and Agentic

- [x] Tool calls one after another grouped into one row, folded by default, slim lines when opened (3e32fb9)
- [x] Scrolling the chat in Agentic works over the margins round the centred column (HexWheelStack)
- [x] The terminal glitches while the panels round it are resized, the shell told the new size only once it holds
      still for 120 ms (horizon TerminalView)
- [x] A blue gutter beside tool calls while they run
- [x] A click anywhere in the message card's writing room lands in the box (the box is two rows at the least)
- [x] Opening or folding a group of calls keeps it where it is on screen
- [x] The ghost resize line in Agentic was the side's HexSplitter laid on the window over Agentic, now hidden while
      Agentic covers the body, and the agents view's second handle in Dawn is gone (confirmed by the user)
- [x] hex_read reads files the index leaves out (hex-extensions), by range
- [x] "Files it touched" in Details lists the newest first, keeps the last 25, shows 10 with a fold for the rest
- [x] A "Send now" button on a queued message, writing it to the agent at once rather than when it next says something
- [x] Harness answers kept across edits to other files: a read or skim of named files says which (HexAnswer.From) and
      holds while they're as they were. Searches still go by the generation, any edit can change them
- [x] A chat keeps its last 200 entries in memory and the rest in a file of its own, read back only when scrolled to,
      the file deleted with the chat
- Performance by default: lists that grow (files, tools, mail, rows) are capped or paged, nothing kept in memory that
  can be read back when it's needed
- [x] A context menu on the agents' chat rows, Copy, Select text (a read-only editor window to pick any part out), the whole chat as Markdown. Selecting in the rows themselves would need the engine's labels to select
- [x] Edit a queued message, put back in the queue when the edit is done. What the person types for a working agent is held by Hex until the agent next says something, and a held one is taken back to the box from its menu
- [x] Resizing the side bar and folding the agent systems no longer jump (confirmed by the user)
- [x] Confirm the ghost resize line is gone from Agentic (grips of hidden components no longer light, horizon 3f8197b)
- [x] Agent systems panel at the foot of Agentic builds bottom up, what's pressed stays under the pointer, lines ease in
- [x] Model and tokens on the chat cards (8e68257)
- [x] Session and weekly limits each with a bar of its own colour (8e68257)
- [x] Harness savings table follows its window (8e68257)
- [x] File on screen is off by default in what's sent (8e68257)
- [x] Agents menu (798fc02)

## Agent systems

- [x] Claude Code's 5-hour session limit shown, every window of rate_limit_event's unifiedWindows reported (to confirm live)
- [x] Codex on the app-server protocol (d986fc4, run live: turns, rows, its weekly limit), so its limits and stats match Claude Code's and grep is refused through its
      approvals (research in chats #2 and #3 of the session that started this list)
- [x] Claude Code through its control protocol, approvals asked in Hex (claude_code 1.10.0, --permission-prompt-tool
      stdio, can_use_tool to the Allow and Deny cards, a read of the workspace by the shell refused at once, cancel takes
      the card back). To confirm live with the Permissions option on Ask first
- [x] Stop asks the agent to end its turn (run's stop, Claude Code's interrupt control request, Codex's turn/interrupt),
      killed only if it hasn't within 5 seconds. To confirm live
- [x] The model picker lists what Claude Code's initialize says (Opus 5.5, Fable 5.2 and the rest), confirmed live
- [x] hex_search finds the extensions' .hor, the walk going into a submodule unless a workspace folder goes by its
      name (a copy of something open), where it used to skip every submodule
- [ ] The mcp self test's "clicking File by its text opens the File menu" fails now and then (passes on a rerun)
- [x] Codex declined both its own shell and hex_terminal_run for a read of the workspace, and fell back to hex_files
      (live check)
- [ ] Gemini through the Antigravity provider, so a Google subscription (Google AI Pro and the like) is what it runs on.
      Find out how Antigravity is driven from outside (its CLI or agent protocol, how it signs in, what it prints) and
      what the user installs, then make it an agent system of Hex's in hex-extensions (the Gemini CLI extension there
      is the starting point, kept as an alternative)
- [x] Codex held to Hex's tools through its approvals, and hex_terminal_run itself refuses a read of the workspace (Codex went round the guard through it)

## Plan: agent commands and skills (compact first)

Done so far: compact. A run says compact (the message that does it, Claude Code's "/compact"), the Agents menu and the
palette have "Compact the agent's context", and Details puts "Compact now" beside a context bar at 95% and up. Asked
mid-turn it waits for the turn to end. "/" in the box lists the commands the run says (Claude Code's from its handshake,
its skills among them), Tab or a press puts one in. Codex compacts with thread/compact/start. Left: Codex's commands, a self
test of the button and the list with a stand-in run that says compact and commands, auto compact.

What an agent system can be told to do beyond a message (compact its context, clear, change model, run one of its
skills), said by its extension and offered by Hex in the composer, the Agents menu, the palette and Details.

1. Extension API. hex.agent's run gains `commands`, a list of { id, title, icon, about, args? } that the system always
   has (compact, clear), and `command(id, args, chat)`, which returns the line or lines for the agent's stdin (a string,
   an object or a list, as message does). The run may also say `discover(chat)`, read from what the agent tells at start,
   for the commands and skills it has of its own.
   - Claude Code: compact is a user message "/compact" (optional instructions after it) on the stream-json stdin. Its
     own slash commands and skills are listed in the `commands` of the initialize control_response (see the Claude Code
     control research chat). A skill or command is sent as a user message "/name args".
   - Codex (app-server): compact is the request `thread/compact/start { threadId }`. Skills go as a turn's input item
     { type: "skill", name, path }, listed by the app-server (look up the method in the generated schema).
2. Hex.Core. HexAgentCommand(Id, Title, Icon, About, TakesArgs) on the profile, and HexAgentChat.Commands, the ones it
   discovered, a capped list kept with the chat.
3. Running one. DawnScene.RunAgentCommand(chat, id, args). While the agent runs, the lines go on its stdin, held like a
   steer, so they can be edited until the next step. While it's idle, a turn starts with them. Each shows as a quiet note
   row, "Compacting context…", put right by the compaction event (chat.compacted) when it comes.
4. Where they're offered.
   - Composer: "/" at the start of the box opens a list of the system's commands and skills, filtered as it's typed.
     Enter runs the picked one with what follows as its args. It's a list of its own, not the editor's completion, which
     is off in the box.
   - The Agents menu and the palette: Compact context, Clear context, Run a skill… (a picker), for the chat on screen.
   - Details: a button beside the context bar, shown from 95% full (where the bar turns red), "Compact now". Greyed out
     while a compaction is under way or the system has no compact command. Drawn with the bar, put right in place, never
     rebuilt under the press.
   - The card's menu: Compact context.
5. Automatic option. A setting per system, "Compact at", off by default. Once the context passes it, Hex compacts
   between turns (never mid-turn), and says so in a note.
6. Tests. Unit tests that each extension's command gives the expected stdin lines (Claude "/compact", Codex
   thread/compact/start with its thread). A self test that the button shows only at 95% and up and that pressing it adds
   the note row and writes to the stand-in agent. A self test of "/" in the composer listing and running a command.
7. Order. The API and Claude's compact first, with the Details button and the menu entry. Then Codex's compact. Then "/"
   in the composer with discovery, and skills last.

## Questions to the person

- [x] hex_ask, an agent of the panel asks the person on a card in its chat, its options as buttons (the one it would
      pick marked), a box for an answer of their own, a countdown (300 seconds by default, 3600 at most) after which
      it's told to go on with its recommended option or the one it judges best. Counts as needing the person
- [x] Claude Code's own AskUserQuestion tool, which comes through can_use_tool, a card for each question (its
      "(Recommended)" option marked), answered with updatedInput.answers by question, nobody answering refuses the
      tool with word to go on with its best pick (claude_code extension)
- [x] Multi-select questions (AskUserQuestion's multiSelect, hex_ask's multiple) as toggles with one Answer button,
      the picks and the box's words joined with commas
- [ ] Codex's own questions (the app-server's tool/requestUserInput if it has one) on the same card

## Models and files

- [x] Full model names wherever a model is said (cards, Details, systems panel, the header), the model the agent says
      it runs over the alias it was started with (HexAgentModels, Claude Opus 5.5, GPT-5.1-Codex)
- [x] Models from the agent system itself. Claude Code is sent initialize with the first message of each run, its
      answer's models fill the picker and its commands are kept (discovered.commands, for the commands plan). Codex's
      names come from its own models_cache.json through chat.models. To confirm live, the picker after a Claude turn
- [ ] Codex's app-server model/list in place of reading its cache file
- [x] Files said in Agentic with the icon of their extension as the file tree has it, in Files it touched and after
      the name of a tool row given a file. Left, links to files in what an agent says
- [x] hex_workspace, an agent lists, adds and removes workspace folders (a project file adds its folder), told of it in
      the harness rules with hex_ask. The engine (..\horizon) is in this workspace now, so Hex's tools reach it
- [x] Details' running spinner turns (its row sat on its scroll panel's clip edge, where a moving icon is drawn still)
- [x] A restart on a folder of the workspace keeps the workspace whole (it was cut to that one folder)
- [x] hex_edit and hex_write take the folder-named paths the harness answers with in a multi-folder workspace (hex/...)
- [x] Groups of tool calls have a gutter by how they went, blue running, green, yellow when one failed but the last
      went (a warning icon), red when it ended failing
- [x] Harness saved is a toggle, Escape closes it, its button a zap in the panel's head (the composer back on the
      bottom) and a line at the foot of Agentic's Agents column
- [x] Agentic's side columns, cards and systems panel a step bigger in text
- [x] A moving icon its clip only nicks (three quarters inside or more) is drawn whole and turning, in the engine (UIDrawList.MovingSymbol), clip the turned quad instead
- [x] The harness guard lets through reads aimed outside the workspace by a relative path (git -C ..\other ls-files,
      cat ../other/x), as it did whole ones (git -C ..\horizon ls-files), it
      should only turn back reads of the workspace's own folders

## Order (the person's)

1. The other items here, Claude Code's integration first
2. Performance of the new agent system (chat paging, rows, the harness). First of it, a chat scrolled back into rows
   loaded from its file and then back down to the latest drops those rows to the file again after a minute there
3. Last of all, Hex's dev and stable builds, below

## Dev and stable builds (last)

- [ ] A dev build's icon is the icon in greyscale with a yellow H
- [ ] The installer installs a stable build and a dev build, each in a folder of its own, side by side
- [ ] Each updates its own way, stable from releases, dev from the latest nightly build (artefact) of the development
      branch, both managed by the installer

## Plan milestones (docs/agent-workspace-plan.md)

- [x] M0
- [ ] M1, left: Welcome row, Agentic themes, VirtualStack in the engine, a noon self-test suite
- [ ] M2 and on

## Known failures that aren't new

- menus self test, File lists 21 rows and Run 25, more than 20
- ux self test, narrow editor keeps file navigation on its own row
- shell self test, "Extensions is lit while its window is up" and "once it's closed the side's button is lit again"
  (first run this session, not yet checked against an older build)
- Seven older unpushed commits (bec71d5 to 96cc281) have a Co-Authored-By line against the no-AI-credit rule
