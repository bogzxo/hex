# Hex3 (3.0.0 Solstice): project health plan

Date: 13 November 2026. Scope: Dawn, Dusk, the shared shell, Hex.Core, and the parts of the Horizon engine that Hex's
idle cost and defaults depend on.

Hex3 adds almost nothing. It makes what is there faster, smaller, more consistent and easier to change. Solstice is the
default look. All text is British English.

## How this was put together

Eight read-only reviews covered the `DawnScene` structure, Hex.Core, the editor and language servers, Dusk and the shared
shell, start-up and memory, the failing self-test checks, the feature subsystems, and build and repository hygiene.
Findings below are from reading code unless marked *measure first*. Nothing in this plan claims a number that was not
measured.

## What the reviews found

| Area | Finding | Consequence |
| --- | --- | --- |
| Structure | `DawnScene` is one partial class over 333 files and about 52,000 lines, with roughly 400 instance fields. 196 of those files are self-tests that ship in the product assembly. | Any change can reach any state. The self-tests add to the size of every build. |
| Structure | Twelve subsystems already follow a good pattern: a class that takes a `DawnContext` and registers itself. Their ticks are still called by name from a hand-ordered list, and the tabs, panel, status bar, documents, notebooks, agents and settings are still raw scene state. | The pattern exists and works. It was never finished. |
| Settings | One setting is written in eight places plus the texts: field, read, write, catalogue row, usual value, getter, setter and clamp. A slider's range and the range the file accepts are stated separately; the file's is the wider on purpose, and a unit test now checks no slider reaches outside it. | Adding a setting is error-prone. Saving drops keys it does not know and is not atomic. |
| Dawn and Dusk | Chrome dressing, effects, look picking, the settings window and scale snapping are written twice, once in each scene. | Fixes land in one mode and not the other. |
| Idle | A terminal view that is not painted (a background tab, a hidden pane) asks for a frame on every tick, because the version it compares against is only updated by painting. | The likely cause of Dusk's 12.8% idle CPU. *Measure first.* |
| Idle | Dusk still runs a blocking, compacting, aggressive collection three seconds after start. Dawn was moved to a non-blocking one because it paused typing. | A pause in the first seconds of every Dusk session. |
| Editing | The engine's `EnsureRows` rebuilds the row list for every line on every keystroke. Language-server traffic is whole-file: no request cancellation, no semantic-token deltas, inlay hints for the whole file. | Cost per keystroke grows with file size. |
| Editing | Save As, rename and move never tell the language server the path changed. | Diagnostics stop after Save As. |
| Correctness | `HexOutline` keeps its cache in three unsynchronised statics read from three threads. | A file can be shown another file's outline. |
| Correctness | Extension folder watchers are never disposed. | They leak on disable, uninstall and exit. |
| Start-up | The constructor builds every hidden view. A blocking collection runs under the loading cover on every scene change, including each mode swap. | Start-up and mode-swap time. *Measure first.* |
| Tests | 176 self-test checks fail on `development` in `settings`, `launch` and `surface`. | The suite cannot tell a regression from the background noise. |
| Hygiene | US spellings in a British file, strings hard-coded in C#, stale documents, an ignore file for a repository layout that no longer exists, no line-ending policy. | Small, visible roughness. |

## Decisions

1. **Finish the existing architecture; do not rewrite.** A rewrite of 52,000 lines would trade known behaviour for
   unknown behaviour with no measured gain. The part-and-context pattern is sound. Hex3 completes it.
2. **Parts tick through an interface.** `IHexPart` gives a part a name and an `Update`. The scene keeps an ordered list
   and one loop. Order stays explicit, the profiler reads names from the list, and a new part no longer edits `DawnScene.cs`.
3. **One description per setting.** A table of key, default, range, kind and scope drives reading, writing, the usual
   values, the clamps and the folder scope. Unknown keys survive a save. Writes go to a temporary file and are moved into place.
