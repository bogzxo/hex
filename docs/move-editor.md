# The move editor, the hitbox editor grown into all of a move

The Hitboxes extension edits one thing about a move, the red box. Everything else (the input, the damage, the phases,
the reroutes) is edited in the code of the file, or the values, or the table, which is fine for whoever wrote the format
and slow for anybody tuning a fighter. This is the plan for turning the extension into the editor of everything a move
in Fighter2D can say, without it ever getting between bogz and the file. The file stays the truth, written by hand or by
the editor, comments and spacing kept, and the editor is a view of it like the code and the table are.

Built, in `Assets/hex/extensions/move_editor/`, a part a file (the comment on top of `extension.hor` says which does
what). What isn't, yet, is the shapes (HIDL hasn't got them) and `--practice` with the parity test in Fighter2D. The
fight does reload what Hex saves by itself now, `Content/ContentWatcher.cs` there, for a game started with `--content`,
which Fight! does. The C# hitbox editor is gone. The plan as it was written follows.

## What a move is, all of it

Out of `Moves/MoveFileReader.cs`, which is the only thing that counts, and the comment on top of `fighting_moves.hor`,
which says what each means. Anything the editor offers has to read back through `MoveFileReader.ReadOne` the same.

The move.

| key | kind | when it isn't there |
| --- | --- | --- |
| `input` | buttons, `"A + B"` both, `"DPadLeft \| DPadRight"` or a list either | none, only rerouted to |
| `trigger` | `held`, `pressed`, `double_tap` | `held` |
| `priority` | whole number, lowest matched first | -1 |
| `stances` | flags of `standing`, `crouching`, `jumping`, `falling` | `standing` |
| `damage` | whole number of 100 | 0 |
| `knockback` | `vec(away, up)` | none |
| `hitstun`, `blockstun` | frames of the move's animation | worked out (CombatRules) |
| `level` | `mid`, `low`, `overhead` | `mid` |
| `knockdown` | flag | no |
| `meter_cost` | whole number of 100 | 0 |
| `cancellable`, `steering` | flags | yes |
| `turning` | flag | what `steering` is |
| `walk_scale` | 0 to 4 | 1 |
| `status` | `attacking`, `blocking`, `invulnerable` | none |
| `stance`, `stance_after` | a stance | none |
| `warns` | flag | yes when it does damage |
| `repeat` | a condition | none |
| `stance_reroutes`, `finish_reroutes` | a move id by stance | none |
| `phases` | the phases, in order | none |

A phase.

| key | kind | when it isn't there |
| --- | --- | --- |
| `animation` | a tag of the character's .ase | the one before carries on |
| `start` | frame of the animation | 0 |
| `hold` | frames of the move a frame of art is held for | 1 with an animation, the one before's without |
| `frames` | how long the phase is | what's left of the animation |
| `hit`, `cancel`, `commit` | frame of the phase | none |
| `status`, `status_frame` | a status and when it starts | none, 0 |
| `loop_while`, `until` | a condition | none |
| `impulse` | `vec(facing, up)` | none |
| `effect`, `effect_frames` | `jump`, `roll`, `haze` and how long | none, 1 |

The conditions are `none`, `held`, `hitstun`, `jumping`, `falling`, `always`. The old names `stun`, `interruptible`,
`interrupt` and `lock` still read (as `hitstun`, `cancellable`, `cancel`, `commit`), the editor writes the new ones and
offers to rename the old ones. Eight ids mean something to the game (`idle`, `hit_stun`, `knocked_out`, `heavy_land`,
`knocked_down`, `get_up`, `victory`, `run`, see `MoveIds.cs`) and `idle` has to be there. The red boxes live apart, in
`Assets/data/hitboxes/<character>.hor`, a box per active frame.

A character takes its moves from a list of files (`moves` in `characters.hor`), a later file's move replacing the earlier
one of the same id. The Male is `mannequin_moves.hor` with `the_male_moves.hor` over it. So which move a character really
has, and where it is written, is a question the editor has to answer every time, see the move list below.

