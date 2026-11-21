# File icons from the operating system: a plan

Status: plan only, nothing here is built.

## Where Hex is today

Every file's icon is a glyph out of an icon font (codicons and file-type glyphs, `UISymbols`), picked by name and
extension in `HexExplorer.FileIcon` (Hex's own kinds: a workspace, a skin, a localisation file) and the engine's
`UIFileKinds.IconOf` (everything else), and tinted with the skin's syntax colours by `HexExplorer.FileIconColor` /
`UIFileKinds.ColourOf`. `UITreeItem.Icon` is the glyph's name, `IconColor` its tint. The `file-icons` self test checks
every kind resolves to a loaded glyph, that the common languages are told apart, and that a backup keeps its source's
icon and an unknown extension gets the plain file.

That is fast, crisp at every scale, themes with the look and never fails. What it can't do is show a file the way the
rest of the desktop shows it: an `.exe` with its own icon, a `.sln` with Visual Studio's, a `.psd` with Photoshop's, a
shortcut with its target's.

## What it would be

A setting, **File icons: Hex glyphs | System icons**, `file_icons` = `hex` (the default) or `system`. With `system`,
the explorer, the tabs, the quick open list, Find in Files' results and the file dialogs ask the platform for a file's
icon and draw that picture where they draw the glyph now. Folders keep Hex's glyph either way (the system's folder is
a yellow manila folder on every row, which is noise in a tree). Hex's own kinds (`.hexspace`, `skin.hor`, a
localisation file) keep their glyphs too: the system has nothing better to say about them.

### The shape of it

```
Hex.Shell/Icons/
  ISystemIconSource        Fetch(key, pixels) -> RGBA pixels or null. One per platform, no UI.
  WindowsIconSource        SHGetFileInfo / IShellItemImageFactory
  MacIconSource            NSWorkspace iconForFile / iconForContentType
  FreedesktopIconSource    shared-mime-info + XDG icon theme lookup, PNG and SVG
  SystemIcons              the cache, the queue and the fetch thread; hands out UIRegion or "not yet"
Horizon.UI
  UIIconAtlas (new)        a growable atlas page of RGBA cells, packed by size, uploaded on the render thread
  UITreeItem.Picture       a UIRegion drawn in place of Icon when set (tabs and lists the same)
```

`SystemIcons.For(path, pixels)` answers at once: the cached `UIRegion` when it has one, otherwise null and the request
goes on a queue. The caller draws its glyph until then; when the picture arrives `SystemIcons.Changed` fires and the
rows ask again (the explorer marks itself dirty, which it does already for a folder change). Nothing on the UI thread
ever waits for the shell.

### Sizes and DPI

The explorer's row icon is 16 units, the tabs' 16, Quick Open's 16, the welcome page's recent list 20. At 100 / 150 /
200% that is 16, 24 and 32 pixels (20 / 30 / 40 for the welcome list). The source is asked for the exact pixel size
the UI will draw (`round(units * compositor.Scale)`), never a 32 scaled down to 24, which is where system icons go
blurry. The cache key includes the pixel size, so a window moved to a 150% screen asks again for 24 and keeps the 16s
for the other window. An icon is uploaded once per size into the atlas; at most three sizes are live in practice.

Windows: `IShellItemImageFactory::GetImage(SIZE{px,px}, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK)` gives an HBITMAP at
the asked size with alpha (premultiplied, top-down). `SHGetFileInfo(SHGFI_ICON | SHGFI_SMALLICON)` is the fallback for
the old shell (it only knows 16 and 32 and is scaled otherwise). `SHGetFileInfo` with `SHGFI_USEFILEATTRIBUTES` asks
by extension without touching the disk.

macOS: `NSWorkspace.sharedWorkspace.iconForContentType:(UTType)` (by type, macOS 11+) or `iconForFile:` (per file),
then `NSImage` drawn into an `NSBitmapImageRep` of `px * px` at the screen's backing scale through
`bestRepresentationForRect:context:hints:`, read back as RGBA. Called through `objc_msgSend` the way
`DesktopWindow.IsFullscreen` already does, no bindings.

