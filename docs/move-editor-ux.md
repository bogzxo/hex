# The move editor, made nice to use

bogz finds the move editor clunky. It does everything a move can say now (see `move-editor.md`), and that's sort of
the problem, it grew a field at a time and a tab at a time until tuning a jab means going round three columns and four
tabs to look at five numbers. This is what hurts, worst first, what to do about each, and in what order. Nothing of it
changes the move format or the way an edit goes into the file (`hex.edit` and friends), that part is good and stays.

It was looked at by running Hex on Fighter2D's `blocky_moves.hor` at 1600 by 900 and going down the move list with the
arrow keys (idle, run, the spin kick, the dragon punch), and by reading every part of the extension. The build that ran
was from before the second move format reached the extension, so it said `knockback` and `cancellable`. The format
doesn't matter here, the layout is the same, and where v2 makes something worse (sixty cells a second instead of
twelve) that's said. The v2 work (step 6 of the physics plan, the combo window and all) is going on in the same files
as this is written, so none of this starts until that has landed.

## What somebody does in it

Worth saying before the pains, because the layout should be built round these and it's built round the file instead.

Tuning a hit. Pick a move, look at startup, active, recovery, on hit and on block, change the hit frame or the
recovery, look again, Fight! to feel it. This is most of the time spent in it and it wants the frame data and the
timeline on screen together, all the time.

Drawing the red box. Go to the hit frame, drag a box, step through the active frames, fix each. Wants a big picture of
the fighter and the overlays a key away.

Making a move. Pick a kind of move, name it, choose its animation, set its phases, then the above. Wants the art to
pick from and not a dropdown of tag names.

Looking over the roster. Which moves are plus, what every button does crouching, what nothing reaches. Wants tables you
can click into.

Right now the screen is split as the code is split (a list part, a frame part, a timeline part, a tab a part), so every
one of those jobs has you hopping about.

## What hurts, ranked

### 1. The move list shows four moves

Blocky has thirty three moves. At 1600 by 900 the list is four rows tall, with the name box, the template dropdown, the
four buttons, Save the hitboxes, Fight! and the status line under it, and then half a column of nothing. The rows are
worked out from the height in `columns.hor` (`(height - 420) / 20`) and come out at four, which is the first thing to
look at, but it's also that the column is spent on things done once an hour (picking the fighter, making a move) and
not on the thing done every ten seconds (picking a move). Going up and down with the arrows works and nobody knows it's
there.

