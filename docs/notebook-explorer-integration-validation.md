# Native documents, explorer and scrolling validation

This extends [the original notebook validation](notebook-support-validation.md) and implements [the integration plan](notebook-explorer-integration-plan.md).

## Behavior

Markdown preview is now a native Hex view for `.md` and `.markdown`. Use **Preview** or **F7** to switch between source and preview. It shares notebook Markdown, formatted tables, code, math, CSV controls and zoom. Unsaved changes update the preview; hidden/closed previews release their render resources. Saved legacy preview selections migrate automatically. The catalog extension is removed and old installed copies are excluded from discovery.

**Settings → Appearance → Motion → Scroll speed** controls scrolling across Dawn and Dusk. The shared `scroll_speed` setting defaults to 1 and ranges from 0.25 to 5 in 0.25 increments. Content scrolling scales once while input bubbles through controls. Ctrl+wheel zoom and numeric-value gestures retain their original units. Discrete controls and terminal programs accumulate fractional wheel input.

Notebook zoom uses the existing editor zoom preference and commands, including Ctrl+wheel. Markdown tables wrap cells and preserve emphasis, inline code and formulas. HTML DataFrames retain the virtualized sortable table. Both expose Copy CSV and Export CSV. Cached formulas scale during zoom; prose formulas reflow only when width or zoom changes. The tested real notebook's cached cell update/layout loop allocates **0 bytes per tick**.

Explorer refresh preserves selection and viewport. Explorer-focused Ctrl+C/X/V supports multiple items; Escape cancels a staged cut. Context menus expose the same actions plus Move To, using the existing folder picker. Transfers run off the UI thread, avoid silent overwrites and recursive destinations, preserve failed cuts for retry, and update moved document paths. Recursive copies do not follow links. Copy failures can leave a partial new destination; cross-volume directory moves can fail and remain available for retry. This is Hex's internal file clipboard.

Common terminal triangles, arrows and prompt chevrons use bundled cell-sized glyphs when painted. Original terminal characters remain intact for copying and input.

## Real engineering notebook

The supplied `C:\Users\bogdan\Downloads\beam_optimisation.ipynb` contains 24 cells, four saved PNG plots and three HTML DataFrames. Tests copy it to an isolated temporary directory before execution. Its numerical self-checks, formulas, tables, plots, CAD exports and executed notebook save/reopen are exercised. The Downloads original is never saved by the harness.

Original SHA-256: `D7DCECB07DDEA9F8089CE4C3A3FDFDDBDAA9F230D0079A9DC074AA605F306838`.

Testing exposed unsupported `\dfrac`, inline HTML subscript formatting, clipped error messages and per-tick reflow allocations. These were corrected, with regression coverage. Screenshots cover the opening assumptions table, representative later cells, plots and zoom.

## Scope

Native preview reuses Hex's document renderer; arbitrary browser HTML, JavaScript widgets, remote assets and arbitrary TeX packages remain outside its supported rendering paths. Static SVG and math retain the resource bounds documented in the original validation. Large-notebook virtualization and other-platform native builds still require separate qualification.

## Checks

Validation on Windows x64 with Python 3.13 and a Vulkan device:

- Full Hex unit suite: 481/481 checks. An earlier named-pipe test timed out during concurrent builds; its isolated retry and subsequent full runs passed.
- Focused engine suite: 34 checks covering wheel routing, fractional terminal scrolling, viewport preservation, glyph geometry, table hover stability, terminal search and Markdown code rendering.
- Native Dawn suite: 67/67 checks across notebook, engineering notebook, supplied notebook, explorer files and native Markdown preview. The final native real-file idle check reports 0 bytes/tick; Run All, CAD exports and output roundtrip pass. The original notebook's SHA-256 is unchanged after execution.
- Native Dusk suite: 50/50 checks across symbols, inline search, mouse behavior, TUIs, backdrop and shared settings. Directional-glyph screenshots were also inspected.
- Native AOT publish succeeds. Its performance check exposed 120 bytes/tick from boxed DataFrame column enumeration; an indexed loop eliminates that allocation.
- `git diff --check` passes for Hex, the shared engine and the extension catalog.

To repeat the native Dawn run from a published build directory:

```powershell
$env:HEX_NOTEBOOK_TEST_FILE = 'C:/Users/bogdan/Downloads/beam_optimisation.ipynb'
$env:HEX_NOTEBOOK_TEST_EXECUTE = '1'
$env:HEX_SELFTEST_SHOTS = "$env:TEMP/hex-notebook-shots"
./Hex.exe --selftest markdown notebook notebookengineering notebookfile explorerfiles --exit --size 1600x900
```

The graphical harness uses temporary documents and configuration. It does not replace the user's running Hex binary. Review changes in both the Hex and shared Horizon repositories, plus the nested `hex-extensions` catalog.

## NativeAOT JSON follow-up

JSON DOM array insertions now explicitly select `Add(JsonNode)` in MCP batch replies and notebook cell/output/line serialization. The generic `Add<T>` overload can otherwise select reflection-based CLR serialization, even for a `JsonObject` or `JsonValue` already representing JSON.

Hex.Core treats IL2026 (trimming) and IL3050 (dynamic code) diagnostics as errors. The unit-test runtime explicitly disables default JSON reflection serialization. Existing notebook roundtrip and protocol tests therefore exercise the native restriction during ordinary managed test runs too.

The Windows x64 native publish succeeds without System.Text.Json trimming or dynamic-code diagnostics. The native executable passes 50/50 MCP and notebook checks, including a new reflection-disabled mixed batch reply test and notebook output save/reopen. Unrelated Silk.NET native-loader trimming warnings remain.
