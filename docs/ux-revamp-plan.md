# Hex UX revamp: performance first

Date: 2026-10-28. Scope: Dawn, Dusk, their shared shell, and the embedded terminal.
Status: core revamp implemented. See [implementation and validation](ux-revamp-validation.md)
for delivered behavior, measured checks, and remaining qualification.

## Direction

Make the current native interface easier to read, navigate, and predict. Keep the
editor or terminal as the dominant surface. Use spacing, typography, stable layout,
and explicit state to establish hierarchy. Every change must preserve responsiveness,
startup, low idle work, and terminal throughput.

Reuse Horizon rendering, HIDL layouts, shared settings, UIChooser, and TerminalPanel.
Do not introduce a web view, a second UI framework, or a replacement terminal engine.
Keep user themes, key bindings, extensions, saved layouts, and portable operation.

## Evidence and limits

Directly inspected the existing Release build identifying itself as Hex 2.3.0:
Dawn's welcome, restored Outline sidebar, title/menu bar, activity bar, collapsed
bottom panel, status bar, and folder picker. The initial maximized view and a smaller
window exposed the same basic hierarchy. This was not a controlled DPI comparison.
Further keyboard navigation did not produce a verifiable settings/editor screen.
Launching additional review instances did not expose additional targetable windows.
Do not treat this review as a completed interactive test of editing or Dusk.

Reviewed the current source for Dawn layout, view switching, sizing, palettes,
settings, status, materials, performance instrumentation, Dusk layout, commands,
materials, update loop, and the shared terminal tabs/splits. The existing binary may
not match every source detail. Findings below distinguish observed issues from
source-derived opportunities. No benchmark was run and no timing below is a result.

| Finding | Evidence | Consequence / proposed response |
| --- | --- | --- |
| Welcome restored an empty Outline sidebar | Observed | Give empty workspaces useful navigation or collapse the irrelevant sidebar; preserve a user's deliberate choice during normal work. |
| Recent area was empty while secondary welcome text was subdued | Observed | Prioritize Open Folder, Open File, and recent work; improve text hierarchy and wrapping before adding onboarding. |
| Many small navigation targets compete around a large central surface | Observed | Make active state and labels clearer; establish consistent control sizes without making all chrome larger. |
| Folder picker is already integrated with the application's styling | Observed | Retain it; validate path entry, focus, keyboard navigation, long paths, and selection feedback. |
| Tabs share a row with view selectors, Run, and layout naming | `Hex.Dawn/Assets/hex/shell/editor_tabs.hor` | These controls compete for width. Specify an overflow order and reserve useful tab space. This needs live narrow-window validation. |
| Fluid Glass, motion, and caret glide are defaults | `Hex.Core/Settings/HexSettings.cs`, `Hex.Shell/Settings/HexLooks.cs` | Establish a solid, motion-free performance baseline; measure optional effects independently. Glass is not proven to be a bottleneck by this review. |
| Dawn and Dusk already persist when switching modes | `Hex.Shell/HexModes.cs` | Improve mode identity and focus restoration around the existing mechanism; retain lazy creation of the other mode. |
| Terminal already has focus frames, split titles, pane zoom, overflow list, and search | `horizon/Horizon.Terminal/TerminalPanel.cs`, `.Overflow.cs`, core plugin | Improve visibility and consistency of existing actions instead of duplicating them. |
| Dawn palette uses structured rows; Dusk builds title/shortcut strings | `DawnScene.CommandPalette.cs`, `DuskScene.Palette.cs` | Use the same structured choice presentation in both modes. |
| Settings already unify editor and terminal, with search and workspace scope | `Hex.Dawn/Settings/DawnScene.SettingsPage.cs` | Clarify organization, inheritance, and effect costs within the existing settings surface. |
| Dirty flags, cached status values, and focused profiling already exist | `DawnScene.cs`, `DawnScene.StatusBar.cs`, `HexProfiler.cs` | Extend these patterns; avoid new per-tick strings, whole-tree rebuilds, or continuous animation. |

## Performance acceptance comes first

Phase 0 must record a reproducible baseline before changing defaults. The README's
roughly 0.5 s usable startup, 128 MB working set, and zero idle-tick allocations are
historical claims on specified hardware, not measurements of this checkout.

Use a fixed Release/AOT build, repository revision, workspace, font, window size,
display scale, refresh rate, and power configuration. Separate shader-cache cold
launch, warm launch, repository/LSP readiness, and first usable frame. Measure each
mode alone, both modes after first switch, embedded Dusk, and optional effects.
Include an integrated-GPU machine as well as the available discrete-GPU machine.

Proposed gates, finalized against Phase 0 measurement noise:

