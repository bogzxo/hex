# Hex next to the other small editors, and what it takes to stand there

Two rounds in (`ide-plan.md` made Hex look like VS Code, `hidl-revamp.md` made it about values rather than text), Hex
does things no other small editor does and misses things every one of them has had for fifteen years. This is the
third plan. It was written after going through Hex as it is (the menus, the commands, the code editor, the designer,
the extensions, the screenshots of it on Fighter2D's files at 1600 by 900) and what the editors it gets compared to
offer, and it's held to one rule bogz set, no bloat. Everything in here is either something a HIDL file or a game made
with Horizon actually needs, or something whose absence makes Hex feel broken to anybody who has used any editor at
all. What doesn't pass that is written down at the end as not doing, so it doesn't come back every round.

## How far it's got

Rounds one to seven are in, round eight stopped after its first part, and the git round was dropped (bogz commits
from elsewhere, it isn't wanted). Files changed on the disk are noticed and reloaded or asked about, unsaved text is
backed up and brought back after a crash, the panel folds away when it has nothing to say. The find bar is a layout of
Hex's own (find_bar.hor) over the code rather than an engine component, with case, whole word and regex, replace and
replace all, and find in files has the same options. Back and forward are Alt+Left and Right and the mouse's side
buttons, the quiet mode is F11 (Ctrl+K Z would have typed a z), wrapping went into round four with folding. The bracket
pair was already there, the plan was wrong about it. The colouring and the check moved off the editor's thread in
round three, the numbers are in performance.md, the quads aren't copied yet. Multiple carets, folding, sticky scroll,
wrapping and signature help are the engine's (CodeEditor, CodeEditor.Rows, UITextDocument), two files side by side is
Ctrl+\, inline values, the likely fix in an error and the runner (Ctrl+F5) are round six, and extensions got their
menus, questions, prompts, keys, on_change and reloading by themselves in round seven. Keys can be put elsewhere
(Help, Change a key, keys.hor). The designer's languages, screens and states side by side were not done, bogz called
round eight off there. Hex also got a picture behind the code (the settings' Backdrop page), which no plan asked for.
After that bogz asked for the shell to be taken apart, `editor.hor` was one file of six hundred lines, and it is now
a skeleton that includes `Assets/hex/shell/` (the activity bar, the side bar, the tabs, the panel, the properties)
through `compositor.include`, an engine feature any layout can use. The menus and `en-gb.hor` were rewritten in plain
IDE English ("Find in Files", "Split Editor", "Keyboard Shortcuts"), the keys list became a window with a filter, the
arrows and enter to rebind, and the command palette and every chooser take the arrows while the box has the keyboard.

## The short of it

Hex is already ahead where it matters to it. The values of a file live on the right, the table, the console, the
designer drawing the real thing with the engine's own code, extensions in the language Hex edits, a move editor no
other tool has, and it's cheap, 0.3% of a core sat still. Where it's behind is the editing itself and trusting it with
your files. There's one caret, find is a dialog, nothing notices a file changing on the disk, nothing folds, long lines
run off the edge, there's no going back to where you were. Those are the things that make somebody close it and open
VS Code, and none of them is big. So the plan is mostly the core of a text editor done properly, on top of the
incremental lexing the performance plan already wants, then Hex's own edge (running things) pushed further, and very
little else.

## Who it was measured against

Not VS Code, that's the shape Hex borrowed and it's 300 MB of everything. The small ones, and the ones built into a
game engine, since that's what Hex is.

| editor | what it is | what it's known for | what Hex takes |
| --- | --- | --- | --- |
| [Lite XL](https://github.com/lite-xl/lite-xl) | a few MB of Lua and SDL, everything a plugin in its own language | multiple carets, find with regex, project search, a fuzzy command palette, split view, tiny and fast | the proof that an editor can be small and still have all of the core. Its plugins are Lua the way Hex's extensions are HIDL |
| [Sublime Text](https://www.sublimetext.com/) | the small fast commercial one | Goto Anything with prefixes, multiple selections ("ten changes at the same time, not one change ten times"), split editing, distraction free | multiple carets, two editors side by side, a quiet mode. Hex has Goto Anything's prefixes already |
| [Zed](https://zed.dev/) | the GPU drawn one, its own UI framework like Horizon's | speed as the feature, a whole window drawn like a game frame, multibuffers | the budget. It's the same bet Hex made (the UI is the engine's), and it says out loud that it's fast. Hex should be able to say the same with numbers |
| [Godot's script editor](https://docs.godotengine.org/en/stable/tutorials/editor/script_editor.html) | the editor built into a game engine | folding, multiple carets (Alt+click), bookmarks, breakpoints, the class reference offline, find and replace across the project | folding and carets, and that an engine's editor is judged on whether you ever want to leave it |
| [Defold](https://defold.com/manuals/hot-reload) | another engine's editor, Lua | hot reload, change a script or a resource and the running game takes it | Fight! and the game's ContentWatcher are this already. Saying so, and making it one key |
| [ZeroBrane Studio](https://github.com/pkulchenko/ZeroBraneStudio) | a small Lua IDE | live coding against a running game (LÖVE and others), a console, folding, outline, fuzzy search | live coding is Hex's values and console. The runner of `hidl-revamp.md` is the next bit of it |
| [Thonny](https://thonny.org/) | the beginner's Python IDE | a variables view, stepping through how an expression is worked out, an assistant that explains an error, a sparse UI on purpose | errors that say what to do, and that sparse is a feature. Hex's values view is Thonny's variables view for a whole file |
| [Light Table](http://lighttable.com/) | the inline evaluation one | results next to the code that made them, watches | inline values, already planned, and it's the cheapest big win left |
| [TIC-80](https://tic80.com/) | a fantasy console with its editors in it | code, sprites, maps and sound without leaving it | the way Hex's views of a file (design, code, table, moves) should feel, one tool and not a tool per file |

What they all have, the small ones included, that Hex hasn't. More than one caret. A find bar in the editor that
highlights as you type, with case, whole word and regex. Folding. Noticing that a file changed under them. Going back
to where you were. Two files side by side. Long lines wrapped when asked. Most of them a quiet mode. That's the list,
and it's not long.

## What Hex has that they haven't, and keeps

So nobody trades it away for a feature off somebody else's list.

- The value of a file next to it, run off the editor's thread as it's typed, a click going to where it was written.
- Editing a value as a value (the values tree, the table, the move editor) with the file's comments and spacing kept.
- The designer drawing a layout with the engine that will draw it in the game, every skin and theme.
- The language's own help, siblings and paths as warnings, the texts of the game in the code.
- Extensions written in the language being edited, with a UI made in Hex.
- A self test that clicks through all of it, 339 checks, and garbage measured as a check.

## What's missing, worst first

### 1. A file changes on the disk and Hex doesn't notice

The explorer's watcher only hears about files made and deleted. A file that's open and is changed by something else
(the game writing its options, `git checkout`, Syncthing bringing the other machine's edit over, which on bogz's three
machines happens all day) keeps what Hex read, and saving it puts the old text back over the new. That's losing work
without anybody seeing it go, the worst thing an editor can do.

What to do. Watch the files that are open for writes as well (a watcher a folder that has an open file, the same
queue the explorer's uses, nothing polled). A file with no changes in Hex is read again quietly, the caret and the
scroll kept, one step of its undo so it can be taken back. One with changes in Hex gets a card, "changed on the disk
too", with keep mine, take theirs and compare. Compare is the two texts in two editors side by side (see 6), nothing
cleverer. A save checks the time on the disk against what was read and asks before writing over a newer file. Small,
and first, because everything else is worth less while this can happen.

### 2. Find is a dialog

Ctrl+F opens a dialog with a text box, enter finds the next one, and the dialog's gone. Nothing is lit, there's no
count while you type, no case, no whole word, no regex, and replace is two dialogs in a row ending in replace all,
with no replacing one at a time. Everybody's find is a bar.

What to do. A find bar laid over the top right of the code editor, VS Code's and Lite XL's. Every match lit as it's
typed (the editor lights every place the word under the caret is already, it's the same drawing), "3 of 17", enter
and shift enter, three toggles (case, whole word, regex, `Aa`, `ab`, `.*` the way everybody draws them), a second row
for replace with replace and replace all, escape closes it and leaves the caret on the match. Find in files gets the
same three toggles. In the engine, it's a part of `CodeEditor` any program gets, Hex only binds the keys. Regex is
.NET's, run on the lines and not the joined text.

### 3. One caret

Ctrl+D selects the next place the word is and edits both, Alt+click puts a caret down, Alt+Shift and a drag or the
arrows makes a column of them, Ctrl+Shift+L a caret at the end of every line selected. In data like Fighter2D's,
forty moves with the same keys, this is how a key gets renamed in twelve of them or a number changed down a column,
and it's the first thing anybody coming from Sublime, Lite XL, Godot or VS Code reaches for. Ctrl+D duplicates the
line now, which moves to Ctrl+Shift+D (VS Code has it on Shift+Alt+Down, either is fine as long as it's in the keys).

What to do. `UITextDocument` keeps a list of carets with their selections instead of one, every edit applied to all of
them from the last to the first so the earlier ones don't move under it, one step to undo for the lot. The code
editor draws them all. It's the biggest change to the editing in this plan and it wants the incremental lexing first
(see the performance part), or ten carets typing is ten whole file highlights a keystroke.

### 4. Nothing folds, and in a big nested file you lose where you are

A move list is a few hundred lines of objects inside objects. Scroll into the middle of the uppercut's phases and
nothing on screen says it's the uppercut. The breadcrumbs say it, in small grey writing at the top, and that's all.

What to do, two things that go together. Folding by the brackets, a chevron in the gutter of a line that opens a `{`
or `[` that closes further down, Ctrl+Shift+[ and ], fold all to a level (Ctrl+K Ctrl+1 to 3 in VS Code, a menu here),
and a folded object showing what's in it on its one line (`jab: { 6 keys }`, the way the values tree does). And
sticky scroll (VS Code's, Zed's), the lines that opened the objects the top of the view is inside of held at the top
of the editor as you scroll, `moves: {` then `uppercut: {` then `phases: {`, a click going to that line. For HIDL's
shape of file sticky scroll does more than a minimap ever would, and it costs a few lines of drawing. Both come out of
the bracket depth at the start of every line, which the incremental lexer keeps anyway.

### 5. No going back

F12 goes to where something is declared, a click in the values goes to where a value was written (often another
file), a problem goes to its line, and there's no way back to where you were. Every editor has Alt+Left.

What to do. A history of places, a place pushed whenever the caret jumps further than a screen or into another file,
Alt+Left and Alt+Right, the mouse's back and forward buttons. A few dozen places, forgotten for a file that's closed.
Small.

### 6. One file at a time

The moves and the characters, a layout's code and the texts it names, the shared file and the fighter's own. Hex shows
one, and Ctrl+Tab between them.

What to do. The middle splits in two side by side, each half its own tabs (VS Code's editor groups, two at the most,
no grid, no vertical split until somebody asks). Ctrl+\ splits with the file on screen, dragging a tab to the other
half moves it. The designer stays one, a layout's design next to its code is the one split worth having for it and
the code view already follows the designer. It needs the panel layout the `SplitPanel` of `hidl-revamp.md` was for,
which is worth building anyway, every edge of Hex's window has had a bug where two of them meet.

### 7. Long lines run off the edge

Fighter2D's files have comments a hundred and fifty wide (characters.hor's header), and at 1600 by 900 with the values
open they're cut off at the right with nothing saying there's more. There's no wrapping.

What to do. Alt+Z wraps lines at the editor's edge, as a setting and a toggle, indented the way the line is, off by
default for code and on for a file of texts. The caret's line and column stay the file's. Cheap once the drawing knows
a line can take more than one row, which folding needs as well.

### 8. A crash or a power cut takes what wasn't saved

Hex asks before closing with changes, and that's it. VS Code and Sublime keep unsaved text across a restart (hot exit).

What to do. Every file with changes is written to a backup under LocalAppData a couple of seconds after typing stops,
off the editor's thread, removed when it's saved or closed without saving. Hex starting with backups there opens them,
said on a card. No auto save to the real file, Syncthing and the game watching the files make that a bad idea.

### 9. The small things everybody has

Each a few lines and each noticed when it's missing.

- The bracket matching the one by the caret lit (Hex colours brackets by depth, it doesn't show the pair).
- Signature help, in a call, the function's parameters with the one being typed in bold, for the library and the
  file's own functions. Godot and every IDE have it, the hint machinery is there.
- Ctrl+K Z, a quiet mode, the side bar, the panel, the right side and the tabs away, the code in the middle. Sublime's
  distraction free. It's the toggles Hex has, pressed together and undone together.
- The panel gets out of the way. It's 120 high whatever is in it, and on a file with no problems that's "nothing wrong
  with this file" taking a seventh of the window. It shrinks to its tabs when the page on it is empty and comes back
  when something's there, the way the status bar already says the count.
- F2 renaming across the workspace, a key or a let of an included file renamed in every file that names it, as one
  step to undo in each (it's the file on screen only now). The source map knows where every name was written.
- Middle click closes a tab, the one thing of the first plan's tabs still missing.
- A focus line, the part with the keyboard marked (planned in `hidl-revamp.md`, still not done). With a code editor, a
  console, a find bar and the values all taking keys it matters more now.
- Format the file, once the language keeps comments (HIDLWriter), not before. Already the plan.

### 10. Hex's own edge, pushed

What the other small editors haven't got and Hex nearly has, from Light Table, ZeroBrane and Thonny. Already in
`hidl-revamp.md` and carried over because they're where Hex is better than anything, not to catch up.

- Inline values, the value of a let that was worked out written faintly at the end of its line.
- The runner for behaviours, a function called tick after tick with a view that can be changed between them.
- Errors that say what to do (Thonny's assistant, cut to one line). A run that fails says where and why already, it
  should say the likely fix when it knows one ("moves.jab has no phases, every move needs one").
- Hot reload made one key. Fight! starts the game with `--content` and the game reloads what Hex saves, which is
  Defold's hot reload. Ctrl+S already does it, so it's a line in the status bar while a game started from Hex is running
  ("the fight reloads on save") and nothing more.

### 11. Extensions, what they're missing

The move editor's UX plan asked for these and so would any extension with a UI. `hex.menu(items, at)` for a context
menu, `hex.ask` and `hex.prompt` (or a window an extension can close), `hex.command(name, fn, key)` with the key shown in
the palette, keys other than the arrows reaching a view (letters, digits, with shift), `on_change(file)` as it's typed,
and reloading an extension without restarting Hex, its folder watched (Lite XL reloads a plugin in place, an extension
author restarting Hex on every change is the slow loop of all of this). Each is small, each gets a self test check.

### 12. Settings and keys

The keys are written in the menus' code. A `keys.hor` next to `hex_settings.hor`, a command by its name and a key
(`"Find in files": "Ctrl+Shift+F"`), read at start and written when changed from the keys list (Help, The keys, a
key pressed on a row), is all rebinding needs to be. The command palette and the menus show whatever it says. Nothing
else in the settings changes.

### 13. Git, a little

The branch in the status bar (read out of `.git/HEAD`, planned in the first round and never done) and a mark in the
gutter of a line changed since the last commit (Lite XL's gitdiff plugin, Zed, Sublime), `git diff` run off the
editor's thread when a file is saved or opened. That's it. No staging, no commits, no history, bogz commits from
elsewhere and Hex isn't going to do it better.

## The designer

It's in better shape than the code, it has what Godot's and Qt Designer's have that matters (the tree, the inspector
in sections, alignment, snapping, layers, the skins, screens, entrances) and draws the real thing, which neither does.
What's left is about seeing a layout the ways a game will.

- Every language of the game at once, the layout drawn in each side by side (or stepping through them with a key),
  since German is what breaks a menu. The texts are loaded already.
- The screens of the Screen menu side by side the same way, 1280 by 720, 1920 by 1080, a phone's.
- A component's states (hovered, pressed, focused, disabled) shown on request, the way Godot's theme editor shows
  every state of its controls.

All three are the stage drawn more than once, which costs a frame each when asked for and nothing when not.

## Staying fast, staying small

The numbers to keep, every one already measured. Idle at 0.3% of a core and nothing allocated a tick. A keystroke in a
2200 line file under a millisecond on the editor's thread (it's 1.7 for the colours and 2.5 for the check now). The
garbage of sweeping the pointer over the designer and the code under 256 bytes a tick, as the self test checks.

What that needs, from `performance.md`, before 3, 4 and 7 rather than after them. The colours worked out line by line
with the lexer's state at the start of each line kept, only the lines from a change on lexed again, the names laid over
from the worker thread a frame late at worst. The syntax check being the parse the values run already does, so one
parse a change and none on the editor's thread. The code editor's quads copied from the last tick when nothing it
paints changed, which is most ticks with a caret blinking. With those, multiple carets, folding, sticky scroll and
wrapping are drawing over state the lexer keeps anyway.

Every item lands with a check in the self test and, where it touches a keystroke or a tick, its cost measured with
`HEX_PROFILE` on the 2200 line file before and after, written into `performance.md`. A feature that puts idle above
0.3% or a keystroke above a millisecond doesn't go in until it doesn't.

And understandable. Every new key is in Help, The keys and on the command in the palette. Every toggle is in a menu as
well as on a key. Nothing new is a page of settings. When there's a way that VS Code does it, Hex does it that way,
the keys especially, because the people who'll pick Hex up know VS Code's.

## Not doing

Written down so it stays down.

- A language server, a debugger with breakpoints, a terminal. HIDL runs a few milliseconds at a time, the values, the
  console and the runner are what those would be used for, and a shell is a window away.
- A minimap. The earlier plans had it. Sticky scroll and the outline say where you are in a HIDL file better, and a
  minimap is the most quads of anything in an editor for the least use. If folding and sticky scroll are in and it's
  still missed, it can come back.
- Git beyond the branch and the gutter marks. A store or a manager for extensions, a folder is enough. Settings sync,
  Syncthing does that. Remote editing, more than one window, more than two editors side by side, notebooks, an AI
  assistant, themes beyond the flat skin's dark and light.
- Auto save to the file itself (see 8).
- Anything for a language other than HIDL beyond colours. Hex opens a .cs or a .json as text and that's right.

## In what order

1. **Trusting it.** Files changed on the disk (1), backups of unsaved text (8), the panel getting out of the way.
   Small, and nothing else is worth much while work can be lost.
2. **The everyday.** The find bar (2), going back and forward (5), the bracket pair, wrapping (7), the quiet mode,
   the focus line, middle click on a tab, renaming across the workspace. A week of small things that make it stop feeling unfinished.
3. **What it stands on.** The line by line lexing, the one parse, the copied quads, measured before and after. No
   visible feature, the next round needs it.
4. **The editing.** Multiple carets (3), folding and sticky scroll (4), signature help.
5. **Side by side.** The split panel, two editors (6), compare for a file changed on the disk.
6. **Hex's edge.** Inline values, the runner, errors with the likely fix, the fight reloading said (10).
7. **Extensions.** Menus, questions, keys and commands, `on_change`, reloading without a restart (11). The move
   editor's steps 3, 6 and 7 wait on these.
8. **The rest.** Rebinding keys (12), the branch and the gutter marks (13), the designer's languages, screens and
   states side by side.

The first three are the ones that matter. After them Hex has the core every small editor has and loses nobody's work,
and the rest is choosing what to make better rather than what's missing.

## Where this leaves the other plans

`ide-plan.md`'s still to come is in here or in not doing (rename across the workspace and back and forward in, the
minimap out, formatting waiting on the language). `hidl-revamp.md`'s open items are in 9 and 10. `performance.md`'s
list is round 3. `move-editor-ux.md` waits on round 7 for its steps 3, 6 and 7.
