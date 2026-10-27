# Hex 2.2.0

glass you can see through again, a Visual Studio look, reference counts over everything you declare, and a long walk
over every corner of the UI to make it all behave the same

## looks

- the glass is liquid again, a light blur with most of what's behind it coming through, a bright rim and a proper lens.
  it had drifted into a frosted slab, and on a 4K screen the blur turned into the picture behind repeated over and
  over. if you'd tuned the sliders before, pick Fluid Glass again in View > Look to get the new ones
- the settings window is glass too, a bit more blurred than a popup so a page of settings still reads
- a Visual Studio look, its dark theme on the editor and the terminal, flat panes and no effects, for anyone who'd
  rather hex looked like what they had open yesterday
- the window's corners are round everywhere, the close button's hover included, and the settings window has a title
  bar big enough to grab

## the editor

- N references over every declaration, Visual Studio's code lens. C# from csharp-ls, other languages from their
  servers, HIDL from hex itself. press one for every place it's used, pick one to go there. `code_lens_references`
  turns it off
- held arrow keys speed up after a moment, how long and how fast are in Editor, Text
- the line's git blame waits half a second before it fades in, so it isn't flickering past as you move
- squiggles are waves under the whole of what's wrong, sticky scroll shows the line a lone brace belongs to
- typing into a big file with the mouse resting on the code doesn't stutter anymore, a .hor of five thousand lines was
  getting parsed again on every key
- the whitespace dots and indent guides stay where they are while you scroll or select, they used to smear across the
  blank lines for a frame, and a line scrolling in no longer lands on top of the one next to it
- a string never closed is wrong to the end of its line, not just at its quote

## the window

- the backdrop can be dragged and resized by hand, in Dawn and Dusk
- the menu bar works on Alt and a letter, trees go by arrows and Enter, focus goes back where it was when a window
  closes, and focus rings only show once you're using the keyboard
- dialogs all look and behave the same, the dangerous choice red and never what Escape picks
- one scroll bar everywhere, menus come in frosted, tabs stop growing and cut long names in the middle
- moving over the title bar on Linux doesn't maximise and unmaximise the window anymore

## git

- submodules and nested repositories sit under the repository they're in, not side by side with nothing to tell them
  apart, and ahead and behind are arrows rather than question marks
- Source Control has a box for the commit message, and Initialize Repository and Clone when there's nothing there

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
