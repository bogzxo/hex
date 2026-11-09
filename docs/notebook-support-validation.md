# Notebook rendering and table validation

Validated on Windows x64, .NET 11 RC2, Ryzen 5 5600X, RTX 3060, Vulkan, and Python 3.13. The authoritative engine in this workspace is `../horizon`, selected by the parent `Directory.Build.props`. Changes span Hex and that shared engine; a standalone Hex checkout needs the corresponding engine changes before its CI can build these features.

## Delivered

- Static `image/svg+xml` output, preferred over raster or plain representations. The SVG renderer handles Matplotlib definitions, glyph paths, clipping, transforms, gradients, and text. Embedded SVG attachments in Markdown use the same renderer.
- Typeset `text/latex`, preferred over `text/plain`. Markdown recognizes `$…$`, `$$…$$`, `\(…\)`, and `\[…\]`; code spans and fences retain literal delimiters. Transparent math textures follow light and dark theme text colors without rasterizing again.
- One background worker renders rich output. It waits for upload before producing another raster, bounds image dimensions, cancels queued work when a view disappears, and never uploads canceled output into a replacement cell. Existing renderer-exclusive texture creation and deferred release remain in use.
- Execution completion waits for both shell reply and IOPub idle. Display IDs retain their owning cell after execution. Clearing output or restarting a kernel removes stale display ownership.
- Table hover, selection, and fill changes retain draw-quad identity. Previously, conditional highlight quads shifted the text and grid slots, causing interpolation to blend unrelated geometry.
- Every notebook Markdown/HTML table has a compact **Copy CSV / Export CSV…** strip. Both actions serialize headers and every row in displayed sort order, including rows outside the viewport. CSV uses UTF-8 without BOM, CRLF records, and quoted escaping for commas, quotes, and embedded line breaks. Narrow strips collapse to icons with tooltips.
- **Notebook › Export as HTML...** (also in the command palette) saves the notebook on screen as one self-contained page, unsaved cells and current outputs included, without saving the notebook. Export now runs all code cells in order using the selected kernel before capturing fresh outputs; a matching installed kernel is required for code notebooks, but nbconvert is not. Errors, interruption, kernel failure, or edits during execution cancel the export and preserve an existing HTML file. The final page is written to a temporary sibling file before replacement. It uses the usual save dialog (notebook name, `.html` added, Save again to replace), writes off the editor's thread and offers **Open Exported File** when done. SVG outputs stay vector, each in an image of its own so ids and styles can't collide, and formulas are drawn as SVG paths, so nothing depends on the screen's density or the render scale. Output HTML and SVG pass an allowlist, the page has no script and loads nothing. Widgets and JavaScript outputs say so and show their static text. A right click on an output picture offers **Save Image As...**, which writes the SVG or PNG exactly as the notebook has it. Details and evidence are in the workspace at `artifacts/notebook-html-export/results.md`.

## Results

| Check | Result |
|---|---|
| Full Hex unit suite | 469 passed, 0 failed, 0 skipped |
| Focused engine table/search/Markdown suite | 13 passed |
| Published Windows Native AOT notebook suites | 41/41 checks passed |
| Native publish | Successful; existing trim/AOT warnings remain |
| Whitespace validation | Both repositories passed `git diff --check` |

The table regression was demonstrated before its fix: hovering changed draw counts from 14 to 16 without a frozen column, and 17 to 19 with one. Tests now verify stable keys, geometry, and draw counts across row/header hover, selection, and changed cell fills. The graphical suite also sorts a real engineering table, exports CSV, checks all rows and headers, and exercises compact controls.

The engineering notebook checks cantilever deflection, RC charging, laminar pipe pressure loss and its Reynolds assumption, steady wall conduction, a two-variable matrix solve, and numerical integration. Seven independent numerical reference checks pass. It also emits real Matplotlib SVG and PNG, LaTeX matrices/integrals, an updated display ID, and an HTML results table. Executed output survives save/read/write unchanged.