Linux: the file's MIME type from shared-mime-info (`/usr/share/mime/globs2`, by name; `magic` is not worth reading for
an icon), its icon name (`/usr/share/mime/generic-icons` and the MIME type with `/` as `-`, then the generic one, then
`text-x-generic`), and that name looked up in the current theme as the Icon Theme Specification says: the theme from
`gsettings get org.gnome.desktop.interface icon-theme` or `kreadconfig5 --group Icons --key Theme` (asked once, at
start), its `index.theme` directories, `Inherits=` down to `hicolor`, the directory whose `Size`/`Scale` fits the
pixels best, `.png` loaded with the engine's image loader and `.svg` rasterised at the exact size with Svg.Skia, which
Dawn already carries for notebooks (`NotebookRender.Svg`).

### Caching: per extension or per file

Per extension, almost always. `Program.cs`, `README.md` and `photo.jpg` look the same as every other file of their
kind, so the key is `(extension, pixels)` and a workspace of ten thousand files costs a few dozen icons. It is asked
with the extension alone (`SHGFI_USEFILEATTRIBUTES`, `iconForContentType`, the MIME glob), which never opens the file.

Per file, for the kinds whose icon is inside the file or points somewhere else:

| Kind | Windows | macOS | Linux |
| --- | --- | --- | --- |
| Programs | `.exe` `.scr` `.cpl` `.msi` (a `.dll` is by extension) | `.app` (a folder, shown as a file) | `.desktop` files (their `Icon=`) |
| Shortcuts | `.lnk` `.url` (their target's icon, plus the arrow) | aliases | symlinks show their target's |
| Own icon | `.ico` `.cur` (the file is the icon) | `.icns` | none |
| Folders with icons | `desktop.ini` (not used, folders keep Hex's glyph) | custom folder icons (same) | `.directory` (same) |

The per-file key is `(full path, last write time, pixels)`, the last write time so a rebuilt exe picks up a new icon.
Per-file entries are capped (512) and dropped least recently used; per-extension entries are never dropped in a
session. Nothing is kept on disk: asking the shell for a cached icon is cheaper than keeping a cache of our own honest.

### The fetch thread

One background thread, created on first use:

- Windows: a thread of its own in a single threaded apartment (`CoInitializeEx(COINIT_APARTMENTTHREADED)`), which the
  shell's icon extractors want (several of them are STA-only COM servers, and some third-party icon handlers deadlock
  in the MTA). The same pattern as the window's OLE drop target thread in the engine (`WindowsDropTarget`).
- macOS: AppKit's `NSWorkspace` icon calls are thread safe for reading since 10.6; the rasterising into a bitmap rep
  happens on the same background thread.
- Linux: plain file reads and Svg.Skia, any thread.

The queue is last in, first out with the visible rows first: scrolling the explorer fast asks for the rows in sight
now, not the ones that went by. A request for a key already in flight is not queued again. Each fetch has a budget
(250 ms); a shell extension that hangs past it is abandoned for that key for the session (the thread is not killed,
the key is marked failed and the glyph stays), and three such hangs turn system icons off for the session with a line
in the status bar saying so.

Pixels go to the atlas on the render thread (`UIIconAtlas.Upload` queued for the next frame's exclusive work), so the
fetch thread never touches the GPU.

### Monochrome looks and theming

System icons are colour pictures and ignore the look. Three rules keep them from shouting:

1. A look marked monochrome (the high-contrast looks, and any skin with `file_icons_tint: true`) keeps Hex's glyphs
   whatever the setting says, and the setting's description says so.
2. With system icons on, `IconColor` is ignored for pictures (they're not tinted), but a leftover (`.orig`, `.bak`)
   is still drawn at the dim alpha the glyph would have had.
3. A picture that is almost entirely one colour with alpha (Linux symbolic icons, `*-symbolic.svg`) is treated as a
   mask and tinted like a glyph. The freedesktop source asks for the non-symbolic name first.

### AOT and trimming

Hex is published AOT. Nothing here may use COM interop that the trimmer can't see:

- Windows: `[LibraryImport]` for `SHGetFileInfoW`, `SHCreateItemFromParsingName`, `DestroyIcon`, `GetIconInfo`,
  `GetDIBits`, `DeleteObject`; `IShellItemImageFactory` called through its vtable with function pointers (slot 3,
  `GetImage`), the way `WindowsDropTarget` calls `IDataObject`. No `[ComImport]`, no `Marshal.GetObjectForIUnknown`.
- macOS: `objc_msgSend` through `[LibraryImport("/usr/lib/libobjc.A.dylib")]`, selectors registered once.
- Linux: no native calls at all; Svg.Skia and SkiaSharp are already AOT-clean in Dawn's publish (they ship their
  native library per RID).
- `IsAotCompatible` stays true for Hex.Shell; the analyzers are the check.

### What fails, and how

Everything fails to the glyph. A source that throws, returns null, times out or hands back an empty or fully
transparent bitmap leaves that key on the glyph for the session. A platform with no source (Android, iOS, a Linux with
no icon theme installed, a headless self test) answers null for everything, and the setting is hidden where there is
no source. The atlas filling up (a page is 1024 square, 4096 cells at 16 px) adds a page; a fourth page means
something is wrong and new requests are refused (glyphs) with a warning in the log.

### Cost

- Memory: a 32 px RGBA icon is 4 KB; a busy session with 80 kinds at three sizes is about 1 MB of atlas.
- Time: `SHGetFileInfo` by extension is 0.1 to 1 ms warm, 5 to 50 ms the first time a handler loads;
  `IShellItemImageFactory` per file 1 to 20 ms; `iconForContentType` under 1 ms; an SVG from a theme 1 to 3 ms. All of
  it on the fetch thread. The UI thread pays one dictionary lookup a row.
- Start: nothing until the first row is drawn with the setting on.

### Tests

- Unit (Hex.Tests): the cache keys (extension vs per-file kinds, pixel sizes, last write time), the LRU, the failure
  marking, the freedesktop lookup over a fake theme directory (an `index.theme` with two sizes, `Inherits=hicolor`, a
  PNG and an SVG), the MIME glob matching.
- Windows-only unit: `.txt` at 16 and 32 px gives a non-empty bitmap of that size; an `.exe` (Hex's own) differs from a
  `.txt`; a `.lnk` made in the test resolves.
- Self test `file-icons-system`: the setting on, the explorer over a folder of the kinds in `file-icons`, every row's
  picture or glyph there within 3 s, the glyph shown first, a monochrome look keeping glyphs, the setting off again
  bringing every glyph back, pictures with `HEX_SELFTEST_SHOTS` at 100, 150 and 200%.

## Estimate

| Part | Work |
| --- | --- |
| `UIIconAtlas` in the engine, `Picture` on tree items, tabs and lists drawing it | 2 days |
| `SystemIcons` (cache, queue, fetch thread, budget, Changed), the setting and its page | 1.5 days |
| Windows source (`SHGetFileInfo`, `IShellItemImageFactory`, HBITMAP to RGBA, `.lnk`/`.exe`/`.ico` per file) | 2 days |
| macOS source (objc_msgSend, `iconForContentType`/`iconForFile`, bitmap rep at backing scale) | 2 days, plus a day on the MacBook to check Retina sizes |
| Linux source (MIME globs, theme lookup with inheritance, PNG/SVG, symbolic tinting) | 3 days, plus a day across GNOME and KDE themes |
| Monochrome rules, failure handling, tests and pictures at three scales | 2 days |
| **Total** | about 2.5 weeks, Windows first (the atlas, the cache and Windows are a usable first step at about a week) |
