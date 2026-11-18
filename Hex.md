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
- [x] The chats of a workspace kept on disk as they go (a file a chat, written only when it moved, every 3 seconds and as
      Hex closes) and brought back when Hex starts on it, so a crash or the machine going down loses none of them.
      Named by the workspace's first folder, in agent_chats beside the settings, their older rows in its history, the
      newest 40 kept. Another workspace opened swaps its chats in once none of this one's agents is working
- The crash of 2026-11-18 broke horizon's development ref and index mid-commit, put back to the commit being made
  (e7512f7, found dangling) and the index made again

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
      Research (2026-11-17). The Antigravity CLI is agy, installed with winget install Google.AntigravityCLI, signed in
      through the system keyring or a browser Google sign-in, AI Pro and Ultra raising its limits. Headless it's
      agy -p with --output-format stream-json (NDJSON, typed init, step_update and result events, tool_info per call),
      --input-format stream-json for more turns on stdin, --model, --effort low/medium/high, --mode accept-edits or
      plan, --conversation id to resume, MCP servers in .agents/mcp_config.json of the workspace. There's also agy acp
      (Agent Client Protocol over stdio), said to run on the plan's models. Gemini CLI's own account sign-in ended
      2026-06-18, an API key only now. Waiting on the person: Antigravity's FAQ is said to call wrapping agy in another
      app unsupported on consumer plans, while a forum answer calls it fully supported, nothing official settles it.
      Build only once they've said to go ahead. The event fields to be read off a real run
        2026-11-18: the person said to go ahead and test it. Only the Antigravity IDE was on the machine (no agy, its
        bin has language_server.exe alone), they're installing the CLI and signing in themselves
        Built: hex-extensions/antigravity (agy 1.5.11 read off real runs, stream-json both ways, .agents/mcp_config.json,
        --conversation to go on), its unit tests in HexAgentSystemsTests, and Hex finds a program installed since it
        started (the system's PATH as it is now). Run live from the Agents panel: hex_symbols through the hex server,
        tool rows as mcp__hex__..., the conversation gone on with by a second message. Headless agy refuses every MCP
        call without an allow-rule, so each run allows mcp(hex/*) through --settings (never saved). A refused step
        ends in ERROR, and what it wasn't let do is said in a note
  - [x] The model in the panel when none is picked, read from agy's own cli.log ("selected model override ...
        label=Gemini 3.8 Flash (High)") since its init names it only when --model is given (asked 2026-11-18)
  - [x] Hex's tools all reach it, checked live: hex_files, hex_symbols, hex_read, hex_search, hex_skim and
        hex_terminal_run each answered right (asked 2026-11-18)
  - [x] The Gemini CLI extension removed, Antigravity runs Gemini on the Google plan (asked 2026-11-18)
- [x] The models in the picker from the agent systems themselves, not written in their extensions (asked 2026-11-18).
      Antigravity asks agy models as Hex starts (hex.program_output, a program's output off the editor's thread),
      Claude Code's initialize and Codex's model/list as they ran already, now kept in Hex's temp folder for the next
      start so the picker has them before the first run
  - The hex server's entry in .agents/mcp_config.json keeps the last chat's id in the folder, as Gemini's
    .gemini/settings.json does, ignored in Hex's repository
- [x] Images in the chat log seen inline (pictures pasted into a message and ones an agent shows or reads), and opened
      full screen with zooming and panning (asked 2026-11-18), 3a31fbd
- [x] Agentic's UX rounds (asked 2026-11-18): two gpt-6-luna reviewers score it, the changes made, three rounds, aiming
      for 8/10. Round 1 scored 7 and 6 (parity with Cursor 5 and 3.5): an Inbox, change review with checkpoints, a
      plan, keys for approvals, a collapsible fleet with a filter, pending decisions on top of Details, ANSI in errors.
      Round 1 built: tabs over Agentic's middle (Chat, Inbox, Changes, Plan, Alt+1 to Alt+4), the Inbox of every agent's
      requests, questions and finished chats, Changes per turn against a checkpoint taken as the turn began
      (HexCheckpoints, a git tree through a copy of the index) with Diff and Restore, the Plan from the agent's to-do
      list, Ctrl+Alt+Y and Ctrl+Alt+N for the request waiting with the keys on its buttons, Waiting on you at the top
      of Details, the fleet's filter, Ctrl+Alt+B and a narrow window putting the fleet away, its empty state's actions,
      terminal codes taken out of notes and errors
      Round 2 scored 7 and 6.5 (parity with Cursor 6 and 4). Round 2 built: the agent launched only once its turn's
      checkpoint is taken, a checkpoint of each repository in a folder of several, Details and the tabs not made again
      under a press (a single click on "All n messages" was lost) nor allocating while idle, only the request the keys
      answer saying them, empty tabs saying what to do next with Plan asking the agent for one, a wheel scroll no longer
      snapping up in a chat just opened, an agent's Escape through hex_key no longer stopping the selected chat.
      Round 3 scored 8 and 7.5 (parity with Cursor 7 and 6), 162713e: Restore naming files changed again since the
      turn and asking again if one changes while it waits, a filter fitting no chat showing them all at a press.
      Left for later, as the reviewers ranked them: inline hunk review with accept and reject, worktrees so agents
      working at once don't share one checkout, @ files and symbols and a model picker in the composer, shortcuts
      on the tabs, family members' ids opening their chats, quieter tool rows
- [x] Leftovers of the UX rounds, the small ones (asked 2026-11-18): the tabs' keys dim at their right (b854089), an
      agent's number on a family message opening its chat (a62ce9c), calls that went drawn quieter (5db96a4)
- [x] The family's conversation and what the harness saved as windows of their own design (asked 2026-11-18, the
      reviewers had left them out): a summary over chips that filter with their counts, messages as cards with a
      stripe and badge in the agent system's colour and who to whom along their top, tools and agent systems as cards
      with bars of what they saved, nothing cut off by the window's bottom, an empty state with the way back, Details
      filtering with the same chips (db9ea80, 89de584)
- [x] An @ in the agents' box lists the workspace's files and declarations that fit what's typed after it, from the
      harness's index, a press or Tab putting the path in (afbd9ba, fe29e51)
- [x] A thin bar under each chat's card of how full its context is, green turning amber and red as it fills, Details'
      bar in the same colours (asked 2026-11-18, 4d94c90)
- [x] The model the next turn runs on as a picker of its own beside the thinking in the agents' box (f103dd1)
- [x] Review in Changes, a file's hunks against the turn's checkpoint as cards under its row, each kept or undone on
      its own with the rest of the file left as it is (c7a7efd)
- [x] Every turn checkpointed however it began, a message sent while the agent works and a chat carried on after a
      restart as well, the checkpoints kept with the chat across restarts, and a turn's title fitted to its card
      (asked 2026-11-18, 7fd5606, 8ec705d)
- [x] What the harness saved drawn the way Horizon's performance overlay draws a frame (asked 2026-11-18): a large
      figure and tiles with meters, a bar cut into each tool's part of the saving, a table of numbers in columns with
      a meter a row, the agent systems the same (a3a9957, 8ec705d)
- [x] Hex 3.1.0 Amphetamine (asked 2026-11-18), the welcome page saying what Agentic brought (6874358)
- [x] Pasted pictures as small ones of themselves in the box opening full screen, the full screen view no longer leaving
      its small one white, room on the chat cards for their context bar (asked 2026-11-18, 08b5b61), the family window's
      chips pressable again and it and the savings window solid (088d48f)
- [ ] A review by gpt-6.2-sol (UX, 7/10) and claude-sonnet-5-5 (code) on 2026-11-18. Fixed: restore never deleting what
      the checkpoint couldn't have had, checkpoints pinned by refs and given longer, hunk undo keeping encoding and
      position and not writing over a newer write, a failed checkpoint no longer holding its agent (65d42c0), held
      messages, waiting questions, unreadable chat files and chats past the cap (b0fbe5b), totals, the fleet under a
      press, texts and number formats (d1ff128), chats written off the editor's thread (d0a8848), Review first, the
      family's one section, savings and model labels (e5a1091). Left: what a turn changed from its successful writes
      rather than tool names, review in the editor's diff with next and previous, search over past chats' text, @ on
      its own and arrow keys in its list, the Inbox's badge telling decisions from unread, a plan to approve before
      it runs, mail marked read only once its agent starts, a stable id for approvals, a limit on agents starting
      each other by mail, earlier rows read page by page, PATH looked through off the editor's thread
- [x] Worktrees per agent left out, the person's call (2026-11-18): agents work together on the one project in the
      workspace, checkpoints, review and restore being how an agent's changes are undone
- [x] The agent that starts another closes its chat or clears it to start afresh, hex_agent_close (asked 2026-11-18)
- [x] Each agent system's badge in its own colour as its extension says it (hex.agent's colour and colour_light), Claude
      Code's coral, Codex's white or black, Antigravity's Google blue (asked 2026-11-18)
- [x] The agent that starts others decides what they may do (asked 2026-11-18). hex_agent_spawn's permissions, allow
      and deny rules in the child system's own words (Claude Code's --allowedTools and --disallowedTools, Antigravity's
      --settings), and what a child asks leave to do goes to its parent as a question it answers with hex_agent_approve,
      the person's card still there, approvals "person" leaving them to the person. Codex has a sandbox, not rules.
      Checked live: an Antigravity agent spawned with command(git log -1 --oneline) ran it, without the rule it's
      refused. agy 1.5 matches a command rule against the whole line only (command(*) for any, no prefixes)
- [x] A chat whose run was still going after its turn was said over (Claude Code woken by a background task of its
      own) had a second run started on the same session by the next message, two agents writing the same files. A
      message then waits in its inbox and starts the next run once that one has gone
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
- [x] Codex asked model/list with its handshake, its answer over the cache file for the picker and the names
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
- [x] Tool groups and a call's details open and fold in place (no row made again), so the chat no longer jumps up and
      back, and the last group of a chat scrolls its lines into sight. At the chat's end too, the scroll not sticking
      to its end while the row is held (it pushed the offset down and the hold pulled it back)
- [x] The agent systems panels ease their height both ways (a clipping holder), the column above them following, in
      Agentic and in Dawn, the Dawn list no longer making its cards again as the height moves. To confirm live
- [x] Markdown files open in the preview, the Code view a press away
- [x] A moving icon its clip only nicks (three quarters inside or more) is drawn whole and turning, in the engine (UIDrawList.MovingSymbol), clip the turned quad instead
- [x] The harness guard lets through reads aimed outside the workspace by a relative path (git -C ..\other ls-files,
      cat ../other/x), as it did whole ones (git -C ..\horizon ls-files), it
      should only turn back reads of the workspace's own folders

## Order (the person's)

1. The other items here, Claude Code's integration first
2. Performance of the new agent system (chat paging, rows, the harness). Done: rows read back go to the file again
   after a minute at the latest, Details no longer made again every second for its clock (a line put right in place),
   a call's file worked out once, the hub's rows kept for chats that didn't move. The agentic-perf self test measures
   it (docs/benchmarks.md, dev and NativeAOT release). Since: switching 259 KB a turn down to 12 (rows made as they
   come into sight), idle 13 KB an update down to 0.4 (keys as numbers, words written only when they moved, weighed
   step by step with HexTicks.Weighing). Next: the release build's working set is 400 MiB against the dev build's
   240 MiB
3. Last of all, Hex's dev and stable builds, below

## Dev and stable builds (last)

- [ ] A dev build's icon is the icon in greyscale with a yellow H
- [ ] The installer installs a stable build and a dev build, each in a folder of its own, side by side
- [ ] Each updates its own way, stable from releases, dev from the latest nightly build (artefact) of the development
      branch, both managed by the installer

## Plan milestones (docs/agent-workspace-plan.md)

- [x] M0
- M1, item by item
  - [x] Agentic themes, solstice_noon and solstice_day_noon, a teal accent while Agentic is up, Dawn's back as it's
        left, a look without a Noon theme as it was (5342a6a)
  - [x] Welcome's Agents row, New Agent, Continue "<last chat>", how many want looking at (1aa4204)
  - [x] VirtualStack in the engine with its tests (horizon 1901c2a). Not yet under the chat, the Agents view still
        pages its rows by hand (200 in memory)
  - [x] The noon self test suite, the title bar's button and the key, the themes, the side bar's width after, the
        welcome row (1aa4204)
  - [x] The chat's rows in a VirtualStack (1.6), only the rows near what's in sight in the tree, a row's height kept by
        its entry so a chat gone back to lands on the row it was read to (HexAgentRowList, horizon 3d2aed6, 8360b4a).
        A chat put on screen makes its rows as they come into sight, switching 259 KB a turn down to 45 (this commit,
        docs/benchmarks.md)
  - [x] The family's whole conversation in a window from Details ("All n messages…"), a VirtualStack over the log
        with kind and member filters, following new messages, and the noon-mail self test of it with
        hex_agent_message between two chats of a family (1.7, this commit)
  - [x] The horizon in Agentic, running while an agent works, red when one fails (17.3, ab90d37)
  - [x] --face on the command line, the last face kept with the workspace's chats (1.9, 476fdde)
  - [x] "Open in Agentic" on a chat's menu in the side bar (1.10, there already)
  - [x] Suites noon-keys and noon-looks (df0356e, which also has Alt+Page Up, Page Down and Home heard while the box
        has the keyboard, they never were in Agentic), noon-fleet for what the fleet has now (family blocks, totals,
        closing a family)
  - [x] Suites noon-panel (f252f57) and dusk-noon (this commit). A self test that names dusk-noon has Dusk in its
        window and goes on stepping while Dusk is on screen, so it's only run when named
  - [x] noon-fleet's stand-in agent system (ping), two started by a chat through hex_agent_spawn, under it in the
        fleet and stopped with it (this commit). hex_agent_brief is M6's, not M1's
  - [x] The agent systems' --version probe has its stdin closed, a program that read it (cmd) sat there and outlived
        a Hex that closed within its 8 seconds (this commit)
- [x] M1 (the face, themes, fleet, chat and family mail on VirtualStack, context, horizon, Welcome, keys, --face and
      the last face, the panel link, and the suites noon, noon-fleet, noon-keys, noon-looks, noon-mail, noon-panel,
      dusk-noon). Not done of the plan's M1 text: the 1.0 screenshot pass and the accept's shots in all seven looks
      at 100/150/200% reviewed by eye (noon-looks takes them with HEX_SELFTEST_SHOTS)
  - [x] The fleet's key a number (AgentCardsKey) rather than a string every quarter second (plan 12.5)
  - The settings self test's "tab_size 3 on Workspace is written to the folder's file alone" fails on 0829def too,
    before any of this
- [ ] M2 and on

## Known failures that aren't new

- menus self test, File lists 21 rows and Run 25, more than 20
- ux self test, narrow editor keeps file navigation on its own row
- shell self test, "Extensions is lit while its window is up" and "once it's closed the side's button is lit again"
  (first run this session, not yet checked against an older build)
- Seven older unpushed commits (bec71d5 to 96cc281) have a Co-Authored-By line against the no-AI-credit rule