## One description of a move, used by everything

The C# hitbox editor had the keys twice over, `MoveFields.cs` for its fields and the comment in `fighting_moves.hor` for
people. The extension mustn't be a third copy. A file of the extension, `move_schema.hor`, describes every key once, its
kind, its choices, what it is when it isn't there (or that the game works it out) and what it means, the text being
cut from the comment in `fighting_moves.hor`. Everything that follows (the fields, the checks, the hover in the code)
reads that.

When HIDL has shapes (`Horizon.HIDL/docs/shapes.md`) the schema becomes `shape FightMove` and `shape FightPhase`, and
Hex's own checks, completion and hover get the move format for free in the code view too. The extension goes on
reading the same file, a shape is data to it. Until then it is a plain object with the same fields, so the move to
shapes is a change of syntax and not of plan.

## What Hex has to give an extension for it

The extension edits the file it is a view of. That needs Hex to grow, the way it grew `hex.view` and `hex.sprite` for
the hitbox editor. Every one of these is general, a layout extension or a localisation extension would want the same.

### Edits by path, the file kept as it was written

`hex.edit(path, value)` changes the value at a path of what the file came to (`["moves", "kick_left", "damage"]`) where
it is written, in the code, the way the values panel edits a value in place now. `hex.insert(path, key, value, after)`
adds a key to an object (after another key, or at the end), `hex.remove(path)` takes a key out (and its comma, and a
comment that was on its line), `hex.rename(path, key)` gives it another name, `hex.reorder(path, keys)` puts the keys
of an object in another order (phases run in the order they're written, so that's how a phase moves). `hex.edits([...])`
does a batch of them as one step to undo.

What it takes in Hex is the source map knowing a bit more. It knows where a key and its value are written, it needs
where an object's braces are, so a key can go in before the closing one at the right indentation, and where a whole entry
is with its comma. `HIDLSourceMap` grows `TryGetObject` (the span of the braces) and the entry span of a key. The
writing is `HIDLWriter.Write` for the value, the indentation taken from the entry above, a new object written over lines
the way the file writes them. An edit of something worked out (`damage: base * 2`, a spread, an include) is refused with
why, the same as the values panel does it, and the editor shows the field read only with "worked out in the code".

Every edit goes through the code editor's document, so it is one step of the file's undo and the view, the code and the
values all follow. The extension's own undo (the red boxes now) becomes one history, an entry of which is either one of
its own changes or a file edit, so Ctrl+Z in the view takes back whatever was done last whichever kind it was.
`hex.undo_file()` and `hex.redo_file()` are what it calls for the second.

Edits to a file that isn't open (the shared mannequin file, editing The Male) open it in a tab that isn't brought to
the front, so it can be saved and undone like any other, and the tab says it has changes.

### Problems where they are

`hex.problems(file, [{ path or line, message, warning }])` puts the extension's checks in the problems panel and
underlines them in the code, by a path of the values (placed with the source map) or a line. Replaced as a whole every
time it is called for that file, the extension's checks are run on every `on_run`.

### A form out of a description

Fields are most of this editor, a few dozen of them, and writing a layout row for each by hand is how the C# editor got
two pages of code for them. The engine gets a `form` component (`compositor.form`), a stack of rows made out of a list of
fields (`{ key, title, kind, choices, default, unset, help, read_only }`), the kinds being number, whole, flag, text,
choice, flags (a toggle a choice), vec (two or more number boxes), and list of texts. A row shows the value, or what it is
when it's left out greyed with the `unset` text ("worked out from the startup"), and has a button that takes the key out
(back to the default). `on_changed(key, value)` and `on_cleared(key)` are what it says. `values` sets them all at once
without telling anybody. Sections (`collapsible`) by a `group` on the field.

It belongs in the engine because it is what an options screen or a debug panel of a game wants as well, and Hex's
inspector is the same thing made by hand in C#, which can move onto it afterwards (not part of this).

