# Hex to-do

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
- [ ] A "Send now" button on a queued message, writing it to the agent at once rather than when it next says something
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
- [ ] Codex on the app-server protocol, so its limits and stats match Claude Code's and grep is refused through its
      approvals (research in chats #2 and #3 of the session that started this list)
- [ ] Claude Code through --permission-prompt-tool or its control protocol, approvals asked in Hex
- [ ] Gemini through the Antigravity provider, so a Google subscription (Google AI Pro and the like) is what it runs on.
      Find out how Antigravity is driven from outside (its CLI or agent protocol, how it signs in, what it prints) and
      what the user installs, then make it an agent system of Hex's in hex-extensions (the Gemini CLI extension there
      is the starting point, kept as an alternative)
- [ ] Codex can't be held to Hex's tools by a hook, only by its instructions, until it's on the app-server

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
