# Hex 2.0.0

the biggest one yet, big enough for a new number. git inside the editor, a settings window that finds things, code
coloured by what each name is, menus that fit on the screen, an MCP server for agents, and hex building hex

## git

- the gutter shows what changed, a bar clicked shows the change with stage and revert, and the caret's line says who
  last touched it
- source control (Ctrl+Shift+G) with coloured status letters, hover actions, a commit box that takes lines and colours
  markdown, amend, undo last commit, commit and push, signed off or without hooks
- branches in the status bar with ahead and behind, switch, make, rename, delete, merge, rebase, tags, cherry-pick,
  revert, stash, pull with rebase, publish
- diffs with word level highlights, merge conflicts lit with Alt+1/2/3 and accept buttons over them, abort and continue
- file history, local history of every save to compare with or put back

## the editor

- semantic highlighting, names coloured by what they are (class, struct, interface, method, field, local, parameter
  and the rest) from the language server, in resharpers colours for C#, from hex itself for HIDL and D. obsolete things
  struck through, reassigned locals underlined
- a minimap, inlay hints, peek definition (Alt+F12), TODO and FIXME lit in every language
- Change Language Mode (Ctrl+K M), file associations, and a file.cs.orig coloured as C#
- search as a side view with replace in files, a command palette, quick open and history in colour with the matched
  letters lit
- rulers, visible whitespace, editorconfig, format on save, Go, Rust, Svelte and TOML picked up
- hex draws at the scale of the screen its on, a 4K one included

## settings

- search with filters and a count, sections that follow you, full width rows, reset all, list rows for exclude patterns
- workspace settings over your own, the raw settings file a click away and kept in sync, mistakes in it in Problems
- import VS Code settings, and VS Codes setting ids understood
- keyboard shortcuts as a proper table with conflicts, chords and a key to record

## for .NET people

- a properties panel (Alt+Enter) for whatever's picked, with Build Action and Copy to Output Directory read from the
  SDK and written back into the .csproj without messing it up
- hex can build itself, its locked files moved aside, and restart into the new build
- Configure Tasks writes build, test and run for any solution

## menus

- reorganised the way a VS Code hand expects, File, Edit, Selection, View, Go, Run, Terminal, Git, Help, with the
  designer's Layout menu in place of Selection when a layout is open
- git is nine rows now instead of forty four, each job a submenu of its own
- no menu leaves the window, a long one scrolls with the wheel or the arrows at its ends

## for agents

- `Hex.exe --mcp` is an MCP server, so Claude Code and anything else that speaks it can drive hex like a person, 27
  tools from screenshots and the UI tree to typing, commands, problems and the terminal. off unless you switch it on,
  `claude mcp add hex -- Hex.exe --mcp` and youre away

## HIDL

- arrows, one statement ifs, `in`, two name for loops, computed keys, default parameters, destructuring, a pile of new
  text and list helpers, and errors that say what you most likely meant. docs/reference.md in the HIDL repo has all of it

## running it

grab the zip for your system from this release, unpack it anywhere and run `Hex` (`Hex.exe` on windows). its compiled
ahead of time so you dont need to install anything, .NET included

- **Windows** 10 1809 or later
- **Linux** on Wayland or X11, with the Vulkan loader and a driver (`libvulkan1` and `mesa-vulkan-drivers` on Debian
  and Ubuntu). `scripts/install-desktop.sh` gives Hex its name and icon in the dock
- **macOS** 11 or later on Apple silicon. Vulkan is MoltenVK, which comes with it. hex isnt signed, so macOS wont start a
  downloaded copy until you run `xattr -dr com.apple.quarantine Hex` on the unpacked folder

hex keeps everything of its own next to itself, settings in `settings`, what it keeps in `data` and its compiled shaders
in `cache`, so a copy you carry around takes all of it with it. where it cant write next to itself (a system folder) its
`%AppData%\Hex`, `~/.config/hex` and `~/Library/Application Support/Hex`
