# Hex 3.0.0 Solstice

Hex3 is a project-health release: a new default look, a steadier base, and fewer rough edges. It adds very little.

## look

- Solstice is the default look for new settings: flush panes on a plum slate, orange for Dawn and violet for Dusk
- Solstice Day is its light version; Graphite and the other looks are still under Appearance · Profiles
- a line under the title bar fills as far as a download, an install or an update check has got, runs full while a build or the tests do, and turns red when one fails
- C# keeps ReSharper's colours in Solstice
- the title bar says what is open, then Hex3 and the release's name, then an extension's note, parted by dots, the last two smaller and quieter
- Welcome starts at the left, shows the release by name and number, and lists what is new with a button to each thing

## the panel under the editor

- its titles are always there; the panel is open under them or folded down to them
- the title of the page that is showing folds it, the same title again opens it, another title turns the page
- Ctrl+J folds and opens it too, where it used to take the titles away with it
- it no longer folds and opens by itself as files with and without problems come up
- it comes folded on new settings

## the terminal

- its minimap comes up by itself once more than two screens of rows have gone off the top, and goes when the history is cleared

## editing

- three presses on a line of code select the line, and dragging on from there selects whole lines
- buttons give far less under the pointer and when pressed; New File on Welcome gives like the buttons beside it

## fixes

- resize handles sit exactly on the line between a side and the editor, and between the editor and the panel, one handle to a line
- the panel under the editor can be dragged taller and shorter again; it could not be until it had been folded and opened by hand
- after Save As, a rename or a move, the language server follows the file to its new path, so problems keep appearing
- switching between Dawn and Dusk no longer pauses for a blocking memory collection each time
- pressing F7 on the Welcome page no longer leaves the side bar hidden
- an outline could be shown for the wrong file when two were asked for at once
- extension folder watchers are released when an extension is removed and when Hex closes
- settings are written to a temporary file and moved into place, so an interrupted save cannot leave half a file
- Compare with Saved and local history report a file that cannot be read instead of failing
- a terminal tab that is not on screen no longer asks for a new frame on every tick
- Dusk no longer pauses for a blocking memory collection three seconds after it starts
- the Editor skin row no longer reads as changed on untouched settings

## language

- the interface text is British English throughout: Colour Theme, Initialise Repository, Organise Imports, catalogue, dialogue

- a setting this Hex does not know is written back as it was, so an older Hex no longer drops a newer one's settings

## for people building Hex

- every setting is one row of one table (`HexSettings.Rows`) that reads it, writes it and gives its range; adding one is its field and its row
- what the editor does each tick is a named, ordered list (`HexTicks`); `HEX_PROFILE` now times each step by name
- what Dawn's and Dusk's windows have in common is one class (`HexFrame`): the field of light, the lights and sparks, the popups' glass, the bars' dressing, the line under the title bar and the sizes a window is drawn at

- `docs/hex3-plan.md` has the review behind this release and the order of the remaining work
- 161 self-test checks that failed on `development` pass again; the suites no longer leave a changed layout open for the next one

**Full Changelog**: https://github.com/bogzxo/hex/compare/v2.9.1...v3.0.0
