#!/usr/bin/env bash
set -euo pipefail

# Runs inside the Ubuntu VM to build, test, and verify Hex on a live Linux desktop.
ROOT="${HEX_LINUX_ROOT:-$HOME/src/horizon}"
MODE="${1:-selftest}"
VIDEO_DRIVER="${HEX_VIDEO_DRIVER:-wayland}"
OUT_DIR="${HEX_LINUX_OUT:-/tmp/hex-linux-test}"
SELFTEST_SUITES="${HEX_SELFTEST_SUITES:-shell,menus,gestures,middle-scroll}"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

mkdir -p "$OUT_DIR"

detect_desktop_env() {
    local uid
    uid="$(id -u)"
    export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$uid}"

    # Pick up the live GNOME/Wayland/X11 session environment from the compositor when invoked over SSH.
    local shell_pid=""
    shell_pid="$(pgrep -u "$USER" -x gnome-shell 2>/dev/null | head -n 1 || true)"
    if [ -z "$shell_pid" ]; then
        shell_pid="$(pgrep -u "$USER" -f 'kwin_wayland|sway|Hyprland|Xorg' 2>/dev/null | head -n 1 || true)"
    fi
    if [ -n "$shell_pid" ] && [ -r "/proc/$shell_pid/environ" ]; then
        while IFS='=' read -r k v; do
            case "$k" in
                DISPLAY|WAYLAND_DISPLAY|XAUTHORITY|XDG_SESSION_TYPE|XDG_CURRENT_DESKTOP|DBUS_SESSION_BUS_ADDRESS)
                    export "$k=$v"
                    ;;
            esac
        done < <(tr '\0' '\n' < "/proc/$shell_pid/environ" | grep -E '^(DISPLAY|WAYLAND_DISPLAY|XAUTHORITY|XDG_SESSION_TYPE|XDG_CURRENT_DESKTOP|DBUS_SESSION_BUS_ADDRESS)=' || true)
    fi

    if [ -z "${WAYLAND_DISPLAY:-}" ] && [ -S "$XDG_RUNTIME_DIR/wayland-0" ]; then
        export WAYLAND_DISPLAY="wayland-0"
    fi
    if [ -z "${DISPLAY:-}" ]; then
        export DISPLAY=":0"
    fi
    if [ -z "${XAUTHORITY:-}" ]; then
        local xauth
        xauth="$(ls "$XDG_RUNTIME_DIR"/.mutter-Xwaylandauth.* 2>/dev/null | head -n 1 || true)"
        if [ -n "$xauth" ]; then
            export XAUTHORITY="$xauth"
        fi
    fi

    export SDL_VIDEODRIVER="$VIDEO_DRIVER"
    echo "[linux-test] Session: XDG_SESSION_TYPE=${XDG_SESSION_TYPE:-?} WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-none} DISPLAY=${DISPLAY:-none} SDL_VIDEODRIVER=$SDL_VIDEODRIVER"
}

check_graphics() {
    echo "[linux-test] Checking Vulkan & OpenGL..."
    if command -v vulkaninfo >/dev/null 2>&1; then
        vulkaninfo --summary 2>&1 | grep -E "Vulkan Instance Version|deviceName|deviceType|apiVersion|driverInfo" || true
    fi
}

build_debug() {
    echo "[linux-test] Building Hex (Debug)..."
    dotnet build "$ROOT/hex/Hex/Hex.csproj" -c Debug --nologo
}

publish_release() {
    echo "[linux-test] Publishing Hex NativeAOT (Release, linux-x64)..."
    local pub="$OUT_DIR/publish-linux-x64"
    rm -rf "$pub"
    dotnet publish "$ROOT/hex/Hex/Hex.csproj" -c Release -r linux-x64 -o "$pub/Hex" --nologo
    dotnet publish "$ROOT/hex/Hex.Notebooks.Render/Hex.Notebooks.Render.csproj" -c Release -r linux-x64 -o "$pub/Hex/modules" --nologo
    ls -lh "$pub/Hex/Hex"
}

run_unit_tests() {
    echo "[linux-test] Running Horizon.Tests and Hex.Tests on Linux..."
    dotnet test "$ROOT/horizon/Horizon.Tests/Horizon.Tests.csproj" -c Debug --nologo --verbosity minimal
    dotnet test "$ROOT/hex/Hex.Tests/Hex.Tests.csproj" -c Debug --nologo --verbosity minimal
}

