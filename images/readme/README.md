# README image provenance

Captured on **4 November 2026** from the same Windows x64 NativeAOT publish used for the
[benchmarks](../../docs/benchmarks.md). These show the local 2.8.0 development working tree, not necessarily the
published `v2.8.0` release. All application images are unedited **1600 × 900 PNGs** from Horizon's framebuffer capture.
No generated mockups, compositing, recoloring or post-capture removal of UI was used.

| File | Actual application state |
| --- | --- |
| `editor.png` | Dawn, Flat skin / Graphite theme, C# example, explorer and minimap, code zoom 115% |
| `light.png` | The same file, layout and zoom, Flat / Light |
| `sunset.png` | The same file, layout and zoom, Flat / Sunset |
| `terminal.png` | Dusk, Graphite, PowerShell, text scale 0.21, minimap disabled |

The C# file is a small demonstration program, not a claim about a running application. Dusk runs a showcase script
that prints a real directory listing and five real Hex Git commits, followed by ANSI color swatches and feature text.
Its prompt is shortened to `PS Signal>`. The script's labels are demonstration content, not benchmark or test results.
Screenshot preferences were isolated from the user's settings and from benchmark settings.

Capture uses the engine's existing `HORIZON_SCREENSHOT` hook, for example:

```powershell
$env:HEX_HOME = 'C:\path\to\isolated-screenshot-settings'
$env:HORIZON_SCREENSHOT = 'C:\path\to\editor.png@5'
./Hex.exe --window --fresh --size 1600x900 --folder C:\path\to\example --open C:\path\to\example\Program.cs
```

The `@5` captures after five seconds. For the editor variants, the isolated `settings.hor` contains:

```javascript
let settings = {
    editor: {
        editor_skin: "flat",
        editor_theme: "graphite", // or "light", "sunset"
        code_zoom: 1.15,
        panel_collapsed: true
    }
};
```

`startup.svg` is a data chart, not an application screenshot. Bars start at zero and show the measured medians;
white whiskers show the minimum and maximum of ten runs per mode. Values come from the linked benchmark dataset.
