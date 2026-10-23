# Hex 1.0.0

ok its actually 1.0 now :3

hex is the editor of [Horizon](https://github.com/bogzxo/horizon) and the IDE for HIDL. started normally its
**Dawn**, the editor. started with `--dusk` its **Dusk**, the terminal. same program, both drawn by Horizon's own UI on
Vulkan, no electron was harmed in the making of this

this one is mostly me going "why does it look like that" at every single panel until it stopped looking like that. the
whole thing got rebuilt around glass, the keyboard works everywhere, D and C# get a proper IDE with a debugger, and
theres a file dialog thats actually nice to use

## whats new

### it looks good now (i hope)

- **looks**, pick one in Settings > Appearance > Profiles or from the palette (View, Look)
  - **Fluid Glass** is the default. clear glass that bends whats behind it at the rim, apple liquid glass style
  - **Frosted Glass** is the calmer milky one, nicer for walls of text
  - **Flat** takes the glass away completely
  - change anything and save it as a profile of your own
- the glass is darker with way more contrast, it was looking washed out af before
- every panel is a rounded card on the window and every window, dialog, menu and popup is the same stuff now
- new welcome page, new About window, the toolbox lines up, the side headings are bold, empty panels say so properly
- the designer's yes/no properties are switches instead of "< yes >" lol
- the bottom panel slides open and shut instead of snapping (and stopped glitching while it does it)
- code is a size bigger, it was smaller than the UI text which was kinda cursed

### the keyboard actually works

- Tab goes round every window and dialog and never escapes to whatevers behind it, with a ring on whatever has it
- Enter or space presses buttons, up and down change dropdowns, Escape closes things (once, not twice)
- a dialog puts the keyboard on its main button, which is always the one on the right
- shortcuts go off when you press the key, so Ctrl+Shift+F doesnt turn into a Ctrl+F if you let go of shift first :skull:

### a file dialog

hex has its own file dialog now, the same on windows, linux and mac (on wayland too, where typing into it didnt work)

- icons, sizes and dates, back, forward and up, your places down the side
- type a few letters to jump to a file, Enter opens, Backspace goes up a folder, type a path and it goes there
- opens any file, not just layouts. if it isnt text it asks first
- All files is the default filter, Save As keeps your extension and only selects the name

### D !!

- D gets coloured, hex has its own D grammar since textmate didnt have one
- errors show up while you type, not just after a build. dmd checks the file you have open (unsaved too) with your
  project's imports, serve-d does the rest
- after a build whatever dmd said lands in Problems and gets underlined in the code
- dub projects and local imports just work, and the program gets built right next to its code
- context aware autocomplete with the docs next to the list, it even offers to download DCD for you
- dmd's hints are quick fixes (Ctrl+.), a misspelt name or a missing import gets fixed for you
- remember "D NEARLY works..."? it works now. like actually

### an actual IDE (D and C#)

- hover (Ctrl+Shift+I shows it under the caret), signature help, go to definition (F12, even into the framework's
  source for C#), find all references, rename (F2), format (Shift+Alt+F), quick fixes (Ctrl+.)
- Problems is grouped by file and covers every file you have open, F8 and Shift+F8 walk through it, Ctrl+F8 checks
  every file in the workspace
- go to line (Ctrl+G) shows the code of each line

### debugging

- F5 debugs C# (netcoredbg, hex installs it for you) and D (lldb-dap, comes with LLVM). Ctrl+F5 runs without the
  debugger, same as vs code
- F9 breakpoints, F10 F11 Shift+F11 to step, a floating debug bar, the call stack and the variables in the Debug side
- watches, Shift+F9 to evaluate something, and hovering a variable while its stopped shows its value
- breakpoints follow their lines when you add lines above them
- what the program prints shows up in Output

### language servers

- add your own in Settings > Editor > Language servers. pick the language, the files (`.d|.di`), the server, done
- a server can be given settings and can ask you things
- a server that crashes gets started again instead of quietly dying, and if it keeps dying it tells you
- the list shows whats on your machine with a little dot instead of a wall of red text

### tasks

- **Set Up F5** writes the build and run tasks for you (D, C#, Rust, Go, C, C++, Python, Node, anything really), then
  Ctrl+F5 builds it and runs it
- tasks can depend on each other and run in the terminal panel or a Dusk window of their own, your pick
- a build that fails brings Problems up, and a D link that failed for want of `-i` offers to add it

### the rest

- Find in Files and quick open (Ctrl+P) go through every text file now, not just .hor
- the problems panel is calmer, only the icon is red
- the palette stopped matching random scattered letters, typing stop doesnt move a line up anymore lmao
- hex can sit in the windows right click menu and add itself to the PATH (Settings)
- Dusk got `--window` for a window of its own, sliders for its numbers, and F1 opens the palette from Dawn's terminal panel
- the window resizes on wayland
- language servers dont get left running anymore when hex gets killed
- a ton of fixes. the status bar cutting things off, Close from the palette saving without asking, the panel squishing
  to one line in a layout's code view, etc etc

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
