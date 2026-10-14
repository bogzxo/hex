# Hex

The layout editor of [Horizon](https://github.com/bogzxo/horizon). Its own UI is a layout like the ones it edits,
drawn by the same code a game draws them with, so what is in the canvas is the real thing. Components out of a
palette, a tree of the layout, properties in sections that fold, the code it writes as it goes, undo, layers,
templates, the skin's art to pick from, and a self test that clicks through the lot.

A layout is a file that says `#define layout` at the top (Hex writes it, Code, Make this file a layout puts it in an
old one) and opens as its designer, the canvas the whole of the middle, the palette, its tree, its layers and its
properties round it. F7, or the two buttons over the middle, turns it into its code instead, with the explorer and
what is wrong with the file round it, and what was typed goes into the layout on the way back (code that doesn't
build keeps it on the code).

It edits every other .hor as well. A file that doesn't build a layout (data, a language, a skin, a move set) opens as
code, coloured, numbered, checked as it is typed, with its outline on the left. Every tab has an x, and closing a
tab or the window with something unsaved asks first. Ctrl+F finds, Ctrl+H replaces, Ctrl+G goes to a line, Ctrl+P opens any file of
the repository by a few letters of its name, Ctrl+Shift+F finds in all of them, F8 checks all of them for what doesn't
parse, F5 runs a file and lists what it declares. Ctrl+/ comments lines, Alt+Up and Down move them, Ctrl+D duplicates.
What is typed is completed (Ctrl+Space, or as it is typed), a component's properties inside its braces, the game's
text keys after an @, the names of the whole workspace anywhere else. F12 goes to where something is declared,
Shift+F12 finds everywhere it is used.

The workspace is any number of folders (File, Open folder, Add folder), a tree in the explorer on the left
(Ctrl+Shift+E, Ctrl+Shift+O for the outline) with new, rename and delete on the right click, saved as a
.hex-workspace and opened again with File, Open workspace. The one that was open comes back when Hex starts.

```
git clone --recursive https://github.com/bogzxo/hex
dotnet build Hex.sln
Horizon.Hex/bin/Debug/net10.0/Horizon.Hex --selftest --exit --size 1600x900
```

The engine is a submodule at `horizon/` (on its `development`), with the language inside it. Work on
`development`, `master` is what's released.
