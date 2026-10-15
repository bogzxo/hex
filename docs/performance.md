# Where Hex's time and garbage go

A sweep of the UI framework, Hex and HIDL, October 2026, after the VS Code round. Measured, not guessed, on the
1600 x 900 self test and on a 2200 line HIDL file (four of Fighter2D's move lists end to end, 61 KB), Release unless it
says Debug. What was fixed in the sweep is marked done, the rest is the plan, the biggest win first in each part.

## The short of it

Hex is cheap where it was thought to be dear and dear where nobody looked. Laying out and painting the whole editor
every tick costs 0.16 ms, the stage with the layout in it 0.01 ms, the updates of every component 0.05 ms. That's the
end of retained mode as an idea, again. What costs is the code, every keystroke in a big file lexes it, colours it,
parses it and outlines it from scratch, each a couple of milliseconds and a couple of megabytes, and HIDL itself, which
boxes every number it touches.

## The UI framework

Per tick, the self test averaged over five thousand ticks, Release.

| | ms a tick | quads |
| --- | --- | --- |
| Hex's own UI, update (pointer, keys, tooltips, every component's Update) | 0.052 | |
| Hex's own UI, laid out and painted | 0.164 | 1671 |
| the stage (the layout being edited), updated | 0.003 | |
| the stage, laid out and painted | 0.012 | 55 |

Garbage sweeping the pointer over the editor, Debug, bytes a tick. The test fails at 256.

| | before | after |
| --- | --- | --- |
| the designer | 1494 | 132 |
| the code | 70 | 57 |
| the code of a layout | 588 | 96 |

- **Done.** `NumberBox` wrote its number out every tick (`ToString` and a compare), a properties panel of them was
  kilobytes a tick. It keeps the text it wrote and only writes again when the number changes.
- **Done.** `UILayoutOverlay.PaintHighlight`, the mark round what is selected in an editor, made a list of the
  component's parents every tick and wrote its label (`"StackPanel  320 x 40  pad 8 8 8 8"`) out every tick. The list
  is kept and the label is only written when the size, the padding or the gap changed. That was most of the designer's.
- **Done.** The compositor went through `Highlights` with a foreach over the interface, an enumerator object a tick for
  nothing in it. It looks at the count first.
- **Done.** `UITextDocument.Text` joined every line up each time it was asked for, 123 KB a time for 2200 lines, and it
  is asked for by the colours, the parse, the outline, the localisation check, the values, the crumbs and every step of
  the undo. It is joined once and kept until the lines change.
- **Done.** Hex's outline lexed the whole file on every move of the caret (the breadcrumbs) and every click on a value.
  It is kept against the text it was made of, which is the same string until the text changes.
- **Done in Hex.** The tabs of the panel under the editor were written in the colour of the panel, OUTPUT and HIDL were
  next to invisible. Not a performance thing, found on the way.

What is left, in the order it would pay.

1. **The colours of the code are worked out for the whole file on every keystroke**, 1.7 ms and 2.2 MB for 2200 lines,
   on the editor's thread, inside the tick. `HidlHighlighter` knows what a name is (a let, a parameter) from the whole
   file, so it can't simply go line by line. Plan, the lexing goes line by line with the lexer's state at the start of
   every line kept (inside a comment, inside a text), only the lines from the change on are lexed again until the state
   at a line start matches what it was, and the names are worked out off the editor's thread and laid over when they
   come back, a frame late at the worst. Spans as a struct array per line instead of `List<HidlSpan>[]`.
2. **The syntax check parses the whole file a moment after typing stops**, 2.4 ms and 3.7 MB, on the editor's thread.
   The values view now runs the file on a worker thread anyway, which parses it. The check should be that same parse,
   one parse a change and none on the editor's thread.
3. **Undo keeps the whole text for every step**, up to `MAX_UNDO` copies of a 61 KB file (120 KB each in UTF-16).
   Typing coalesces, everything else (a paste, a comment toggled, Alt+Up) is a copy. A step should be what changed, the
   span that was replaced and what was there.
