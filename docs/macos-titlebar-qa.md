# macOS titlebar drag QA

Use a disposable, locally built Hex debug apphost on a logged-in Mac. Open a scratch source file with several lines. Keep the window restored on one display with at least 64 points of free space to its right. Keep the same UI scale for each launch.

## Choose the two start points

Launch Hex with `--window --size 1000x700` and identify the title caption between the last menu and the mode switch. Choose a point halfway down that caption, clear of controls and the outer resize rim. For the negative control choose the middle of a long, visible line in the source editor. An editor gutter, tab, splitter or blank panel tests a different interaction. Record both points relative to the upper left of the content view. Coordinates are logical AppKit points. Divide a framebuffer screenshot's pixel offsets by the window backing scale before using them.

## Process-local simulated input

From the hex repository root, after build allocation, compile the standalone fixture with the macOS command line tools. Set APP to the absolute path of the disposable executable. Run each target in a fresh process, with Hex focused during its eight-second startup delay. Substitute the measured numbers for X and Y. Use the same scratch workspace or file arguments you used when locating the targets.

```sh
fixture="$(mktemp -d)/titlebar-frame.dylib"
clang -fobjc-arc -fblocks -dynamiclib -framework AppKit tools/macos/titlebar-frame.m -o "$fixture"

probe() {
    target=$1
    shift
    /bin/sh -c '
        export HEX_TITLEBAR_PID=$$
        export HEX_TITLEBAR_PROBE="$1"
        export DYLD_INSERT_LIBRARIES="$2"
        shift 2
        exec "$@"
    ' sh "$target" "$fixture" "$APP" --window --size 1000x700 "$@"
}
probe caption,X,Y
probe control,X,Y
```

The fixture posts five mouse events to the test application's AppKit queue. It supplies synthetic cursor coordinates and button state to SDL inside that process. It samples the real NSWindow frame and exits the disposable process. The fixture attaches an SDL event watch to the SDL2 library already loaded by Hex. It logs the original SDL button and motion events. PASS requires a left press within two points of the selected target on the focused SDL window, a left-held motion, a subsequent release, the original window size, and a horizontal frame displacement of 64 points for the caption or zero for the control, within one point. The AppKit local-monitor count is diagnostic. Inspect stderr for SDL window IDs, coordinates and the explicit TITLEBAR result. If posting produces no SDL events, repeat with `HEX_TITLEBAR_SEND_DIRECT=1` exported to dispatch through `NSApplication.sendEvent:` on the main thread. Record the delivery mode with each result. An absent result is inconclusive, including when a hardened binary declines to load the fixture. Use a local debug apphost. Leave signing and system input permissions as configured.

## Verified M1 results with simulated input

The Mac QA run used direct AppKit dispatch on an M1 Mac with a 2× backing scale and a restored 1000 × 700 point window. The fixture measured the live NSWindow frame. For a new layout, run `--selftest titlebar-targets --exit` at the intended size to obtain caption and editor coordinates in logical points.

| Build and target | Input evidence | Window frame result |
| --- | --- | --- |
| Before fix, caption (510, 12) | AppKit observed 5 events. SDL button sequence stayed at 0. | Origin stayed at (220, 130), size 1000 × 700. The drag failed. |
| After fix, caption (510, 12) | AppKit observed 5 events. SDL observed the press, held motion and release. | Origin moved from (220, 130) to (284, 130), exactly 64 points right. Size stayed 1000 × 700. |
| After fix, editor control (520, 180) | SDL observed the button sequence. | Frame stayed fixed. |

Physical mouse or trackpad verification and visual confirmation of text selection remain pending.

## Physical pointer demonstration

Use a normal launch without the fixture and a real mouse or trackpad. Record the window frame before and after each gesture with the paired Mac window observer. Drag the same caption 64 points to the right. Confirm the frame origin follows the hand and its size stays fixed. Drag across the source line and confirm the selection visibly spans text while the frame stays fixed. Click the menu, switch modes and operate the minimize and restore controls. Finally start maximized, drag the caption, and confirm the restored window follows the grab. Record observed frame values, backing scale and selection evidence separately from the simulated result. This physical check only needs a person at the Mac and a passive frame observer.
