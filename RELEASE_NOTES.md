# Hex 1.0.1

a little hotfix for [1.0.0](https://github.com/bogzxo/hex/releases/tag/v1.0.0), everything in there still goes

## fixes

- lists scroll smoothly now instead of jumping a whole row at a time and smearing the rows into each other. the
  autocomplete, the file dialog, the palette, dropdowns, the explorer and the other side lists, tables, and the code
  itself when the caret goes off the edge
- the scroll wheel over the autocomplete list scrolls the list, it used to scroll the code underneath it lol
- in the file dialog, click a file and press a letter and it jumps to the first file starting with it, the way explorer
  and finder do. before the letter just got stuck on the end of the name and found nothing
- Open Folder sits right under Open in the File menu now, on Ctrl+Alt+O

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
