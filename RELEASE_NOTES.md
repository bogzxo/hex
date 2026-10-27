# Hex 2.2.1

a fix for the code jumping about while you type, most of all when a line goes red and you put it right again

- a mistake typed into a file with a table (the localisation file) took the Code and Table buttons away and putting it
  right brought them back, shoving the whole editor sideways and back. they stay put now, Table greyed out meanwhile
- the panel under the editor doesn't open itself for a mistake and fold again once it's fixed, it only decides that
  when a file comes up, so the code doesn't get pushed up and down as you type
- the "N references" rows don't vanish while a declaration is half typed, so the lines under it don't jump up and
  back down, and a new one doesn't push the line you're typing on down
- a line you type on keeps its inlay hints, the code after them doesn't slide left and back
- the line numbers keep room for four digits, so line 100 doesn't push the code over
- a folded block stays folded while its brackets are out for a moment
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
