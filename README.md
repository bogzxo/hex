# Hex

**This is an IDE. It opens, you write code, it gets the fuck out of your way.**

That's the whole pitch. You've been using editors that ship a browser, a JavaScript runtime, an account system, a
telemetry pipeline and an upsell, and somewhere in the middle of all that there's a text box. Hex is the text box, and
then everything a working developer actually needs round it, and nothing else.

No Electron. No Chromium wearing a code editor costume. No account. No sign in to sync. No telemetry. No AI sidebar
you didn't ask for, no "try our new assistant" toast, no marketplace of first party "experiences" waving at you from
the activity bar. It's a native program that draws its own UI on your GPU and does what you fucking tell it to.

## It's fast. No, actually fast.

Measured on Hex 2.0.0, the release build, on a Ryzen 5 5600X with an RTX 3060, opening this repository.

| | |
|---|---|
| process start to `main` | **14 ms** |
| window on screen | **~0.4 s** |
| editor up, repository open, first real frame | **0.5 s** (461 to 594 ms, 515 ms median) |
| the very first launch, filling the shader cache | 2.9 s, once, ever |
| memory, sat there idle | **~128 MB** working set, 20 MB managed heap |
| CPU, sat there idle | **~0.5%** of one core |
| garbage made per idle tick | **0 bytes** |
| download | **~35 MB** zip, nothing to install, not even .NET |

Half a second from double click to typing. Go time your current editor, we'll wait. Actually we won't, we've already
opened the file.

It gets there by not doing stupid shit. It's C#, compiled ahead of time to a native binary, so there's no runtime to
spin up and no JIT warming up while you watch. It draws with Vulkan through the same engine a game would use, on a
render thread of its own. When nothing's happening it does nothing, and when you're typing a tick costs a few
milliseconds. Every number here comes out of Hex itself, `HORIZON_LOG_STARTUP=memory` gets you the same on your box,
and [docs/performance.md](docs/performance.md) has the lot step by step.

## Fully featured, not "lightweight"

"Lightweight" usually means "does fuck all". Hex is light and it does the job.

- **Code.** Any language with a language server, fifteen known out of the box and installed with a click (C#, C and C++, Rust,
  Go, Python, TypeScript, D, Bash, YAML, TOML, JSON, Markdown and the rest). Completion, hovers, go to definition,
  peek, find references, inlay hints, a minimap, format on save, editorconfig. Semantic highlighting that colours a name
  by what it actually is, in ReSharper's colours for C#. TODO and FIXME lit up in everything.
- **Git, inside.** Changes in the gutter with stage and revert on a click, blame on the line you're on, staging, commits,
  amend, branches, merge, rebase, cherry-pick, stash, tags, word level diffs, merge conflicts you settle with a button,
  file history and a local history of every save.
- **A real terminal.** Dusk, built in and standalone, ConPTY on Windows and a pty everywhere else, tabs, splits and a
  drop down. It can be your system's default terminal if you want it to be.
- **Debugging.** Breakpoints, stepping and the Debug side for .NET, through netcoredbg.
- **For .NET people.** A properties panel that reads Build Action and Copy to Output Directory from the SDK itself and
  writes them back into the .csproj without wrecking it, tasks for any solution, and Hex builds and restarts itself.
- **The usual furniture.** Command palette, quick open, search and replace across files, a settings window that
  actually finds things, workspace settings, a key bindings table with conflicts and chords, and an importer for your
  VS Code settings so you can leave without rebuilding your config by hand.
- **A layout designer and a language.** Hex is the editor of the [Horizon](https://github.com/bogzxo/horizon) engine and
  of HIDL, its language. A layout opens as a designer with a live canvas, and HIDL files show what they evaluate to as
  you type.
- **Everywhere.** Windows, Linux (Wayland or X11) and macOS, same program.

## What it doesn't do

**It doesn't track you.** No telemetry, no analytics, no crash reports phoning home, no account, no identifiers. Here is
every single time Hex goes online, to GitHub unless it says otherwise.

- Once a day, new versions of the language servers you've got. Off with one switch in the settings.
- Once a day, new versions of extensions you installed yourself out of the catalog. Off with another one.
- The changelog, when you open it.
- An extension, a language server or a debug adapter, when you click install (a server comes through its own
  installer, npm or dotnet or whatever it says).

That's it. Switch both off and Hex never says a word to anybody unless you ask it to.

**It doesn't push shit at you.** There's no "recommended extensions" nag, no first party plugins installed behind your
back, no service to sign up for, no "Hex Pro". The extensions catalog is there if you go looking, and that's where it
stays. Extensions are HIDL you can read in a text editor, which is handy, because you're holding one.

**It doesn't need installing.** Unzip it anywhere and run it. Settings live in a folder next to it, so carry it around
on a stick if you want.

## Bring your own agent

Hex doesn't ship an AI and it isn't selling you one. What it has is an [MCP](https://modelcontextprotocol.io) server,
so the agent *you* chose can drive Hex the way you do. Screenshots, the UI tree, clicks, typing, commands, problems,
definitions, the terminal and the tasks, 27 tools in all.

```
claude mcp add hex -- C:\path\to\Hex.exe --mcp
```

It's off until you switch it on. When it's on it listens on a pipe only you can open, only from your machine, only with
a token Hex writes next to itself, and a repository you cloned can't switch it on for you. While an agent's connected
the status bar says so. Claude Code, or anything else that speaks MCP over stdio, is set up the same way.
[docs/agents.md](docs/agents.md) has the details and every tool.

## Get it

Grab the zip for your system from [Releases](https://github.com/bogzxo/hex/releases), unpack it, run `Hex`.

- **Windows** 10 1809 or later.
- **Linux** with the Vulkan loader and a driver (`libvulkan1` and `mesa-vulkan-drivers` on Debian and Ubuntu).
  `scripts/install-desktop.sh` gets its name and icon in your dock.
- **macOS** 11 or later. It isn't signed, so run `xattr -dr com.apple.quarantine Hex` on the unpacked folder first.

Or build it.

```
git clone --recursive https://github.com/bogzxo/hex
dotnet build Hex/Hex.csproj
```

## More

- [Layouts, HIDL and the workspace](docs/layouts-and-hidl.md), the designer, HIDL's values and console, and its language server
- [Extensions](docs/extensions.md), writing your own, everything `hex` can do
- [Agents](docs/agents.md), the MCP server and its tools
- [Performance](docs/performance.md), the numbers and how to get them yourself
- [Hacking on Hex](docs/hacking.md), finding your way round the code, building on every system, the self test

Work happens on `development`, `master` is what's released.
