# Hex 1.0.2

another little hotfix, on top of [1.0.1](https://github.com/bogzxo/hex/releases/tag/v1.0.1)

## fixes

- pasting code copied out of a browser crashed hex outright (or did nothing at all in a debug build). the clipboard
  couldn't be read and the error came out somewhere nothing could catch it. on windows hex reads the clipboard itself
  now, and whatever the window library complains about goes in the log instead of killing the program

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
