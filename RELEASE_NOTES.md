# Hex 2.1.0

a big round of the things that got in the way. automations, a run button that knows what it runs, git that copes with a
folder full of repositories, a terminal you can scroll properly, and a caret that keeps up

## automations

- a tasks.hor can have `automations` next to its tasks, each one a command in the palette (and on a key if it says
  one). a command line run in a terminal tab, bash if it asks for it, or HIDL driving hex with the same `hex` an
  extension gets plus `hex.run` for programs, `print`, `progress` and an `ask` that waits for the answer. HIDL ones run
  off the editor's thread with no time limit and stop from the work list
- push-all.ps1 lives in the workspace's tasks.hor now as Push all and Push all (dry run)
- Set Up F5 only rewrites the file's tasks, the automations and comments round them stay put

## running things

- an arrow next to Run picks what F5 runs, the current file, any run task in the workspace or a .NET program in it. it's
  kept with the workspace and the button says its name

## git

- an active repository, the last one you touched in Source Control or the open file's, and commit, sync, branches and
  stash go to it rather than whichever one the open file happened to be in
- each repository's heading has its branch, what it's ahead and behind, how many changes and its own commit, sync and
  branch buttons
- the status bar names the repository and a click picks one then its branch. it's never blank when the folder only holds
  repositories
- new repositories, clones and git inits turn up by themselves in a few seconds, and a fetch or commit in the terminal
  is noticed
- submodules say what's changed in them and take you to their own heading, nested repos have names
- a GitHub account setting for machines with more than one, so Git Credential Manager stops asking which every time.
  the background fetch never puts anything up at all

## the editor and the window

- the caret glide gets where it's going and stops, it used to creep up on the caret over its last few pixels
- window edges you can actually grab, thicker, corners that reach along both edges, and the resize arrows on Windows
- the mouse's side buttons go back and forward in the file dialog
- the wheel goes where the pointer is, a window keeps it over itself, and settings scrolls instead of the code behind it
- scroll bars you can get hold of, wider to grab, growing under the pointer, track clicks that jump
- csharp-ls in a folder with no project doesn't get restarted over and over with a red card stuck in the corner

## the terminal

- a scroll bar, thin until you're on it, drag it or page with the track
- a minimap of the scrollback, coloured like the text, the prompts marked down its edge by how they ended
- scrolled up it stays on the same text while output carries on underneath

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
