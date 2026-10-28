# Hex 2.5.1

2.5.0 fell over building for Intel macs before it got out, so this is the first of it, with the PATH found on linux
and macOS again and Setup happy to install under a folder that's a link.

hex has an installer now, and updates itself. [Hex Setup](https://github.com/bogzxo/hex-installer/releases/latest)
puts it on your machine properly, with shortcuts, a place in the app menu and an uninstaller, and Help > Check for
Updates brings a newer one down from inside hex. on top of that a pass over the whole UI, notebooks that draw
everything Jupyter does, and Markdown previewed natively

## installing and updating

- Hex Setup is a hex of its own, the same look, built on the same engine. it shows where hex goes and which release
  before it touches anything, needs no admin, and Customize says which shortcuts and menu entries you get
- it goes to `%LOCALAPPDATA%\Programs\Hex` on windows, `~/.local/opt/hex` on linux and `~/Applications/Hex.app` on a
  mac, Browse to put it somewhere else
- Help > Check for Updates asks GitHub when you ask it to, never behind your back. Install closes hex the usual way,
  downloads the release, checks its size and SHA-256 against what GitHub says, swaps it in and starts it again
- an update builds the new copy beside the old one and puts the old one back if anything goes wrong. uninstalling
  only takes away what Setup put there, your settings, extensions and files stay
- builds for ARM64 on every system now, and Intel macs, next to x64 and Apple silicon

## notebooks

- SVG output is drawn, matplotlib's included, and LaTeX is typeset, in outputs and in markdown between `$` and `$$`
- every table in a notebook has Copy CSV and Export CSV, all its rows in the order it's sorted
- markdown tables wrap their cells and keep their bold, code and formulas
- pictures and formulas are rendered off the UI thread, so a big notebook doesn't stall while it comes up
- notebooks zoom with Ctrl+wheel like code does
- a cell is done once the kernel's both replied and gone idle, its outputs don't straggle in after it says it's finished

## everything else

- Markdown files preview natively, Preview or F7 to flip between it and the source, tables, code, maths and all. the
  markdown_preview extension is gone, a saved preview moves over by itself
- a pass over the whole UI. Dawn and Dusk have a selector to swap between them, the welcome leads with Open Folder and
  Open File, tabs that don't fit go in an overflow list with their paths, the active tab has a marker, and nothing
  round the editor grows or shifts under the pointer anymore
- new installs start Flat, solid and without animation, your saved look stays as it was
- Find in the terminal, an inline bar with a count, next and previous, and it doesn't slow a terminal with a huge
  scrollback down
- Scroll speed in Settings, Appearance, Motion, for Dawn and Dusk both
- the explorer copies, cuts and pastes files with Ctrl+C, X and V, several at once, and Move To in its menu. a refresh
  keeps what you had selected and where you were
- Pyright installed globally with npm is found and works, and installing a language server through npm from hex does
- terminal arrows, triangles and prompt chevrons are drawn cell sized, whatever the font has

## running it

**[Hex Setup](https://github.com/bogzxo/hex-installer/releases/latest)** is the easy way, unpack the `Hex-Setup` zip
for your system and run it. it's on this release too.

or grab the portable zip for your system from this release, unpack it anywhere and run `Hex` (`Hex.exe` on windows).
its compiled ahead of time so you dont need to install anything, .NET included

- **Windows** 10 1809 or later, x64 or ARM64
- **Linux** on Wayland or X11, x64 or ARM64, with the Vulkan loader and a driver (`libvulkan1` and
  `mesa-vulkan-drivers` on Debian and Ubuntu). `scripts/install-desktop.sh` gives a portable copy its name and icon
  in the dock, Setup does that itself
- **macOS** 11 or later, Apple silicon or Intel. Vulkan is MoltenVK, which comes with it. hex isnt signed, so macOS wont
  start a downloaded copy until you run `xattr -dr com.apple.quarantine` on the unpacked folder (`Hex Setup.app` too)

hex keeps everything of its own next to itself, settings in `settings`, what it keeps in `data` and its compiled shaders
in `cache`, so a copy you carry around takes all of it with it. where it cant write next to itself (a system folder) its
`%AppData%\Hex`, `~/.config/hex` and `~/Library/Application Support/Hex`
