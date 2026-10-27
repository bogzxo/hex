# Hex 2.2.1

a quick fix for things jumping about on screen when you select and delete

- taking a selection out doesn't stretch it across the whole line for a frame anymore, it used to fly into the
  highlight of the caret's line
- Ln and Col in the status bar stay where they are when you select something, the count of what's selected used to
  shove them a hundred pixels left and back again
- the boxes on a pair of brackets don't swap ends of the line as the caret moves between them
- squiggles coming and going don't smear the other squiggles across the file

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
