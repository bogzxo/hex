# Language Servers window: plan

A window of its own that takes over from the language-server rows in Settings and from the cards that say a server
isn't installed. For every language it walks the user through three things, in order, with a button for each: what the
machine needs before the server can run (Node, a .NET SDK, a JDK, Python, LLVM, Go, Rust, a D compiler), the server
itself, and what the project needs before the server can read it (a restore, a `compile_commands.json`, a Python
environment, a `tsconfig.json`). It is planned here, nothing is built yet.

## Brief

```
Domain:           dev-tool (a native code editor's setup surface)
Audience & tone:  both ends: a first-timer who opened a .go file and has no Go, and an expert who wants the exact
                  command and path. Calm, utilitarian.
Mood:             precise: every row says what's there, what's missing, and the one next action
Motion depth:     subtle feedback only (a step's state changing, progress); nothing choreographed
Layout family:    master/detail. Left: languages, the workspace's own first. Right: one language as a vertical
                  checklist (Environment, Server, Project) with one primary action. An Overview page leads with what
                  this workspace still needs.
Reference anchor: Hex's own Extensions window and Setup installer, in the Solstice tokens and codicons
```

Hex is native (Horizon UI, `.hor` layouts), not HTML. The kit's render gates (`verify_states`, `slop_tells`,
`taste_audit`, `verify_responsive`) can't open it, so none of them is claimed for this window. What carries over from
the doctrine is the substance: token by intent (Solstice's success, warning and error, one accent), every control in
all its states, one thing leading on each page, status as an icon plus words and never colour alone, no one-sided
accent borders, no emoji, no em dashes in the copy. Verification is Hex's own: self tests, screenshots, and a tester
driving it over MCP.

## What's wrong today

| Where | What it does | The trouble |
|---|---|---|
| Settings, Editor, Language servers (`HexLanguages.Settings.cs`, `HexLanguages.Editor.cs`) | 18 text rows (a path), Choose, Install, Changes; a status line of markdown under each | A wall of equal rows. Nothing says which matter for the workspace. Install runs a command and that's the whole story. |
| The card for a missing server (`HexLanguages.Missing.cs`) | Copy the install command, Open settings, Don't show again | Copying a command into a terminal is the user's job. It doesn't know what the command itself needs. |
| What a server needs to run | Only npm is checked (`HexLanguages.Prerequisites.cs`); the rest is prose in `about` ("Wants a JDK 21 or later on the PATH", "Needs a .NET SDK of the same major version") | A missing JDK, .NET SDK or D compiler shows up as the server failing to start, with stderr nobody reads. |
| What a project needs | Scattered: the status bar says csharp-ls needs a project; a restore runs quietly; Python's interpreter is a command; CMake projects are found for clangd | None of it is shown as something to do, or done. |
| Installs | Run hidden, a card with a percentage; output in the Output panel | When an install fails the reason is somewhere else. Nothing re-checks the machine. |

## The model: three steps, every language

Each language is a short checklist. Every step has exactly one of these states, each with an icon and a word:

| State | Icon (codicon) | Word | Primary action |
|---|---|---|---|
| Checking | `loading` (spins) | Checking... | none |
| Ready | `pass-filled`, success | Ready, and the version | none (Details) |
| Needed | `circle-large-outline`, plain | Not installed | Install, or Get it for a download |
| Optional | `circle-large-outline`, dim | Optional, what it adds | Install |
| Outdated | `arrow-circle-up`, warning | 1.2 installed, 1.4 out | Update |
| Installing | `loading` + a progress bar | Installing, the line it's on | Cancel |
| Failed | `error`, error | Failed, the first line of why | Try again, Show log, Copy command |
| Blocked | `lock`, dim | Needs the step above | none; says which |
| Manual | `link-external` | Download it, then come back | Open download page, Choose file, Check again |

Steps run top to bottom. A step that needs another (pyright from npm needs Node) is Blocked until that one's Ready,
and says so.

1. **Environment.** The tools a server runs on or is installed with: Node and npm, a .NET SDK of the right major
   version, a JDK 21+, Python, LLVM, Go, rustup/cargo, a D compiler and DCD, ShellCheck (optional). Defined once and
   shared: one Node row serves seven servers.
2. **Server.** The server itself: where it is, its version, Install, Update, Choose file (for a server somewhere else),
   What changed (its releases). Today's path setting moves here.
3. **Project.** Only what the folder on screen needs, found by looking: a .NET project that was never restored, a
   Python folder with no environment picked, a CMake project with no `compile_commands.json`, a TypeScript project
   with no `tsconfig.json` (optional), a D folder with no `dub.json`, a Go folder with no `go.mod`. Each with its own
   button: Restore, Create environment, Pick interpreter, Configure with CMake, Create tsconfig.json.

## Pages

The window is the Extensions window's shape: a title bar, a list on the left, the page on the right.

### Overview (opens first)

One thing leads: a sentence at display size saying where the workspace stands, then its languages.

- "This workspace is ready." or "2 things left before C# and Python work." Display size, the rest body size.
- Under it, the workspace's languages only (found from its files, and files open), as rows: language icon, name, a
  one-line state, the one button that moves it on ("Install .NET SDK", "Restore Signal.csproj"). Not cards in a grid:
  a list, the first not-ready row first.
- "Set up everything" as the one primary button when more than one step is left. It runs the steps one at a time in
  the right order and stops at the first that needs the user (a download, a licence, a choice).
- A quiet link to "All languages" (the other 16).

### A language: Setup

A checklist page for one language: its name and icon, what server it uses (one sentence from `about`), and the three
steps as a vertical stepper with a line joining them. Each step row: state icon, what it is, the state in words, the
version or path in dim text under it, and its buttons on the right, the primary filled, the rest quiet.

**Every step that has a command shows it, and has a button to run it.** The command sits in the step as a line of code
(the code font, selectable, wrapped where it's long), with Copy beside it, and Run as the step's primary button: the
user can read exactly what will happen, run it themselves in their own terminal, or press Run and watch it in the
step's log. A Manual step shows its download page and, where there is one, the command to run after (a JDK's
`java -version` to check). The command shown is the one that runs, for this system, from the catalog, never a
paraphrase.

When everything's Ready the page says so and shows what the server gives ("Completion, errors as you type, go to
definition") and a Restart server button.

### A language: Settings

Each language has a Settings page beside its Setup (two tabs at the top of the language). Everything that's that
language's, in one place, out of the global Settings:

- **Server:** where it is (the path that was a Settings row, with Choose file and Use the one on the PATH), its
  arguments for a server added by hand, Restart server, What changed.
- **What it's told:** the server's own settings from the catalog's `settings` (HTML's `format.wrapLineLength`, CSS's
  `validate`, C#'s `analyzersEnabled`, D's `lintOnFileOpen`), each as a field of its kind: a switch for a flag, a number,
  a text, a list. The user's changes are kept in `HexSettings.ServerSettings[name]` and laid over the catalog's when the
  server starts; Reset puts one back to the catalog's.
- **The language's own:** what lives elsewhere today moves here: Python's interpreter and Create environment, the
  colours C# and TypeScript are drawn in (ReSharper's, Visual Studio's, the look's), a CMake build folder for clangd.
- **Files:** which extensions go to this server, and files associated with the language by hand.

A changed setting that needs the server started again says so and offers Restart server; one that a running server
takes as it is (`workspace/didChangeConfiguration`) is sent straight away.

### All languages

Every language in `language_servers.hor` and the ones added by hand, grouped: This workspace, Installed, Not installed.
A filter box. Each row the same as on Overview.

### Advanced

What's for every server rather than one: check for updates (the flag and Check now), show a card when a server is
missing (the flag), and add a server for a language Hex colours (today's form in `HexLanguages.Editor.cs`). A server's
path is on its language's Settings page.

### The window

A window of its own, the way Extensions is (a `WindowPanel` in Hex's window, `language_servers_window.hor`), not a page
of Settings: it can stay open beside the code while an install runs and a file is tried, and it's brought to the front
at the right language from wherever it's asked for. Settings keeps one row, "Language servers", with an Open button,
and its search still finds "language server", "gopls", "pyright" and the rest by pointing at it.

## Ways in

- The card for a missing server gets one button, "Set up Go", which opens the window at Go. Copy command moves into
  the window.
- The status bar's language item: a press opens the window at that language. When something's left for the language
  on screen it shows a small `warning` icon with "Set up" next to the server name.
- Command palette: "Language Servers: Open", "Language Servers: Set Up This Workspace", "Restart Language Server" (it
  exists).
- Settings, Editor: the Open button above.
- When a server stops for a reason the window knows how to fix (no JDK, no project restored), the card says that
  reason and its button goes to that step.

## Data: what the catalog says

`language_servers.hor` gains a `tools` object next to `servers`, so a tool is written once:

```
let tools = {
    node: {
        name: "Node.js and npm", check: ["node", "--version"], min: "20",
        install: { windows: "winget install --id OpenJS.NodeJS.LTS -e", macos: "brew install node" },
        download: "https://nodejs.org/en/download",
        about: "Runs the servers that come from npm."
    },
    dotnet: { name: ".NET SDK", check: ["dotnet", "--version"], match: "runtime-major", ... },
    jdk:    { name: "Java JDK 21", check: ["java", "-version"], min: "21", install: { windows: "winget install Microsoft.OpenJDK.21" } },
    python, llvm, go, rustup, dmd, cmake, shellcheck ...
};
```

A server says what it needs and what's optional:

```
python: { ..., tools: ["node"], project: ["python-environment"] },   // pyright from npm
java:   { ..., tools: ["jdk"] },
bash:   { ..., tools: ["node"], tools_optional: ["shellcheck"] },
csharp: { ..., tools: ["dotnet"], project: ["dotnet-restore"] },
```

(`tools` is new; the existing `needs`, the files a server won't run without, stays as it is and becomes one of the
project checks.)

and `projects` names the checks of the third step, each a small piece of Hex's code (`HexLanguages.Restore`, the Python
environment, `CMakeProject`, ...) given a name the catalog refers to, so the logic stays in C# and the wiring stays in
the catalog. The existing `needs` key (files a server won't run without) becomes one of these checks.

## Installing

- One queue. One install at a time, in order. A step waiting behind another says "Waits for Node".
- Every step runs the same way: the command shown before it runs, the output streaming into its log, progress where
  the command reports it (npm and winget do), Cancel kills the process tree.
- After an install the machine is checked again for that step and every step that depended on it. `ProcessEnvironment`
  already re-reads the PATH from the system, so a tool installed a moment ago is found without restarting Hex.
- A step that needs the user (an installer with its own window, a licence, a download) is Manual: Open download page,
  then Check again, or Choose file.
- winget, Homebrew and the distribution's package manager are used where they're there. Where none is, the step is
  Manual with the download page.
- Nothing is installed without a press, apart from what Hex does today (restore, which is moving into the Project step
  and becomes visible).
- Every command is the catalog's, shown before it runs. No shell strings put together from file contents.

## The words

The `ux-writing` rules: verbs first on buttons, errors that say what happened and what to do, no filler.

| Place | Text |
|---|---|
| Overview heading, done | This workspace is ready |
| Overview heading, not done | 2 things left before C# and Python work |
| Primary, many steps | Set up everything |
| Step, needed | Not installed. Pyright needs Node.js to install. |
| Step, blocked | Install Node.js first |
| Step, failed | npm couldn't reach the registry. Check your connection, then try again. |
| Step, manual | Download the JDK, run its installer, then check again. |
| Project step | Signal.csproj hasn't been restored. C# can't see its packages until it is. |
| Missing card | Go files need gopls, which isn't installed. |
| Missing card button | Set up Go |

## States, all of them

Every button: default, hover, pressed, focused (keyboard), disabled (with the reason as its tooltip), busy (a spinner
in it, its width kept). The list rows: hover, selected, focused. The window keyboard: Up/Down through languages, Enter
to open, Tab through a step's buttons, Escape closes the log, then the window.

Empty and odd cases: a workspace with no language Hex knows ("Nothing here needs a language server. All languages"),
no package manager on the machine (every step Manual), offline (Check for updates says it couldn't, nothing goes red),
a server set to a path that's gone (Failed, Choose file), an install cancelled half way (Needed again, its log kept).

## Building it, in order

1. **Model.** `tools` and project checks in the catalog, read into `HexServerCatalog`; a `HexServerSetup` that works
   out every step's state for a language from what `HexLanguages.Updates` already finds (paths and versions) plus the
   tools' checks. Unit tests: needs and blocked ordering, version matching (.NET's runtime major, JDK 21), a missing
   package manager, a server added by hand.
2. **Install queue.** Lift `InstallServer` and `RunCommandLine` into a queue that any step uses, with the log kept per
   step, Cancel, and the re-check after. Unit tests with a fake command.
3. **The window.** `language_servers_window.hor` and `DawnScene.LanguageServersWindow.cs` on the Extensions window's
   pattern: Overview, a language's Setup and Settings, All languages, Advanced. Strings in `en-gb.hor`.
4. **Per-language settings.** `HexSettings.ServerSettings` (a server's own settings laid over the catalog's, with the
   settings goldens), the server path moving out of Settings into its language page, Python's interpreter and the C#
   and TypeScript colours moving with it; a change sent to a running server or a Restart offered.
5. **Ways in.** The missing card's button, the status bar item, the palette commands, the Settings row; the server
   rows leave Settings.
6. **Project checks.** Restore, Python environment, CMake configure, tsconfig: the code that exists becomes steps; the
   quiet restore stops being quiet.
7. **Proof.** A self test suite `servers-window`: a fake server and fake tools (scripts in the test folder) walk every
   state, a failed install shows its log and Try again works, a blocked step unblocks, Set up everything runs in
   order and stops at a Manual step. Screenshots of every page in Solstice and Solstice Day. Then a Sonnet tester
   drives it over Hex's MCP from a clean environment and rates it, and the design-critic agent is given the screenshots.

## Decided

1. **Commands are shown, and run with a button.** Every step's command is on screen, copyable, and runs on Run.
2. **Per-language settings get their own page**, a language's Settings tab beside its Setup.
3. **A separate window**, the Extensions window's kind, with one row in Settings that opens it.
4. **Restore stays automatic** (not asked about): shown as the Project step, Ready once it's done, Restore again on it.
