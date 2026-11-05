# Jupyter notebook support

Implemented and validated for engineering notebooks. See [validation, measurements, and compatibility boundaries](notebook-support-validation.md).

## Acceptance

Preserve nbformat 4 cells, metadata, attachments, and MIME bundles when saving. Run notebooks through the existing native Jupyter transport. Render engineering results as SVG plots, typeset LaTeX, raster images, tables, and Markdown. Support dollar and backslash math delimiters in Markdown without treating fenced code as mathematics. Prefer rich representations over `text/plain`. Keep malformed output recoverable and editable.

Reuse the existing deferred texture upload and release path. Decode each immutable output once; do not introduce browser processes, scripts, or work in the idle render loop. Bound image dimensions and source sizes. Disable external SVG resource loading.

## Implementation order

1. Add a dedicated SVG/LaTeX renderer with pinned dependencies and pixel-level tests.
2. Integrate MIME selection, Markdown math, theme invalidation, and picture lifetime management.
3. Correct execution completion and display-update ownership across Jupyter channels.
4. Add a reusable engineering notebook and execute it through a real Python kernel. Check numerical answers, rich output, save/reopen, and graphical notebook behavior.
5. Run focused tests followed by the full unit suite and notebook graphical suite. Record exact limitations and measurements.
6. Stabilize native table hover geometry and add compact CSV copy/export controls, preserving sorted row order and complete data.

## Compatibility boundary

SVG support targets static SVG documents, including Matplotlib paths, transforms, definitions, clipping, gradients, and text. Embedded JavaScript, animation, remote assets, arbitrary HTML applications, and interactive widget runtimes require a separate trusted execution design. Preserve their MIME data and show available static representations. Math support is TeX mathematics supported by CSharpMath, not arbitrary TeX packages or document compilation.

## Dependencies

Svg.Skia 5.2.3 supplies the SVG renderer. CSharpMath.SkiaSharp 1.0.0-pre.2 supplies TeX layout compatible with modern SkiaSharp. The math package is a prerelease; numerical, rendering, and publish validation must cover this integration. These libraries load when notebook rich output is used. Native Skia adds deployment size; it avoids maintaining a partial SVG or equation engine in Hex.
