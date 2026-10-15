# Extensions, the plan for giving them a UI, and the hitbox editor as the first that has one

An extension is HIDL that drives Hex (see the README). So far it can say things, put a few words in the title bar, add a
command and hear about files opened and saved. That's enough for Hello Dolly and nothing that does real work. This is
the plan for what an extension can show and touch, and for the first big one, Fighter2D's hitbox editor, which is a
C# app of its own now (`Fighter2D/HitboxEditor`, 3000 lines) and moves into Hex as an extension written in HIDL.

## What an extension can make

Every bit of UI an extension makes is a layout, a `.hor` file in its folder, made in Hex's own designer like everything
else of Hex, the same components a game has. The extension finds the parts by name and gives them what to do, the way
Hex's own code does with its layouts. Where the layout goes is the extension's choice.

| where | made with | is |
| --- | --- | --- |
| a view of a file | `hex.view({ id, title, icon, layout, when, open })` | the middle of Hex, beside a file's code and table, a button on the tab strip for it, F7 goes round them. `when` is a `#define` a file has to have for the view to be offered, and the view comes up by itself when such a file is opened |
| a page of the panel | `hex.panel({ id, title, icon, layout, open })` | a tab of the panel under the editor, next to the problems, the output and the console |
| a window | `hex.window({ title, layout, width, height })` | a little window over Hex (a WindowPanel), dragged about, closed with its x |

