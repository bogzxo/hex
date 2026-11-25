# Making Hex feel native on macOS: plan

Status: plan, 2026-11-24. Owner: the macOS workstream (orchestrator chat #110, work done by Sonnet agents).
Verification Mac: `bogzmacpro.local`, an Apple M6 running macOS 27.2 (arm64), reached over SSH. Hex Link had no
Mac connected when this was written.

This plan covers what Hex does on a Mac today, what a developer tool needs to feel like a Mac app (judged against
Xcode, Zed, Nova and VS Code), and the ranking of those features. It splits the top ones into work packages, each
with a contract and the files it owns. It also says, for each package, what can be tested on Windows and what only
a real Mac can show.

---

## 1. What exists today

### 1.1 Engine (horizon/, the Horizon engine)

| Area | Today | Where |
| --- | --- | --- |
| Windowing | Silk.NET 2.23 over **SDL2** on macOS and Linux, GLFW on Windows (`HORIZON_WINDOWING` overrides). `Native.Cocoa` gives the NSWindow. | `Horizon.Platform.Desktop/Window/DesktopWindow.Sdl.cs:15`, `DesktopWindow.cs:76,103` |
| Frame | Custom frame: a borderless window (`WindowBorder.Hidden`) with a title bar drawn by the UI. **No traffic lights.** Dragging is tracked by hand from `SDL_GetGlobalMouseState`, about 1 kHz, because Cocoa's SDL hit test swallowed presses. The result has no Aqua tiling or snapping and no native double-click-to-zoom. | `DesktopWindow.Frame.cs:82-190`, `DesktopWindow.HitTest.cs:31`, `DesktopHost.cs:27-33` |
| Corners | A Mac branch rounds the content view's layer through objc. | `DesktopWindow.Corners.cs:30-85` |
| Full screen | `SDL_SetWindowFullscreen(DESKTOP)`. `IsFullscreen` also reads `styleMask & NSWindowStyleMaskFullScreen` to catch the green button. | `DesktopWindow.cs:327-370` |
| Rendering | Vulkan through MoltenVK (portability bit). Mailbox, Fifo or FifoRelaxed present modes, sRGB only, with no EDR. The swapchain is sized by hand on a Mac. | `Horizon/Graphics/Vulkan/Swapchain.cs:62,188-213`, `VulkanContext.cs:81-88` |
| Retina | `SDL_GetWindowSizeInPixels` gives `displayScale`, which is updated on `Move` and `Resize`. | `DesktopWindow.cs:158-259`, `DesktopWindow.DisplayScale.cs:44` |
| Idle and energy | `DrawOnChange`, `SlowWhenIdle` and `WaitForEvents` (`SDL_WaitEventTimeout`, with a 60 s timeout when dormant). | `WindowManagerConfiguration.cs:24-45`, `DesktopWindow.Waiting.cs:107-150` |
| Refresh rate | Never queried. MoltenVK's present timing paces the frames, and the software limiter is `WindowManager.WaitForFrame`. | `WindowManager.cs:577-590` |
| Keys | Cmd arrives as `Key.SuperLeft/Right`. `Keyboard.PrimaryIsCommand` is true on a Mac and `Keyboard.Primary` is Cmd there. | `Horizon/Input/Keyboard.cs:64-70`, `SilkInputSource.cs:82-93` |
| Text input | Silk's `KeyChar` comes one char at a time as `InputEvent.Typed`. **No composition (preedit) events and no candidate-window position.** `IPlatformInput.SetTextInputActive` exists, but only touch code uses it. | `SilkInputSource.cs:56`, `IPlatformInput.cs:37` |
| Scroll | `ScrollY` only, read as "notches". **No horizontal axis, no precise or pixel deltas, no phase or momentum.** | `SilkInputSource.cs:60-63`, `InputEvent.cs:32`, `Horizon.UI/UICompositor.cs:142` |
| Gestures | None on a Mac (touch exists on Windows only). | `WindowsTouch.cs` |
| Clipboard | Text only, through SDL. | `DesktopInput.cs:63-70` |
| Drops | SDL's drop event, batched into one `FileDrop`. No hover feedback (that is Windows OLE only). | `DesktopWindow.FileDrop.cs:30-48` |
| Dialogs | In-app `UIFileBrowser`. **No NSOpenPanel or NSSavePanel.** | `Horizon.UI/Components/Popups/UIFileDialogs.cs:14-38` |
| Notifications | In-app toasts only. | `Horizon.UI/Components/Display/Notifications.cs` |
| Theme | Never reads the system appearance or accent colour. | none |
| Fonts | Ships Hack, Cascadia Mono, Inter, codicons and devicon. No system fonts. Mac text is greyscale, and unhinted at a scale of 1.75 or more. | `Horizon/Assets/fonts`, `GlyphSmoothing.cs:30-65` |
| Cursors | Silk `ICursor` standard shapes, with a UI-drawn cursor as an option. | `GlfwCursor.cs`, `Horizon.UI/Input/UICursor.cs` |
| Menu bar | **No NSMenu anywhere.** SDL2's default app menu is all there is (About, Hide, Quit, Window). | none |
| objc interop | Ad hoc `DllImport("/usr/lib/libobjc.A.dylib")` with one blittable overload per call shape, copied in three places. Callbacks are `[UnmanagedCallersOnly]` function pointers. No shared helper, no runtime class creation and no blocks. | `DesktopWindow.cs:345`, `DesktopWindow.Corners.cs:78`, `hex/Hex.Dawn/Shell/MacOpenDocuments.cs:70-90` |
| Platform seams | `IPlatform`, `IPlatformHost`, `IPlatformWindow`, `IPlatformInput`, `IPlatformEvents` and `PlatformFeatures` flags. Nothing covers dialogs, menus, appearance, notifications or IME. | `Horizon/Platform/*.cs` |

### 1.2 Hex (hex/)

* **Menus:** `UICommands` (`Horizon.UI/Components/Popups/UICommands.cs`) collects the commands from each part into an in-window `MenuBar` (`DawnScene.cs:77`, `DawnScene.Menus.cs`). Keys are written as `Ctrl+…` and Ctrl means Cmd on a Mac (`docs/context/dawn-app.md:252`). Users change keys in `dawn_keys.hor`.
* **Key labels:** `UIShortcutText.Shown` writes "Cmd" and "Option" as words, not ⌘⌥⇧⌃ (`Horizon.UI/Input/UIShortcutText.cs:15`).
* **Text keys:** `UIKeyboard.CodeMeaning` (`Horizon.UI/Input/UIKeyboard.cs:353-386`) already handles Option+arrow word moves, Cmd+arrow line and document moves, and Option+Backspace. **There are no Emacs/Cocoa Ctrl keys** (Ctrl+A/E/K/F/B/N/P/D/H/T/O). The terminal gets a real Ctrl.
* **Mac-specific code already there:**
  * Apple Event file opening (`MacOpenDocuments.cs`, which adds `application:openFiles:` to SDL's delegate).
  * The bundle layout (`HexBundle.cs`).
  * A Keychain-wrapped Link key (`HexLinkSecrets.cs:75-111`).
  * Trash via `~/.Trash`, so Put Back doesn't work (`HexTrash.cs:66`).
  * Reveal in Finder (`open -R`, `HexExplorer.Files.cs:125`).
  * Bundle updates (`DawnScene.Updates.cs:88`).
  * The Hex.app script, Info.plist and signing (`scripts/macos/*`, `docs/macos-app.md`).
  * Titlebar drag QA (`docs/macos-titlebar-qa.md`).
* **Dialogs:** `Hex.Shell/Platform/HexDialogs.cs` wraps the in-app dialogs.
* **Tests:** `Hex.Tests` uses xUnit and has about 640 tests. CI runs it on Windows, Ubuntu and macOS. GUI self tests run with `--selftest`.

---

## 2. What native looks like: features judged against Xcode, Zed, Nova and VS Code

Value: how much a Mac user notices it (5 is "it feels broken without it"). Effort: S is under a day, M is 1–3 days, L is more than 3 days, for one agent with the Mac.

| # | Feature | Xcode / Nova | Zed | VS Code | Hex today | Value | Effort |
|---|---|---|---|---|---|---|---|
| 1 | **Global NSMenu bar** with App, File, Edit, View, Window and Help menus, Cmd key equivalents shown, and Services, Window and Help search | yes | yes | yes | none (in-window bar) | 5 | M |
| 2 | **Traffic lights**, native full screen, tiling (green-button menu, drag to the edge), native drag and double-click zoom | yes | yes (transparent titlebar) | yes | custom drawn, hand-dragged | 5 | M–L |
| 3 | **Trackpad scrolling**: pixel-precise, horizontal, momentum, no "notch" stepping | yes | yes | yes | vertical notches only | 5 | M |
| 4 | **Mac key labels** ⌘⌥⇧⌃ in menus, tooltips and the palette | yes | yes | yes | "Cmd+Shift+P" text | 4 | S |
| 5 | **Cocoa text keys** (Ctrl+A/E/K/Y/F/B/N/P/D/H/T/O) and Option or Cmd text moves in every text field | yes | yes | yes | code editor partly, no Ctrl keys | 4 | S |
| 6 | **IME and input methods**: marked-text preedit, candidate window at the caret, emoji picker (Ctrl+Cmd+Space), dictation | yes | yes | yes | committed text only | 4 (5 for CJK users) | M–L |
| 7 | **System appearance**: dark and light follow the system, accent colour | yes | yes | yes | no | 4 | M |
| 8 | **Native open and save panels** | yes | yes | yes | in-app browser | 4 | M |
| 9 | Pinch zoom and smart zoom (font zoom); swipe between tabs | some | pinch: no | pinch: no | none | 2 | S–M (once the objc base exists) |
| 10 | Dock badge, bounce and menu (agent needs approval, build finished) | Xcode bounce | badge | bounce | none | 3 | S |
| 11 | System notifications (UNUserNotificationCenter) when Hex isn't focused | yes | yes | yes | in-app toasts | 3 | M (needs a signed bundle) |
| 12 | `hex` CLI install command ("Install 'hex' command in PATH") | `xed` | `zed` cli | `code` | manual `ln -s` | 4 | S |
| 13 | Info.plist document types and UTIs (json, md, yaml, sh, cs, …), LSApplicationCategoryType developer-tools, rank Alternate | yes | yes | yes | generic types only | 3 | S |
| 14 | Trash with Put Back (NSFileManager `trashItemAtURL`) | yes | yes | yes | plain move | 3 | S |
| 15 | Quick Look (Space in the explorer) | yes (Xcode) | no | no | none | 2 | S (`qlmanage -p`) / M (QLPreviewPanel) |
| 16 | SF Mono or Menlo available as code fonts, SF Pro for the UI | yes | yes | yes (Menlo default) | Hack | 3 | M (TTC and variable-font support to check) |
| 17 | ProMotion 120 Hz and frame pacing to the display | yes | yes | yes | unknown, to be measured | 3 | S to measure, M to fix |
| 18 | App Nap and idle energy | yes | yes | yes | waits on events (good) | 2 | S to verify |
| 19 | Native file drag hover feedback (drop target highlight) | yes | yes | yes | drop works, no hover | 2 | M |
| 20 | Force Touch or Ctrl+Cmd+D "Look Up" | yes | no | no | none | 1 | M |
| 21 | System spell checker (NSSpellChecker) in comments, Markdown and commit messages | yes | no | extension | none | 2 | M |
| 22 | Window restoration and native tabs | yes | no | no | Hex sessions do this | 1 | skip |
| 23 | VoiceOver (NSAccessibility for a UI the engine draws) | yes | partial | yes | none | 3 (5 for VO users) | L+ |
| 24 | Keychain for every secret (AI keys, tokens) | yes | yes | yes | Link key only | 3 | S–M |
| 25 | Notarisation and code-signing readiness | n/a | n/a | n/a | scripted, needs an Apple ID | 3 | owner action |
| 26 | Services menu acting on the selection; Handoff and Continuity | yes | no | no | none | 1 | M; skip |
| 27 | Shortcuts or App Intents, Spotlight | Nova | no | no | none | 1 | skip (needs Swift) |

---

## 3. Design decisions

### 3.1 One shared objc layer (engine)
New folder `horizon/Horizon.Platform.Desktop/Mac/`. Every macOS call goes through it:

* `MacObjC`: `[LibraryImport]` or `DllImport` blittable `objc_msgSend` overloads, one per call shape. The shapes are `(nint,nint)→nint`, `(…,nint)`, `(…,nint,nint)`, `(…,nint,nint,nint)`, `(…,nuint)`, `(…,bool)`, `(…,double)`, `(…,CGRect)`, `(…,CGPoint)`, `→double`, `→bool` and `→CGRect`. On arm64 a struct return needs no `_stret`. On x64 `objc_msgSend_stret` is needed for large struct returns, and `MacObjC` hides that.
* Cached selectors (`Sel("name")`) and classes (`Class("NSMenu")`).
* `NSString` from and to string (`stringWithUTF8String:`, `UTF8String`), plus an autorelease pool scope.
* `MacClassBuilder`: `objc_allocateClassPair`, `class_addMethod` with `[UnmanagedCallersOnly]` function pointers and `objc_registerClassPair`. This gives target/action objects, notification observers and delegate methods **without blocks**.
* One main-thread rule: AppKit is called **only from the process main thread**, which is SDL's event thread on a Mac. `MacMain.Run(Action)` runs a call now when already on that thread, otherwise it queues the call through the host's `Invoke` or `Post`.
* Everything is behind `OperatingSystem.IsMacOS()` and `[SupportedOSPlatform("macos")]`, so on Windows and Linux none of it is loaded or called.
* AOT and trimming: no reflection, no `Marshal.GetDelegateForFunctionPointer` and no dynamic codegen. Strings are marshalled by hand or with `LPUTF8Str`.
* The existing copies in `DesktopWindow.cs`, `DesktopWindow.Corners.cs` and `MacOpenDocuments.cs` move onto it over time.

### 3.2 Native menu bar: AppKit draws it, Hex's keys stay Hex's
* The engine gets a platform-neutral menu model, `Horizon/Platform/PlatformMenu.cs`:
  ```
  PlatformMenu   { Title, Items }
  PlatformMenuItem { Label, Shortcut ("Ctrl+Shift+P", UIKeys syntax), Role, Submenu,
                     IsSeparator, Enabled(), Checked(), Visible(), Picked() }
  ```
  `Role` takes the values None, About, Settings, Services, Hide, HideOthers, ShowAll, Quit, Minimize, Zoom, FullScreen, BringAllToFront, WindowList and HelpSearch.
* The host can take it through `IPlatformMenuBar { bool IsNative; void SetMenus(IReadOnlyList<PlatformMenu>); void Invalidate(); }`, which the desktop host returns on macOS.
* `Horizon.UI` gets a bridge, `UIMenuBridge`, that turns the `MenuBar`/`Menu`/`MenuItem` tree `UICommands.Build` made into that model. It is pure, so it can be unit tested.
* **Keys:** Hex's own key dispatcher (`UICommands.Keys`) stays the single source of truth, so chords, the user's `dawn_keys.hor` and Cmd keys meaning something else while typing all keep working.
  * Native items show their key equivalent, and `validateMenuItem:` answers **NO while AppKit is matching a key equivalent** (that is, `[NSApp currentEvent]` is a key-down and the menu isn't being tracked). The key then falls through to SDL's view and Hex as it does today, with no double firing.
  * Mouse picks run `Picked` on the UI thread.
  * The system roles keep real AppKit actions and their own keys: Hide ⌘H, Hide Others ⌥⌘H, Quit ⌘Q (which goes to `MayClose` as now), Minimize ⌘M, Zoom, Enter Full Screen ⌃⌘F and Bring All to Front.
  * `setServicesMenu:`, `setWindowsMenu:` and `setHelpMenu:` give the Services menu, the window list and Help search for free.
* Enabled, checked and visible state is read when a menu opens (`menuNeedsUpdate:` through a delegate), never every frame.
* Hex hides its in-window bar on a Mac when the native bar is on. A setting, `macos.native_menu`, defaults to on. Hex then adds the App menu:
  * About Hex
  * Settings… ⌘,
  * Check for Updates…
  * Install `hex` Command…
  * Services
  * Hide, Hide Others and Show All
  * Quit ⌘Q
* Shortcut strings become `keyEquivalent` and `keyEquivalentModifierMask` through a pure, tested mapper, `MacKeyEquivalents`. The mapper handles function keys, arrows and the Delete, Escape, Tab, Return and Space glyph keys, and gives chords (`Ctrl+K Z`) no equivalent, only their text in the title.

### 3.3 Window chrome: real traffic lights over Hex's drawn title bar
* Hex keeps its own custom title bar. On a Mac the NSWindow is a titled, full-size-content window rather than a borderless one, with:
  * `titlebarAppearsTransparent` and `titleVisibility = hidden`;
  * the style mask `Titled|Closable|Miniaturizable|Resizable|FullSizeContentView`.
* That gives the system traffic lights, the green-button tiling menu, native full screen, edge resizing, the shadow and the rounded corners.
* The engine reports `IPlatformWindow.FrameInsets` (the traffic-light area: leading inset ≈ 78 pt and height ≈ 28 pt, measured from `standardWindowButton:` frames). Hex's title bar leaves that space empty and centres its contents vertically with the buttons. In full screen the inset is 0.
* Caption drag: `BeginMove` on a Mac calls `[window performWindowDragWithEvent:[NSApp currentEvent]]` from the main thread during the mouse-down, which gives native drag, tiling and Stage Manager behaviour. The 1 kHz hand tracker stays as the fallback.
* Double-click on the caption follows `AppleActionOnDoubleClick` (Maximize, Minimize or nothing) via `NSUserDefaults`.
* This needs the Mac. The existing `docs/macos-titlebar-qa.md` fixture is the regression check.

### 3.4 Scrolling
* `InputEvent` gains `ScrollX` and a `ScrollPrecise` flag (pixel deltas versus line notches). Momentum arrives as ordinary wheel events on macOS, which gives free inertia once they are not quantised.
* Silk's `ScrollWheel` already carries X and Y as floats. SDL2's `preciseX` and `preciseY` are used when present.
* UI scroll containers (`UICompositor.cs:142` and the code editor) scroll by `ScrollY × lineHeight` for notches and by `ScrollY × points` for precise input. Horizontal scroll works in the code editor, the terminal, tables and tabs.
* Shift+wheel stays horizontal for a mouse.
* Windows behaviour stays the same: GLFW reports notches. The precise flag is off there unless high-resolution wheels are detected, which is not part of this plan.

### 3.5 IME (NSTextInputClient)
* SDL2's Cocoa view already implements NSTextInputClient and sends `SDL_TEXTEDITING` (the marked text and cursor) and `SDL_TEXTINPUT` (the commit). Silk drops `TEXTEDITING`.
* The engine adds an SDL event watch (`SDL_AddEventWatch`, `UnmanagedCallersOnly`) for `TEXTEDITING` and `TEXTEDITING_EXT`, raised as `InputEvent.Composition { Text, Cursor, Length }`. It also implements `IPlatformInput.SetTextInputRect(rect)`, which is `SDL_SetTextInputRect` at the caret, so the candidate window sits under the caret.
* The code editor and text fields draw the preedit underlined at the caret, and do not insert it until it is committed.
* This works on Linux too, since SDL is used there. On Windows, GLFW has no composition events, so nothing changes.

### 3.6 Appearance
* `IPlatformAppearance { bool IsDark; Color Accent; event Changed; }`. On a Mac it reads `NSApp.effectiveAppearance` (`bestMatchFromAppearancesWithNames:` with Aqua and DarkAqua) and `NSColor.controlAccentColor` converted to sRGB. It is told of changes by the distributed notification `AppleInterfaceThemeChangedNotification` and `NSSystemColorsDidChangeNotification`, through a `MacClassBuilder` observer.
* Hex adds the setting `theme.follow_system` (Mac default on for new installs only, existing settings untouched), with the pair of themes it maps to dark and light. The accent tints the selection and focus rings when the theme says `accent: system`.

### 3.7 Native panels
* `IPlatformDialogs` adds `OpenFiles`, `OpenFolder` and `Save`. On a Mac they use NSOpenPanel and NSSavePanel run modally (`runModal`) on the main thread, with the callback posted to the UI thread. They take filters (`allowedContentTypes` from extensions via `UTType typeWithFilenameExtension:`) and a starting folder.
* `HexDialogs` uses them when `PlatformFeatures.NativeDialogs` is set and the setting `dialogs.native` is on (Mac default on). Otherwise it uses the in-app browser as now.

---

## 4. Work packages

The waves run in order. Inside a wave the packages own disjoint files and run in parallel, with at most four agents at once. Other workstreams own `Hex.Dawn/LocalAi/*`, `Hex.Dawn/Code/DawnScene.Prediction*.cs`, `DawnScene.AiEdit.cs` and `Hex.Core/Link/*`, and no package here touches those files.

### Wave 1
| WP | Scope | Owns (new or changed) | Contract | Verify |
|---|---|---|---|---|
| **ENV** | Mac build and test rig over SSH: .NET 11, sync script, build, unit tests, launching the GUI, screenshots | scratch scripts only | `sync.sh`, then build, test and run commands written down | runs on the M6 |
| **K: keys and labels** | ⌘⌥⇧⌃ labels on a Mac in menus, tooltips, the palette and the Keyboard Shortcuts window. Cocoa Ctrl text keys in the code editor and text fields on a Mac. Check Cmd+, for Settings, Cmd+W, Cmd+Shift+[ and ], Cmd+1…9 and Cmd+` | `Horizon.UI/Input/UIShortcutText.cs`, `Horizon.UI/Input/UIKeyboard.cs` (CodeMeaning and the text-field meaning), text-field key code in `Horizon.UI`, new tests | Pure functions that take `mac: bool` so tests run on Windows. Windows output is unchanged. | xUnit on Windows; type on the Mac |
| **A: objc base and native menu** | §3.1 and §3.2 | `horizon/Horizon.Platform.Desktop/Mac/*` (new), `horizon/Horizon/Platform/PlatformMenu.cs` and `IPlatformMenuBar` (new), `Horizon.UI/Components/Popups/UIMenuBridge.cs` (new), the `DesktopHost` hookup, `hex/Hex.Dawn/Shell/DawnScene.MacMenu.cs` (new), a small edit to `DawnScene.cs` hiding the in-window bar | as §3.2 | Mapper and bridge tests on Windows; menu, keys and picks checked on the Mac |
| **S: precise scrolling** | §3.4 | `Horizon/Input/InputEvent.cs`, `SilkInputSource.cs`, `Horizon.UI/UICompositor.cs` scroll handling, the code editor's scroll, the terminal view's scroll | `ScrollX` and `ScrollPrecise` are added, and the old fields keep their meaning | Unit tests for the conversion; trackpad on the Mac (a person, or a synthetic CGEvent scroll posted over SSH) |

### Wave 2
| WP | Scope | Owns | Depends on |
|---|---|---|---|
| **W: traffic lights and native drag** | §3.3 | `DesktopWindow.Frame.cs`, `DesktopWindow.HitTest.cs`, `DesktopWindow.Corners.cs`, `DesktopWindow.cs` (the Mac creation branch), `IPlatformWindow.FrameInsets`, Hex's title bar layout insets | A (objc base) |
| **I: IME composition** | §3.5 | `SilkInputSource.cs` or a new `Desktop/Input/SdlTextEditing.cs`, `IPlatformInput`, the code editor and text field preedit drawing | none (S owns `SilkInputSource` in wave 1, so this waits for it) |
| **P: appearance** | §3.6 | `Horizon.Platform.Desktop/Mac/MacAppearance.cs`, `IPlatformAppearance`, Hex's theme setting | A |
| **D: Dock, CLI, plist and Trash** | Dock badge and bounce for agents waiting on approval or a finished build while Hex isn't active. "Install `hex` command" (a symlink in `/usr/local/bin` through `osascript … with administrator privileges`, or `~/.local/bin` when that's on PATH). Info.plist UTIs and LSApplicationCategoryType. Trash through `NSFileManager trashItemAtURL` (Put Back works). | `Mac/MacDock.cs`, `Hex.Core/Workspace/HexTrash.cs`, `scripts/macos/Info.plist.in`, new `Hex.Dawn/Shell/MacCli.cs` | A |

### Wave 3
| WP | Scope |
|---|---|
| **F: native panels** | §3.7 |
| **G: gestures** | Pinch (`magnifyWithEvent:`) and smart zoom (`smartMagnifyWithEvent:`) added to SDL's view class with `class_addMethod`, as MacOpenDocuments does, becoming editor font zoom. Swipe (`swipeWithEvent:`) switches to the previous or next tab. |
| **R: frame pacing** | Measure fps on the M6 (ProMotion) with vsync on. If it is capped at 60, set `CAMetalLayer.displaySyncEnabled` / `maximumDrawableCount`, MoltenVK `MVK_CONFIG_*`, and ask `NSScreen.maximumFramesPerSecond` for the limiter. Confirm App Nap and idle CPU (`top -pid`) stay near 0 % when idle. |
| **N: system notifications** | UNUserNotificationCenter needs a signed bundle with an identifier and block-based authorization. This needs a block ABI shim (a static `Block_literal` with an `UnmanagedCallersOnly` invoke). Do it after D, falling back to the Dock bounce. |
| **T: fonts** | Check whether `GlyphOutlines` reads TTC (Menlo.ttc) and variable fonts (SFNS.ttf, SF Mono). If it does, list the system monospace fonts in the font picker on a Mac and use SF Mono or Menlo as the Mac default for new installs. |

### Later or not planned
VoiceOver (a large NSAccessibility bridge for a UI the engine draws; a plan of its own), the spell checker, Look Up, Quick Look panel integration, Services acting on the selection, Handoff, App Intents, native tabs and window restoration. Notarisation is waiting on the owner's Apple Developer account (the script and CI are ready, see `macos-app.md`).

---

## 5. Rules for every package
* AOT and trim safe:
  * blittable P/Invoke, or `LibraryImport` with explicit marshalling;
  * `[UnmanagedCallersOnly]` callbacks;
  * no reflection-based marshalling and no `Marshal.GetDelegateForFunctionPointer`;
  * no `dynamic`.
* Every Mac path is guarded by `OperatingSystem.IsMacOS()`, and Windows and Linux behaviour is unchanged, which the existing tests show.
* AppKit is touched only on the main thread. Managed UI state is touched only on the UI thread.
* Pure logic (key mapping, menu bridging, scroll conversion, label text) gets xUnit tests that run on Windows.
* Gates: `hex_dev_build` and the whole `hex_dev_test` on Windows; a build and `dotnet test` of Hex.Tests on the Mac; a manual check on the Mac for what only a Mac shows.

## 6. What only a real Mac can verify
* That the NSMenu appears, the App menu items work, key equivalents don't fire twice, Services and the Window list appear, and the menu state updates as it opens.
* Traffic lights, tiling, full screen, native drag and double-click zoom, and the title bar insets at 1× and 2×.
* Trackpad pixel scrolling and momentum, and horizontal scroll.
* IME: Japanese, Chinese and Korean input methods, the emoji picker and dictation.
* That appearance changes are followed live and the accent colour is right.
* NSOpenPanel and NSSavePanel; the Dock badge and bounce; Put Back from the Trash; the `hex` symlink.
* ProMotion fps, idle energy and App Nap.
* Native AOT publish (`make-app.sh`) with the new interop: no trimming warnings, and the app starts.

## 7. Results log
Filled in as packages land (what was built, how it was checked, and on which machine).

### 2026-11-24, wave 1 (implemented on Windows; the Windows build is green; none of it checked on the Mac yet)
* **K, key labels and Cocoa keys:**
  * `UIShortcutText.Shown(written, mac)` writes ⌃⌥⇧⌘ glyphs in Apple's order, plus ↩⌫⌦⎋⇥ and the arrows.
  * `UIKeycaps` paints one cap per stroke.
  * The U+2300 block was added to `DistanceFieldFont.DefaultRanges`, with the cache version bumped to 3. Hack has none of these glyphs, so menus rely on Inter.
  * Cocoa Ctrl keys (A/E/F/B/N/P/D/H/K/Y/T/O, with a kill buffer) work in `CodeEditor` and `TextBox`.
  * Tests: `Hex.Tests/MacKeysTests.cs`, with 63 cases. One of them, `Shift_option_or_nothing_held_are_not_the_cocoa_keys`, still fails.
  * Found on the way: Cmd+, and Cmd+W already worked. Cmd+Shift+[ and ] are fold and unfold. Ctrl+Tab on a Mac is Cmd+Tab, which macOS takes, and Cmd+` is probably taken too.
* **S, precise scrolling:**
  * `InputEvent.ScrollX` and `ScrollPrecise` were added, with the conversion in `PreciseScroll.cs`. `RouteScroll` and `GiveWheelX` route it, and `OnScrollX` is handled in ScrollPanel, Table, TreeView, ListBox, TabStrip and CodeEditor.
  * Notched wheels are unchanged.
  * Tests: `PreciseScrollTests.cs`, 10 cases.
  * A Mac agent is now checking the 0.1 SDL factor and adding the `hasPreciseScrollingDeltas` flag and pinch zoom.
* **A, objc base and native menu:**
  * The objc layer is `Horizon.Platform.Desktop/Mac/` (`MacObjC`, `MacClassBuilder`, `MacMain`, `MacMenuBar`).
  * The menu model and seam are `Horizon/Platform/PlatformMenu.cs`, `IPlatformHost.MenuBar` and `PlatformFeatures.NativeMenuBar`, with the pure `MacKeyEquivalents` and `Horizon.UI/.../UIMenuBridge.cs`.
  * On the Hex side, `DawnScene.MacMenu.cs` and the setting `macos_native_menu` (default on) are added.
  * Tests: `MacMenuTests.cs`.
  * To check on the Mac: that the menu appears, that a ⌘ key fires once through Hex, picks, refreshing state, the system roles, ⌘Q against the persistent process, and the title bar without the in-window menu.
* **Mac rig:** `scratch/mac/` (`macssh.sh`, `sync.sh`, `mac-agent.sh`, README).
  * Hex builds on the M6 in 22 to 55 s.
  * The unit tests fail 7 of 3115 there, all of them platform or environment problems: a Windows path in the settings golden, `/var` versus `/private/var` in the Link temp folders, the file watcher, and process groups.
  * The full selftest on the Mac stood at 2121 passed and 145 failed when it was stopped (editor look 39, prediction 12, tree looks 7). Those were not investigated and are worth a look.

### 2026-11-24, wave 1 checked on the Mac (Mac lead, chat #3 on bogzmacpro, run inside Hex Dev)
* **A, native menu: it was never installed.**
  * The cause: `DawnScene` is built (`DawnProgram.cs:239`) before `WindowManager.Run` calls `host.Initialize()` (`WindowManager.cs:647`), and `Initialize` is what makes `MacMenuBar`. So `BuildMacMenu` found `Host.MenuBar` null and returned, and the in-window menu stayed.
  * The fix: `UpdateMacMenu` takes the bar on the first tick that has it.
  * Proven on this Mac with a diagnostic copy (logging from inside a test instance):
    * without the fix, `Host.MenuBar = null`;
    * with it, the menus install on the main thread and `[NSApp mainMenu]` is ours, with Hex, File, Edit, …, Window and Help, and the keys mapped (⌘S, ⇧⌘O, ⌘W).
    * The in-window bar hides.
  * Found in the same dump and being fixed:
    * AppKit's Dictation and Emoji items are duplicated in Edit.
    * File › Exit shows ⌥F4.
    * Redo shows ⌘Y, not ⇧⌘Z.
    * The Layout menu isn't hidden at install.
    * ⌘Q (`terminate:`) reaches Hex as a window close, so with `persistent_process` on, it only hid Hex.
  * `osascript` System Events has no Accessibility permission on this Mac, so menus are checked by walking `[NSApp mainMenu]` from inside a test build.
* **S, gestures and the scroll watch** (from the headless agent's `~/work/scroll`): ported into the tree.
  * Removed: `MacSynthDebug.cs` and every TEMPDBG log.
  * `MacScrollWatch` now pairs each turn with Silk's wheel event by its X/Y (`ScrollTurnQueue`, unit tested in `Horizon.Tests`) rather than by position in the queue alone.
  * Unit tests in `MacGesturesTests.cs` cover ToNotches with units per point (non-Mac unchanged), ZoomSteps, ZoomFactor, PinchFactor and SmartZoomTarget.
  * `--selftest gestures` passes 8 of 8 on this Mac. It checks posted events, not a real trackpad.
* **Unit tests:** fixed on this Mac.
  * MacKeys: the Cocoa key codes E023–E026 collided with TOGGLE_STAR and PICK_AND_OPEN, and were moved to E027–E02A.
  * Link AI, Wiring and Symbol: the expectations were stale against the product's readable rights text.
  * Settings golden: the cwd is written as `<cwd>`.
  * HoldSync: a product fix, git's `/private/var` top against the file's `/var` path (`HexLinkSync.Hold.cs`).
  * AgentJob: a product fix, the watcher's shell script carried CRLF on a CRLF checkout and macOS `sh` refused it (`HexAgentGroup.cs`).
  * TreeCache and Folders: FSEvents replays events from just before the watch, so the tests are isolated from that.
  * GitFollow: a real sync race is being chased.
  * Tests run with `--artifacts-path` outside the tree need `hex-extensions`, `Hex.Dawn`, `Hex.Dusk` and `Hex.Shell` symlinked beside the artifacts (about 145 environmental failures otherwise).

### 2026-11-25, wave 2 and fixes (Mac lead, merged with the PC at de57c6a)
* **W, traffic lights** (56aa245, 21f1785):
  * The window is titled with a full-size content view, and the lights are centred in Hex's bar. `FrameInsets` leaves leading and trailing room, so Dawn, Agentic and Dusk clear the corner.
  * Drag is native (`performWindowDragWithEvent:` with the press kept by an SDL watch), double-click zoom follows `AppleActionOnDoubleClick`, and full screen works.
  * The first build broke input: `setStyleMask:` rebuilt the frame view and reset `contentView.nextResponder`, which dropped SDL2's `Cocoa_WindowListener` (the mouse moves and releases) out of the responder chain. So clicks stayed held and nothing hovered. `MacWindowChrome.KeepSdlListening` puts it back and checks every tick.
  * Verified on this Mac with posted CGEvents, and by the person. `HORIZON_MAC_CHROME=off` turns the chrome off.
* **P, appearance** (a4525a8, 593903e): `MacAppearance` (effectiveAppearance, controlAccentColor, and the change notifications) and the `theme_follow_system` setting.
  * The setting defaults on on a Mac and is not written into fresh files. Existing installs are pinned off once, by a marker file.
  * Themes can say `accent_color: system`.
  * Unit tests and the settings selftest (347/347) pass.
* **D** (1d3ec6a): Dock badge and bounce (`IPlatformAttention`/`MacDock`), Hex › Install 'hex' Command…, Info.plist document types and UTIs, and Trash through `trashItemAtURL` (so Put Back works).
  * The badge readback and the trash were checked on the Mac. The bounce and Put Back are left for a person to check.
* **Fixes:**
  * `Hex --version` used to start a whole editor; it was the windows that flashed at start-up (4977791).
  * The pipe server's `SocketException` on re-bind crashed Hex Dev; servers now retry and reclaim stale sockets (e2560f9).
  * Selftests send ⌘ for a written Ctrl and expect ⌘ text (3d1a791).
  * Line endings were restored (6686383).
  * The Horizon tests pass on macOS, and the LanguageServer test hang was a pipe-disposal order problem in a test (0e5a2d7, 101a692).
* **After the merge** (fe57c20, 3671820, e7b4129, 6c29385, c3c4ed1):
  * Input after minimise came back only with a full-screen toggle. Silk never raises a state change on restore, so `windowMinimised` stayed true. `SyncMinimized` now reads SDL's flag every tick.
  * Full screen was drawn at the windowed size and stretched. Native full screen now draws at the screen's pixels.
  * The full screen title bar strip that slides down from the top (NSTitlebarContainerView) painted the unskinned system bar over Hex's bar. It is hidden in full screen, and the bar leaves out its logo there.
  * MacCli joins paths with '/' on any host, which fixes its tests on Windows.

### 2026-11-25, trackpad scrolling (Mac lead 2)
* **Cause of the jumpy trackpad:** Silk.NET.Input.Sdl 2.23 makes `ScrollWheel` out of SDL's integer `wheel.x/y`. SDL 2.32 adds AppKit's `deltaY` (lines) up and hands out only whole notches of the sum. So a trackpad came through as runs of 0 and then a ±1. MacScrollWatch paired on `preciseX/Y` against those ints, never matched, and the ±1 was scrolled as a whole notch (48 pt × lines). Small moves did nothing, and bigger ones lurched.
* **Fix:** `MacScrollWatch` (an SDL event watch, now on every SDL platform) keys its queue by SDL's whole x/y and keeps the real turn: `NSEvent.scrollingDeltaX/Y` on a Mac, and `preciseX/Y` elsewhere (so Linux trackpads get fractions too). `SilkInputSource` uses it and drops 0,0. Windows (GLFW) is unchanged. The person confirmed it scrolls smoothly.
* **Rubber band and momentum:** `MacScrollWatch.Monitor` is an NSEvent local monitor (a hand-built global ObjC block). It raises precise scrolls with `ScrollPhase` (Touching, Released, Momentum, MomentumEnded), including the zero-delta "fingers lifted" events that SDL drops. `Mouse.WindowFingersOn` and `WindowScrollGliding` feed `UIWheel.Held`, `Gliding` and `Seconds`.
  * `UIOverscroll.Scroll` holds the stretch with AppKit's band ((1 − 1/(x·0.55/d + 1))·d) while the fingers are on, springs it back when they lift, and bounces a glide off an end once.
  * It is used by CodeEditor, ScrollPanel, TreeView, Table and ListBox. A turn with no phase at an end still chains to the parent.
  * Tests: `UIOverscrollBandTests` and `ScrollTurnQueueTests`.
  * `HORIZON_SCROLL_TRACE=<file>` logs every turn with its phase and momentumPhase.
* **Perf pass** (b3b1164). Measured on the M6 with `top` and `sample`. Managed frames were symbolized with `DOTNET_PerfMapEnabled=1` and /tmp/perf-PID.map; `dotnet-counters` 10 gets no payload from the .NET 11 runtime.
  * **Idle:** a fresh Hex sits at about 3.1% CPU and about 11 wakeups/s. The idle ticks (4/s) still repaint the whole tree, CodeEditor mostly.
  * **Release under load:** scrolling about 11%, typing about 28%.
  * **Hex Dev with agents streaming:** about 47% (Debug, full-rate ticks).
  * **Fixed:**
    * TerminalKindOf joined the whole file into one string for every hovered word; it is now cached per document version.
    * UserFolder did a mkdir on every call under HEX_HOME.
    * UIShortcuts asked every shortcut about its key twice a tick with no key down.
  * **Left:**
    * CodeEditor.Paint (PaintCode/UIDrawList.Text, PaintMinimap) repaints on every changed tick.
    * The whole-tree repaint has no dirty skip.
    * MoltenVK's vkQueueSubmit2 (a blit encoder per submit) and vkAcquireNextImage cost about as much as the managed UI.
* **Primary keys** (badea4f): about two dozen `Keyboard.Control` reads that meant the shortcut modifier now read `Primary`: Ctrl+wheel zoom, terminal link clicks, multi-select, extension and automation keys, the key recorder, Local AI's Ctrl+F, and the inline editor's Ctrl+S/Q. Terminal Ctrl+C/V, the keys sent to the shell, Ctrl+Tab and Ctrl+Down stay physical Ctrl. Drag-copy is ⌥ on a Mac (`TreeView.CopyHeld`).
* **Not yet verified by the person:** the rubber band, springback and glide bounce. Synthetic CGEvents carried the scroll phases (began, changed, and the zero-delta ended) through the monitor, but not momentumPhase.
* **Trackpad, round 2** (f38ea29, 1f754ef), from the person's tries:
  * **Glide:** a trace of a real trackpad showed macOS sends this window no momentum events at all (2,500 events, every one with momentumPhase 0), so flicks stopped dead. `UICompositor.Glide` measures the fingers' speed over the last 80 ms as they lift and glides with AppKit's deceleration (0.998/ms). It bounces off ends, and stops when the fingers touch again or the system's own momentum arrives.
  * **Springback after a hold:** it snapped home. The zero-delta "fingers lifted" event only reaches the NSEvent monitor (SDL drops it) and didn't wake an idle app, so the spring integrated a whole idle tick at once. The monitor now reports input (`InputHeard`), and the spring caps its step.
  * **Horizontal direction:** it was inverted; `scrollingDeltaX` is now used as AppKit gives it.
  * **Chat log:** `ScrollPanel.ScrollToEnd` leaves the offset at `float.MaxValue` until the next layout, so a turn up from there bounced the band over the newest messages. `UIOverscroll.Scroll` and `UIScrollEase.Wheel` start from where the offset can be (every platform).
* **Full screen** (f38ea29, 1f754ef, c28efdd):
  * Lights of the window's own kind (`standardWindowButton:forStyleMask:`) sit in the content view over the UI's bar, so the window can be closed and taken out of full screen.
  * The system strip's own window (`NSToolbarFullScreenWindow`) is given no shadow, no alpha and no mouse events, and the titlebar separator is None.
  * Its sliding shadow can't be taken off, so the UI's bar is exactly as tall as the system bar in full screen (`PlatformFrameInsets.Exact`), and the two line up.
  * Nothing depends on the notch; without one, full screen reaches the top edge.
* **Zoom** (f38ea29 to d2b97e5, plus the flow commit):
  * Pinch and ⌘+wheel keep the middle of the view in place, the share of the whole line's rows when wrapped. Sideways, the middle is kept only once the code has been scrolled along; at the start, the start stays.
  * A zoom tick marks the editor's frames `Jumped`, so the in-between frames don't slide the new rows over from the old scroll. Lenses and inlays move with the code.
  * While a zoom goes on (0.2 s after the last change), the scale isn't rounded to whole-pixel cells, and letters come from their distance field (`UIDrawList.FieldText`), not an atlas still making each new size. A pinch catches at every quarter.
  * The wrap-on fracturing was `CodeEditor.Flow`, which eases every word to where the wrap puts it; during a zoom every word trailed at its own pace. It now snaps when the line height changes.
  * Line numbers stop growing at 150% (`MostGutterScale`).
* **Tests:** Horizon.Tests needs `--artifacts-path` under `horizon/` (it looks for `horizon/Horizon.sln`), and Hex.Tests under `hex/`.
* **Still open:** I (IME); the "Alt+N" tab hints (⌥N). Where they are drawn wasn't found yet; `local_ai.page_key: "Alt+{number}"` in en-gb.hor is unused. Also open: wave 3, and the rest of the perf list above.

## 8. Waves 2 and 3, refined on the Mac
Facts below were read on bogzmacpro (M6, macOS 27.2, built-in display 1512×982 pt @ 120 Hz). SDL is `ultz.native.sdl` 2.32.10 (universal) under Silk.NET 2.23.

### Wave 2
**W, traffic lights and native drag.** Owns `DesktopWindow.cs` (the Mac creation branch), `DesktopWindow.Frame.cs`, `DesktopWindow.HitTest.cs`, `DesktopWindow.Corners.cs`, `IPlatformWindow.FrameInsets` (new), `Horizon.UI/Components/Containers/TitleBar.cs` (a `LeadingInset`), and Hex's title bar setup (`DawnScene.Chrome.cs`, Dusk's).
1. After `Window.Create`, on the main thread through `MacObjC`, set the style mask to `Titled|Closable|Miniaturizable|Resizable|FullSizeContentView` (SDL itself calls `setStyleMask:`). Also set `titlebarAppearsTransparent=YES` and `titleVisibility=NSWindowTitleHidden`. Keep `WindowBorder.Hidden` for Silk's sake.
   * Check: SDL's Metal view still fills the window, and `SDL_GetWindowSizeInPixels` and Hex's viewport agree at 1× and 2×.
2. `Corners.cs`: skip the clear background and masked layer on a Mac once the window is titled, since the system rounds it.
3. `FrameInsets`: the leading inset is the maximum X of `standardWindowButton:` 0–2 frames + 8 pt, and the height is the title bar's. It is 0 in full screen, re-read on `NSWindowDidEnterFullScreen` and `DidExitFullScreen` (a `MacClassBuilder` observer).
   * `TitleBar.LeadingInset` goes into `IconRoom`, scaled by `Unit` (Hex's bar is not `PixelSized`).
   * On a Mac, `WindowButtons = false`, and the bar's 32 px height centres the lights. `setFrameOrigin:` on the buttons can move them down to centre if needed.
4. Drag: `performWindowDragWithEvent:` needs the live mouse-down `NSEvent`, and `BeginMove` is queued (`PostToWindow`), so by then `currentEvent` is stale. Install an SDL event watch for `SDL_MOUSEBUTTONDOWN` (the same pattern as `MacScrollWatch`) that keeps `[NSApp currentEvent]` retained.
   * `BeginMove` on a Mac then calls `performWindowDragWithEvent:` with that event on the main thread, if it is still the press now down.
   * The 1 kHz tracker stays as the fallback.
5. Double click on the caption: read `NSUserDefaults AppleActionOnDoubleClick` ("Maximize" by default here, as the key is unset, then "Minimize" or "None") and call `performZoom:` or `performMiniaturize:`.
6. Verify on the Mac: lights hover and work, the green button's tiling menu, full screen in and out (inset 0), drag to the top for tiling, Stage Manager, `docs/macos-titlebar-qa.md` (its fixture drives the hand tracker, so add a native-drag case), and selftest `titlebar-targets`.

**I, IME composition.** Owns a new `Horizon.Platform.Desktop/Mac/../Input/SdlTextEditing.cs` (a watch, not Mac-only: it works on Linux too), `IPlatformInput` (`SetTextInputRect`, a composition event), `InputEvent` (a `Composition` kind with Text, Cursor and Length), `UICompositor.Input.cs` (routing to the focus), and the preedit in `CodeEditor.PaintCaret` (`CodeEditor.cs:3518-3580`, which already publishes `CaretPoint` and `CaretLineHeight`) and `TextBox` (`TextBox.cs:661,700`).
1. An `SDL_AddEventWatch` for `SDL_TEXTEDITING` and `SDL_TEXTEDITING_EXT` raises `InputEvent.Composition`. Set the hint `SDL_HINT_IME_SUPPORT_EXTENDED_TEXT=1` before init.
2. Check `SDL_IsTextInputActive` on the Mac (SDL turns it on by default on desktop). Call `SDL_SetTextInputRect` with the caret rectangle in window points whenever the caret moves (throttled to changes), so the candidate window sits under it.
3. The preedit is drawn underlined at the caret and pushes the rest of the line. It is never inserted, and the commit comes through `TEXTINPUT` as now.
4. Also fix: Silk's `KeyChar` is a UTF-16 char, so an emoji commit arrives as two `Typed` events. Taking `SDL_TEXTINPUT` whole from the same watch (and dropping Silk's chars on SDL) delivers it in one piece.
5. Verify by hand: Japanese (Kotoeri Romaji→Kana, Space to convert, Return to commit), Pinyin, 2-Set Korean, the emoji picker (⌃⌘Space), dictation. A selftest posts `Composition` events.

**P, system appearance.** Owns `Horizon.Platform.Desktop/Mac/MacAppearance.cs`, `IPlatformAppearance` (`IsDark`, `Accent`, `Changed`) on the host, and Hex's `theme.follow_system` with the dark and light pair.
* Hex's themes are `HexSettings.EditorSkin` and `EditorTheme` (`HexLooks.cs`), applied live by `UseEditorSkin` (`DawnScene.EditorSkin.cs:24-51`).
* Following the system swaps between the looks' dark and light themes (`graphite`/`solstice` ↔ `solstice_day`) without saving them as the user's choice.
* The accent goes in through `UIPalette.Accent` (theme key `accent_color`, `UIPalette.cs:128`) when the theme says `accent: system`.
* Change notifications come from the distributed `AppleInterfaceThemeChangedNotification` and `NSSystemColorsDidChangeNotification`, observed by a `MacClassBuilder` object and posted to the UI thread.
* The setting defaults on for new installs only.

**D, Dock, CLI, plist and Trash.**
* **Trash:** `HexTrash.cs:66` today does a `Directory.Move` into `~/.Trash`, with no Put Back. Use `[[NSFileManager defaultManager] trashItemAtURL:resultingItemURL:error:]` (an out-parameter: pass the address of a stack `nint`, blittable).
* **Dock:**
  * When Hex isn't active and an agent waits for approval (`HexAgentHub.ApprovalAsked`, hooked at `DawnScene.AgentsPanel.cs:115`), call `[NSApp requestUserAttention:NSInformationalRequest]`. Use `NSCriticalRequest` for a question addressed to the person.
  * Set `[[NSApp dockTile] setBadgeLabel:]` to the count of pending approvals.
  * A finished build (`HexTasks.Ended`, `HexTasks.cs:231`) gets an informational bounce.
* **The `hex` command:** a Hex › "Install `hex` Command…" item makes `~/.local/bin/hex` when that is on the PATH. Otherwise it makes `/usr/local/bin/hex` through `osascript -e 'do shell script "ln -sf …" with administrator privileges'`. The link points at `Contents/MacOS/Hex`.
  * The single-instance pipe (`HexInstance`, a socket in `$TMPDIR`) already hands files and folders to the running Hex.
  * Check that `HexBundle.Inside` resolves through the symlink (`AppContext.BaseDirectory` follows the real path).
* **Info.plist:**
  * Add `CFBundleDocumentTypes` per family (C#, JSON, Markdown, YAML, shell, TOML, `.hor`, and so on) with their public UTIs, plus `UTImportedTypeDeclarations` for the ones that lack a system UTI (`.hor`, `.csproj`, `.sln`/`.slnx`).
  * Rank: `Alternate` everywhere, and `Owner` for `.hor`.
  * `LSApplicationCategoryType` is already developer-tools.

### Wave 3
**F, native panels.** `HexDialogs` (`Hex.Shell/Platform/HexDialogs.cs`) has Task-based `Open`, `Save` and `PickFolder`, single selection, with filters as `(Name, "*.png;*.jpg")` and a start directory.
* Add `IPlatformDialogs` and `PlatformFeatures.NativeDialogs`. On a Mac, run NSOpenPanel and NSSavePanel through `beginSheetModalForWindow:completionHandler:` (needs the block shim) or `runModal` on the main thread, posted so the UI thread isn't blocked. Completion goes back to the Task on the UI thread.
* Filters become `allowedContentTypes` through `UTType typeWithFilenameExtension:`, and `*` means none.
* The `dialogs.native` setting is on by default on a Mac.
* `runModal` is the first step, since it needs no blocks. Pumping SDL while it runs is AppKit's own modal loop, so Hex's frames pause; that is acceptable for a modal panel.

**R, ProMotion and energy. Measure first.**
* Today Hex asks for 120 fps (240 under selftest) and vsync on, which picks `Mailbox` (`Swapchain.cs:188-197`). `WaitForFrame` paces in software, and no MoltenVK config is set.
* Measure on the M6: fps while scrolling with `HORIZON` frame stats or the profiler, present-to-present intervals, and idle CPU and energy (`top -pid`, Activity Monitor's energy column and `powermetrics` if allowed).
* Fix only what the measurement shows: `CAMetalLayer.displaySyncEnabled`, `maximumDrawableCount`, `MVK_CONFIG_*`, and `NSScreen.maximumFramesPerSecond` for the limiter (60 on an external display).

**N, system notifications.** The dev build is a bare executable (no bundle identifier), so UNUserNotificationCenter only works from `Hex.app` (`make-app.sh`, `com.bogzxo.hex`).
* It needs the block ABI shim (a static `Block_literal` with an `[UnmanagedCallersOnly]` invoke) for `requestAuthorizationWithOptions:completionHandler:`.
* Only when Hex isn't active: an agent waiting for approval, a long task ending.
* The fallback is D's Dock bounce. Do it after D, and verify in a signed `Hex.app`.

**T, fonts.**
* The loaders (`GlyphOutlines.cs:56-65,78`, `DistanceFieldFont.cs:114,131-138`) use stb_truetype with face index 0 and accept only `.ttf` and `.otf`.
* Menlo is `Menlo.ttc` (4 faces, static). SF Mono and SF Pro (`SFNSMono.ttf`, `SFNS.ttf`) are variable fonts whose default instance is Light, and nothing reads `fvar` or `gvar`.
* Plan:
  * accept `.ttc` with a face index (`stbtt_GetFontOffsetForIndex`) and map Regular, Bold and Italic;
  * add a path-based loader for system fonts (`HexCodeEditing.Font.cs:35` hard-codes the bundled assets);
  * list Menlo and Monaco in the font picker on a Mac;
  * make Menlo the Mac default for new installs only.
* SF Mono waits for variable-font instancing (`fvar`/`gvar` deltas applied to outlines), which is a package of its own.