### Smaller bits

`hex.watch(path)` tells an extension (`on_file_changed`) when a file it read changes on the disk, the .ase being redrawn
in Aseprite. `UISprite.ForgetAll` exists for the art, the extension calls it from there. `hex.clipboard` and
`hex.set_clipboard` for copying a move or a phase between files. `hex.open(path, path_in_values)` opens a file at a value,
for "where is this move written".

## The editor

### Laid out

The three columns stay, they each grow.

On the left the fighter and the moves, now all of the character's moves (merged from its files) in a tree, grouped by
what they are (attacks, specials, movement, the eight the game names, the rest) with a filter box. A move shows where it
is written and whether it is replaced by a later file (struck through in the earlier one, a link to the later one).
Buttons for a new move (out of a template), duplicate, rename and delete under it. A move written in another file than
the one open is edited there, the editor says so over the fields with a link.

In the middle the frame and the timeline, as now, and the timeline grows to edit the phases (below).

On the right tabs, Move, Phase, Hitbox, Data. Move is the form of the move's keys in sections (input, hit, movement,
status, reroutes), Phase the form of the selected phase, Hitbox the red box (what the column is now), Data the frame
data. The column still drags wider and gives the room back.

### The timeline, where the phases are edited

Every phase is a block along the timeline, as long as it lasts, its name and animation on it. Dragging the end of a block
changes its `frames` (snapped to whole frames, with the length the art gives it marked, so `frames` can be taken out
again when it matches). The hit, cancel and commit frames are markers above the cells, dragged along, put down with a
right click on a cell ("the hit comes out here", "cancellable from here", "committed from here") and taken off the same
way. The status of a phase and its `status_frame` are a band under the cells. A phase that loops (`loop_while`) or waits
(`until`) has its condition written on it and the cells after its least length drawn hatched, they might not happen.
`impulse` and `effect` are small marks at the start of the phase.

Right click on a block for a phase before or after, duplicate, delete, and a drag of a block reorders the phases. A
double click names it. Under the timeline a strip of the art of the phase, every frame of its animation from `start`, so
the animation and `start` are picked by clicking the art, not by remembering tag names. The animation picker lists every
tag of the .ase with its first frame.

Playing is the same as now, plus slowed (a half, a quarter) and the loop of a phase that loops shown going round twice.

### Data, everything the game works out

What `MoveFrameData` works out (startup, active, recovery, hitstun, blockstun, on hit, on block) is there already. The
rest of `CombatRules` comes in the same way, mirrored in HIDL with the numbers named the same as in the C#. The counter
hit (hitstun and damage scaled), the hitstop of a hit, a heavy hit, a counter and a block, the chip damage, the meter the
move builds on hit, on block and on a whiff, the damage of it as the second, third and fifth hit of a combo, and juggle
(when the knockback launches, how much hitstun it adds each time). A line per number of what it comes out of, so changing
`damage` shows which numbers moved.

A table of the character's moves with their frame data side by side (the Data tab of the move list), sortable, the
plus and minus ones coloured, which is how a fighter gets balanced. Copied out as markdown for a wiki or a patch note.

The knockback and the impulse drawn over the frame, the path of the one being hit and the shove of the player, from the
numbers in the comment of `fighting_moves.hor` (100 away is 6 units, 1100 up is 30, 2000 is 75) fitted to a curve, and
checked against the game (below). Rough is fine, it's for seeing that a launcher launches and a sweep doesn't, the game
is where the exact numbers are.

### Inputs and how a character's moves fit together

The Inputs tab of the move list is a grid, the buttons down the side and the stances across, and in a cell the moves an
input starts in that stance in the order the game matches them (priority, then the trigger, then the meter it costs, the
next one being what comes out without the meter). Two moves that can never both come out (the same input, stance and
trigger with the one with the lower priority always taking it) are flagged, so is an input of a stance nothing answers.