4. **Shared chrome lives in `Hex.Shell`.** What both modes do the same way moves there, one piece at a time, each with
   the same behaviour before and after.
5. **Self-tests become optional at build time.** A build property removes the `Testing` folders. Development and CI
   builds keep them. Whether release builds ship them is a product decision, because `--selftest` and the MCP self-test
   tool are documented features.
6. **The release title stays `Hex 3.0.0 Solstice`.** The updater in every installed 2.x reads the codename from a title
   that begins `Hex ` and the number. The product is called Hex3 in the window title, Welcome and About.
7. **Engine changes are allowed where Hex's behaviour is decided there**, and are kept minimal: the terminal's usual
   theme, the unpainted-view frame request, and later the row rebuild.

## Order of work

Each slice builds, passes the unit tests, and leaves the self-test failure set no larger than before.

| Slice | Contents | Status |
| --- | --- | --- |
| 0. Identity | Version 3.0.0, codename Solstice, title Hex3, Solstice as the default look for editor and terminal, What's new for 3.0.0. | Done |
| 1. Correctness | Outline race. Watcher disposal. Atomic settings write. Unhandled reads in Diff and History. | Done |
| 1b. Correctness, more | Language server follows a file to its new path. The bottom panel can be dragged again. No blocking collection on a mode swap. | Done |
| 2. Idle and pauses | Unpainted terminal views stop asking for frames. Dusk's collection made non-blocking. | Done, idle CPU not yet measured |
| 3. Parts | `IHexPart` and `HexTicks`: 36 named steps in three ordered lists replace the hand-written call chains. The profiler times each step by name. Five parts tick through the interface. | Done |
| 3b. Parts, remaining | The tabs, panel, status bar, documents, notebooks, agents and settings are still raw scene state. | Planned |
| 4. Settings table | `HexSettings.Rows`: 110 rows drive reading, writing and the ranges. Recorded goldens of all keys on three inputs pass unchanged against it. Unknown keys survive a save. The settings window's own table of ranges is gone. | Done |
| 4b. Settings table, remaining | The settings window's getter and setter switches (about 74 arms each) and the catalogue's usual values still repeat the keys. | Planned |
| 5. Shared chrome | `HexFrame` owns the field of light, the effects layers, the shared effects settings, the bars' dressing, the horizon line and the scale list for both modes. Look picking and the look picker's wiring are shared. Dusk keeps one read of the shared settings in place of seven copied fields. `HexSplitter` is the one handle on each side boundary. | Done |
| 5b. Shared chrome, remaining | The settings window set-up, the command palette, the backdrop arranger and the focus hand-over on a mode swap are still written twice. | Planned |
| 6. Build shape | Optional self-tests. Line-ending policy. Stale ignore rules. | Planned |
| 7. Language | British English in the interface texts. | Done |
| 7b. Language, remaining | Hard-coded strings moved to the texts. One voice for notices. Comments and documents. | Planned |
| 8. Small features | Triple click selects a line. The panel folds by its titles. Progress on the horizon line. | Done |
| 8b. Small features, remaining | Just My Code in the settings. Inline module filter. Notebook menu gaps. | Planned |
| 9. Editing cost | Incremental rows, request cancellation, token deltas, viewport inlay hints. | Planned, *measure first* |
| 10. Start-up | Lazy hidden views, no blocking collection on mode swap. | Planned, *measure first* |
| 11. Self-test debt | 176 failing checks in the reviewed suites reduced to 15. The side bar left hidden after F7 on Welcome explained 155. | Done for the cascade |
| 11b. Self-test debt, remaining | Twelve checks need a window wider than 1280. One launch check, and two in the move editor. | Planned |

## Acceptance

The gates in [the UX revamp plan](ux-revamp-plan.md) apply unchanged: warm start, typing latency, idle CPU, memory and
terminal throughput within 5% of the same-machine baseline, or better. Slices 2, 9 and 10 need a before and after
measurement on a published NativeAOT build before their claims go in the release notes.