The native graphical suite checks actual GPU textures, Markdown formulas, SVG attachments, light-theme ink without a new texture, narrow equation bounds, malformed SVG fallback, table tools, and closure during a deliberately blocked background render. UI steps continue while the worker is blocked; canceled work never receives a texture.

The final test logs and screenshots are retained under `%TEMP%`:

- `hex-notebook-unit-complete.log`
- `hex-notebook-engine-final.log`
- `hex-notebook-complete-publish.log`
- `hex-notebook-complete-ui.out` and `.err`
- `hex-notebook-complete-shots/`
- `hex-engineering-proof/engineering.executed.ipynb` and rendered output PNGs

## Performance evidence

Ten alternating warm launch pairs compared the pre-notebook managed Release build (`hex-ux-ready`) with the background-rendering implementation (`hex-notebook-async`). Each build received an unrecorded warmup; every shell self-test passed. This comparison predates the CSV strip and transparent-math finishing changes, so it is evidence for the renderer dependency/startup path rather than a final-build performance certification.

| Main to first rendered frame | Baseline | Notebook build |
|---|---:|---:|
| Median | 1,516 ms | 1,529.5 ms |
| p95 | 1,581 ms | 1,562 ms |
| Mean working set at first frame | 163.1 MB | 165.6 MB |
| Mean private memory at first frame | 198.2 MB | 199.5 MB |
| Managed heap at first frame | 18 MB | 18 MB |

The measured median difference is +0.9%; working set is +1.5%. These are small warm-launch samples, not input-latency, idle-power, or large-notebook benchmarks. Raw samples are in `notebook-startup-samples.json`. Native rendering libraries add deployment size: the tested Windows publish contains approximately 12.2 MB of Skia and 2.1 MB of HarfBuzz DLLs, alongside the 43.9 MB native executable. Other assets are not included in those figures.

## Reproduce

From the Hex repository, using Python 3.13 and the shared engine changes:

```powershell
python -m pip install -r Hex.Tests/engineering-requirements.txt
dotnet test Hex.Tests -c Release
dotnet publish Hex/Hex.csproj -c Release -r win-x64 -o out/notebook-proof
```

Run from the published directory:

```powershell
./Hex.exe --selftest notebook notebookengineering --exit --size 1600x900
```

Open `samples/notebooks/engineering.ipynb` in Dawn and choose **Run All** for an interactive check. Its generator is `scripts/build-engineering-notebook.py`. The graphical tests require a window and Vulkan device. CI now installs the pinned engineering Python dependencies before running unit tests; remote CI and other platforms have not been run in this session.

## Boundaries

Subsequent real-file testing, native Markdown preview, formatted tables, responsive prose math and shared scrolling are covered in [the integration validation](notebook-explorer-integration-validation.md). Its results supersede the older inline-formatting and prose-scaling limitations below.

This is engineering notebook support, not complete browser/JupyterLab parity. JavaScript widgets, arbitrary HTML applications, SVG scripting/animation, remote resources, arbitrary TeX packages, and document compilation remain unsupported. Their original MIME bundles remain in the notebook. Static SVG/PNG, tables, Markdown, LaTeX, and plain text are the supported presentation paths.

SVG input is limited to 4,000,000 characters; rasters are bounded to 4,096 pixels per side and 8,000,000 pixels total. Math input is limited to 32,000 characters. Math uses CSharpMath's supported TeX grammar, with explicit control-word boundary normalization. Inline formulas render with their prose paragraph; Markdown styling inside that same paragraph does not have full native Markdown fidelity. Very wide formulas and prose blocks scale to the available width.

SVG 2 and TeX conformance beyond the exercised static engineering cases is not certified. Linux/macOS native publishing, accessibility/UIA, large-notebook virtualization, and native OS clipboard interoperability still need their own qualification. CSV serialization, toolbar wiring, and file export are tested; the suite avoids overwriting the user's clipboard.

Renderer references: [Svg.Skia](https://github.com/wieslawsoltes/Svg.Skia), [CSharpMath](https://github.com/verybadcat/CSharpMath), and [Python CI setup](https://github.com/actions/setup-python/tree/v5).