A graph of the reroutes, each move a box, `stance_reroutes`, `finish_reroutes` and `repeat` arrows between them, the
eight the game names marked, so a reroute to a move that isn't there or a move nothing ever reaches shows up.

### Checks

Run on every `on_run`, on every file of the character, into the problems panel with `hex.problems`. What
`MoveFileReader` would throw for (an unknown key, a word that isn't one of the choices, an effect that isn't one) as
errors, so a file that wouldn't load in the game is red in Hex first, and the old key names as warnings with the rename on
offer. Then what loads but is wrong. An animation that isn't a tag of the .ase. A hit, cancel or commit frame past the end
of its phase. Commit before cancel. A hit frame in a move that does no damage, or damage without a hit frame. A move with
an input and no priority. A reroute to a move the character hasn't got. `idle` missing. A red box for a move that doesn't
hit, or whose active frames are more than the move has after its hit frame.

### Trying it in the game

Fight! as it is now (it saves and starts the game on the fighter and the map of the settings). Then the game itself takes
some of this further, which is Fighter2D's to decide and bogz's call, listed so it's in one place. The game watching its
`--content` folder and reloading the data when a file of it changes (the pause menu's reload made automatic), so a change
saved in Hex is in the fight a moment later without restarting. A `--practice move` argument, the dummy doing the move over
and over, or standing there taking it, with the frame data the game measured written to a file the editor reads back and
shows next to its own (which is the check that the HIDL copy of `CombatRules` is right, and of the knockback curve).

### New moves

Templates in the extension's folder, a jab, a launcher, a sweep, an overhead, a special with a meter cost, a dash, a
block. A new move is a copy of one with a free id, put after the move selected (or at the end), the animation set to the
one that's selected in the art strip. Duplicate copies the one selected. Rename changes the id and every reroute that
names it, in every file of the character, as one step to undo. Delete asks when anything reroutes to it.

## How it is built inside

The extension is one file now, about a thousand lines, which is as far as one file should go. It becomes a folder of
includes, each a part (`timeline.hor`, `frame_data.hor`, `tracer.hor`, `checks.hor`, `inputs.hor`, `files.hor`,
`schema.hor`), `extension.hor` being what starts it and wires the layouts. The state of a view is one object, as now,
the parts taking it as their first argument.

Keeping it quick. The merged move list and the checks of a character are worked out on `on_run`, not every tick. The
frame data of every move for the table is cached by the move's text. The timeline and the frame are drawn again when
something changed, never every tick (playing excepted). The form isn't built again when the move is, its `values` are set.

Tests. The self test of Hex grows a fixture of two move files of one character (shared and own) and checks every edit
through the form writes what it should where it should, comments kept, undone in one step, and that the file reads back
through the game's own rules. That last one is a check in Fighter2D's repo, a test that runs every move of every
character through `MoveFileReader` and through the extension's `frame_data.hor` (a HIDL runtime in the test, the same
way the extension runs) and fails on a difference, so the two can't drift apart quietly.

## In what order

1. Hex. Edits by path (`hex.edit`, `insert`, `remove`, `rename`, `reorder`, `edits`), the source map's object and entry
   spans, one history for an extension's view. The self test of it against a file with comments in every awkward place.
2. The engine. The `form` component, tested in `Horizon.Tests` (the rows, defaults, clearing) and shown in an example.
3. The extension split into its parts and the schema written, nothing new on screen yet.
4. The Move and Phase tabs on the form, every key of a move and a phase editable. This is the point where everything a
   move can say is edited in the view, the rest makes it nicer.
5. The timeline editing the phases, the art strip and the animation picker.
6. `hex.problems` and the checks.
7. The move list as the character's merged moves, new, duplicate, rename, delete, templates.
8. The rest of `CombatRules` in the Data tab, the table of a character's moves, the knockback drawn.
9. The Inputs grid and the reroute graph.
10. Shapes, when HIDL has them, the schema becomes `shape FightMove` and Hex's code view gets it too.
11. The game's side, if bogz wants it, reloading on a change and `--practice`, and the parity test.
