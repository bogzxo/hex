# Layouts, HIDL and the workspace

The full suite of editing tools of [Horizon](https://github.com/bogzxo/horizon), and the IDE of HIDL. VS Code's shape, a title bar of
its own with the menus in it, an activity bar and a side bar (the explorer, search, the outline, the designer's toolbox,
components and layers), tabs and breadcrumbs over the editor, the problems and the output under it, the properties on
the right and a status bar, icons throughout (Codicons). Ctrl+Shift+P is every command there is. Its own UI is a layout like the ones it edits,
drawn by the same code a game draws them with, so what is in the canvas is the real thing. Components out of a
palette, a tree of the layout, properties in sections that fold, the code it writes as it goes, undo, layers,
templates, the skin's art to pick from, and a self test that clicks through the lot.

A layout is a file that says `#define layout` at the top (Hex writes it, Code, Make this file a layout puts it in an
old one) and opens as its designer, the canvas the whole of the middle, the palette, its tree, its layers and its
properties round it. F7, or the two buttons over the middle, turns it into its code instead, with the explorer and
what is wrong with the file round it, and what was typed goes into the layout on the way back (code that doesn't
build keeps it on the code).
A save keeps the comments somebody wrote into the file, the ones over the header and over each declaration, and writes
them back over the same declaration, renamed or moved (a comment inside the braces of one is lost). A drag lands on
tenths of a pixel, so a position in the code reads 26.8 and not whatever the zoom made of the pointer.

It edits every other .hor as well. A file that doesn't build a layout (data, a language, a skin, a move set) opens as
code, coloured, numbered, checked as it is typed, with its outline on the left. Every tab has an x, and closing a
tab or the window with something unsaved asks first. Ctrl+F finds, Ctrl+H replaces, Ctrl+G goes to a line, Ctrl+P opens any file of
the repository by a few letters of its name, Ctrl+Shift+F finds in all of them, F8 checks all of them for what doesn't
parse, F5 runs a file and lists what it declares. Ctrl+/ comments lines, Alt+Up and Down move them, Ctrl+D duplicates.
What is typed is completed (Ctrl+Space, or as it is typed), a component's properties inside its braces, the game's
text keys after an @, the names of the whole workspace anywhere else. F12 goes to where something is declared,
Shift+F12 finds everywhere it is used. Every key is in Help, Keyboard Shortcuts, a list typed into to narrow it
down, enter on a command to press a new key for it and delete to take its key off, kept in `keys.hor` next to
`hex_settings.hor`, and the command palette and every chooser go up and down with the arrows.

HIDL is a language of values first, so the right side of a file of code is what it comes to (Values), run a moment after
it was last typed into on a thread of its own, the way the game that reads it would see it, as a tree. A click on a
value goes to where it was written, in whichever file that was, the comment over a key is its tooltip, and what went
wrong running it is underlined where it went wrong. Under the editor, next to the problems and the output, is the HIDL
console, a line typed in runs in what the file came to (`moves.jab.phases`, `len(keys(strings))`, a behaviour called
with a made up view), what it declares kept for the next lines, Up and Down for what was typed before.

The workspace is any number of folders (File, Open folder, Add folder), a tree in the explorer on the left
(Ctrl+Shift+E, Ctrl+Shift+O for the outline) with new, rename and delete on the right click, saved as a
.hex-workspace and opened again with File, Open workspace. The one that was open comes back when Hex starts.

## HIDL's language server

`Hex --lsp` is a language server for HIDL on stdin and stdout, for any editor that speaks the protocol. It knows a file the
way Dawn does, because it's the same code (`Hex.Core/Hidl`): what the parser didn't like, where running the file went
wrong and the lints of what it came to, completion (the language's words, the file's and the workspace's names, a kind
of component after `compositor.` and its properties inside its braces, the words a property like `anchor` takes),
hovers, where a name is declared (the file, the other open files, then the workspace) and the outline. Dawn doesn't
start it for its own files, it has all of that inside already.