| Metric | Gate |
| --- | --- |
| Warm start to usable editor / terminal | Median and p95 within 5% of the same-machine baseline; run at least 30 launches per configuration. |
| Typing and terminal echo | End-to-end input-to-present p95 no worse than baseline; aim for one display interval at the target refresh rate. CPU update time alone does not establish this. |
| UI update and rendering | No new sustained frame-budget overruns at 60/120 Hz; report CPU and GPU timing separately. |
| Settled idle allocations | Zero new recurring UI-thread allocations; account separately for existing background work and all-thread allocations. |
| Idle CPU/GPU and wakeups | No statistically meaningful increase in focused, unfocused, minimized, and hidden-mode states. Caret timing must not force continuous full-window redraw. |
| Memory | Settled working set and managed heap within 5% of baseline on identical workloads; investigate all persistent growth after repeated mode/panel cycles. |
| Terminal throughput | No more than 5% regression in bytes processed per second on identical output replay; no lost input/output, unbounded queues, or interactive starvation. |
| Large lists | Palette, explorer, and search stay responsive with 100k indexed paths; materialize bounded visible results and cancel superseded work. |

Use `HORIZON_LOG_STARTUP=memory`, `HEX_PROFILE`, the existing garbage self-tests,
and terminal output/scroll/TUI tests as starting points. Add opt-in bounded timing
for missing metrics rather than shipping continuous diagnostics. Profile rendering
and presentation separately from `HexProfiler`'s editor-thread update groups.

Measure the existing aggressive collection after three seconds in both scenes for
input stalls. Change its scheduling only if a repeatable interaction pause is proved;
do not remove it on speculation or conceal its memory tradeoff.

## Proposed experience

### 1. Shared shell and visual language

- Keep a compact title bar with clear workspace context. Replace the single
  destination-only mode button with an explicit `Dawn | Dusk` selector showing the
  active mode. Keep Ctrl+Alt+D and the existing menus/palette commands.
- Introduce shared semantic tokens for surface, raised surface, border, primary
  text, secondary text, accent, warning, and error. Keep syntax colors independent.
- Use an initial spacing scale of 4/8/12/16 logical units, restrained radii, and
  consistent row heights. Validate at 100%, 150%, and 200% system scale before
  settling the values. Keep editor text zoom independent from shell scale.
- Active tab, keyboard focus, selected row, and hover must look distinct. Use an
  outline or indicator as well as color. Avoid moving or growing controls on hover.
- Make solid surfaces and immediate caret movement the proposed new-install
  default. Preserve explicit existing user choices. Keep Fluid/Frosted looks
  available and label their visual tradeoffs. A complete motion-off setting must
  disable decorative transitions and caret glide; audit the existing Flat preset
  because it leaves `ui_animation` at 1.0.
- Keyboard focus must be visible throughout. Audit UI Automation exposure: the
  inspected custom UI exposed only the top-level window to the inspection tool.
  Verify with accessibility tooling before asserting screen-reader support.

Performance: shared cached styles; opaque default surfaces; invalidate only changed
state; no idle effect timers or new shader variants on the typing path.

### 2. Dawn: faster entry and steadier editing

- Welcome: lead with Open Folder and Open File; keep New File, recent workspaces/files,
  and visible shortcuts nearby. Put layout creation and reference links below the
  primary entry actions. An empty Recent area should offer a useful next action.
- With no document, avoid restoring a useless empty Outline by default. Do not
  change the sidebar automatically after a user explicitly selects it.
- Give document tabs priority. Keep primary Run/Debug accessible, move less frequent
  view actions to a small selector when space runs out, and keep layout naming in
  the designer context. Define minimum tab width and a visible overflow list.
- Preserve the existing document-specific Code/Design/Table/Notebook behavior.
  Hide unavailable actions and keep applicable ones in stable positions.
- Keep explorer, search, Git, outline, and debug where users expect. Improve labels,
  tooltips with actual configured shortcuts, selection, and empty/loading/error states.
- Keep Problems/Output/Terminal consistently positioned at the left edge of the
  bottom panel header; show useful counts and an obvious resize/maximize affordance.
- Keep the status bar quiet: workspace/branch and diagnostics at left; caret/language
  and essential file state at right; collapse lower-priority metadata at narrow widths.
  Retain cached text updates already present in the source.
- Preserve focus, cursor, selection, undo, scroll, and split state when opening and
  dismissing palettes, settings, panels, and the other mode. Escape returns focus to
  the originating work surface without changing the document.

Performance: use existing dirty flags; avoid rebuilding all rows on pointer motion;
cancel old search work; retain cached document editors. Only add virtualization
where large-data measurements show the current implementation needs it.

### 3. Dusk: readable panes and predictable navigation

- Keep the shell/output dominant. Use the existing accent focus frame and split
  headers consistently, with a legible active pane title and optional working
  directory from existing shell integration. Never spawn a subprocess per tick to
  discover the directory or guess unsupported shell state.
- Make New Tab and its profile dropdown distinct. Expose split right/down, zoom,
  rename, and tab list through consistent menus and palette entries. Retain current
  bindings and the existing overflow implementation.
- Specify tab naming priority and full-title tooltips so shell-generated titles,
  renamed tabs, and profile names remain understandable with many tabs.
