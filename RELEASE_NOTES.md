# Hex 2.7.0 preview

Agents get a home of their own in Hex, hover hints you can actually read, a markdown preview that looks like everyone
else's, and scratchpads worth opening. It's a preview, Check for Updates stays on 2.6.1 until it isn't.

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
