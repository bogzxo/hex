# Hex UX revamp: implementation and validation

2026-10-28. Changes are in the Hex checkout and the workspace's shared Horizon
checkout selected by the parent `Directory.Build.props`. No new UI framework,
renderer, dependency, service, or persistent diagnostic subsystem was added.

## Delivered behavior

- Both modes show a shared Dawn / Dusk selector with the active mode indicated.
  The other scene is still constructed lazily. Returning restores the previous
  eligible focus target instead of always forcing focus into code or the terminal.
- New editor settings use Flat, solid surfaces and immediate motion. Existing
  saved appearance values survive a save/load round trip. Flat explicitly clears
  effect switches, caret glide and interface animation. Interface animation at zero
  also stops terminal pane tweens/cursor glide and code wrap glide without rewriting
  the terminal's saved animation preferences.
- Welcome leads with Open Folder and Open File. It has bounded, wrapping text,
  an actionable empty recent list, responsive columns, and scrolling on short windows.
  Code's initial sidebar is Explorer; deliberate later sidebar selection remains.
- Open editors have a visible overflow chooser with paths to distinguish duplicate
  basenames. Tabs retain full-path tooltips. Narrow code areas put contextual actions
  below file navigation. Active document/terminal tabs have an accent marker.
- Problems / Output / Terminal navigation aligns left. Shared terminal status offers
  Return to live while reading scrollback. The terminal's plus opens the default
  profile directly; the adjacent dropdown chooses another profile.
- Terminal Find is a shared inline bar with match count, previous/next, Escape to
  close, and focus return. Enter/Down and Up step matches while editing the query;
  existing F3/Shift+F3 commands remain available from terminal input.
- Inline search waits 120 ms after query edits, scans at most 256 rows per update,
  restarts if scrollback shifts, and caps retained inline results at 100,000 with a
  visible `100k+` indication. The synchronous search API remains uncapped. Search
  highlight painting starts at the visible row range rather than scanning every
  result outside the viewport. Closing search cancels pending work.
  Closed search controls detach from the update tree; closing a searched pane
  also clears its query focus and returns input to the remaining pane.
- Dusk uses structured palette rows. Settings explain the cost of optional glass
  and how the shared motion controls relate. Welcome captions use semantic theme
  colors. Common navigation controls no longer grow or shift on hover.

## Verification

All builds and graphical runs used isolated output/settings/test directories rather
than replacing the user's running Hex executable. The existing test limits were
not loosened.

| Check | Result |
| --- | --- |
| Complete Hex unit suite | 447 passed. The initial sandbox run had 432 passes and nine access/pipe failures; rerunning with required test access passed all tests. |
| Horizon terminal/settings/chooser/flow selection | 213 passed, including four new incremental-search regressions. |
| Dusk graphical UX/start/tabs/splits/mouse/palette/themes/scroll/output/panes/TUI selection | 103 / 103 passed. |
| Final Dusk UX/effects/backdrop/settings run | 35 / 35 passed, including closing a searched pane and the new Flat-default assertion. |
| Dawn new UX/shell/launch/effects selection | 54 / 54 passed before adding the additional text-editor assertion; the broader editing run also passed every new UX assertion. |
| Isolated Dawn shell + garbage comparison | Baseline 14 / 14; revamp 14 / 14. Revamp hover sweeps: designer 80, code 111, layout code 73 bytes/tick, all below the unchanged 256-byte limit. |
| Combined Dawn settings/terminal/shell/launch/effects/garbage run | Revamp 439 / 440 including new UX checks. Baseline 433 / 434. Both fail the same layout-code hover allocation check after earlier suites. Baseline measured 369 bytes/tick; revamp measured 322. |
| Code/workspace regression run | 375 / 377 after explicitly enabling animation in the animation-test fixture. The remaining two are baseline fake-language-server failures; baseline code alone was 216 / 218 with the same failures. All new UX assertions pass. |

The new focused coverage checks default/reset consistency, saved appearance
preservation, the mode selector, shared/terminal motion interaction, focus restoration,
editor overflow and narrow geometry, inline search/cancellation/batching/result caps,
and return from scrollback. Existing graphical suites exercise embedded terminals,
profiles, splits, pointer interactions, palettes, settings, scrolling and console TUIs.

## Reproducing checks

```powershell
dotnet test Hex.Tests/Hex.Tests.csproj --no-restore
dotnet test ../horizon/Horizon.Tests/Horizon.Tests.csproj --no-restore --filter 'FullyQualifiedName~Terminal|FullyQualifiedName~SettingsPage|FullyQualifiedName~UIChooser|FullyQualifiedName~Choice|FullyQualifiedName~Flow'
dotnet build Hex/Hex.csproj -c Release --no-restore -o <isolated-output>
```

Run the resulting executable with `--selftest ux shell launch effects settings terminal garbage --exit --size 1600x900`
for Dawn, and `--dusk --cwd <isolated-output> --selftest ux start tabs splits mouse palette themes scroll output panes tui --exit --size 1600x900`
for Dusk. The explicit Dusk working directory is required by the existing file-picker
fixture, which searches for the built DLLs.

Use `HEX_SELFTEST_SHOTS` and `DUSK_SELFTEST_SHOTS` to capture optional graphical
checkpoints. `HEX_PROFILE=20` records slow update groups and allocations. Debug
profiling and screenshot capture were disabled for startup measurement.

`scripts/measure-ux-startup.ps1` compares two existing build directories with one
unrecorded warmup and 30 alternating recorded launches per build by default. Each
launch runs the shell regression suite. It records main-to-first-frame time and
first-frame memory from `HORIZON_LOG_STARTUP=memory`; the JSON and per-launch logs
are written to its output directory. The script restores its process environment
and does not change system execution policy.

## Warm-start comparison

Thirty recorded launches per build, alternating order, plus one unrecorded warmup
per build. Managed Release, .NET 11 RC2, Ryzen 5 5600X, Vulkan on an RTX 3060, 1600×900 window,
warm shader caches, the same synthetic shell-test workspace. Every launch passed
its shell checks. This is the main-to-first-rendered-frame trace, not process spawn
to a fully indexed repository. The small differences are within normal desktop noise.

| Metric | Baseline | Revamp |
| --- | ---: | ---: |
| Median first frame | 1,499 ms | 1,494 ms |
| p95 first frame | 1,520 ms | 1,515 ms |
| Mean first-frame working set | 163.9 MB | 163.6 MB |
| Mean first-frame private memory | 198.6 MB | 198.4 MB |
| First-frame managed heap | 18 MB | 18 MB |

The startup and first-frame-memory comparison stays within the proposed 5% limit.
The [60 recorded samples](ux-startup-samples.json) are retained with this report.
This is not a claim that all performance gates in the original plan are qualified.

## Qualification still required

The historical README AOT numbers are not the baseline for these managed Release
builds. First-frame working set is not settled idle memory, and update timings are
not end-to-end input latency. Startup results do not establish typing latency,
terminal throughput, idle GPU power, or performance on another GPU.

Retain the existing combined-suite allocation failure and fake-language-server
failures as separate follow-up investigations. Do not call the entire Dawn suite
green or weaken its thresholds. Full accessibility/UI Automation coverage,
integrated-GPU measurements, cross-platform QA, 100k-file workspace measurements,
and production AOT/presentation-latency qualification remain outside the evidence
collected on this Windows machine.
