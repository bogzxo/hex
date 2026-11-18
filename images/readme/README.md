# README image provenance

Captured on **18 November 2026** from the same Windows x64 NativeAOT publish used for the
[benchmarks](../../docs/benchmarks.md), Hex 3.1.0 Amphetamine at `98c6516`. All application images are unedited
**1600 × 900 PNGs** of the real window. No generated mockups, compositing, recoloring or post-capture removal of UI
was used.

They were taken by an agent driving Hex through its own MCP server: Hex started with `--window --mcp` and a settings
folder of its own (`HEX_HOME`), a small client on `Hex.exe --mcp-connect` opening files, running palette commands and
calling `hex_screenshot`. That's why the status bar says Agent on the right. `terminal.png` is the engine's
`HORIZON_SCREENSHOT` hook instead, Dusk has no MCP server.

| File | Actual application state |
| --- | --- |
| `editor.png` | Dawn, Solstice, `Mixer.cs`, csharp-ls running: semantic colours, inlay hints, reference counts, inline blame, sticky scroll, minimap. Code zoom 115% here and in every Dawn picture |
| `completion.png` | `mixer.` typed into `Program.cs`, the completion list csharp-ls answered with. The red mark is the half-typed line |
| `hover.png` | Go: Show Hover on `MathF` |
| `debug.png` | F5 on the demo project, stopped by netcoredbg at a breakpoint in `Render`. Hex installed netcoredbg into the settings folder on that first F5 |
| `git.png` | Source Control with uncommitted changes to `Mixer.cs` and Open Changes beside it |
| `search.png` | Find in Files for `Peak`, with the uncommitted changes marked in the gutter |
| `notebook.png` | `spectrum.ipynb` after Run All on the machine's Python 3.13 kernel (numpy, matplotlib) |
| `markdown.png` | `notes.md` in its preview |
| `designer.png` | `Assets/hex/welcome.hor`, Hex's own welcome page, in the designer with its Open Folder button selected |
| `settings.png` | Settings at Appearance, Profiles |
| `agentic.png` | Agentic with two Claude Code chats on Claude Haiku 5.5, started with `hex_agent_spawn` and asked read-only questions about the demo. The Claude Code extension was copied beside Hex.exe first. The limits in Details are the account's own |
| `light.png`, `graphite.png`, `glass.png` | The same file and layout after File, Preferences, Look: Solstice Day, Graphite and Fluid Glass |
| `sunset.png` | The same in the Flat look with Dawn's theme set to Sunset |
| `terminal.png` | Dusk, Solstice Dusk, PowerShell |

The project in the pictures is a small demonstration, a mixer of sines in two C# files with notes and a notebook, in a
git repository of five commits. It builds and runs; it isn't a claim about anything else. Dusk runs a showcase script
that prints the demo folder's listing and six real Hex commits, followed by ANSI colour swatches. Its prompt is
shortened to `PS Signal>`. Screenshot settings were kept apart from the user's and from the benchmark's.

One thing was worked round. After the demo's files were changed on disk under an open tab, csharp-ls reported syntax
errors the file didn't have until Run: Restart Language Server. `git.png` and `search.png` were taken after that.

`startup.svg` is a data chart, not an application screenshot. Bars start at zero and show the measured medians;
white whiskers show the minimum and maximum of ten runs per mode. Values come from the linked benchmark dataset.

The site's pictures (`site/images/*.webp`) are these same files at 1600 and 800 pixels wide, WebP at quality 86.
