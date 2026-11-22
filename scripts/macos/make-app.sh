#!/bin/bash
# Makes Hex.app, and a .zip or a .dmg of it, from this checkout, on a Mac.
#
#   scripts/macos/make-app.sh [options]
#
#   --arch arm64|x64|universal   what to build for, the Mac running this by default. universal publishes both and joins
#                                the programs and libraries with lipo.
#   --out DIR                    where Hex.app and its packages are made, artifacts/macos in this checkout by default.
#   --identity NAME              the signing identity, "Developer ID Application: Name (TEAMID)" for a Hex to hand out
#                                (hardened runtime, secure timestamp, the entitlements in Hex.entitlements). "-" is the
#                                default: an ad hoc signature, which a Mac accepts for the one it was made on.
#   --zip                        also Hex-<version>-macos-<arch>.zip (a ditto archive, what Finder makes).
#   --dmg                        also Hex-<version>-macos-<arch>.dmg with Hex.app and a link to Applications.
#   --notary-profile NAME        submit the zip or dmg to Apple's notary service with the stored credentials
#                                `xcrun notarytool store-credentials NAME` made, wait, and staple the ticket. Only with
#                                a Developer ID --identity. Nothing is sent anywhere without this.
#   --skip-publish               use the publish output of an earlier run in the out folder.
#   --bundle-id ID               the CFBundleIdentifier, com.bogzxo.hex by default.
#   --channel dev                the nightly's channel (dotnet publish -p:HexChannel=dev, with its own icon).
#   -- ARGS                      the rest goes to dotnet publish.
#
# HORIZON_ROOT=/path/to/horizon (the engine checkout) builds against that engine, as the release builds do from a clean
# checkout of the exact commits. Without it the workspace's Directory.Build.props, or the engine cloned in hex/horizon, is used.
#
# What the bundle is (docs/macos-app.md has the reasons):
#   Hex.app/Contents/MacOS/Hex            the native executable (Native AOT, no .NET on the Mac needed)
#   Hex.app/Contents/MacOS/*.dylib        the libraries it loads by name next to itself (SDL, MoltenVK, Skia, slang, ...)
#   Hex.app/Contents/Resources/           Assets, shaders and everything else that isn't code, and Hex.icns
#   Hex.app/Contents/Info.plist
# Nothing is ever written inside it, settings and data are in ~/Library/Application Support/Hex.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
arch=""
out="$repo/artifacts/macos"
identity="-"
zip=0; dmg=0; skip=0
notary=""
bundle_id="com.bogzxo.hex"
channel=""
extra=()

while [ $# -gt 0 ]; do
    case "$1" in
        --arch) arch="$2"; shift 2 ;;
        --out) out="$2"; shift 2 ;;
        --identity) identity="$2"; shift 2 ;;
        --zip) zip=1; shift ;;
        --dmg) dmg=1; shift ;;
        --notary-profile) notary="$2"; shift 2 ;;
        --skip-publish) skip=1; shift ;;
        --bundle-id) bundle_id="$2"; shift 2 ;;
        --channel) channel="$2"; shift 2 ;;
        --) shift; extra=("$@"); break ;;
        -h|--help) sed -n '2,32p' "$0"; exit 0 ;;
        *) echo "make-app.sh: what is $1? (--help)" >&2; exit 2 ;;
    esac
done

if [ "$(uname -s)" != "Darwin" ]; then
    echo "make-app.sh makes a macOS app, run it on a Mac" >&2
    exit 1
fi
if [ -n "$notary" ] && [ "$identity" = "-" ]; then
    echo "make-app.sh: notarizing needs a Developer ID --identity, not an ad hoc signature" >&2
    exit 2
fi

host_arch="$(uname -m)"; [ "$host_arch" = "x86_64" ] && host_arch="x64"
[ -z "$arch" ] && arch="$host_arch"
case "$arch" in arm64|x64|universal) ;; *) echo "make-app.sh: --arch is arm64, x64 or universal" >&2; exit 2 ;; esac

# .NET is where the installer puts it, a shell started from the Finder or ssh may not have it on the PATH
for dir in /usr/local/share/dotnet "$HOME/.dotnet" /opt/homebrew/bin; do
    [ -x "$dir/dotnet" ] && export PATH="$dir:$PATH"
done
command -v dotnet >/dev/null || { echo "make-app.sh: no dotnet on the PATH" >&2; exit 1; }
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

version="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$repo/Directory.Build.props" | head -1)"
[ -n "$version" ] || { echo "make-app.sh: no <Version> in Directory.Build.props" >&2; exit 1; }
project="$repo/Hex/Hex.csproj"

props=()
if [ -n "${HORIZON_ROOT:-}" ]; then
    root="${HORIZON_ROOT%/}/"
    props+=("-p:HorizonRoot=$root" "-p:HidlRoot=${root}Horizon.HIDL/")
fi
[ -n "$channel" ] && props+=("-p:HexChannel=$channel")

mkdir -p "$out"
out="$(cd "$out" && pwd)"
work="$out/work"

