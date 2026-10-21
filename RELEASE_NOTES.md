# Hex 0.4.0

Hex is the editor of [Horizon](https://github.com/bogzxo/horizon), the engine, and the IDE of HIDL, the language its
games, layouts and skins are written in. It's one program with two sides. Started plainly it is **Dawn**, the editor.
Started with `--dusk`, or as a copy of it called Dusk, it is **Dusk**, a terminal in a window of its own. Both are drawn
by the engine, with the same UI code a game draws its menus with, so what Hex shows of a layout is the real thing.

## Dawn, the editor

VS Code's shape. A title bar with the menus in it, an activity bar and a side bar (the explorer, search, the outline,
the designer's toolbox), tabs and breadcrumbs over the editor, problems, output and a terminal under it, the properties
on the right and a status bar. Ctrl+Shift+P is every command there is.

**Layouts.** A `.hor` file that says `#define layout` opens as its designer. Components come out of a palette onto a
canvas, there's a tree of the layout, layers, templates, the skin's art to pick from, properties in sections that fold
and undo throughout. F7 turns the layout into its code and back, and what was typed goes into the layout on the way.
A save keeps the comments somebody wrote, over the same declaration even after it was renamed or moved.

**HIDL.** Every other `.hor` opens as code, coloured, checked as it is typed and with its outline on the left.
- Completion as you type, a component's properties inside its braces, the game's text keys after an `@` and the
  names of the whole workspace everywhere else.
- F12 goes to a declaration, Shift+F12 finds every use, F8 checks every file of the workspace.
- Values, the right side of a file, is what the file comes to, run a moment after it was typed into and shown as a
  tree. A click on a value goes to where it was written.
- The HIDL console under the editor runs a line in what the file came to.

**Other languages.** C#, Python, JSON, Markdown, HLSL and Slang among sixty more are coloured with VS Code's own
grammars, C# in Visual Studio's colours. A language server named in `language_servers.hor` lints a file and completes
in it, when it is on the machine.

**Debugging C#.** F9 puts a breakpoint on a line, F5 builds the project and runs it under
[netcoredbg](https://github.com/Samsung/netcoredbg), looked for next to Hex, in the settings folder or on the PATH.
Where it stops the line is lit and the Debug side has the stack and the variables, on VS Code's keys (F10, F11,
Shift+F11, Shift+F5).

**Tasks.** A `tasks.hor` in the workspace lists its tasks. Ctrl+Shift+B runs the build, and a task runs in a terminal
tab named after it.

**The workspace.** Any number of folders, saved as a `.hex-workspace` and opened again when Hex starts. Ctrl+P opens
a file by a few letters of its name, Ctrl+Shift+F finds in all of them. Every key is in Help, Keyboard Shortcuts, and
can be changed there.

**Extensions.** An extension is HIDL that drives Hex, a folder with an `extension.hor` in it. It can add commands and
keys, put up menus, questions, cards and windows, give a file a view of its own, read and write files and change a
`.hor` by the paths of its values with the file's comments and spacing kept. It is read again by itself when it is
saved. Hello Dolly and Fighter2D's move editor come with Hex. See `docs/extensions.md`.

**HIDL's language server.** `Hex --lsp` is the same knowledge of HIDL for any other editor that speaks the protocol.

## Dusk, the terminal

Tabs of shells, every tab a tree of splits dragged by their edges, a profile for every shell the machine has
(PowerShell, cmd, Git Bash, WSL, bash, zsh), a command palette, themes and plugins in HIDL. On Windows it can drop down
from the top of the screen on a key. On Linux and macOS bind `hex --dusk --toggle` to a key in the desktop's settings.

## Running it

Download the zip for your system from this release, unpack it anywhere and run `Hex` (`Hex.exe` on Windows). It is
compiled ahead of time, so nothing else needs installing to run it, .NET included.

- **Windows** 10 1809 or later.
- **Linux** on Wayland or X11, with the Vulkan loader and a driver (`libvulkan1` and `mesa-vulkan-drivers` on Debian
  and Ubuntu). `scripts/install-desktop.sh` gives Hex its name and icon in the dock.
- **macOS** 11 or later on Apple silicon. Vulkan is MoltenVK, which comes with it. Hex isn't signed, so macOS stops a
  downloaded copy from starting until `xattr -dr com.apple.quarantine Hex` is run on the unpacked folder.

Hex keeps everything of its own next to itself, its settings in `settings`, what it keeps in `data` and its compiled
shaders in `cache`, so a copy carried about takes all of it along. Where it can't write next to itself (a system
folder) it's `%AppData%\Hex`, `~/.config/hex` and `~/Library/Application Support/Hex` as before.

## In this release

- Everything Hex says comes out of its strings file, `Assets/hex/localisation/en-gb.hor`, every menu, notice, dialog,
  tooltip and setting, so another language is another file.
- The release notes come from Hex's releases on GitHub, with the history from before them underneath.
- A Motion section in the settings, how quick the interface moves and how long the caret takes to glide, 0 for nothing
  moving at all, and in Dusk how long the cursor glides.
- A preview under the glass sliders that shows what they make as they move. Frosted glass is see through again, it had
  come out as a solid panel.
- Settings, data and shader caches next to Hex, see above. The shader cache knows the compiler it was made with and
  clears out what hasn't been used for a month, and the driver's pipeline cache is kept as it's made.
- Neither side asks the system about gamepads any more, a broken controller driver had held Hex's window up for a minute.
- Dusk starts straight away on a machine with WSL but no distribution (it had waited on `wsl` and the profiles plugin
  failed), and `wsl` gets two seconds to answer, once a run.
- Up in Dusk brings back the command that was run, as it was run, it had come back scrambled after an edit in the
  middle of the line. Micro scrolling a long file no longer leaves letters smeared between the rows.
- Tab completes git, dotnet and friends in Dusk.
- On Linux and macOS a shell's first line is no longer lost before the terminal is listening.