What `open` is handed is the made layout, `ui`, with `ui.get("name")` for a part (a component, which HIDL sees as an
object of its properties, `text`, `items`, `value`, `on_pressed` and the rest, the way a layout's own code sees it),
`ui.on_tick(func(dt))` for something that has to happen as time goes by (playing a move), `ui.on_key(func(key))` for the
keys while the view is the one on screen, and `ui.file` for the file a view is of.

## Drawing

A layout has the components a game has, and one more for an extension that has to draw, `drawing`, a component the
extension draws into. What is drawn is kept until it is drawn again (it isn't asked for every frame, an extension isn't
run every frame), in the drawing's own units, the middle of it at 0 0 and up being up, zoomed and panned by the
drawing (`zoom`, `pan`, the wheel and the middle button do both unless the extension says no).

```
let canvas = ui.get("canvas");
canvas.clear();
canvas.sprite(art, "jab", 3, vec(-64, -64), vec(64, 64));   // a frame of a sprite into a box
canvas.rect(vec(0, 0), vec(20, 30), vec(1, 0.2, 0.2, 0.35));   // a filled box
canvas.frame(vec(0, 0), vec(20, 30), 2, vec(1, 0.2, 0.2, 1));  // its edge, so thick
canvas.line(vec(-64, 0), vec(64, 0), 1, vec(1, 1, 1, 0.2));
canvas.text("hit", vec(0, 34), 0.12, vec(1, 1, 1, 1));
canvas.on_pointer = func(e) { ... };    // e.at (in the drawing's units), e.kind ("down", "drag", "up", "move"), e.button
```

## Reading and writing

| | |
| --- | --- |
| `hex.read(path)` | the text of a file, what it says in Hex if it is open (unsaved and all), from the disk otherwise |
| `hex.write(path, text)` | writes a file, an open one changed in its editor (one step to undo) and the disk left for its save |
| `hex.exists(path)` | whether there is a file or a folder |
| `hex.find_up(from, name)` | the nearest folder at or over a path that has `name` in it, how a file finds the root of its game |
| `hex.load(path)` | runs a file of HIDL and hands back what it declared, an object, includes next to it |
| `hex.document()` | the file on screen, `{ path, name, text, values, defines }`, `values` what it came to the last time it ran |
| `hex.sprite(path)` | a picture made of frames, an Aseprite file (its tags its animations) or a sheet with its definition |

A path is from the extension's folder unless it is rooted, `hex.read` and the rest take a file's own path as it is.

A sprite is `{ width, height, animations }` (the frames each tag has), drawn by a `drawing`, and looked into with
`art.mask(animation, frame)`, which is what is drawn on a frame as a grid of solid and not (alpha over a threshold),
`mask.width`, `mask.height`, `mask.columns()` (how many solid pixels every column has), `mask.bounds()` and
`mask.bounds_in(x0, y0, x1, y1)` (the box round what is solid, in all of it or a part, with how many pixels). That is
what Fighter2D's box tracer is made of, and an extension can trace in HIDL without touching a pixel itself.

## Calling .NET

Some of what an extension wants is written already, in C#, in a game or a library. HIDL gets a bridge to .NET of its own
(`Horizon.HIDL.Interop`, any host can switch it on, Hex does for extensions), so an extension can use it instead of
writing it again.

```
let fight = dotnet.load("../../Fighter2D/bin/x64/Debug/net10.0/Fighter2D.dll");
let rules = fight.type("Fighter2D.Combat.CombatRules");
let floor = rules.MIN_HITSTUN;                         // a constant, a field, a property
let math = dotnet.type("System.Math");
let biggest = math.Max(3, 7);                          // a static method, the overload picked by the arguments
let builder = dotnet.new("System.Text.StringBuilder"); // an object, its members called the same way
builder.Append("hi");
```

What goes across. Numbers are whatever number the method wants (an int, a float, a double, an enum by its number or
its name), texts are strings, booleans are booleans, a list is an array or a list of what the method wants, an object
of HIDL is a dictionary, null is null, and a vector is a `Vector2`, `Vector3` or `Vector4`. What comes back is the same the
other way, and anything else is the .NET object itself, its members reached with a dot, internal ones included (it's
your own code). A method with `out` parameters hands back an object, what it returned as `returned` and every out parameter by its name.

Two limits. A native AOT build (a published Hex) can't load assemblies while it runs, `dotnet.load` says so and the
rest of the extension carries on. And a method that takes a `Span` can't be called through reflection, which is why the
hitbox editor's tracer is written in HIDL on `mask` rather than called in Fighter2D, the tracer takes the pixels as a
span. An assembly is loaded with what it depends on looked for next to it, and what Hex has already (Horizon itself,
.NET) shared rather than loaded twice.

All of it runs on Hex's thread, a second at the most a call, as an extension does now. A view's handlers are the
extension's functions, called the same way, and one that throws switches the extension off and says why.

## The hitbox editor, as an extension

This was the first plan, the red boxes only. It grew into the move editor (`move-editor.md`), which is in
`Assets/hex/extensions/move_editor/` now and does everything below and the rest of a move. Kept for how it got there.

`Assets/hex/extensions/hitboxes/`, `extension.hor` and its layouts. A move file says `#define fight_move` (any of
Fighter2D's `*_moves.hor`) and the Hitboxes view is offered on it and opens by itself.

What it does, everything the C# one does and what Hex makes easier.

- **Finds its game and its fighters.** Up from the move file to the folder with `Assets/data/characters.hor`, every
  character whose moves name this file, a dropdown of them (Cammy's own file has one, the shared mannequin moves have
  three), the art of the chosen one out of its `.ase`, its frame rate.
- **The moves of the file** down the left, the ones that hit or all of them, a star on the changed ones, "(drawn)" on
  the ones with a red box.
- **The frame** in the middle, as big as it goes, the hurtbox (blue, the box round what is drawn), the traced hitbox
  (yellow, the limb sticking out in front of the body, Fighter2D's tracer written again in HIDL on `mask`), the red box
  drawn by hand, solid on its active frames and a ghost on the others. Drag inside it to move it, a corner to size it,
  on nothing to draw one, snapped to the pixels of the art. The wheel zooms, the middle button pans.
- **The timeline** along the bottom, a cell a frame coloured startup, active, recovery, the phases over it and the hit
  frame marked, a click goes to a frame. Left and right step, Home and End, space plays at the character's frame rate.
- **The frame data**, startup, active, recovery, hitstun, on hit, on block, the way the game works it out
  (`MoveFrameData`, `CombatRules`, in HIDL), following every change.
- **The red box** by its numbers (in pixels of the frame), how many frames it's out for, from the trace, this box on
  every active frame, delete it. Ctrl+Z, saving into `Assets/data/hitboxes/<character>.hor` the way the game reads it.
- **The move itself** isn't edited in the extension. It's the file that is open, the code, the values and the table
  are right there, F7 goes back to them and the view follows what is typed. The C# editor had two pages of fields for
  it, Hex has the whole file.

## Settings, and Fight!

The extension says what it can be told in its extension object (`settings`, the exe of the game, the map, the opponent,
what the dummy does) and has its own layout for its tab of the settings, `hitbox_settings.hor`. The exe is a path
absolute or from the game's folder (the one with `Assets/data/characters.hor` in it), left empty it's looked for in the
game's `bin` folder, the newest kind of build first. The tab says whether it's there, the maps and the opponents are
the lists of the last game a move file was opened in (`last_game`, a setting the extension keeps without showing it).

Fight! on the view saves the red boxes and starts the game the way the C# editor did, `--content` the game's folder,
`--map`, `--character` the fighter on screen, `--opponent`, `--dummy`, `--windowed`, through `hex.start`.

## In what order

1. HIDL, the bridge to .NET, `HIDLInterop`, tested on .NET's own types, and switched on for Hex's extensions.
2. Hex, `drawing` in the engine (it's general, a game can draw into one too), views, panel pages and windows from
   layouts, `ui.get`, ticks and keys for a view, `#define` for when.
3. Hex, the reading and writing, `hex.document`, `hex.sprite` and masks.
4. The hitbox extension, its layout, finding the fighters, the moves, the frame with its boxes, the timeline and
   playing, the frame data, editing and saving the red boxes.
5. Fighter2D's move files get `#define fight_move`, which is bogz's to put in, the extension works on any file that says it.
