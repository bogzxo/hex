# Hex

The full suite of editong tools of [Horizon](https://github.com/bogzxo/horizon), and the IDE of HIDL. VS Code's shape, a title bar of
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

## Extensions

An extension is HIDL that drives Hex, a folder with an `extension.hor` in it. The ones that come with Hex are in
`Assets/hex/extensions`, your own go in an `extensions` folder next to Hex. They are switched on and off on their page
of the settings (the puzzle piece on the activity bar), and one that goes wrong is switched off for the rest of the run
and puts a card up saying why. Hello Dolly, the greeting in the title bar, is the first of them.

```
let extension = { name: "Hello Dolly", description: "...", version: "1.0.0", author: "bogz" };
let welcome_greetings = ["Hello, Dolly!", "..."];

// Called once Hex is up, with what an extension may do to it
func on_start(hex) {
    hex.title_note(math.pick(welcome_greetings));
    hex.command("Another greeting", func(hex) {
        hex.title_note(math.pick(welcome_greetings));
    });
}
```

What `hex` has for now. `hex.title_note(text)` puts a few words after Hex in the title bar, in brackets. `hex.say(text)`
says something in the status bar, `hex.notify(text)` puts a card up in the corner (`hex.notify(text, true)` an error
that stays). `hex.command(name, func(hex) { ... })` puts a command in the command palette under the extension's name,
and `hex.command(name, func(hex) { ... }, "Ctrl+Alt+J")` puts it on that key as well, the palette saying which.
`hex.file()` is the path of the file on screen. `hex.version`, `hex.name` and `hex.folder` are Hex's version, the
extension's name and its folder. Besides `on_start(hex)`, an extension hears about `on_open(hex, file)` and
`on_save(hex, file)`, and `on_change(hex, file)` a moment after a file is typed into. What it prints goes in the output
with its name in front. An extension runs in a runtime of its own, a second at the most for anything it does, and it is
read again by itself when a `.hor` of its folder changes, its views back on the files that had them up, so writing one
is save and look rather than starting Hex again.

It can ask things. `hex.menu([{ label, icon, key, on_pressed, enabled }, "-", { label, items: [...] }])` puts a menu up at
the pointer, a `"-"` a rule and `items` a menu inside it, `hex.ask({ title, text, buttons: ["Yes", "No"] }, func(hex,
pressed) { ... })` a question with buttons, and `hex.prompt({ title, text, value, placeholder, button }, func(hex, typed) {
... })` a line of text.

It can have a UI too, a view of a file (`hex.view`, offered on the files that say its `#define` and opened on them by
itself, `left: false` or `right: false` in it putting Hex's sides away while it's up, `hex.show_side("right", false)`
doing it for good), a page of the panel (`hex.panel`) or a little window (`hex.window`), each a layout of its folder, and it can read
and write files (`hex.read`, `hex.write`, `hex.load`, `hex.find_up`), draw sprites into a `drawing` (`hex.sprite`), ask
for a file or a folder with Hex's own dialog (`hex.pick_file({ title, folder, filter: ["Pictures", "*.png;*.jpg"] },
func(path) { ... })`, `hex.pick_folder(func(path) { ... })`) and start a program (`hex.start(path, [arguments])`). `hex.try_load(path)` is `hex.load` that can't go wrong, `{ values,
error }`, for a file that may be half typed.

It can change a file by the paths of its values, written where they are in the text with the file's comments and spacing
kept, one step of the file's undo a call (null when it went, why it didn't as a text). `hex.edit(file, ["moves", "jab",
"damage"], 8)`, `hex.insert(file, path, key, value, after)`, `hex.remove(file, path)`, `hex.rename(file, path, key)`,
`hex.reorder(file, path, keys)`, `hex.edits(file, [{ edit: path, value }, { insert: path, key, value }, ...])` for several
as one step, and `hex.undo_file(file)` and `hex.redo_file(file)`. A file that isn't open is opened behind the one on screen.
`hex.problems(file, [{ path or line, message, warning }])` puts what it finds wrong in Hex's problems, `hex.watch(path)`
has it told `on_file_changed(hex, path)`, `hex.open(file, path)` opens a file at a value, `hex.copy(text)` puts text on
the clipboard. See `docs/extensions.md`, the move editor (Fighter2D's moves, `extensions/move_editor`) is all of it at
once.

What an extension can be told goes in its extension object, `settings: { game: { title, about, kind, default } }`, the
kind being text, path, number or flag. It gets a tab of its own in the settings (made from the template the settings'
pages name in `settings.hor`, `extension_tab.hor`), a row for each of them there, or its own layout for the tab if it says
`settings_layout: "file.hor"`, which Hex hands to `on_settings(hex, ui)` once it's made. `hex.setting(name)` is what it
was told (or its default), `hex.set_setting(name, value)` changes it, and Hex keeps the lot in `hex_settings.hor` under
`extension_settings`. A change made on its tab is told to the extension, `on_setting(hex, name, value)`.

## HIDL's language server

`Hex --lsp` is a language server for HIDL on stdin and stdout, for any editor that speaks the protocol. It knows a file the
way Dawn does, because it's the same code (`Hex.Core/Hidl`): what the parser didn't like, where running the file went
wrong and the lints of what it came to, completion (the language's words, the file's and the workspace's names, a kind
of component after `compositor.` and its properties inside its braces, the words a property like `anchor` takes),
hovers, where a name is declared (the file, the other open files, then the workspace) and the outline. Dawn doesn't
start it for its own files, it has all of that inside already.

## Finding your way round the code

Hex is one scene, `DawnScene`, a partial class in parts by what they're about, and a few classes of its own next to them.
`Shell/DawnScene.cs` is where it starts (the constructor finds the parts of `Assets/hex/editor.hor` by name, and
`editor.hor` is the skeleton that includes the parts in `Assets/hex/shell/`, the activity bar, the side bar, the tabs,
the panel and the properties, each a layout of its own that opens on its own in Hex) and its
`UpdateState` is a tick of the whole editor in a handful of steps, read that first. Every part of the class says what it is in
a comment at its top.

- `Shell/` the frame round everything, the size of things (Chrome), the menus and keys, the command palette, the side
  bar and its trees, the tabs and breadcrumbs, the panel under the editor, the status bar, which view is on screen
  (Views), the welcome, Hex's own texts, and `HexProfiler`.
- `Designer/` a layout's designer, the canvas and the pointer on it, the handles, selecting, editing and arranging, the
  palette of components and what's in it (Catalog), the inspector, the tree and layers (Lists).
- `Documents/` an open file (`HexDocument`, a layout with its history and code, or text), opening, saving and closing,
  the recent files.
- `Code/` a file as code, its values, the console, completion, hints, finding, the outline, the lints and the table.
- `Editing/` changing a .hor file's text by what's in it (a key set, renamed, taken out) and keeping the rest as written,
  which the extensions' `hex.edit` and friends are.
- `Extensions/` loading extensions and everything `hex` is to them, their views, panels and windows.
- `Workspace/` the folders open, the explorer, the trash, and the work done on other threads over all of the files.
- `Settings/` `hex_settings.hor`, the settings window and the skin Hex is drawn in.
- `Platform/` and `Dialogs/` (in `Hex.Shell`) opening, saving and picking a folder, Hex's own dialog on every system.
- `Testing/` the self test, a file per part of Hex, and `--check`.

`HEX_PROFILE=20` (or any number of milliseconds) has Hex write out every tick that took longer than that with the steps
that took it, and when it's closed what every step cost altogether. See `docs/performance.md`.

## Building

```
git clone --recursive https://github.com/bogzxo/hex
dotnet build Hex/Hex.csproj
Hex/bin/Debug/net11.0/Hex --selftest --exit --size 1600x900
```

Hex runs on Windows, Linux and macOS, with the .NET 10 SDK on each.

- **Windows** 10 1809 or later (the terminal is ConPTY). Nothing else.
- **Linux**, Wayland or X11. It draws with Vulkan, so it wants the Vulkan loader and a driver (`libvulkan1` and
  `mesa-vulkan-drivers` on Debian and Ubuntu, `vulkan-icd-loader` and your GPU's driver on Arch). Its window is SDL's,
  which comes with it. A desktop that shows apps by their `.desktop` file (GNOME, KDE) wants
  `scripts/install-desktop.sh` run once, for Hex's name and icon in its dock.
- **macOS** 11 or later, Apple silicon or Intel. Vulkan is MoltenVK, which comes with it. Shortcuts are on command
  where they're on control elsewhere, the files say Ctrl either way.

Settings are in `%AppData%\Hex` (or a `settings` folder next to Hex.exe), `~/Library/Application Support/Hex` and
`~/.config/hex`. A Dusk drop down key is Windows' own there, on Linux and macOS it's the desktop's: bind
`hex --dusk --toggle` to it in the keyboard settings. Published ahead of time (`dotnet publish Hex/Hex.csproj -c Release
-r linux-x64`, `osx-arm64`, `win-x64`) it wants clang on Linux and the Xcode command line tools on a Mac.

The self test is a hundred seconds all of it, so it comes in parts, `--selftest code` (seven seconds) or any of `layout`
(the designer, the minute and more of it), `code`, `workspace`, `extensions` and `garbage`, as many as you like. A part
that wants a file an earlier part makes is given a stand-in for it.

The engine is a submodule at `horizon/` (on its `development`), with the language inside it. Work on
`development`, `master` is what's released.
