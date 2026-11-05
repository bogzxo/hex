# Hex 2.8.8 thickboy

Hex fits on a tablet now. Dawn runs on Android with touch, a keyboard and a mouse, and keeps your files and settings
in the app's storage. This is still early. The editor works, but Android rendering is not yet as quick as it needs
to be, and local notebook kernels, language servers and running projects are still desktop jobs.

## changed

- rendering has its own settings now, with Auto, native or a manual scale, CAS, FSR 1, Snapdragon GSR 1, bilinear and
  nearest. Keep the interface sharp at native resolution, or scale the whole frame if the GPU needs a break
- notebooks export to one HTML file, including unsaved cells and their current outputs. No kernel needed, no extra
  files to carry about. SVG plots and formulas stay vector instead of becoming blurry screenshots
- right-click an output picture to save its original SVG or PNG
- Android has the same Hex icon, a real shell on supported devices, and backups when the app goes into the background
- plain boxes and shadows no longer pay for the text shader's texture work

## fixed

- mobile title bar text follows the interface zoom instead of cancelling it out
- pulling past the end of a page lets the spring settle after you let go, instead of feeding it more scroll impulses
- missing upscale shaders fall back without throwing away the setting you picked
- Android asks before closing unsaved work

# Hex 2.8.2 thickboy

A quieter Hex. Graphite is the new look and what a fresh install starts in, neutral surfaces in Dawn and Dusk alike
with the motion toned down, and the README has been written again with pictures of the real thing.

## changed

- Graphite comes first among the looks (Appearance), with a Graphite theme for the code and the terminal
- the side bar's views and a file switched to settle in quicker and from closer, so moving around reads as calm
- Dawn and Dusk swap with Ctrl+Alt+Shift+D now, Ctrl+Alt+D is left to whatever else wants it
- the welcome page, the command palette and the theme picker say plainly what the keys and the mouse do
- the agents panel has a thinking level for the next message, and says how long a turn took once it's done
- markdown in a notebook is set in the UI's fonts, only the formulas go through the maths typesetting

# From 2.8.0

Hex moves a little now, the way something you can touch does. Windows land rather than appear, the side bar's views
and the panel's pages settle in, notifications slide up out of the corner and a file switched to fades its code up.
All of it is small and quick, follows Interface animation (Appearance, Motion) and is gone entirely at 0.

## tactile

- the settings, the keys, About and every other window pop in from a touch smaller and sink away when they're closed,
  Escape included, and the dim behind a modal one fades out with it
- a window opened again while it's still going comes straight back rather than opening a second one
- the side bar fades as it slides in and out, and a view picked from the activity bar drops into place row by row
- the panel's pages (Problems, Output, Terminal) and every other row of tabs bring the page in from the side it was picked on
- notifications come in from the right instead of popping up whole
- switching tabs fades the file's code up from faint
- dialogs, menus, dropdowns, completions and tooltips go at the speed Interface animation says, they ignored it before
- scrolling past the top or the bottom of the code, a list, a tree or a table gives a little and springs back, the
  way it does on a phone, and what's pulled past stays inside of its box
- the setup and Check for Updates say what a release is called as well as its number

## fixed

- clicking a reference count above a line in a C# file opens its menu without selecting the line under it
- jumping to a reference in another file keeps the line you land on in place when that file's reference counts load

# From 2.7.5

The UI moves the way it should now. Scrolling stays on the screen's pixels instead of warbling, nothing jumps,
morphs or slides into its neighbour between frames, a dragged window keeps up with the mouse, and the plots and
formulas in a notebook are sharp and dark again. Everything 2.7.0 brought is here too, it's the first of the 2.7s
that Check for Updates offers.

## smoother

- scrolling anything keeps the words on the screen's pixels at any scale, they used to warble against the boxes under them
- between two frames a list, a tree, the code or a notebook moves as one, a row coming into sight comes in with the
  rest and a letter cut off at the edge moves with its line
- lighting something up under the pointer doesn't nudge or morph what's next to it, and a menu or a tooltip opening
  over another one comes in rather than the last one sliding over
- a window dragged by its title bar is where the mouse is, however slow the page it's showing is to draw
- drawers sliding and panels folding move smoothly, and so do fades
- the pointer hits what's drawn while a scroll is still easing, not where it was going to be

## sharper and faster

- an SVG or a formula in a notebook is rendered at the screen's own resolution, crisp on a 1080p screen without the
  lines going grey, and rendered again when the zoom or the monitor changes
- the settings page is a third of the work to draw, nothing scrolled out of sight is painted any more
- rounded boxes, their edges and their shadows are drawn in one go, a fifth fewer quads a frame
- code that hasn't changed isn't painted again

## notebooks

- Enter in a cell no longer scrolls its code up out of the cell

## look

- the Horizon theme's sun has set a little, its gold is a deep orange going on red

# From 2.7.0

Agents get a home of their own in Hex, hover hints you can actually read, a markdown preview that looks like
everyone else's, and scratchpads worth opening.

## agents

- the Agents view on the activity bar (Ctrl+Alt+A) runs Claude Code and Codex right in the side bar, their pickers for
  the model, permissions and effort under the box, tool calls as rows you can open, `path:line` in a reply a link
- each agent is an extension, `claude_code` and `codex` in hex-extensions, and any program that prints JSON can be one
- the harness answers an agent's questions about the code in fewer, smaller calls than grepping and reading would
- agent calls made while Dusk is on screen bring the editor back instead of waiting until they time out

## hover hints and tooltips

- a hint stays up while the pointer moves about the word it's for, goes over to it, or sits on it
- clicking a hint only takes it down, it doesn't land on the code under it
- hints are only as wide as what they say, and plain tooltips wrap to even lines

## markdown

- the preview reads like VS Code's, code in the code font with room round it, air between the lines, proper headings,
  lists, task lists and quotes
- a table's Copy CSV and Export CSV are on its right click menu instead of stacked over it

## scratchpads

- Ctrl+Alt+N makes one in a language, with its colours, its language server and the Values pane
- they save themselves without asking and empty ones go away
- Open Scratchpad lists them by their first line, Move Scratchpad into Workspace makes one a real file

## fixes

- a closed file gives its memory back, a big one used to leave Hex at well over a gigabyte
- Dawn and Dusk draw at the same size
- the outline doesn't take over the side bar when a tab is clicked
- Ctrl+Alt+Enter in the replace box replaces everything rather than committing the lot
- Paste in the code's right click menu, F2 and Del in the Explorer, a rename picks the name without its extension,
  and a new file can be made in folders that aren't there yet
- a task that fails keeps its tab and what it printed
- Git rows say their paths from the repository, the Values pane doesn't list an error twice
- a file goes to the language server registered for its own extension, whatever it's coloured as
- icons can move, things that are working spin or breathe while they work
