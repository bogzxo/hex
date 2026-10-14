# Hex for HIDL, the plan after VS Code

The first round made Hex look and behave like VS Code, and that was the point, a shell everybody already knows how to
drive. This is the plan for the next thing, an editor shaped round what HIDL files actually are. VS Code treats every
file as lines of text with colours on. A .hor file is a program that comes to a value, and nearly all of them are
written to come to a value somebody else reads (a game, the engine, Hex itself). That is the difference this plan is
built on.

## What HIDL is used for

Going through every .hor in the repos round the engine, there are six kinds of file, and they want different things.

1. **Layouts.** Fighter2D's menus, Hex's own window, the engine's performance card. Built by calling the compositor,
   `#define layout` at the top. They have the designer already.
2. **Texts.** `let strings = { ... }`, a file a language (en-gb.hor, Fighter2D's localisation). A tree of keys with a
   text at every leaf, the same keys in every language, named from layouts as `@main_menu.quit`.
3. **Skins and sprite sheets.** `skin.hor` (colours, fonts, spacing, syntax colours, themes) and `sprites.hor` (rects
   into Aseprite files, nine slices, inheritance from another sheet).
4. **Game data.** Fighter2D's characters, move lists, maps and hitboxes. Big nested objects, the same shape over and
   over (every move has stances, phases and a cancel window), files that name other files by path
   (`"Assets/data/mannequin_moves.hor"`), and later files that replace what earlier ones said by name. A move list
   runs to hundreds of lines and its header comment is the only documentation of its shape.
5. **Behaviour scripts.** `dummy_behaviours.hor`, functions the game calls once a tick with a view of the fight,
   handing back the buttons to hold. Real code with state between calls.
6. **Settings.** `hex_settings.hor`, a game's options, flat keys and values.

And a seventh as of this round, extensions, HIDL that drives Hex.

The common thread is that the value matters more than the text. A move list is right when the moves it comes to are
right, a skin is right when it looks right, a file of texts is right when every language has every key. Hex should show
the value, check the value, and let it be edited as a value without ever losing the comments round it.

## What it should feel like

Open a move list and the right side shows the moves, as a tree of what the file evaluates to, live. Click a phase and
the caret goes to it. Drag a number in the tree and the number in the code changes, nothing else in the file does.
Type `cancel: ` inside a phase and the completion offers what the other phases have for cancel. Misspell `stances` as
`stance` in one move out of forty and it is underlined, every sibling says `stances`. A path in a string is a link.

Open a file of texts and it is a table, keys down the side, a column for every language in the folder, the empty cells
red, every text drawn the way a label would draw it (markup, icons and all). Open a skin and there is a sheet of every
component drawn in it next to the code, changing as the colours are typed. Open a behaviour and there is a console
underneath to call it from, with a made up view of the fight to hand it.

## The window

The shell stays VS Code's, the parts of it change job.

```
 [=] File Edit Selection View Go Run Help              moves.hor - Hex (greeting)               _ [] x
 +--+--------------+---------------------------------------------------+------------------+
 |EX| OUTLINE      | moves.hor *  | characters.hor |      [Code|Values]|> | VALUES           |
 |SR|  {} moves    | Fighter2D > Assets > data > moves.hor > jab       | v {} moves  41   |
 |OU|   {} idle    |                                                   |   v {} jab       |
 |EXT   {} jab     |   the editor                                      |     # damage  8  |
 |  |   ...        |                                                   |     > [] phases 3|
 |  |              +---------------------------------------------------+   > {} run       |
 |@ |              | PROBLEMS | OUTPUT | HIDL                          |                  |
 +--+--------------+  > moves.jab.phases[0].cancel                    +------------------+
 data, 41 moves, evaluates in 0.6 ms     Ln 120, Col 8   Spaces: 4   LF   HIDL data
```

- **The right side is whatever the file is about.** Properties for the designer (as now), Values for code (the tree of
  what the file comes to), a preview for texts and skins. What it says about the file itself (the path, the line
  endings, whether it parses) moves to where VS Code has it, the status bar and the problems.
- **The bottom panel gets a third tab, HIDL.** A console that evaluates whatever is typed in the scope of the file on
  screen, the way a browser's console runs in the page. `moves.jab.phases` and see it, `len(keys(moves))`, call a
  behaviour with `wander({ tick: 3, distance: 400 })`. Up and Down go through what was typed before.
- **The toolbar thins out.** The name box was for layouts when a layout was all Hex opened, renaming is F2 on the tab
  now. Design and Code become the editor's own actions in the corner of the tab strip, with Run next to them, the way
  VS Code puts a file's actions there. The row under the menus goes, the title bar has room for the menus already.
- **The activity bar gets Extensions,** the list of what is loaded, each switched on and off, and Settings goes to the
  bottom of the bar where VS Code keeps its gear.
- **The outline says what everything is.** An object, a list, a function, a text, a number, a vector, a colour, by its
  icon, and how many things are in it.
- **Quick open learns VS Code's prefixes.** `>` for commands, `@` for something in the file, `:` for a line, and a key
  of the texts with `#`, so Ctrl+P is the one box for going anywhere.

## The kinds of file

Hex knows a layout by `#define layout`. The other kinds go the same way, `#define strings`, `#define skin`,
`#define data`, `#define script`, and when a file doesn't say, by what it declares (`let strings`, `let skin`,
`let sprites`, mostly functions). The kind picks the views on the tab's corner and what the right side shows, the file
is the same text whichever view it is looked at in, and the code is always one of the views.