- Make scrollback Find show query, match position, next/previous, and close in a
  compact stable bar. Verify existing capabilities before adding controls. Preserve
  the viewport while reading older output; provide a clear return-to-live action
  when newer output is below it.
- In split layouts, use pane headers for local actions instead of repeating global
  controls. Zoom should make its temporary state and return action clear.
- Treat terminal shortcut routing as part of UX: shell editing, alternate-screen
  TUIs, history search, selection copy, bracketed paste, and application commands
  must keep their current input semantics. Validate before changing any shortcut.
- Apply the same controls to embedded Dusk. Do not assume embedded and standalone
  sessions are the same session; make their relationship clear without silently
  merging shells or changing working directories.

Performance: process output off the input-critical path where the existing
architecture permits; bound UI work per update without dropping terminal data;
reuse visible-row drawing and existing terminal state. Do not reparse scrollback or
query every pane's process tree each frame.

### 4. Commands, settings, and feedback

- Present Dawn and Dusk palettes with the same structured icon/title/category/keycap
  rows, recent-command ordering, empty state, keyboard handling, and focus return.
  Reuse UIChooser; retain contextual command availability and plugin commands.
- Cache searchable command metadata until commands, keys, language, or plugins
  change. Evaluate context eligibility correctly; do not cache enabled state forever.
- Keep the unified settings window. Lead with common editor/terminal settings and
  group appearance, behavior, and advanced effects clearly.
- Make user/workspace scope and overridden values explicit. Offer a per-setting
  reset to inherited/default value and keep the existing file-editing route.
- Mark settings that affect blur, motion, minimap, or background rendering with a
  concise cost explanation. Do not invent live performance estimates.
- Reuse status and notifications. Routine completion can stay in status; actionable
  failures should remain discoverable with a direct recovery action. Avoid repeated
  toasts from the same failing background job and avoid notification animations at idle.

## Delivery order and implementation boundaries

| Phase | Deliverable | Primary code areas | Exit condition |
| --- | --- | --- | --- |
| 0: evidence | Reproducible benchmarks and live walkthrough of file opening, editing, search, settings, Git/debug, embedded terminal, standalone Dusk, and mode switching | Existing profiler, startup traces, self-tests | Baselines recorded; uncertain findings confirmed or removed. |
| 1: shell foundation | Shared tokens, legible focus/selection, motion-off behavior, explicit mode selector, adaptive sizing rules | `Hex.Shell`, Dawn/Dusk Materials/Chrome/Modes, HIDL shell layouts | Both modes pass narrow-window/DPI and focus checks; performance gates pass. |
| 2: Dawn flow | Useful welcome, tab/toolbar overflow, panel header, consistent sidebar/status states | Dawn Welcome/PlainWelcome/Tabs/Views/Panel/StatusBar, `Assets/hex/shell` | Open workspace → find file → edit → inspect problem → return works with keyboard and pointer. |
| 3: Dusk flow | Coherent tab/profile/split controls, pane identity, search/live-output states | Dusk Chrome/Palette, shared `TerminalPanel`/`TerminalView`, core plugin | Tabs/splits/search/TUIs behave correctly embedded and standalone; throughput and latency gates pass. |
| 4: consistency | Structured palettes, settings scope/reset clarity, bounded feedback | Shared UIChooser/SettingsPage, Dawn/Dusk adapters | Commands and settings behave consistently; final accessibility and regression review passes. |

In this workspace, the parent `Directory.Build.props` selects the shared `../horizon`
checkout; that is where shared terminal/UI changes belong. Standalone Hex clones
use the `hex/horizon` submodule instead. Keep the corresponding engine changes with
Hex when integrating or publishing. Do not modify the sibling `dusk` checkout or other
Horizon copies as part of this revamp. Split changes into reviewable commits with
screenshots and before/after performance measurements. Preserve existing user
settings; new defaults apply to new or unset configuration, with rollback available.

## Review scenarios and completion

Exercise 1280×720 and 1920×1080 windows plus a high-DPI display; also test smaller
widths for graceful overflow. Include long/localized labels, identical filenames,
many tabs, light/dark/high-contrast themes, and keyboard-only navigation.

Use an empty workspace, the Hex repository, a large file index, large source files,
multiple split terminals, burst output, and a supported alternate-screen TUI.
Run relevant existing workspace/code/garbage and terminal panes/scroll/output/TUI
checks as each slice lands. Add focused regression coverage for new overflow,
focus restoration, mode switching, and settings inheritance behavior.

Compare the same scenario before/after with diagnostics disabled for the final
timing runs. Measure both immediate response and settled resource use; an attractive
first frame must not conceal later stalls or background cost.

The revamp is complete when the primary flows require less searching for controls,
focus and active context remain clear, existing input/session behavior is preserved,
and every agreed performance gate passes. Defer optional visual polish that fails
those gates. Do not expand this into a terminal renderer or IDE architecture rewrite.
