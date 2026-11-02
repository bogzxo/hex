# Scratchpads, the plan

Ctrl+N made an empty `scratch N.txt` in the scratch folder of the settings folder and opened it. That's a file like
any other after that, so it asks to be saved when it's closed, it's forgotten about the moment its tab goes, the empty
ones pile up, it's always plain text, and getting something worth keeping out of it is Save As and a delete by hand.
A scratchpad should be the place you put something down without thinking about where, and get back to later.

## What it becomes

**Ctrl+N stays instant.** A plain text scratchpad, no question first. Anything that asks before you can type is not a
scratchpad anymore.

**Ctrl+Alt+N picks a language first.** Every language Hex has a grammar for, plain text and the ones picked last on
top. The scratchpad is made with that language's extension (`scratch 3.py`, `scratch 4.hor`), and that's all it takes,
the highlighting, the language server and for HIDL the Values pane come from the extension the same as any other
file. No scratch only language plumbing.

**Scratchpads are never asked about.** One is saved quietly a moment after typing stops whatever the auto save setting
says, and closing one (its tab or Hex) saves it without a question. That's the hot exit, they're files already so
being written out is all keeping them across restarts needs. The backups never get involved.

**Empty ones go away.** Closing a scratchpad with nothing in it deletes its file, and the list sweeps out empty ones
that aren't open. Nobody wants twelve `scratch N.txt` files with nothing in them.

**Open Scratchpad lists them.** A chooser of every scratchpad, the newest first, each named by its first line that
isn't blank (the file name if it's empty), with the file name and how long ago it was touched dim after it. Typing
narrows it down like quick open. A sidebar section was weighed and left out, the list is something you go to now and
then, not something to look at all day, and a chooser is one command away.

**Move Scratchpad into Workspace.** Asks for a name in the first folder of the workspace, the first line made into a
file name as the suggestion with the scratchpad's extension, saves it, moves the file there and opens it in its place.
It's a real file from then on with the usual saving.

**One global folder, not one per workspace.** A scratchpad is for when there isn't a place yet, tying it to a workspace
means it's gone when you open another folder, and with no folder open there'd be nowhere for it. Move into Workspace
is the link to a workspace when one is wanted.

## Where it goes

- `Shell/DawnScene.Scratchpads.cs`, a partial of its own with all of the above, the scratchpad bits of
  `DawnScene.Conveniences.cs` moved there.
- `UpdateAutoSave` (Conveniences) also runs for a scratchpad, `Close` (Documents) and `AskBeforeClosing` (Views) save
  one quietly instead of asking.
- A Scratchpads menu off File holding New Scratchpad (Ctrl+N), New Scratchpad In Language (Ctrl+Alt+N) and Open
  Scratchpad, Move into Workspace in the palette only, strings in `en-gb.hor` under `scratchpads`.
- `Testing/DawnScene.SelfTest.Scratchpads.cs`, a part of the self test for making, the list's names, closing empty and
  closing with something in it, and moving into the workspace.

## Left out

Naming the tab after the first line (the file name is honest about where it lives), scratch per workspace, a sidebar,
running a scratch on its own (HIDL already runs, the rest have their terminals and tasks). Bringing scratchpad tabs
back on a restart too, a folder's start brings back only that folder's tabs and Open Scratchpad is the way back.