What to do. The list takes all the height the column has, the actions go to a row of icons over it (new, copy, rename,
delete) and a right click on a row, the fighter goes up into the header next to the move's name (see 13), Save goes
(see 9) and Fight! moves over the frame next to Play, where the other doing buttons are. Then the list becomes a tree
grouped by kind (the game's, movement, attacks, specials, the same groups the dropdown has now) with how many in each, a
row saying what it needs to (a dot for a red box, the file's name dimmed when it's written in another, a red or yellow
mark when the problems have something on it, the input dimmed after the name, `uppercut  RT`). The filter box stays and
takes the input too, typing `crouch` or `A + B` finds them.

The rows being right is HIDL, today. The tree wants `TreeView` (`tree_view`) to take its items from HIDL, it's
registered for layouts but its `Items` and `OnExpanding` aren't exposed, so that's the engine. The right click wants
Hex to give an extension a context menu, see the end.

### 2. The frame data is in a tab

Startup, active, recovery, on hit, on block. The five numbers a fighting game is tuned by are behind the Data tab, as a
paragraph, while the Move tab is open because that's where damage is. So you change damage, click Data, read, click
Move, change it again. The timeline does colour the cells startup, active and recovery, which is the right idea, but
doesn't count them or say what they come to.

What to do. Street Fighter 6's frame meter, more or less, as the top of the timeline and always there. A row of the
attacker (green startup, red active, blue recovery, the counts written over each run) and under it a row of the
opponent (yellow hitstun or blockstun from the hit on, a toggle between on hit and on block), the frame where both are
free marked, and the advantage written at the end of it, `+3` green or `-6` red. It reads at a glance whether a move is
plus and why, which a paragraph never will. When an edit changes a number, the number says where it came from for a
couple of seconds (`-4 → -2`, lit) and settles back, so changing damage and seeing hitstun go up needs no clicking at
all.

The Data tab keeps the long version (counter hits, hitstop, meter, combo scaling) and the combo window the v2 work is
adding, which is the right thing for a launcher, but its one line answer ("free 12 frames before they recover, 70 up")
belongs on the meter too, at the end of the victim's row. All HIDL, it's a drawing.

### 3. The timeline is a 92 pixel strip with five lanes in it

Cells, phase blocks, hit, cancel and commit markers, the active bar the v2 work adds and the status band, all inside 92
units, each 5 to 14 tall, with names written at 0.095 and art frame numbers at 0.07 over the cells. On the spin kick
the phase name sits half under its block. The markers are coloured squares with no letter on them, so the hit, a cancel
and a commit are white, light blue and orange and you learn which by dragging them. The cells are centred in the strip
and at most 22 wide, so a twelve cell move is a third of the width with the rest empty either side. With v2 a cell is a
game frame, a forty five frame move is forty five cells, and that strip gets denser yet. There's no ruler, so "frame 14"
is counted by eye, and no zoom.

You can't make anything on it either. A marker can be dragged once the phase has one, but it's put there by typing a
number into the Phase tab. The plan said right click a cell for "the hit comes out here", and the drawing can't hear the
right button, it pans with it.

What to do. Lay it out the way Aseprite lays out a timeline and FrayTools lays out its collision boxes, lanes with
their names in a gutter on the left and frames going across from it, left aligned, with a ruler of frame numbers over
them, and taller (twice now, it gives up height to the canvas when the window is short). The lanes, top to bottom.

| lane | what's on it |
| --- | --- |
| frames | the ruler, every fifth numbered, the frame on screen lit right down through the lanes |
| meter | the frame meter of 2, the attacker and the opponent |
| phases | a block a phase, long enough to write its name in, `(loops)` or `(until held)` on it, the end dragged |
| hit | the hit frame and the active run as one bar from it, its end dragged, H on it |
| cancel | the cancel window as a span, from the cancel frame to the commit or the end, rather than two loose squares, `hit_cancel` as a span from the frame after the hit |
| status | invulnerable, blocking, attacking as bands with the word in them |
| art | the art (see 4) |

The wheel zooms the frames (round the pointer, the way the drawing already zooms round it), shift and the wheel
scrolls, and a move that doesn't fit scrolls rather than squashing to two pixel cells. What's under the pointer is said
in a line under the timeline ("drag to make swing 18 frames, it's 14", "the hit, frame 6, drag it along") instead of a
tooltip of four sentences that's the same wherever you are. A right click on a frame has a menu, the hit here, active to
here, cancel from here, commit from here, status from here, a phase splits here, a new phase after this one, and a
double click on a phase names it. A phase splitting at a frame is how Rivals and Rivals 2 think about a move, windows
one after another, and it's much easier than making a phase and then setting its animation and start to carry on where
the last one was.

The drawing is HIDL. The engine's `DrawingPanel` has to tell the extension more, which button went down (the right
button only pans now, `e.button` is in `extensions.md` but the code doesn't send it), a double click, and the wheel when
the drawing isn't navigable (so the timeline can zoom its own way and stay unzoomed vertically). A cursor that says what
a drag would do (the resize arrows on a block's end) would be nice and is a small engine change, it's not needed.

### 4. The art strip is a second timeline that only shows one phase

Under the timeline, a row of every frame of the selected phase's animation, a click setting `start`. It doesn't line up
with the cells over it, so which art frame is drawn on which game frame is something to work out (the v2 timeline puts
a tick where the art moves on, which helps). A phase that carries on the art before it has nothing to show and says so
in the status line when clicked. The animation itself is picked by name in the Phase tab's dropdown of every tag.

What to do. It becomes the art lane of the timeline, each art frame a thumbnail as wide as the game frames it's drawn
on (a cel, in Aseprite's words), so the picture and the timing are one thing. Click a thumbnail to go there, drag the
first one's left edge to change `start`, and a click on the lane's name or on the phase's animation opens a picker of
every tag of the .ase with its first frame drawn, as big as the room lets, rather than a dropdown of names. The picker
can be the `ArtPicker` idea (a menu of pictures) done in the drawing, or a `hex.window`. HIDL either way.

### 5. Picking a thing and editing it are in different places

Click a phase on the timeline and its fields are in the Phase tab, which isn't the tab showing, it's Move. Drag the red
box and its numbers are in the Hitbox tab. The overlay toggles for the canvas are in the Hitbox tab as well, so turning
the onion skin on means leaving the move's fields. Five tabs (Move, Phase, Hitbox, Data, Inputs) over one 300 pixel
column, and three of them are about the thing you just clicked somewhere else.

What to do. One inspector, scrolled, made of collapsible sections, that follows what was picked. The move's sections
(input, hit, movement, status, reroutes) and under them the phase's (art, timing, status, movement), with the phase's
name in its title bar and its buttons (before, after, copy, delete) there too. Clicking a phase on the timeline opens
and scrolls to its section, clicking the hit marker to the timing part of it, the red box to the box section. The tabs
that are left are about the fighter and not the move, Data's table and Inputs, and they want more width than a column
anyway (see 12).

And show what's there before what could be. Every row of the form is shown now, set or not, so the uppercut has the
same twenty five rows as idle and the six that matter are somewhere among them. The section shows the keys the move
says, then a row `+ add` with the rest in a menu, the way Unity adds a component or VS Code's settings show the
modified ones. A key that isn't set but that the game works out (hitstun, blockstun) stays shown with what it comes to,
greyed, since that's what you're tuning against. That's the field list worked out in HIDL from what the move has, the
form takes whatever list it's given, so nothing in the engine.

The phase buttons want their icons sorting while this is done. A new phase before is `arrow-left` and playing a phase
sooner is `chevron-left`, side by side, the same for after and later. Words, or a menu.

### 6. The fighter is small and the overlays are a tab away

The canvas fits the 128 pixel frame and the fighter is a third of it, so Blocky is 180 pixels tall in a 640 by 400
canvas, and his fist (where the red box is) is a few pixels across. The overlays (the hurtbox, the traced box, the onion
skin, the launch arc) are toggles in the Hitbox tab. There's no floor, so the launch arc and a jump's height float in
space, and nothing says how far away the other fighter would be standing.

What to do. Fit to what's drawn on the frames of the move, every frame of it, not the frame (`mask.bounds()` of each,
put together, which the tracer has already), with the floor drawn as a line, so the fighter fills the canvas and doesn't
jump about from move to move. A strip of toggles along the top edge of the canvas, the overlays and the onion skin,
with keys. The opponent drawn faintly at a range you can set (Fighter Factory's axis, Ultimate's training mode does the
same), standing in their idle, so a box that's too short is seen being too short, and with v2 the launch arc lands
somewhere real. All HIDL.

### 7. Nothing tells you what happened

Everything the editor says goes to one grey line at the bottom of the left column, under Fight!, at 0.115. An edit Hex
refuses (a value worked out in the code), "type a name for it first", "press delete again to take the uppercut out",
"there's a shove already", "undone", the fighter it loaded. That line is the furthest place from where you were looking,
and it's overwritten by the next thing. Problems are in Hex's problem panel at the bottom, where "nothing wrong with this
file" took a quarter of the screen in every run.

What to do. What went wrong goes to `hex.notify` (Hex has the cards already, `Notifications`), what's under the pointer
and what just happened go to a line under the canvas that's in sight. The problems of the move on screen go next to
what they're about, the field in the inspector underlined with the message on it, the move's row in the list marked, so
nobody has to open the panel to see that the hit frame is past the end of its phase. And see 2 for numbers changing,
the best feedback this editor can give is the frame meter moving. HIDL, with `hex.problems` already putting them in Hex.

### 8. Making, copying and renaming a move go through a text box

New, Copy and Name take what's typed in the box over them. Press New with the box empty and it says type a name first.
The template is a dropdown of eight names with nothing to say what a launcher template is. Delete is a second press,
said in the grey line. Renaming a phase is the same box and button, in the Phase tab.

What to do. New opens a little window (a `hex.window` is enough) with the templates as cards, a picture of the animation
it'll use and the numbers it starts with, a name box already focused and Enter to make it. Copy makes `uppercut_2` and
asks for its name the same way, with the name picked. F2 or a double click renames whatever is picked, a move or a phase.
Delete asks in a dialog that says what reroutes to it, rather than wanting a second press. This wants Hex to give an
extension a dialog, or a window it can close itself, see the end.

### 9. The red boxes save apart from everything else

Every change to a move goes into its file and is saved with the file. The red boxes are kept in the view and written by
Save the hitboxes, or by Ctrl+S on the move file, and that they aren't saved is only said in the line under the timeline
("red boxes unsaved"). They have their own history that the view's Ctrl+Z goes through, so undoing in the code view
doesn't touch them and undoing in the move view does.

What to do. The boxes file is written through Hex the way the shared moves are, open in a tab behind the others, every
change one step of its undo, the tab's dot saying it has changes, and Ctrl+S saving all of them. The box history and the
button go. `edits.hor` already does it this way for files, the boxes are the one thing that doesn't, and `hex.write` to an
open document is already one step to undo. HIDL, unless writing the whole file every drag is too slow, in which case
`hex.edit` by the box's path.

### 10. Most of it is mouse only

What the keys do now is left and right a frame, home and end, up and down a move, space plays, undo, redo and delete
(the red box). The rest is clicking. Fighter Factory and Aseprite are fast because the hands stay on the keys.

What to do. A small set, said in a list behind a `?` over the canvas and in the tooltips of what they do.

| key | does |
| --- | --- |
| left, right | a frame, with shift to the next phase |
| `,` `.` | a frame too (Aseprite's), for the left hand |
| home, end | the first frame, the last |
| space | plays, L loops the picked phase only, `[` `]` slower and faster |
| H, A, C, M | the hit here, active to here, cancel from here, commit from here |
| up, down | the move before, the one after (in the list's order, filtered) |
| ctrl+F | the filter of the list |
| ctrl+N, ctrl+D, F2, delete | a new move, a copy, renaming, deleting (the red box when it's picked, the move when the list is) |
| arrows with alt | the red box a pixel, shift for 8, ctrl and alt sizes it |
| 1 to 5 | the overlays |

Every one of them also a command (`hex.command`), so Ctrl+Shift+P finds them by name. The keys are HIDL in
`view.key_pressed`. A command showing its key in the palette wants `hex.command` to take one, which is Hex.

### 11. You learn it by reading tooltips

The canvas, the timeline and the art strip each have a tooltip of two to four sentences that is the whole manual for
them. It comes up wherever the pointer rests, the same text, and in the runs it sat over the playback controls. Nothing
says a red box is drawn by dragging on nothing until you read that.

What to do. Mostly covered by 3 (the line saying what's under the pointer), 10 (the key list) and the empty states
saying what to do. A move with no red box says so on the canvas, with a button to trace one and "or drag on the frame".
A move with no phases has a button for one. A file whose game can't be found says where it looked and how to point it
somewhere. The long tooltips go.

### 12. The fighter's tables are text

The Inputs tab is a markdown list of every input and what it starts in every stance. Blocky's file has the same thing at
the top as a comment, a grid of buttons by standing, crouching and in the air, written by hand, which says the tab isn't
doing it. The Data tab's table of every move is a code block of padded text in a 300 pixel column, sorted by a dropdown,
copied out as markdown, and you can't click a row to go to the move.

What to do. Both become real tables, wide, in the place the canvas is (a toggle between the move and the fighter, or a
panel page with `hex.panel` that has the whole width). Inputs as the grid in the comment, buttons down and stances across,
a cell the moves in the order they're tried, a move that never comes out red, a click going to it. Data with a column a
number, a click on a header sorting, a click on a row going to the move, plus and minus coloured. The engine's `Table`
does all of that (columns, cells with a colour and a fill, `OnPressed`, `OnActivated`) and is registered for layouts,
but its columns and rows aren't exposed to HIDL, which is the engine change. The reroute graph from the first plan
would go here too, later.

### 13. The fighter dropdown is the first thing on the screen

It's rarely changed (a shared file has three fighters, Blocky's own file has one) and it's the biggest control in the
left column. It goes in a header over the frame, `Blocky › uppercut › swing`, a breadcrumb (`Breadcrumbs` is in the
engine), with the fighter a dropdown there and where the move is written after it. HIDL.

### 14. Playing is all or nothing

Space plays the whole move round and round. There's no playing the picked phase only, no stopping at the end, and the
speed is a selector of three. Want loop the phase (L), play once and stop on the last frame (shift space), and the speed
as `[` `]` with what it is said on the play button. HIDL.

### 15. A narrow window loses columns

Under a thousand wide the left column goes, then the right one, which is the right order but they just vanish. With the
list and the inspector able to slide away (`Slide`, the drawer that Hex's own sides are) and a key each, a laptop gets
the canvas and the timeline whole and the rest a key away. HIDL with what the engine has.

## What it'd look like

The three columns stay, they're just about the jobs now.

```
 ┌ moves ───────────┐┌ Blocky › uppercut › swing   blocky_moves.hor ─────────────────────┐┌ inspector ────────┐
 │ [+][⧉][✎][🗑]    ││ [hurt][traced][red][onion][arc][foe]              ▶ Fight!         ││ ▾ input           │
 │ filter, or A + B ││                                                                    ││   input  RT       │
 │ ▾ the game's  10 ││                      the fighter, big, on a floor                  ││   trigger pressed │
 │ ▸ movement     6 ││                        the opponent faint at range                 ││ ▾ hit             │
 │ ▾ attacks     14 ││                                                                    ││   damage 10       │
 │   punch_left  X  ││                                                                    ││   launch 40, 90   │
 │   uppercut   RT ●││ drag the red box's corner to size it                               ││   hitstun 22  (w) │
 │   sweep    ⚠ B   ││ ├─ frames ─┼1───5────10───15───20───25───30───35───40──┤           ││   + add           │
 │ ▸ specials     3 ││ │ you      │▓▓▓▓▓▓▓▓▓▓▓██▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒       │ 11·3·24    ││ ▾ swing (phase 1) │
 │                  ││ │ them     │           ███████████████████████▕+3    │ on hit     ││   [before][after] │
 │                  ││ │ phases   │ windup        │ swing              │      │           ││   animation ▣     │
 │                  ││ │ hit      │           H▬▬                     │       │           ││   frames 30       │
 │                  ││ │ cancel   │                 ░░░░░░░░▏         │       │           ││   hit 11, act. 3  │
 │                  ││ │ status   │ invulnerable ▬▬▬▬                 │       │           ││ ▸ red box         │
 │                  ││ │ art      │ ▣ ▣ ▣  ▣  ▣  ▣  ▣   ▣    ▣    ▣  │       │           ││                   │
 │                  ││ ⏮ ‹ ▶ › ⏭  L  1×      frame 12 of 40, swing, the hit                 ││ Data   Inputs     │
 └──────────────────┘└────────────────────────────────────────────────────────────────────┘└───────────────────┘
```

## What it needs from the engine and from Hex

Most of this is HIDL in the extension's own files. What isn't is small and general, every one of them something
another extension or a game's tool would use.

The engine. `DrawingPanel` sends which button (`e.button`, left or right, and the right button only pans when the
extension doesn't take it), a double click, and the wheel to a drawing that isn't navigable, for the timeline. `Table`
gets its columns, rows (text, colour, fill, tooltip) and `on_pressed`, `on_activated` exposed to HIDL, and `TreeView`
its items (text, icon, colour, detail, children, expanded) and `on_pressed`, `on_expanding`. Optionally a cursor a
drawing can ask for under the pointer. Tests in `Horizon.Tests/UI` for the exposed properties, a line each in the engine
CLAUDE.md.

Hex. `hex.menu(items, at)` for a context menu at the pointer, its items `{ label, icon, key, on_pressed }` with
separators and submenus (the engine's `ContextMenu` and `Menu` underneath, the same as Hex's own). `hex.ask({ title,
text, buttons })` and `hex.prompt({ title, value, placeholder })` for a question and a name, calling back with the
answer (`UIDialog`), or a window an extension can close itself, which would cover both. `hex.command(name, fn, key)` so
a command has a key and the palette shows it. The self test of extensions gets a check each.

HIDL only, the rest. The list's rows, the frame meter, the new timeline and its lanes and keys, the art lane and the
animation picker, the inspector and its add row, the canvas toolbar, the fit and the floor and the opponent, feedback
into notifications and the line under the canvas, problems by the field, the new move window, the boxes through Hex,
the keys, the empty states, the header, the playing, the drawers.

## In what order

Once the v2 work has landed in the extension, since it's in the same files (`timeline_view.hor`, `panels.hor`,
`move_view.hor`, `wiring.hor`, `columns.hor`, the combo window).

1. The quick ones, HIDL. The list as tall as the column with its actions in a row over it, the fighter into a header,
   the canvas toolbar with the overlays out of the Hitbox tab, the fit to the fighter with a floor, what the editor says
   going to notifications and a line under the canvas, loop and play once, and the phase buttons with words. A day, and
   it's most of "clunky" gone.
2. The frame meter on top of the timeline, with the numbers that changed lit. HIDL. This is the one that changes how it
   feels to tune a move.
3. The engine's drawing events (the button, double click, the wheel), then the timeline in lanes with a ruler, zoom and
   scroll, the menu on a right click and the keys of 10 that go with it. Needs `hex.menu` from Hex for the menu, the
   drag and the keys don't wait for it.
4. The inspector, one scrolling column that follows what's picked, set keys first and an add row. HIDL.
5. The art lane and the animation picker. HIDL.
6. Hex's `hex.ask`, `hex.prompt` and keys on commands, then the new move window, rename on F2 and delete that asks. The
   boxes file through Hex, one save and one undo.
7. The engine's `Table` and `TreeView` for HIDL, then the move list as a tree and the Data and Inputs tables wide.
8. The opponent at range, the empty states, the drawers for a narrow window, the key list behind `?`.

Each step gets its checks in `Testing/DawnScene.SelfTest.MoveEditor.cs` as it goes, a list that shows more than four
rows at 1600 by 900 being the first of them, and a screenshot of Blocky's uppercut before and after each, which is how
"clunky" gets checked rather than argued about.

### How far it's got

Steps 1 and 2 are in, looked at on Blocky's file at 1600 by 900. The list is nineteen rows there, the column is the
title, the four buttons (words while there's room), the name and the template, the filter and then the list down to the
bottom (`columns.hor`, `LEFT_REST` and `LIST_ROW`). The fighter is a dropdown in a header over the frame with
`› uppercut › swing   in blocky_moves.hor` after it, and under that a toolbar, the four overlays as buttons lit while
they're on, Save as an icon and Fight!. The frame is fitted to what's drawn on every frame of the move and the first of
the idle together, so Blocky is the same size from one move to the next and only a kick that reaches further zooms out,
and there's a floor where the idle's feet are with the launch arc landing on it. What the editor says goes to a line
under the frame, and anything refused (no name typed, a name taken, press delete again, the ghost of a box, hex.edit
saying no, no game) to a card in Hex's corner as well, `state.warn` against `state.say`. A file that doesn't run is only
said, the problems have it already and a card a keystroke would be a lot of cards.

The meter is the top two rows of the timeline, you (startup, active and recovery as runs with their counts on) and them
(on hit or on block, a click on the name swaps), the hit landing on the last active frame because that's how the game
counts on hit, the frames one of you is free and the other isn't shaded, a line where you both are and the advantage
after it. A number an edit changed reads `12 » 40` lit for two seconds. The meter counts cells, so its recovery is the
frames after the last active one and the Data tab's (the game's) is one more. To make room the cells went left aligned
after a gutter for the row names, the phase names and art frame numbers sit inside their blocks and cells now, and the
timeline is 104 tall.

What step 1 said that isn't done. Loop and play once want L and shift space, and Hex only hands a view the arrows,
home, end, space, delete, enter and the pages, so it waits for the keys of step 10 (or a button, which is cheap if
wanted sooner). The phase buttons still have no words, they want the inspector of step 4 to have the room. The line
under the frame says what happened and not yet what's under the pointer, the drawing does send `move` with nothing held,
so that can go in with step 3.

## What other editors do, and what's taken from them

Aseprite. Lanes with names in a gutter and frames across, tags as coloured spans over the frames with their ends
dragged, a cel as wide as its duration, onion skin as a toggle on the timeline, the comma and full stop for frames,
playing a range (its loop section). Taken for the timeline, the art lane and the keys.

Street Fighter 6's frame meter (and Fighter2D's own `FrameBarDisplay`, which is the in game version of the same).
Both fighters as rows of coloured frames, the advantage as a number where they part. Taken for the meter.

Rivals of Aether and Rivals of Aether II. A move is windows one after the other, each a length with the art spread over
it, and a hitbox is put in a window from a frame for so many (`HG_WINDOW_CREATION_FRAME`, `HG_LIFETIME` in the first,
a Start Frame and a Length in Frames on a Hitbox Window in the second). The same shape as Fighter2D's phases, and why
splitting a phase at a frame is the natural way to make one. The first game's workshop is code with a helper for
placing hitbox origins, the second's is forms in Unreal, neither has a timeline you drag, which is where this can do
better than both.

FrayTools (Fraymakers). An animation is a timeline of layers, sprites, collision boxes and scripts each on their own,
boxes drawn by hand or made off the picture. The boxes being a lane of the timeline rather than a tab is from there,
and the traced box to start from is what the extension already does.

MUGEN's Fighter Factory. Clsn1 and Clsn2 (the attack and the hurt boxes) drawn over the frame with its axis, a frame
list with each one's time, boxes nudged a pixel at a time from the keyboard, and a default box for every frame. Taken
for the nudging keys and the opponent at an axis, "this box on every active frame" is the default box already.

Smash modding (BrawlBox and BrawlCrate's subaction view, Ultimate's hitbox viewers and frame data sites). The model and
its hitboxes played a frame at a time next to the list of what happens on which frame, and the frame data put next to
the picture of each frame. The idea that the numbers and the picture are never on different screens.

Sources, for whoever wants to read further. Rivals of Aether's workshop hitbox reference
(https://rivalsofaether.com/?p=157), Rivals II's "Setting up an Attack" (https://rivals2.com/workshop/?p=550), the
McLeodGaming wiki on FrayTools (https://mcleodgaming.fandom.com/wiki/FrayTools), Aseprite's timeline docs
(https://www.aseprite.org/docs/timeline/). What's said of the SF6 meter, Fighter Factory and the Smash tools is from
what's commonly known of them and wasn't checked against a page, so look before borrowing anything exact.