run_selftest() {
    local hex_bin="${1:-$ROOT/hex/Hex/bin/Debug/net11.0/Hex}"
    if [ ! -x "$hex_bin" ]; then
        echo "[linux-test] Hex binary not found at $hex_bin" >&2
        exit 1
    fi

    # Ensure desktop session is unlocked so Wayland/X11 surfaces receive input and present frames.
    if command -v loginctl >/dev/null 2>&1; then
        loginctl unlock-session 2>/dev/null || true
    fi
    if command -v gdbus >/dev/null 2>&1 && [ -n "${DBUS_SESSION_BUS_ADDRESS:-}" ]; then
        gdbus call --session --dest org.gnome.ScreenSaver --object-path /org/gnome/ScreenSaver --method org.gnome.ScreenSaver.SetActive false >/dev/null 2>&1 || true
    fi

    local log_file="$OUT_DIR/selftest-${VIDEO_DRIVER}.log"
    local suite_args=()
    read -r -a suite_args <<< "${SELFTEST_SUITES//,/ }"
    echo "[linux-test] Running Hex --selftest (${suite_args[*]}) with SDL_VIDEODRIVER=$SDL_VIDEODRIVER..."
    timeout 180s "$hex_bin" --window --selftest "${suite_args[@]}" --exit > "$log_file" 2>&1 || {
        local rc=$?
        echo "[linux-test] Selftest exited with code $rc. Tail of $log_file:"
        tail -n 80 "$log_file" || true
        exit $rc
    }
    echo "[linux-test] Selftest PASSED. Summary:"
    grep -E "pass:|FAIL:|checks passed|took |Wayland touchpad|X11 XInput|Vulkan 1\." "$log_file" | tail -n 60 || true
}

run_dusk_selftest() {
    local hex_bin="${1:-$ROOT/hex/Hex/bin/Debug/net11.0/Hex}"
    local log_file="$OUT_DIR/dusk-selftest-${VIDEO_DRIVER}.log"
    echo "[linux-test] Running Dusk --dusk --selftest (shell scroll tabs) with SDL_VIDEODRIVER=$SDL_VIDEODRIVER..."
    timeout 120s "$hex_bin" --dusk --window --selftest shell scroll tabs --exit > "$log_file" 2>&1 || {
        local rc=$?
        echo "[linux-test] Dusk selftest exited with code $rc. Tail of $log_file:"
        tail -n 80 "$log_file" || true
        exit $rc
    }
    echo "[linux-test] Dusk selftest PASSED. Summary:"
    grep -E "pass:|FAIL:|checks passed|took |Wayland touchpad|Vulkan 1\." "$log_file" | tail -n 40 || true
}

run_smoke_mcp() {
    local hex_bin="${1:-$ROOT/hex/Hex/bin/Debug/net11.0/Hex}"
    local shot="$OUT_DIR/hex-linux-${VIDEO_DRIVER}.png"
    local log_file="$OUT_DIR/mcp-smoke-${VIDEO_DRIVER}.log"
    echo "[linux-test] Running live MCP smoke + screenshot test ($VIDEO_DRIVER)..."
    python3 "$ROOT/hex/scripts/linux/smoke-mcp.py" --hex "$hex_bin" --workspace "$ROOT/hex" --screenshot "$shot" --log "$log_file"
}

run_touchpad_tests() {
    local hex_bin="${1:-$ROOT/hex/Hex/bin/Debug/net11.0/Hex}"
    local out_json="$OUT_DIR/touchpad-results-${VIDEO_DRIVER}.json"
    echo "[linux-test] Running Linux laptop touchpad uinput/libinput E2E tests ($VIDEO_DRIVER)..."
    python3 "$ROOT/hex/scripts/linux/test-touchpad-uinput.py" --hex "$hex_bin" --workspace "$ROOT/hex" --out "$out_json" --screenshot-dir "$OUT_DIR"
}

detect_desktop_env
check_graphics

case "$MODE" in
    build)
        build_debug
        ;;
    unit)
        run_unit_tests
        ;;
    selftest)
        build_debug
        run_selftest
        ;;
    dusk)
        build_debug
        run_dusk_selftest
        ;;
    smoke)
        build_debug
        run_smoke_mcp
        ;;
    touchpad)
        build_debug
        run_touchpad_tests
        ;;
    publish)
        publish_release
        run_selftest "$OUT_DIR/publish-linux-x64/Hex/Hex"
        ;;
    all)
        build_debug
        run_unit_tests
        run_selftest
        run_dusk_selftest
        run_smoke_mcp
        run_touchpad_tests
        ;;
    *)
        echo "Unknown mode: $MODE (expected: build|unit|selftest|dusk|smoke|touchpad|publish|all)" >&2
        exit 2
        ;;
esac