4. **The layout of the editor is painted anew every tick it is awake.** 0.16 ms is nothing next to a frame, but it is
   1671 quads compared with the last tick's (`SameAs`) and copied out for the render thread when they differ, and with the
   caret blinking they differ twice a second all day. A component could say it is the same as last tick (nothing it
   paints changed) and have its quads copied from the last list instead of painted. Worth it for the code editor, which
   is most of those quads, not for the rest.
5. **Hidden documents keep their stage modules disabled, but the stage of the open layout is laid out and painted in
   the code view**, where the canvas isn't shown. Cheap (0.012 ms), still pointless, and in a big layout it isn't cheap.
6. **Rows of lists are made out of a template a row** (`layout.Instantiate`), which runs the template's HIDL, makes the
   components and their script objects (a `Func` and an `Action` for every property of every component, the garbage the
   code sweep shows when the outline is rebuilt). A list of six hundred values is six hundred little programs. The new
   `TreeView` paints its rows itself, as `ListBox` does, and the outline, the explorer, the problems and the components
   should all go over to it.

## Hex

- **Done.** Running a file to show what it comes to happens on a thread of its own, a moment after typing stops, with
  a newer change cancelling the run before it, so the editor never waits on a program. It was F5 on the editor's thread.
- **Done in HIDL.** A function that calls itself for ever overflowed the stack, which in .NET takes the process with it.
  Calls are limited to 256 deep and every loop and call can be cancelled, which is what the values, the console and
  extensions run with (a second each at the most).
- Idle Hex is 0.3% of a core (two or three frames a second, twenty ticks), measured in the last round and unchanged.
- **Plan.** The tooltip of the code editor and the completion both look up names by walking tokens, they should ask the
  values of the last run (the scope is right there) and the source map, see the HIDL plan.
- **Plan.** `RebuildTree` and `RebuildInspector` throw away every row and make them again for any change (a selection
  that moved, a name typed into). Once the rows are a `TreeView` that is a list of items handed over, no components made.

## HIDL

The interpreter, on the 2200 line file and on Fighter2D's dummy behaviours (called once a tick by the game).

| | ms | garbage |
| --- | --- | --- |
| lex | 1.6 | 1.9 MB |
| parse | 2.4 | 3.7 MB |
| run it | 3.1 | 4.2 MB |
| call a small behaviour once | 0.0015 | 378 B |

1. **Every value is a struct behind an interface**, `NumberValue`, `ObjectValue`, `Vector2Value` and the rest implement
   `IRuntimeValue`, so every number made is boxed, every add, every compare, every argument. A behaviour called 120 times a
   second by two fighters makes 90 KB a second of it. The fix is either the values as classes with the small ones cached
   (true, false, null, the integers up to a few hundred, which is most numbers in a game's data), or a tagged union struct
   (`HidlValue`, a double and an object) that the interpreter passes around and only boxes at the edges. The second is the
   faster by far and the bigger change, it touches every library function.
2. **Every scope is two dictionaries**, a call, every turn of a for, made as the scope is. `systemVariables` is empty in
   nearly every one of them. Made when something is first declared in it instead, half the garbage of a call goes.
   Further on, names resolved to slots when the program is parsed (which scope, which index), no dictionaries at all.
3. **Every token is a string**, the lexer cuts the text up into substrings, 1.9 MB for 61 KB. A token is a place in the
   text (it has its index and length already), the value only made for the names and texts the parser keeps.
4. **Nodes are structs boxed as `IStatement`**, which is the cost of a class without its benefits. They should be classes,
   sealed, and carry where they came from (see source spans in the HIDL plan).
5. **An include is parsed every time it is included**, a character file names the shared moves and its own, five
   characters is the shared file parsed five times. Parsed trees kept by path and the time the file was written.
