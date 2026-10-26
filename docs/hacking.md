# Hacking on Hex

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
that took it, and when it's closed what every step cost altogether. See [performance](performance.md).

## Building

```
git clone --recursive https://github.com/bogzxo/hex
dotnet build Hex/Hex.csproj
Hex/bin/Debug/net11.0/Hex --selftest --exit --size 1600x900
```

Hex runs on Windows, Linux and macOS, with the .NET 11 SDK on each.

- **Windows** 10 1809 or later (the terminal is ConPTY). Nothing else.
- **Linux**, Wayland or X11. It draws with Vulkan, so it wants the Vulkan loader and a driver (`libvulkan1` and
  `mesa-vulkan-drivers` on Debian and Ubuntu, `vulkan-icd-loader` and your GPU's driver on Arch). Its window is SDL's,
  which comes with it. A desktop that shows apps by their `.desktop` file (GNOME, KDE) wants
  `scripts/install-desktop.sh` run once, for Hex's name and icon in its dock.
- **macOS** 11 or later, Apple silicon or Intel. Vulkan is MoltenVK, which comes with it. Shortcuts are on command
  where they're on control elsewhere, the files say Ctrl either way.

Settings are in `%AppData%\Hex` (or a `settings` folder next to Hex.exe), `~/Library/Application Support/Hex` and
`~/.config/hex`. A Dusk drop down key is Windows' own there, on Linux and macOS it's the desktop's: bind
`hex --dusk --toggle` to it in the keyboard settings.

Dusk can be the system's terminal, Terminal, System, Dusk as the default terminal, in either mode's settings. On Windows
a console program started from anywhere but a terminal (cmd from the Run box, a script double clicked) then opens in
Dusk, a tab of the Dusk that's open or a Dusk of its own. Windows passes a console on through Windows Terminal's
console host (it's the only one that hands consoles to a terminal), so Windows Terminal has to be installed, it comes
with Windows 11, but the program shows up in Dusk. It's `HKCU\Console\%%Startup` and Dusk registered as a COM server
under `HKCU\Software\Classes`, nothing needing admin, and switching it off puts back the terminal that was the default
before. On Linux it's Dusk first in `xdg-terminals.list` and GNOME's own terminal setting, and Dusk takes `-e command`
the way a desktop's terminal is asked to run something. Published ahead of time (`dotnet publish Hex/Hex.csproj -c Release
-r linux-x64`, `osx-arm64`, `win-x64`) it wants clang on Linux and the Xcode command line tools on a Mac.

The self test is a hundred seconds all of it, so it comes in parts, `--selftest code` (seven seconds) or any of `layout`
(the designer, the minute and more of it), `code`, `workspace`, `extensions` and `garbage`, as many as you like. A part
that wants a file an earlier part makes is given a stand-in for it.

The engine is a submodule at `horizon/` (on its `development`), with the language inside it. Work on
`development`, `master` is what's released.
