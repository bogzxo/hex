# README image provenance

Captured on **25 November 2026** from the same Windows x64 NativeAOT publish used for the
[benchmarks](../../docs/benchmarks.md), Hex 3.5.0 Dexamphetamine (`build 5541`, base `cfc771b6`). All application
images are unedited captures of the real window (`3200 × 1800` at 2× interface scale, resized to `1600 × 900` PNGs here
and `3200`, `1600` and `800` WebPs in `site/images/`). No generated mockups, compositing, recoloring or post-capture
removal of UI was used.

They were taken by an agent driving Hex through its own MCP server: Hex started with `--window --fresh --mcp --size
3200x1800 --scale 2` and a settings folder of its own (`HEX_HOME`), a small client on `Hex.exe --mcp-connect` opening
files, running palette commands and calling `hex_screenshot`. That's why the status bar says Agent on the right.
`terminal.png` is the engine's `HORIZON_SCREENSHOT` hook on Dusk (`--dusk --run`), and `webdev` and `typescript` come
from `--selftest webshots`.

| File | Actual application state |
| --- | --- |
| `editor.png` | Dawn, Solstice, `Mixer.cs`, csharp-ls running: semantic colours, inlay hints, reference counts, inline blame, minimap |
| `completion.png` | `mixer.` typed into `Program.cs`, the completion list csharp-ls answered with |
| `hover.png` | Go: Show Hover on `MathF` in `Mixer.cs` |
| `debug.png` | F5 on the `Tones` project, stopped by netcoredbg at a breakpoint in `Mixer.Render` |
| `git.png` | Source Control with uncommitted changes to `Mixer.cs` and `Program.cs`, and Open Changes beside it |
| `search.png` | Find in Files for `Peak`, with the uncommitted changes marked in the gutter |
| `notebook.png` | `notebooks/engineering.ipynb` after running on the machine's Python 3.13 kernel (numpy, matplotlib) |
| `markdown.png` | `README.md` in its native Markdown preview |
| `designer.png` | `Assets/hex/welcome.hor`, Hex's own welcome page, in the designer with its Open Folder button selected |
| `settings.png` | Settings at Editor, Text |
| `agentic.png` | Agentic with Claude Code chats on Claude Haiku 5.5, started with `hex_agent_spawn` |
| `agentic-inbox.png` | The Inbox with pending and finished agent cards |
| `agentic-review.png` | Changes for the turn that edited `Mixer.cs` |
| `light.png`, `graphite.png`, `glass.png` | The same `Mixer.cs` layout in Solstice Day, Graphite and Fluid Glass |
| `terminal.png` | Dusk, Solstice Dusk, PowerShell running a showcase script in `Tones` |

The project in the pictures is a small demonstration, a mixer of sines in two C# files with notes and a notebook, in a
git repository of five commits. It builds and runs; it isn't a claim about anything else. Dusk runs a showcase script
that prints the demo folder's listing and six real Hex commits, followed by ANSI colour swatches. Its prompt is
shortened to `PS Signal>`. Screenshot settings were kept apart from the user's and from the benchmark's.

csharp-ls can take up to a minute to come up on a project. No code picture was taken until it had, which is told by
Roslyn's own hints arriving in the problems list and the status bar saying `C#, csharp-ls`.

One thing was worked round. After the demo's files were changed on disk under an open tab, csharp-ls reported syntax
errors the file didn't have until Run: Restart Language Server. `git.png` and `search.png` were taken after that.

`startup.svg` is a data chart, not an application screenshot. Bars start at zero and show the measured medians;
white whiskers show the minimum and maximum of ten runs per mode. Values come from the linked benchmark dataset.

The site's pictures (`site/images/*.webp`) are these same files at 1600 and 800 pixels wide, WebP at quality 86.
