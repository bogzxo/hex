#!/bin/sh
# Puts Hex on a Linux desktop: a .desktop file for Dawn and one for Dusk, with their icons, for the current user. A
# Wayland desktop (GNOME, KDE) shows a window's name and icon from the .desktop file its app id names, hex-dawn and
# hex-dusk (WindowManagerConfiguration.AppId), and lists them among the apps. Run once, and again if Hex moves.
#
#   scripts/install-desktop.sh [path to Hex]
#
# Without a path it's the Release build in this repository, or the Debug one.
set -e

repo="$(cd "$(dirname "$0")/.." && pwd)"
hex="$1"
if [ -z "$hex" ]; then
    for build in Release Debug; do
        if [ -x "$repo/Hex/bin/$build/net10.0/Hex" ]; then hex="$repo/Hex/bin/$build/net10.0/Hex"; break; fi
    done
fi
if [ -z "$hex" ] || [ ! -x "$hex" ]; then
    echo "No Hex to put on the desktop, build it (dotnet build Hex/Hex.csproj) or say where it is" >&2
    exit 1
fi
hex="$(cd "$(dirname "$hex")" && pwd)/$(basename "$hex")"

data="${XDG_DATA_HOME:-$HOME/.local/share}"
icons="$data/icons/hicolor/256x256/apps"
mkdir -p "$data/applications" "$icons"
cp "$repo/Hex.Shell/Assets/hex_icon.png" "$icons/hex-dawn.png"
cp "$repo/Hex.Dusk/Assets/dusk_icon.png" "$icons/hex-dusk.png"

cat > "$data/applications/hex-dawn.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Hex
GenericName=Code editor
Comment=The editor of Horizon's layouts and code
Exec="$hex" %F
Icon=hex-dawn
Terminal=false
Categories=Development;IDE;TextEditor;
StartupWMClass=hex-dawn
MimeType=text/plain;
Actions=NewWindow;

[Desktop Action NewWindow]
Name=New window
Exec="$hex" --window
EOF

cat > "$data/applications/hex-dusk.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Dusk
GenericName=Terminal
Comment=Hex's terminal
Exec="$hex" --dusk
Icon=hex-dusk
Terminal=false
Categories=System;TerminalEmulator;
StartupWMClass=hex-dusk
Actions=Toggle;

[Desktop Action Toggle]
Name=Drop down or put away
Exec="$hex" --dusk --toggle
EOF

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q "$data/icons/hicolor" 2>/dev/null || true
echo "Hex and Dusk are on the desktop, running $hex"
echo "For Dusk's drop down, bind a key to: \"$hex\" --dusk --toggle"
