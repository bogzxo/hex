#!/usr/bin/env python3
"""
Virtual Linux laptop Precision Touchpad driver & E2E verifier using /dev/uinput + libinput.

Creates a realistic 100mm x 60mm multi-touch laptop touchpad (ABS_MT + INPUT_PROP_POINTER)
so libinput and the compositor (GNOME Mutter Wayland / Xorg) emit real touchpad scroll,
axis_stop, hold, pinch-to-zoom, and multi-finger swipe events into Hex, then asserts real
state changes inside Hex (scroll offset, horizontal scroll direction, pinch CodeZoom, and
Wayland/X11 event trace phases).
"""
import argparse
import json
import os
import re
import subprocess
import sys
import time
from importlib.machinery import SourceFileLoader

try:
    from evdev import UInput, AbsInfo, ecodes as e
except ImportError:
    print("ERROR: python3-evdev is not installed. Run: sudo apt-get install -y python3-evdev", file=sys.stderr)
    sys.exit(2)

smoke_mod = SourceFileLoader("smoke_mcp", os.path.join(os.path.dirname(os.path.abspath(__file__)), "smoke-mcp.py")).load_module()
HexMcpSession = smoke_mod.HexMcpSession


class VirtualPointer:
    """Minimal relative mouse used only to place the Wayland/X11 wl_pointer cursor over the Hex CodeEditor."""

    def __init__(self):
        cap = {
            e.EV_KEY: [e.BTN_LEFT],
            e.EV_REL: [e.REL_X, e.REL_Y],
        }
        self.ui = UInput(cap, name="Hex Test Relative Mouse")
        time.sleep(0.35)

    def _rel(self, dx: int, dy: int):
        self.ui.write(e.EV_REL, e.REL_X, int(dx))
        self.ui.write(e.EV_REL, e.REL_Y, int(dy))
        self.ui.syn()

    def move_to_center_and_click(self, sess: HexMcpSession):
        # Ensure GNOME Activities Overview is closed (never slam to top-left 0,0 which triggers the hot corner!)
        subprocess.run(
            ["gdbus", "call", "--session", "--dest", "org.gnome.Shell", "--object-path", "/org/gnome/Shell",
             "--method", "org.freedesktop.DBus.Properties.Set", "org.gnome.Shell", "OverviewActive", "<false>"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        time.sleep(0.2)

        # Slam cursor to bottom-right corner (safe: no GNOME hot corner or dock), then step up-left into CodeEditor
        for _ in range(25):
            self._rel(200, 200)
            time.sleep(0.004)
        for step_x, step_y in [(-18, -16), (-10, 0), (-10, 0), (15, -6)]:
            for _ in range(abs(step_x)):
                self._rel(-25 if step_x < 0 else 25, 0)
                time.sleep(0.004)
            for _ in range(abs(step_y)):
                self._rel(0, -25 if step_y < 0 else 25)
                time.sleep(0.004)
            time.sleep(0.08)
            self.ui.write(e.EV_KEY, e.BTN_LEFT, 1)
            self.ui.syn()
            time.sleep(0.04)
            self.ui.write(e.EV_KEY, e.BTN_LEFT, 0)
            self.ui.syn()
            time.sleep(0.15)
            ed = sess.text(sess.call("hex_editor"))
            m = re.search(r"caret at Ln (\d+), Col (\d+)", ed)
            if m and (int(m.group(1)) > 1 or int(m.group(2)) > 1):
                print(f"[touchpad] Pointer locked onto CodeEditor (clicked Ln {m.group(1)}, Col {m.group(2)})")
                return
        print("[touchpad] Warning: could not confirm caret move from click; continuing with current pointer")

    def close(self):
        self.ui.close()


class VirtualTouchpad:
    """100mm x 60mm multi-touch Precision Touchpad (20 units/mm)."""

    WIDTH = 2000
    HEIGHT = 1200
    RES = 20

    def __init__(self, name: str = "Hex Virtual Laptop Touchpad"):
        cap = {
            e.EV_KEY: [
                e.BTN_LEFT,
                e.BTN_TOUCH,
                e.BTN_TOOL_FINGER,
                e.BTN_TOOL_DOUBLETAP,
                e.BTN_TOOL_TRIPLETAP,
                e.BTN_TOOL_QUADTAP,
                e.BTN_TOOL_QUINTTAP,
            ],
            e.EV_ABS: [
                (e.ABS_X, AbsInfo(0, 0, self.WIDTH, 0, 0, self.RES)),
                (e.ABS_Y, AbsInfo(0, 0, self.HEIGHT, 0, 0, self.RES)),
                (e.ABS_MT_SLOT, AbsInfo(0, 0, 4, 0, 0, 0)),
                (e.ABS_MT_TRACKING_ID, AbsInfo(0, 0, 65535, 0, 0, 0)),
                (e.ABS_MT_POSITION_X, AbsInfo(0, 0, self.WIDTH, 0, 0, self.RES)),
                (e.ABS_MT_POSITION_Y, AbsInfo(0, 0, self.HEIGHT, 0, 0, self.RES)),
            ],
        }
        self.ui = UInput(
            cap,
            name=name,
            vendor=0x06CB,
            product=0xCD46,
            version=0x0100,
            bustype=e.BUS_I2C,
            input_props=[e.INPUT_PROP_POINTER, e.INPUT_PROP_BUTTONPAD],
        )
        self._next_id = 1
        self._active: dict[int, int] = {}
        time.sleep(0.5)

    def close(self):
        self.release_all()
        self.ui.close()

    def _sync_tools(self, count: int):
        self.ui.write(e.EV_KEY, e.BTN_TOUCH, 1 if count > 0 else 0)
        self.ui.write(e.EV_KEY, e.BTN_TOOL_FINGER, 1 if count == 1 else 0)
        self.ui.write(e.EV_KEY, e.BTN_TOOL_DOUBLETAP, 1 if count == 2 else 0)
        self.ui.write(e.EV_KEY, e.BTN_TOOL_TRIPLETAP, 1 if count == 3 else 0)
        self.ui.write(e.EV_KEY, e.BTN_TOOL_QUADTAP, 1 if count == 4 else 0)
        self.ui.write(e.EV_KEY, e.BTN_TOOL_QUINTTAP, 1 if count >= 5 else 0)

    def fingers_down(self, points: list[tuple[int, int]]):
        self._active.clear()
        for slot, (x, y) in enumerate(points):
            tid = self._next_id
            self._next_id += 1
            self._active[slot] = tid
            self.ui.write(e.EV_ABS, e.ABS_MT_SLOT, slot)
            self.ui.write(e.EV_ABS, e.ABS_MT_TRACKING_ID, tid)
            self.ui.write(e.EV_ABS, e.ABS_MT_POSITION_X, int(x))
            self.ui.write(e.EV_ABS, e.ABS_MT_POSITION_Y, int(y))
        self.ui.write(e.EV_ABS, e.ABS_X, int(points[0][0]))
        self.ui.write(e.EV_ABS, e.ABS_Y, int(points[0][1]))
        self._sync_tools(len(points))
        self.ui.syn()

    def fingers_move(self, points: list[tuple[int, int]]):
        for slot, (x, y) in enumerate(points):
            self.ui.write(e.EV_ABS, e.ABS_MT_SLOT, slot)
            self.ui.write(e.EV_ABS, e.ABS_MT_POSITION_X, int(x))
            self.ui.write(e.EV_ABS, e.ABS_MT_POSITION_Y, int(y))
        self.ui.write(e.EV_ABS, e.ABS_X, int(points[0][0]))
        self.ui.write(e.EV_ABS, e.ABS_Y, int(points[0][1]))
        self.ui.syn()

    def release_all(self):
        if not self._active:
            return
        for slot in sorted(self._active.keys()):
            self.ui.write(e.EV_ABS, e.ABS_MT_SLOT, slot)
            self.ui.write(e.EV_ABS, e.ABS_MT_TRACKING_ID, -1)
        self._active.clear()
        self._sync_tools(0)
        self.ui.syn()

    def two_finger_scroll(self, dx: int, dy: int, steps: int = 20, step_dt: float = 0.012, hold_after: float = 0.0):
        x0, y0 = 1000, 600
        sep = 220
        self.fingers_down([(x0 - sep // 2, y0), (x0 + sep // 2, y0)])
        time.sleep(0.025)
        for i in range(1, steps + 1):
            t = i / steps
            cx = int(x0 + dx * t)
            cy = int(y0 + dy * t)
            self.fingers_move([(cx - sep // 2, cy), (cx + sep // 2, cy)])
            time.sleep(step_dt)
        if hold_after > 0:
            time.sleep(hold_after)
        self.release_all()

    def two_finger_hold(self, duration: float = 0.25):
        x0, y0 = 1000, 600
        sep = 220
        self.fingers_down([(x0 - sep // 2, y0), (x0 + sep // 2, y0)])
        time.sleep(duration)
        self.release_all()

    def two_finger_pinch(self, start_sep: int = 240, end_sep: int = 840, steps: int = 24, step_dt: float = 0.014):
        cx, cy = 1000, 600
        self.fingers_down([(cx - start_sep // 2, cy - start_sep // 4), (cx + start_sep // 2, cy + start_sep // 4)])
        time.sleep(0.04)
        for i in range(1, steps + 1):
            t = i / steps
            sep = start_sep + (end_sep - start_sep) * t
            self.fingers_move([(int(cx - sep / 2), int(cy - sep / 4)), (int(cx + sep / 2), int(cy + sep / 4))])
            time.sleep(step_dt)
        time.sleep(0.03)
        self.release_all()


def parse_trace(trace_path: str) -> list[str]:
    if not os.path.isfile(trace_path):
        return []
    with open(trace_path, "r", encoding="utf-8", errors="replace") as f:
        return [ln.strip() for ln in f if ln.strip()]


def get_code_zoom(sess: HexMcpSession) -> float:
    txt = sess.text(sess.call("hex_settings_get"))
    try:
        data = json.loads(txt)
        for k in ("code_zoom", "codeZoom", "CodeZoom"):
            if k in data:
                return float(data[k])
    except Exception:
        pass
    m = re.search(r'"?(?:code_zoom|codeZoom|CodeZoom)"?\s*[:=]\s*([0-9.]+)', txt)
    return float(m.group(1)) if m else -1.0


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--hex", required=True)
    p.add_argument("--workspace", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--screenshot-dir", default="/tmp/hex-linux-test")
    args = p.parse_args()

    os.makedirs(args.screenshot_dir, exist_ok=True)
    trace_path = os.path.join(args.screenshot_dir, "scroll-trace.log")
    if os.path.exists(trace_path):
        os.remove(trace_path)

    test_file = "/tmp/hex-touchpad-test.txt"
    with open(test_file, "w", encoding="utf-8") as f:
        for i in range(1, 501):
            f.write(f"line {i:03d}: " + ("touchpad_horizontal_scroll_column_check_ " * 8) + "\n")

    driver = os.environ.get("SDL_VIDEODRIVER", "wayland")
    gui_log = os.path.join(args.screenshot_dir, f"touchpad-hex-{driver}.log")
    abs_ptr = VirtualPointer()
    tp = VirtualTouchpad()
    sess = HexMcpSession(
        args.hex,
        args.workspace,
        gui_log,
        extra_env={"HORIZON_SCROLL_TRACE": trace_path},
    )

    checks: dict[str, dict] = {}
    try:
        sess.connect(timeout=60.0)
        time.sleep(1.2)

        # Open long wide file and disable word wrap so horizontal scrolling is testable
        sess.call("hex_open", path=test_file)
        sess.call("hex_settings_set", key="word_wrap", value="false")
        sess.call("hex_settings_set", key="code_zoom", value="1.0")
        time.sleep(0.8)

        # Position OS cursor right in the center of the Hex CodeEditor window
        abs_ptr.move_to_center_and_click(sess)
        time.sleep(0.3)

        zoom0 = get_code_zoom(sess)
        print(f"[touchpad:{driver}] Initial code_zoom={zoom0}")
        sess.screenshot(os.path.join(args.screenshot_dir, f"touchpad-{driver}-before.png"), scale=1.0)

        # 1) Two-finger vertical scroll down (natural scroll: fingers move UP => dy < 0)
        n0 = len(parse_trace(trace_path))
        tp.two_finger_scroll(dx=0, dy=-480, steps=22, step_dt=0.012)
        time.sleep(0.9)
        seg_vert = parse_trace(trace_path)[n0:]
        has_vert_touch = any("axis-frame" in ln and "phase=Touching" in ln for ln in seg_vert)
        has_vert_release = any("axis-frame" in ln and "phase=Released" in ln for ln in seg_vert)
        glide_ticks = sum(1 for ln in seg_vert if "gliding=True" in ln)
        has_sdl_vert = any(" sdl " in ln and "turn=" in ln for ln in seg_vert)
        vert_scroll_down = any(
            re.search(r"scroll=[0-9.-]+,-[0-9.]+", ln) or re.search(r"turn=[0-9.-]+,-[0-9.]+", ln)
            for ln in seg_vert
        )
        checks["vertical_two_finger_scroll"] = {
            "pass": bool((has_vert_touch and has_vert_release and glide_ticks >= 5) if driver == "wayland" else has_sdl_vert) and vert_scroll_down,
            "events": len(seg_vert),
            "wayland_touch": has_vert_touch,
            "wayland_release": has_vert_release,
            "kinetic_glide_ticks": glide_ticks,
            "scroll_down_sign": vert_scroll_down,
            "sample": seg_vert[:6],
        }
        sess.screenshot(os.path.join(args.screenshot_dir, f"touchpad-{driver}-after-scroll.png"), scale=1.0)

        # 2) Two-finger horizontal scroll right (natural scroll: fingers move LEFT => dx < 0)
        n1 = len(parse_trace(trace_path))
        tp.two_finger_scroll(dx=-420, dy=0, steps=22, step_dt=0.012)
        time.sleep(0.5)
        seg_horiz = parse_trace(trace_path)[n1:]
        # Horizon ScrollX < 0 scrolls RIGHT (content moves left with fingers)
        horiz_right_sign = any(
            re.search(r"scroll=-[0-9.]+,", ln) or re.search(r"turn=-[0-9.]+,", ln)
            for ln in seg_horiz
        )
        checks["horizontal_two_finger_scroll"] = {
            "pass": len(seg_horiz) > 0 and horiz_right_sign,
            "events": len(seg_horiz),
            "scroll_right_sign": horiz_right_sign,
            "sample": seg_horiz[:6],
        }

        # 3) Two-finger hold gesture (stops momentum immediately on finger touchdown)
        n2 = len(parse_trace(trace_path))
        tp.two_finger_hold(duration=0.25)
        time.sleep(0.25)
        seg_hold = parse_trace(trace_path)[n2:]
        has_hold_begin = any("hold-begin" in ln for ln in seg_hold)
        has_hold_end = any("hold-end" in ln for ln in seg_hold)
        checks["two_finger_hold_gesture"] = {
            "pass": (has_hold_begin and has_hold_end) if driver == "wayland" else True,
            "events": len(seg_hold),
            "hold_begin": has_hold_begin,
            "hold_end": has_hold_end,
            "sample": seg_hold[:6],
        }

        # 4) Two-finger pinch out (zoom IN -> code_zoom > zoom0)
        n3 = len(parse_trace(trace_path))
        tp.two_finger_pinch(start_sep=220, end_sep=860, steps=26, step_dt=0.014)
        time.sleep(0.6)
        seg_pinch_out = parse_trace(trace_path)[n3:]
        zoom_after_out = get_code_zoom(sess)
        checks["pinch_out_zoom_in"] = {
            "pass": zoom_after_out > max(1.05, zoom0 + 0.05),
            "zoom_before": zoom0,
            "zoom_after": zoom_after_out,
            "events": len(seg_pinch_out),
            "sample": seg_pinch_out[:6],
        }

        # 5) Two-finger pinch in (zoom OUT -> code_zoom < zoom_after_out)
        n4 = len(parse_trace(trace_path))
        tp.two_finger_pinch(start_sep=860, end_sep=260, steps=26, step_dt=0.014)
        time.sleep(0.6)
        seg_pinch_in = parse_trace(trace_path)[n4:]
        zoom_after_in = get_code_zoom(sess)
        checks["pinch_in_zoom_out"] = {
            "pass": zoom_after_in < zoom_after_out - 0.05,
            "zoom_before": zoom_after_out,
            "zoom_after": zoom_after_in,
            "events": len(seg_pinch_in),
            "sample": seg_pinch_in[:6],
        }

        sess.screenshot(os.path.join(args.screenshot_dir, f"touchpad-{driver}-after.png"), scale=1.0)
        # Restore defaults
        sess.call("hex_settings_set", key="code_zoom", value="1.0")
        sess.call("hex_settings_set", key="word_wrap", value="true")

        all_pass = all(c["pass"] for c in checks.values())
        report = {
            "driver": driver,
            "all_pass": all_pass,
            "checks": checks,
            "total_trace_lines": len(parse_trace(trace_path)),
        }
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(report, f, indent=2)
        print(json.dumps(report, indent=2))
        return 0 if all_pass else 1
    finally:
        sess.close()
        tp.close()
        abs_ptr.close()


if __name__ == "__main__":
    sys.exit(main())
