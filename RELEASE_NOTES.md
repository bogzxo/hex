# Hex 2.7.5

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