| kind | views | right side | what is checked |
| --- | --- | --- | --- |
| layout | Design, Code | properties, values in code | properties against UIPropertyHelp, `@keys` against the texts |
| texts | Table, Code | the preview of a text | a key missing from a language, a key nobody names |
| skin | Preview, Code | the preview sheet | colours that aren't colours, fonts and art that aren't there |
| sprite sheet | Sheet, Code | the sprite under the caret | rects outside the picture, names a skin wants that are missing |
| data | Code | values | shapes that don't match their siblings, paths to files that aren't there |
| script | Code | values, and the console | the parse, calls to names nobody declared |

## Editing values without losing the file

The designer writes a layout's file anew every time, which is why it warns before a file becomes a layout. Nothing in
this plan does that. Every edit outside the code (dragging a number in the values, typing in a cell of the table,
picking a colour) is a replacement of the span of one literal in the text, through the code editor's own document, so
it is one step to undo, the comments stay, the spacing stays, and the code view shows the change as if it was typed.

That needs the parser to say where every literal came from. The lexer has every token's line and column already, the
syntax tree doesn't keep them. The plan is to keep the span of every literal and every key on its node (a start and an
end, two ints), and to have the interpreter hand back, with the value of an object, which node each of its keys came
from. A value whose span is known can be edited in place. One that was worked out (`3 * 4`, a spread, a loop) is shown
and can't be, it says so on hover.

## Help that comes from the files themselves

There is no schema for a move list and there never will be, bogz writes the shape in a comment at the top and changes it
when the game does. So the shape is taken from the file.

- **Siblings.** The objects in one object (every move in `moves`) are taken to be the same kind of thing. A key most of
  them have is offered by completion in the ones that haven't got it, with the value they mostly have. A key only one
  of them has that is a letter off a key most of them have is a warning, a typo nine times in ten.
- **Paths.** A string that is the path of a file that is there (from the file's folder, or from the workspace, the way
  the game reads it) is a link, Ctrl+click opens it, hover shows its first lines. One that isn't there is a warning
  when the string looks like a path (it has a slash and an extension).
- **Inline values.** After a let whose value was worked out, its value faintly at the end of the line,
  `let reach = base * 1.5;   = 96`. Only where the value isn't the literal written there already.
- **The header comment.** Hovering a key of a data file shows the line of the header comment that mentions it, which
  is how every data file in Fighter2D documents itself already.

## Evaluating as you type, safely

Everything above runs the file. A file is run a moment after it last changed and parses, on a worker thread, and what
it came to is swapped in when it is done. A newer change cancels the run before it. The interpreter checks for that in
its loops and calls, and has a limit on how deep calls go (a function that calls itself for ever overflowed the stack,
which takes the process with it, there is no catching that in .NET). A run that takes longer than a second is stopped
and said so, the values stay what they were. Includes are run too, from the file's folder, read through the open
documents first so an unsaved change in an included file counts.

Nothing a game would hand a file is there when Hex runs it, a layout gets the designer's compositor and a behaviour gets
nothing. The console can declare what a script expects (`let view = { ... }`) and the declarations stay for the next
lines.

## Extensions

The first round of this plan loads extensions and runs Hello Dolly as one, see "Extensions" in the README. What they
should be able to do, in about the order it is worth doing.

1. Know when things happen. `on_start` (done), `on_open(file)`, `on_save(file)`, `on_change(file)`, `on_close`.
2. Add commands. `hex.command("Sort the moves", func() { ... })`, in the command palette and a menu of its own under
   View, Extensions.
3. Say what is wrong. `hex.problem(file, line, column, text)` from `on_change`, which is how a game's own lints
   (every move has a recovery, every character's moves file is there) get written by the game's developer in HIDL.
4. Read and change the file on screen, by the value as well as the text, through the same span editing as above.
5. Items in the status bar, and a panel of their own made out of a layout, so an extension can have a UI made in Hex.

They run in a runtime each, with `hex` the only thing handed to them, no files outside what Hex opens for them. One
that throws is switched off for the rest of the run and says why in the output.

## In what order

1. **This round.** The values on the right for code, the HIDL console in the panel, the outline that says what things
   are, the tabs of the panel readable, the interpreter made safe to run while typing, and extensions with Hello Dolly
   the first of them. The README and the changelog.
2. The kinds of file, `#define` and guessing, the views on the corner of the tab strip, the status bar saying the kind.
   The toolbar row goes.
3. Spans on the syntax tree and editing literals in place, from the values first (numbers dragged, booleans flipped,
   colours picked), then the table of texts.
4. The table of texts with its languages side by side, and renaming a key across the workspace.
5. Siblings, paths and inline values.
6. The skin preview and the sprite sheet view.
7. Extensions grow, events, commands, problems, then UI.
8. A script runner for behaviours, a function called tick after tick with a view that can be edited between calls,
   and what it handed back each tick in a list.

## Not doing

A debugger with breakpoints. HIDL runs a few milliseconds at a time inside a game and the console plus values covers
what one would be used for. A language server for VS Code. The VS Code extension in the language's repo stays the
colours it is, Hex is where HIDL is edited. Formatting a whole file. HIDLWriter writes values, not the comments and
spacing between them, and a formatter that loses comments is worse than none.
