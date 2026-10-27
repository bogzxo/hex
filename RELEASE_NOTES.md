# Hex 2.3.0

jupyter notebooks, natively. open a `.ipynb` and its cells come up the way they do in JupyterLab, run in a real
kernel that hex talks to itself, no jupyter server and no browser in between

## notebooks

- File > New Jupyter Notebook, or open any `.ipynb`. every cell is a card, code in its language's colours with
  completions and hover from the kernel, markdown written out until you go into it
- Run All, run above, run below, Ctrl+Enter, Shift+Enter and Alt+Enter as Jupyter has them, `[*]` while a cell's
  waiting, its count once it's run, and the kernel's state on the notebook's bar
- Jupyter's keys out of a cell, A and B for a new one above or below, D D takes it out and Z puts it back, M, Y and R
  for markdown, code and raw, Enter to go into it, Escape back out, the arrows or J and K to move about
- kernels are found where Jupyter looks for them, pick one off the bar. interrupt (I I), restart (0 0), restart and
  run all, shut it down. a kernel that dies says so and the next run starts a fresh one
- what a cell prints comes in as it runs, `\r` progress bars drawn over themselves, tracebacks in the colours IPython
  gives them, long lines wrapped and long output scrolled past thirty rows
- plots and pictures are drawn as they came, DataFrames and markdown tables are real tables with their numbers lined
  up right, a MultiIndex's levels and all. markdown, HTML and JSON outputs are written out, `display` with an id
  updates in place and `clear_output` clears
- `input()` and `getpass` ask in a box under the cell, a password starred out as you type it
- notebooks are saved the way Jupyter writes them, keys sorted, one space indents, so a diff of one shows what you
  changed and nothing else. the Code view is the notebook's JSON if you want it, undo, backups and reload as any file
- agents get the notebook over MCP too, to read it, edit cells, run them and drive the kernel

## everything else

- Greek, Cyrillic, maths, arrows, box drawing and the rest of the symbols people actually type are drawn, everywhere.
  they were question marks, Δt and Ω included
- a table that's all in sight lets the wheel through to whatever it's in
- code that grew to fit its box doesn't scatter its letters for a frame while it scrolls past

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
