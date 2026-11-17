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
- [ ] Harness answers kept across edits to other files: they're keyed by the index generation now, which every edit
      moves, so repeats rarely count as cache hits
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
- [ ] Stop for Claude Code could send {"type":"control_request","request_id":"..","request":{"subtype":"interrupt"}}
      rather than killing it, so its session is left whole. Research is in the chat "Claude Code control research"
- [ ] hex_search finds nothing in files the index leaves out (extension .hor files), so an agent held to the harness
      can't search them; search them from the disk as hex_read reads them
- [ ] The mcp self test's "clicking File by its text opens the File menu" fails now and then (passes on a rerun)
- [x] Codex declined both its own shell and hex_terminal_run for a read of the workspace, and fell back to hex_files
      (live check)
- [ ] Gemini through the Antigravity provider, so a Google subscription (Google AI Pro and the like) is what it runs on.
      Find out how Antigravity is driven from outside (its CLI or agent protocol, how it signs in, what it prints) and
      what the user installs, then make it an agent system of Hex's in hex-extensions (the Gemini CLI extension there
      is the starting point, kept as an alternative)
- [x] Codex held to Hex's tools through its approvals, and hex_terminal_run itself refuses a read of the workspace (Codex went round the guard through it)

## Plan: agent commands and skills (compact first)

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
