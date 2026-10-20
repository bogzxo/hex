# Taking Hex apart, and putting it back as pieces

A plan for the shape of Hex and of the parts of the engine it leans on, so that a change to one thing is a change in
one place, a part can be tested without the rest of it, and nobody has to hold a hundred and eight files in their head to
add a button. Nothing in here has been done yet. Every step below lands on its own, builds, and passes the self tests
(Dawn's, Dusk's and the engine's) before the next one starts, so the work can stop anywhere and leave things better.

## Where it stands

Dawn is 26,000 lines in 139 files, and 108 of those files are one class. `HexScene` is a partial class with about a
thousand and fifty methods and the state of the whole editor in its fields, which every part reaches into: the open
documents are touched from 43 files, the settings from 42, the compositor from 48, the code editor on screen from 51.
Dusk is the same thing smaller, `DuskScene` in 28 parts. The self tests (5,700 lines of Dawn's) are more parts of the
same class, so they test by reaching into private fields, and none of them can run without the whole editor up.

`Hex.Shell` is what Dawn and Dusk share, and it's a drawer things got put in: the dialogs, the settings window, the
glass, the window modes, the single instance pipe, the recent list. `Hex.Dusk.Tests` is two files of unit tests, and
Dawn has none, every check of it goes through the self test.

The engine underneath is one assembly, `Horizon`, 87,000 lines, and 44,000 of them are the UI. Inside the UI is a
whole terminal emulator (12,400 lines, the VT parser, the screen, the panes, the shell integration, the TUI programs),
a language server client, a debug adapter client and the TextMate grammars. A game made with Horizon carries all of
it, Fighter2D uses the UI in 85 files and the tooling in none, and every change to the terminal rebuilds the game.

The repositories are held together by a `Directory.Build.props` above them that isn't in any of them, which points Hex
at the engine checkout next door instead of its submodule. That's lovely while working and means a clone of Hex on its
own doesn't build (its submodule is seventeen commits and a hundred and fifty files behind). The last commit of hex,
hidl and Fighter2D (`26b56c1`, `895ff7b`, `148fd65`, the 19th at 22:05) has a message that's a dump of file paths from
some tool, worth rewording before anything is built on top.

## What hurts

Adding anything to Dawn means adding to `HexScene`, and anything in `HexScene` can see and change everything else, so
nothing stops a feature leaning on another's insides, and a lot do. Finding where something happens is a grep over
everything, the folders (`Code/`, `Shell/`) say what kind of file it is and not what it's for. A menu entry, its key and
its palette command for one feature live in three central files (`Menus`, `Keys`, `CommandPalette`) far from the
feature. The tests can't be run a feature at a time without starting the editor and can't run at all without a GPU.
The engine can't be changed a bit at a time either, the terminal and a sprite batch are the same compile.

## The shape it should have

### The engine, four assemblies instead of one

| Assembly | What's in it | What it needs |
|---|---|---|
| `Horizon` | the runtime, Core (threading, platform, processes), Graphics, Rendering, Physics, Input, Audio, Content, Engine, Logging | Silk.NET |
| `Horizon.UI` | the compositor, the components and controls, skinning, layouts, the code editor and its highlighters | `Horizon`, `Horizon.HIDL` |
| `Horizon.Terminal` | the VT parser, the screen and its blocks, the views and panes, the pty, the host, profiles, shell integration and its scripts, the TUI programs | `Horizon.UI` |
| `Horizon.Tooling` | the language server client, the debug adapter client, TextMate's grammars and anything else that is for an editor and not a game | `Horizon.UI` |

A game references `Horizon` and `Horizon.UI`, Hex references all four. Nothing in `Horizon` may name the UI, and
nothing in `Horizon.UI` may name the terminal or the tooling, which a build check enforces (an analyzer, or simply that
the project references don't allow it). The namespaces mostly stay as they are, `Horizon.UI.Components` keeps its
name, the types only move assembly, so the games' code doesn't change.

The `WorkScheduler` and `PlatformPaths` stay in `Horizon`, a game wants both.

### Hex, a core with no window and the modes on top of it

| Project | What's in it |
|---|---|
| `Hex` | the exe and nothing else, it picks a mode and starts it, the composition root |
| `Hex.Core` | everything that is Hex's own and not a window: the settings model and its file, the workspace, documents as text and history, the language server catalog, GitHub's releases, the recent list, the single instance and the remote pipe, file kinds, the trash, the backups. No `Horizon.UI` in it, so it's tested with plain unit tests |
| `Hex.Shell` | the window furniture both modes use: dialogs, the settings window, notifications of work, glass and effects, modes, the chrome |
| `Hex.Dawn` | the editor, as features (below) |
| `Hex.Dusk` | the terminal, as features |
| `Hex.Tests` | unit tests of `Hex.Core` and of the features' logic, what `Hex.Dusk.Tests` is now and a lot more |

The self tests stay where they are, they're the end to end test and nothing replaces them, but they stop being part
of the scene (see the tests below).

### Dawn as features

`HexScene` becomes `DawnScene`, a few hundred lines that make the features, hand each the `DawnContext`, and call them
every tick. A feature is a class, in a folder of its own named for what it does, that owns its state and its layout
files and talks to the others only through the context. The context is small and made of interfaces:

| Service | What it gives |
|---|---|
| `IDocuments` | the open documents, the one on screen, open and close and save, and events for each |
| `IWorkspace` | the folders, the explorer's reading of them, and their changes |
| `INotices` | `Say`, `Notify` and `Note`, the status bar, the cards and the output |
| `ICommands` | where a feature puts its commands, each with a name, a menu, a key and whether it's on, which the menus, the palette and keys.hor are made from |
| `IPanels` | the sides, the panel under the editor and its pages, for a feature that has one |
| `WorkScheduler` | the engine's, for anything off the editor's thread |
| `HexSettings` | the settings, with a `Changed` event |
| `IStrings` | Hex's texts |

The features, and the files of `HexScene` each one is made of today:

| Feature | Out of |
|---|---|
| `Documents` | Documents/, Editing/, Backups, DiskChanges, Recent, Tabs |
| `Workspace` | Workspace/ (Workspace, Explorer, Folders, Trash), SideTree, Places |
| `CodeEditing` | Code (the editor set up), CodeFont, FindBar, Fixes, Hints, Outline, Docs, Signatures, Completion of HIDL |
| `Values` | Values, ValueEditing, ValuesSide, Table, Console, Runner, HexEvaluation |
| `Languages` | Linting, ServerCompletion, ServerUpdates, ServerSettings, HexServerCatalog, HexReleases, the C# self test |
| `Find` | Find (in files) |
| `Debugging` | Debugging |
| `Designer` | Designer/ (Canvas, Catalog, Editing, Handles, Inspector, Lists, Palette, Pointer, Selection) |
| `Extensions` | Extensions/ |
| `TerminalPage` | Terminal, TerminalFiles, TerminalProblems |
| `Tasks` | Tasks (tasks.hor) |
| `Chrome` | Chrome, Menus, Keys, CommandPalette, StatusBar, Sides, SideBar, Split, Panel, Welcome, Quiet, Effects, Backdrop, Modes, Views, Remote |
| `Settings` | Settings/ |

`Chrome` is the one that knows the window, the others ask it for room through `IPanels` and `ICommands` and never
touch its components. A feature's menu entries and keys move into the feature, registered with `ICommands` as it's
made, so deleting a feature deletes its menu entries too.

Dusk gets the same, a `DuskContext` and features out of its 28 parts (`Tabs`, `Palette`, `Themes`, `Session`, `Quake`,
`Instance`, `Plugins`, `Chrome`, `Backdrop`, `Effects`), much of it shared with Dawn's through `Hex.Shell`.

### Names

The `Hex` in front of every type goes where the namespace already says it (`Hex.Core.Workspace`, not
`Hex.Dawn.HexWorkspace`), the folders are named for features, a file is named for its type, and a type that's still a
partial is split along what it does (`DocumentHistory`, not `HexDocument.History.cs`). The assets of a feature sit
next to it (`Features/Languages/Assets/language_servers.hor`) and are copied to where they're found now, so nothing
that reads them changes.

### The repositories

One repository for the engine and Hex together (with HIDL in it as it is now), or the engine as a package Hex takes
by version. The workspace `Directory.Build.props` above the repositories is how it's really worked today, and a
monorepo makes that the truth instead of a trick: one clone builds, one change can touch both sides and be one commit,
one CI runs everything. Fighter2D stays apart and takes the engine by submodule or package. Either way the stale
`hex/horizon` submodule goes.

## The tests

The self tests come out of the scene: each feature gets a `SelfTest` class next to it that is handed the scene and the
script, and reaches what it checks through `internal` members of the feature (Hex.Dawn says `InternalsVisibleTo` its
own self test assembly, or they stay in Hex.Dawn behind `#if`, whichever reads better). The suites stay as they are
named, `--selftest languages` still runs the same checks.

What doesn't need a window gets unit tests in `Hex.Tests` as it moves into `Hex.Core`: the settings file, the
workspace file, the catalog and the version sums, the changelog, path handling, the backups, the file browser's
filtering, the work cards' state machine. These run in seconds and on CI without a GPU.

CI is a matrix of Windows, Linux and macOS that builds everything and runs the unit tests, and on Linux the self tests
under xvfb with lavapipe, which is what WSL showed works.

## The order

1. **Tidy what's there.** Reword the three garbled commits (it rewrites their history, so only if they aren't pushed
   anywhere that matters), commit the engine work and move the submodule, or decide on the monorepo first.
2. **`ICommands`.** Menus, keys and the palette made from registrations, the central files reduced to the registrations
   of the features that aren't features yet. Nothing else moves, and every later step gets easier.
3. **`DawnContext`, its interfaces implemented by `HexScene` itself.** A feature can be written against the context
   while the scene is still the scene.
4. **`Languages` comes out first.** It's the newest, the best tested and the least tangled, and it proves the pattern.
5. **Then `Find`, `Debugging`, `Tasks`, `TerminalPage`, `Extensions`, `Values`.** Each one a commit, each one its
   self tests passing.
6. **`Hex.Core` and `Hex.Tests`.** The settings, workspace, document model and the rest move down as the features that
   use them come out, with unit tests as they go.
7. **`Designer`, `CodeEditing`, `Workspace`, `Documents`.** The four everything else leans on, last, when the rest no
   longer reach into them.
8. **`Chrome` and `DawnScene`.** What's left of `HexScene` is the chrome, named for it, and the scene is the composition root.
9. **Dusk the same way**, sharing with Dawn what turns out to be the same.
10. **The engine split.** `Horizon.Terminal` and `Horizon.Tooling` out of `Horizon.UI`, then `Horizon.UI` out of
    `Horizon`. Mostly moving folders and fixing what reached the wrong way, which the build will list.
11. **CI**, once there's something worth running on every push.

Steps 2 to 8 are the bulk, a few days each for the big features. Step 10 can go any time after step 1, it doesn't
touch Hex's insides.

## What could go wrong

A feature that comes out may turn out to need half of another, which is the coupling showing itself. The answer is a
narrow interface on the context, not a reference to the other feature, and a note here of what it was. The self tests
are the net, they have to pass after every step, and a step that can't make them pass isn't done. Performance is a
check too, the garbage sweep (`--selftest garbage`) has to stay where it is, an interface call a tick is nothing, an
event that allocates is not.

Moving files loses `git blame` for whoever doesn't use `--follow`, so every move is a commit of its own with no edits
in it, which git follows.
