# Hex for HIDL, the plan after VS Code

The first round made Hex look and behave like VS Code, and that was the point, a shell everybody already knows how to
drive. This is the plan for what comes after, an editor shaped round what HIDL files actually are, how it should look,
what it needs from the engine's UI and from the language, and what it costs. VS Code treats every file as lines of text
with colours on. A .hor file is a program that comes to a value, and nearly all of them are written to come to a value
somebody else reads (a game, the engine, Hex itself). That is the difference the whole plan is built on.

Written after going through every part of Hex, every .hor in the repos round the engine, the UI framework and the
language. See `performance.md` for what everything costs, measured, and `Horizon.HIDL/docs/plan.md` in the engine for
the language's own plan, which this one leans on.

## Who opens Hex, and with what

Going through every .hor in the repos, there are seven kinds of file, and they want different things.

1. **Layouts.** Fighter2D's menus, Hex's own window, the engine's performance card. Built by calling the compositor,
   `#define layout` at the top. They have the designer already. What they lack is seeing a layout in the game's
   languages and screens, and working with the texts and the art the layout names.
2. **Texts.** `let strings = { ... }`, a file a language (en-gb.hor, Fighter2D's localisation). A tree of keys with a
   text at every leaf, the same keys in every language, named from layouts as `@main_menu.quit`. They want a table,
   languages side by side, and to know which keys nobody uses and which a layout uses that aren't there.
3. **Skins and sprite sheets.** `skin.hor` (colours, fonts, spacing, syntax colours, themes) and `sprites.hor` (rects
   into Aseprite files, nine slices, inheritance from another sheet). They want to be seen, every component drawn in
   the skin, a sprite drawn over its picture.
4. **Game data.** Fighter2D's characters, move lists, maps and hitboxes. Big nested objects, the same shape over and
   over (every move has stances, phases and a cancel window), files that name other files by path, later files that
   replace what earlier ones said by name. The header comment is the only documentation of their shape. They want the
   value seen, checked against its siblings, edited as a value.
5. **Behaviour scripts.** `dummy_behaviours.hor`, functions the game calls once a tick with a view of the fight. Real
   code with state between calls. They want to be run, tick after tick, with a made up view.
6. **Settings.** `hex_settings.hor`, a game's options, flat keys and values. They want a form.
7. **Extensions.** HIDL that drives Hex, as of this round. They want an API, a reload, and their output.

The common thread is that the value matters more than the text. A move list is right when the moves it comes to are
right, a skin is right when it looks right, a file of texts is right when every language has every key. Hex should show
the value, check the value, and let it be edited as a value without ever losing the comments round it.

## What it should feel like

Open a move list and the right side shows the moves, live, as a tree of what the file comes to. Click a phase and the
caret goes to it, in whichever file it was written in. Hover a key and the comment written over it is the tooltip.
Drag a number in the tree and the number in the code changes, nothing else in the file does. Type `cancel: ` inside a
phase and the completion offers what the other phases have for cancel. Misspell `stances` as `stance` in one move out
of forty and it is underlined, every sibling says `stances`. A path in a string is a link.

Open a file of texts and it is a table, keys down the side, a column for every language in the folder, the empty cells
red, every text drawn the way a label would draw it. Open a skin and there is a sheet of every component drawn in it
next to the code, changing as the colours are typed. Open a behaviour and there is a console underneath to call it
from, and a runner to call it a hundred ticks over with a view that can be changed between them.

## Where it stands (done)

- **Values** on the right for every file of HIDL that isn't a layout, what it comes to, run a moment after typing stops
  on a thread of its own, a newer change cancelling the run before it, stopped after a second. A click goes to where the
  value was written (the source map, includes and spreads followed, opening the included file), the tooltip is the
  comment over it, the run's error is underlined where it happened. Few declarations come open.
- **The HIDL console**, the third page of the panel, a line run in what the file came to, what it declares kept for the
  next lines and declared again in the next run of the file, Up and Down through the history, a value's path from its
  right click.
- **The outline says what everything is**, an object, a list, a function, a text, a number, a colour, a vector, by icon.
- **Extensions**, loaded from Assets/hex/extensions and an extensions folder next to Hex, switched on and off on their
  page of the settings and from the activity bar, `on_start(hex)`, `hex.title_note`, `say`, `file`, `version`, `name`,
  `folder`. Hello Dolly is the first, the greeting in the title bar.
- **The language under it** got spans, errors that say where, the source map, a call depth limit, cancellation, `??`,
  `?.`, template texts, `merge`, and a lexer and parser that make half the garbage. See its plan.
- **What it costs** is measured, and the garbage the pointer made in the designer is a tenth of what it was.
- **The lists are trees.** `TreeView` in the engine, painted rows, only the ones in sight. The values, the outline, the
  explorer (read once a folder and watched) and search's results by file are on it, icons coloured by kind, guides.
- **The table of a file** (F7 between it and the code), an object of objects a row each and a column a key (Fighter2D's
  moves and characters), a file of texts a row a key and a column a language, every language in its folder, the missing
  ones red. `Table` in the engine, a sticky header, a frozen first column, sorting, columns dragged wider, fitted to what
  is in them.
- **Editing values** in place, in the values and the table, a double click on one written as itself, its text replaced
  where it is (the language's source map knows the span of every value and whether it is a literal), a boolean flipped.
- **Siblings and paths.** A key only one of its siblings has, a letter or two off one most of them have, and a path to a
  file that isn't there from the file's folder or any over it, are warnings. Nothing on Fighter2D's data, as it should be.
- **Every parse error**, the parser goes on after a mistake.
- **The shell.** The toolbar row is gone, a file's views and Run are at the right end of its tabs, the activity bar has
  its accent and Extensions, a welcome page, cards in the corner, F8, find in files and the names for completion off the
  editor's thread.

## The window

The shell stays VS Code's, the parts of it change job.

```
 [=] File Edit Selection View Go Run Help         moves.hor - Hex (greeting)              _ [] x
 +--+---------------+-------------------------------------------------+------------------+
 |EX| OUTLINE    v  | moves.hor *  | characters.hor |  [Code|Values|Table]  > | VALUES     ... |
 |SR|  {} moves  41 | Fighter2D > Assets > data > moves.hor > jab     | v {} moves  41   |
 |OU|   {} idle      |                                                 |   v {} jab       |
 |EX|   {} jab       |   the editor                                    |     # damage  8  |
 |  |   ...          |                                                 |     > [] phases 3|
 |  |                +-------------------------------------------------+   > {} run       |
 |  |                | PROBLEMS 2 | OUTPUT | HIDL | RUNNER        ^  x  |                  |
 |@ |                |  > moves.jab.phases[0].cancel                   |                  |
 +--+---------------+-------------------------------------------------+------------------+
 data, 41 moves, ran in 0.6 ms  (x) 0 (!) 2     Ln 120, Col 8   Spaces: 4   LF   HIDL data
```

- **The right side is whatever the file is about.** Properties for the designer, values for code, the preview of a
  text or a skin. What it says about the file itself (the path, the line endings) is the status bar's.
- **The toolbar row goes.** It was there for layouts when layouts were all Hex opened. The name of a layout is its file,
  renamed with F2 on its tab. Design, Code and the other views of a file are a segmented control at the right end of the
  tab strip, Run next to them, the way VS Code puts a file's actions there. The title bar has the menus already. That's
  a row of the window back for the code.
- **The activity bar** gets an accent bar down the left of the view that is open (VS Code's), Extensions, and Settings
  at the bottom of the bar. The views are Explorer, Search, Outline (or the Toolbox in the designer), Extensions.
- **The side bar's views get title bars with actions**, collapse all, refresh, a filter box, the way VS Code's do, and
  sections that fold (the toolbox, the components, the layers are those already).
- **A welcome page** instead of an empty untitled layout when Hex starts with nothing open. Recent files and workspaces,
  new layout, new file, open folder, the keys, what changed, and the language's help, a document like any other that
  closes with its x.
- **Notifications**, a little card in the bottom right corner that fades, for what the status bar can't hold (an
  extension that failed, a file changed on disk, a save that didn't work), with a button where there is something to do
  about it. The output keeps all of it.
- **Quick open learns VS Code's prefixes.** `>` for commands, `@` for something in the file, `:` for a line, `#` for a
  key of the texts, so Ctrl+P is the one box for going anywhere.

## How it looks

Hex is drawn in the flat skin, dark unless asked. The round to come tidies how it looks rather than what it is.

- **One grid.** Every gap a multiple of four, every row of every list 24 high at a scale of one (the tree rows are 26,
  the explorer's 26, the problems 26, the properties 28, it shows), the side bar's padding 8.
- **Icons coloured by what they are**, the way VS Code colours its symbols. Objects orange, lists and functions purple,
  keys and fields blue, texts and numbers in the colours the code writes them in, a colour as its own swatch. The skin
  says the colours (`symbol_colors`), the light theme its own.
- **Lists that read as lists.** Indent guides down the levels of a tree, a row lit under the pointer and the selected
  one in the accent at a third, the chevrons in the gutter before the icon, nothing jumping when a row opens.
- **Focus you can see.** The panel with the keyboard has a thin accent line along its top, the way VS Code shows which
  editor group has focus.
- **The tab strip** gets the accent line along the top of the open tab and the dot that turns into an x under the
  pointer (it has the dot and the x, they don't swap).
- **Empty states with buttons.** "Nothing open" in the explorer is a button that opens a folder, not a sentence that
  says to.

## The kinds of file

Hex knows a layout by `#define layout`. The other kinds go the same way, `#define strings`, `#define skin`,
`#define data`, `#define script`, and when a file doesn't say, by what it declares (`let strings`, `let skin`,
`let sprites`, mostly functions). The kind picks the views on the tab's corner and what the right side shows. The file
is the same text whichever view it is looked at in, and the code is always one of the views.

| kind | views | right side | what is checked |
| --- | --- | --- | --- |
| layout | Design, Code | properties, values in code | properties against UIPropertyHelp, `@keys` against the texts, sprite names against the skin |
| texts | Table, Code | the preview of the text under the caret | a key missing from a language, a key nobody names, markup that doesn't close |
| skin | Preview, Code | the preview sheet | colours that aren't colours, fonts and art that aren't there |
| sprite sheet | Sheet, Code | the sprite under the caret over its picture | rects outside the picture, names a skin wants that are missing |
| data | Code, Table (an object of objects) | values | shapes against siblings, paths to files that aren't there, the shapes the language gets |
| script | Code, Runner | values, and the console | the parse, names nobody declared |
| settings | Form, Code | values | the shape, when there is one |

**The table of a data file** is the one view everything above wants most after the values. An object whose values are
all objects (moves, characters, maps) is a table, a row each, a column for every key any of them has, the cell empty
where a row hasn't got the key, a nested object a cell that opens. Sorting by a column, a filter. That's Fighter2D's
characters and moves looked at the way a designer of a fighting game would, frame data in columns.

## Editing values without losing the file

The designer writes a layout's file anew every time, which is why it warns before a file becomes a layout. Nothing in
this plan does that. Every edit outside the code (dragging a number in the values, typing in a cell of the table,
picking a colour, a toggle in a form) replaces the span of one literal in the text through the code editor's own
document, one step to undo, the comments and spacing left alone, and the code view shows it as if it was typed.

The source map knows where every key and item was written. It needs one more thing for this, the span of the value of
a key and not only of the key, which the parser has as it reads the value. A value whose span is a literal (a number,
a text, a boolean, a vec of numbers) can be edited in place. One that was worked out (`3 * 4`, a spread, a loop) is shown
and can't be, it says so on hover, with a button to go to the code.

## Help that comes from the files themselves

There is no schema for a move list and there never will be, bogz writes the shape in a comment at the top and changes it
when the game does. So the shape is taken from the file, until the language's shapes are there to say it outright.

- **Siblings.** The objects in one object (every move in `moves`) are taken to be the same kind of thing. A key most of
  them have is offered by completion in the ones that haven't got it, with the value they mostly have. A key only one
  of them has that is a letter off a key most of them have is a warning, a typo nine times in ten.
- **Paths.** A string that is the path of a file that is there (from the file's folder, or from the workspace, the way
  the game reads it) is a link, Ctrl+click opens it, hover shows its first lines. One that isn't there is a warning
  when the string looks like a path (a slash and an extension).
- **Inline values.** After a let whose value was worked out, its value faintly at the end of the line,
  `let reach = base * 1.5;   = 96`. Only where the value isn't the literal written there already.
- **Comments as documentation.** The comment over a key is its tooltip in the values (done), in the code's hover, and in
  completion.
- **Keys and names.** `@main_menu.quit` in a layout goes to its text with F12, hovers with every language's, and a key
  missing from a language is a warning (done for the open language). A key renamed is renamed in every layout.

## Running, safely

Everything above runs the file, which is now done off the editor's thread with a newer change cancelling the older run
and a second at the most. The language's plan has a step budget instead of the clock, which is what to move to. A run
reads the open documents before the disk, so an unsaved change in an included file counts. Nothing a game would hand a
file is there when Hex runs it, a layout gets the designer's compositor and a behaviour gets nothing. The console can
declare what a script expects and the declarations stay.

The runner for behaviours. A function, a view to hand it (an object in HIDL, edited in the runner, kept per file), a
number of ticks, and a list of what it handed back every tick with what it printed. Change the view between ticks, step
one at a time, or run them all. The state at the top of the script (`pressed_for` in the dummy's) is shown as it goes.

## What the engine's UI needs

New components, each a component of its own in `Horizon.UI.Components` and a name in a layout, usable by a game too.

1. **`TreeView` (`tree_view`).** Rows painted by the component itself, only the ones in sight, like `ListBox` and the code
   editor do, instead of a template instantiated a row (six hundred rows were six hundred little programs and the
   garbage of the code sweep). Items handed over as a tree (`UITreeItem`, text with markup, icon, icon colour, a
   detail after the text in the dim colour, children, open or not, anything as its tag), chevrons, indent guides,
   hover, selection, the keys (up, down, left closes, right opens, enter), a right click, a tooltip per item, scrolled
   by the wheel with its own bar. The values, the outline, the explorer, the components, the problems and search's
   results all go over to it.
2. **`SplitPanel` (`split`).** Two parts and the edge between them dragged, the size kept. The side bar, the panel and
   the properties are done by hand with grips and FitChrome now, and every one of them has had a bug where they meet.
3. **`Table` (`table`).** Columns with headers that sort and can be dragged wider, rows painted like the tree's, a cell
   edited in place by a text box laid over it. The texts and data views.
4. **`Notification` and `UINotifications`.** The cards in the corner.
5. **`SegmentedControl` (`segments`).** The views of a file on the tab strip. (The `segments` there is now is a
   `SegmentBar`, a bar in pieces, so this one gets another name in a layout, `switcher`.)
6. **The code editor** gets ghost text after a line (inline values), a line of its own above a line (a code lens, "3
   uses"), folding by brackets, a minimap down its right side, and lexing line by line (see the performance plan).

## Extensions, what comes next

1. Know when things happen. `on_start` (done), `on_open(file)`, `on_save(file)`, `on_change(file)`, `on_close`.
2. Add commands. `hex.command("Sort the moves", func() { ... })`, in the command palette and a menu of its own under
   View, Extensions.
3. Say what is wrong. `hex.problem(file, line, column, text)` from `on_change`, which is how a game's own lints get
   written by the game's developer in HIDL.
4. Read and change the file on screen, by the value as well as the text, through the same span editing as above.
5. Items in the status bar, and a panel of their own made out of a layout, so an extension can have a UI made in Hex.
6. Reload one without restarting Hex, and watch its folder to do it by itself.

They run in a runtime each, with `hex` the only thing handed to them. One that throws is switched off for the rest of
the run and says why. The language's binding generator, when it comes, makes `hex` out of a class with attributes
instead of `Natives.Object` by hand.

## Performance, what Hex itself does about it

See `performance.md` for the numbers. What is Hex's to do, the biggest first.

1. **The explorer reads the disk every time it is rebuilt**, which is every time the side bar is, a selection that moved
   included. It should read a folder when it is opened and when a `FileSystemWatcher` says something in it changed.
2. **F8 (check every file), find in files and the first completion read and parse the workspace on the editor's
   thread.** A big workspace freezes Hex for as long as that takes. Off the thread, with the results coming in as they
   are found, the way VS Code's search does.
3. **Every list is rebuilt out of templates for any change**, see the `TreeView`.
4. **The code of a layout is written anew from the layout at most ten times a second while it is out of sight**, and
   the whole of it every time. Fine for a layout of a hundred components, it'll be the next thing to show up for a big
   one. The designer knows which component changed, so it could patch that component's span.
5. **Undo in a layout keeps the whole code a step.** The same fix as the text document's, steps as what changed.

## Not doing

A debugger with breakpoints. HIDL runs a few milliseconds at a time inside a game and the console, the runner and the
values cover what one would be used for. A language server for VS Code, Hex is where HIDL is edited. Formatting a whole
file until the language keeps trivia, a formatter that loses comments is worse than none.

## In what order

1. **Done.** Values, the console, the outline's kinds, extensions, the language underneath, the sweep.
2. **Done, mostly.** The values, the outline, the explorer and search on `TreeView`. Still to go over, the problems
   and the designer's components and layers.
3. **Done, mostly.** The toolbar row went, the views and Run on the tab strip, the activity bar's accent and
   Extensions, the welcome page, notifications, the background work. Still to do, quick open's prefixes, Settings at the
   bottom of the activity bar, focus lines.
4. **Done, the first of it.** Values and cells written as themselves edited in place, booleans flipped. Still to do,
   numbers dragged, colours picked from a swatch in the values, a cell of another language's file edited (it is
   read only now).
5. **Done, the table.** Data and texts. Still to do, the kinds of file by `#define` and the views they pick, the skin's
   preview and the sprite sheet.
6. **Done, siblings and paths.** Still to do, inline values, paths as links, completion from siblings.
7. **The runner** for behaviours.
8. **Extensions grow**, events, commands, problems, then UI.