publish() {
    local rid="$1" dest="$work/publish-$1"
    if [ "$skip" = 1 ] && [ -x "$dest/Hex" ]; then
        echo "== using the publish of $rid in $dest"
        return
    fi
    echo "== dotnet publish $rid"
    rm -rf "$dest"
    dotnet publish "$project" -c Release -r "osx-$rid" ${props[@]+"${props[@]}"} ${extra[@]+"${extra[@]}"} -o "$dest" \
        -p:DebugType=none -p:DebugSymbols=false
}

archs=("$arch"); [ "$arch" = "universal" ] && archs=(arm64 x64)
for rid in "${archs[@]}"; do publish "$rid"; done

# The folder the bundle is made from: code (the executable and the libraries) apart from what isn't code
stage="$work/stage"
rm -rf "$stage"
mkdir -p "$stage/code" "$stage/resources"

first="$work/publish-${archs[0]}"
for path in "$first"/*; do
    name="$(basename "$path")"
    case "$name" in
        Hex|*.dylib)
            if [ "$arch" = "universal" ]; then
                other="$work/publish-${archs[1]}/$name"
                if [ -f "$other" ] && ! lipo -info "$path" 2>/dev/null | grep -q "Architectures in the fat file"; then
                    lipo -create "$path" "$other" -output "$stage/code/$name" 2>/dev/null || cp "$path" "$stage/code/$name"
                else
                    cp "$path" "$stage/code/$name"
                fi
            else
                cp "$path" "$stage/code/$name"
            fi ;;
        *.pdb|*.dSYM) ;;
        *) cp -R "$path" "$stage/resources/$name" ;;
    esac
done

app="$out/Hex.app"
echo "== $app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$stage/code/." "$app/Contents/MacOS/"
cp -R "$stage/resources/." "$app/Contents/Resources/"
chmod +x "$app/Contents/MacOS/Hex"

# The icon: the PNG Hex has, as the sizes an .icns holds
png="$app/Contents/Resources/Assets/hex_icon.png"
[ "$channel" = "dev" ] && png="$app/Contents/Resources/Assets/hex_icon_dev.png"
icons="$work/Hex.iconset"
rm -rf "$icons"; mkdir -p "$icons"
for size in 16 32 128 256 512; do
    sips -z $size $size "$png" --out "$icons/icon_${size}x${size}.png" >/dev/null
    sips -z $((size * 2)) $((size * 2)) "$png" --out "$icons/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$icons" -o "$app/Contents/Resources/Hex.icns"

# The oldest macOS the executable says it runs on
min="$(vtool -show-build "$app/Contents/MacOS/Hex" 2>/dev/null | awk '/minos/ {print $2; exit}')"
[ -n "$min" ] || min="13.0"
build="${version}"
sed -e "s|@BUNDLE_ID@|$bundle_id|" -e "s|@VERSION@|$version|" -e "s|@BUILD@|$build|" -e "s|@MIN_MACOS@|$min|" \
    "$here/Info.plist.in" > "$app/Contents/Info.plist"
printf 'APPL????' > "$app/Contents/PkgInfo"
plutil -lint "$app/Contents/Info.plist" >/dev/null

# Signing, the libraries and the executable first, then the bundle (codesign --deep is only there for ad hoc signing:
# Apple's notary service wants every piece signed on its own)
sign() {
    if [ "$identity" = "-" ]; then
        codesign --force --sign - "$@"
    else
        codesign --force --sign "$identity" --options runtime --timestamp --entitlements "$here/Hex.entitlements" "$@"
    fi
}
echo "== signing ($identity)"
for lib in "$app"/Contents/MacOS/*.dylib; do [ -e "$lib" ] && sign "$lib"; done
sign "$app/Contents/MacOS/Hex"
sign "$app"
codesign --verify --deep --strict --verbose=2 "$app"

name="Hex-$version-macos-$arch"
package=""
if [ "$zip" = 1 ] || [ -n "$notary" ] && [ "$dmg" = 0 ]; then
    package="$out/$name.zip"
    rm -f "$package"
    ditto -c -k --sequesterRsrc --keepParent "$app" "$package"
    echo "== $package"
fi
if [ "$dmg" = 1 ]; then
    package="$out/$name.dmg"
    rm -f "$package"
    dmgroot="$work/dmg"
    rm -rf "$dmgroot"; mkdir -p "$dmgroot"
    cp -R "$app" "$dmgroot/Hex.app"
    ln -s /Applications "$dmgroot/Applications"
    hdiutil create -quiet -volname "Hex $version" -srcfolder "$dmgroot" -fs HFS+ -format UDZO "$package"
    [ "$identity" != "-" ] && codesign --force --sign "$identity" --timestamp "$package"
    echo "== $package"
fi

if [ -n "$notary" ]; then
    echo "== notarizing $package (the notary service of Apple, with the credentials of $notary)"
    xcrun notarytool submit "$package" --keychain-profile "$notary" --wait
    if [ "$package" = "$out/$name.dmg" ]; then
        xcrun stapler staple "$package"
    else
        xcrun stapler staple "$app"
        rm -f "$package"
        ditto -c -k --sequesterRsrc --keepParent "$app" "$package"
    fi
fi

echo "Hex $version for $arch: $app"
