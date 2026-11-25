# Hex on macOS: Hex.app

Hex is a native program (Native AOT), so the Mac app is a bundle with Hex's executable in it and nothing else to
install: no .NET, no runtime. `scripts/macos/make-app.sh` makes it from a checkout, on a Mac.

## Installing

1. Download the `.dmg` for the Mac's chip, `Hex-<version>-macos-arm64.dmg` (Apple silicon, M1 and newer) or `-macos-x64.dmg`\n   (Intel), open it and drag **Hex** onto **Applications**. The `.zip` of the same name has the same app.
2. Releases are signed ad hoc, not with a Developer ID, and not notarized (that takes a paid Apple Developer account, see
   below). macOS stops an app like that the first time it opens after a download, with *"Hex can't be opened because Apple
   cannot check it for malicious software"*. Open it anyway, once: *Control-click Hex in Applications, Open, Open*, or
   open it, then System Settings, Privacy & Security, *"Open Anyway"* (macOS 15 Sequoia and newer no longer have
   the Control-click way, only the Settings one; right after the first try, the button is under Privacy & Security). The Terminal way is `xattr -dr com.apple.quarantine /Applications/Hex.app`.
   After that it opens like any other app. A Hex built on the Mac it runs on has nothing to be stopped for.
3. Hex needs macOS 14 or newer (the minimum is read from the executable into Info.plist) and a Metal graphics card. The
   release has one app for each chip, built on a Mac of that kind; `--arch universal` (both in one app) is not used.

Hex updates itself from the app: Check for Updates downloads `Hex-<version>-macos-<arch>.zip` of the release, checks its
digest, extracts it next to Hex.app, and when Hex closes a shell script moves the old app aside, the new one in and
opens it, putting the old one back if a move fails (`BundleUpdate`). The folder Hex.app is in has to be writable by you,
else the update says so and the dmg is the way. A Hex that isn't in an app opens the releases page.

Settings, keys, the agents' chats and caches are in your own folders, never in the app, so replacing Hex.app with a
newer one keeps all of it:

| What | Where |
| --- | --- |
| settings.hor, keys.hor, plugins, sessions | `~/Library/Application Support/Hex` |
| agents' chats, pictures, harness index | `~/Library/Application Support/Hex/data` |
| new layouts, extensions you install | `~/Library/Application Support/Hex/layouts`, `.../extensions`, `.../extension_catalog` |
| caches (compiled shaders, the harness's code index) | `~/Library/Application Support/Hex/cache`, `.../data/cache` |
| the debug log | `~/Library/Logs/Hex/debug.log` |

`HEX_HOME=<folder>` (an environment variable, `open --env HEX_HOME=<folder> -n -a Hex`) moves the first two rows under
that folder, for a second Hex that mustn't touch the first one's files.

## Opening files and folders

`open -a Hex file.cs`, Open With in the Finder and dropping a file on the Dock icon open it in the running Hex.
`open -a Hex folder` opens the folder as the workspace. From a shell, `Hex.app/Contents/MacOS/Hex path:12:3` is the
same as `hex` on the other systems (`--help` has all of it), and a shell command `hex` is one line:

```sh
ln -s /Applications/Hex.app/Contents/MacOS/Hex /usr/local/bin/hex
```

Programs started from the Finder or the Dock get the system's short PATH, not your shell's. Hex asks your login shell
for its environment as it starts, so `git`, `dotnet`, `claude` and `codex` are found the way they are in Terminal.

## Making the app

```sh
scripts/macos/make-app.sh                      # Hex.app for this Mac, ad hoc signed, in artifacts/macos
scripts/macos/make-app.sh --zip --dmg          # and Hex-<version>-macos-<arch>.zip and .dmg
scripts/macos/make-app.sh --arch x64           # for Intel (on an Intel Mac, or cross built on Apple silicon)
scripts/macos/make-app.sh --arch universal     # arm64 and x64 joined with lipo (not what releases use)
HORIZON_ROOT=~/release/horizon scripts/macos/make-app.sh   # against a clean checkout of the engine, as releases are made
```

It needs Xcode (or the Command Line Tools: clang, `codesign`, `sips`, `iconutil`, `hdiutil`) and the .NET SDK the repository
asks for in `global.json`. The publish takes about two minutes (Native AOT). It does what `dotnet publish -c Release -r osx-arm64` does
and arranges the result:

```
Hex.app/Contents/Info.plist                 made from scripts/macos/Info.plist.in (version from Directory.Build.props)
Hex.app/Contents/MacOS/Hex                  the program
Hex.app/Contents/MacOS/*.dylib              SDL, MoltenVK, Skia, glfw, slang, HarfBuzz, oniguruma: the program loads these by name from its own folder
Hex.app/Contents/MacOS/modules/*.dylib      optional NativeAOT module shared libraries (e.g. libHex.Notebooks.Render.dylib), loaded lazily via HexNativeModule
Hex.app/Contents/Resources/Hex.icns         from Assets/hex_icon.png
Hex.app/Contents/Resources/Assets, shaders  what Hex ships
```

Optional NativeAOT module libraries live in `Contents/MacOS/modules/` rather than `Contents/Resources/` because macOS code signing and Apple Notarization require Mach-O binaries to reside inside `Contents/MacOS/` (or `Contents/Frameworks/`), never in `Resources/`. `make-app.sh` signs every `.dylib` in `Contents/MacOS/` and `Contents/MacOS/modules/` individually before signing `Contents/MacOS/Hex` and finally `Hex.app`, so `codesign --verify --deep --strict` and notarization succeed cleanly.

In a bundle, `HexBundle.Inside` is true and `HexHost.ProgramFolder` is `Contents/Resources`: Hex reads what it ships from there
and writes nothing there (a signed bundle that changes fails `codesign --verify` and macOS then calls it damaged). The
rest of the engine already knew the layout (`PlatformPaths.Program`).

## Native macOS integration

* **Native Finder panels (`NSOpenPanel` / `NSSavePanel`)**: Opening files, choosing folders, and saving files use macOS's native Finder panels via `IPlatformDialogs` (`MacDialogs`) and `HexDialogs` when `dialogs.native` is enabled (macOS default on), with `UTType` extension filters and fallback to the in-app `UIFileBrowser`.
* **Native IME composition & Emoji picker (`⌃⌘Space`)**: `SdlTextEditing` captures `SDL_TEXTEDITING`, `SDL_TEXTEDITING_EXT`, and full UTF-8 `SDL_TEXTINPUT` commits (delivering multi-code-unit emoji and CJK/dead-key compositions intact without UTF-16 surrogate splitting) and positions the macOS candidate window at the caret via `SDL_SetTextInputRect`.
* **macOS system monospace fonts**: `GlyphOutlines` supports TrueType collections (`.ttc`, e.g. `Menlo.ttc`) alongside `.ttf`/`.otf`, and `HexCodeEditing.Font` surfaces macOS system monospace fonts (`SF Mono`, `Menlo`, `Monaco`) in the code font picker.
* **ProMotion 120 Hz & automatic Low Power Mode**: The display host queries `NSScreen.maximumFramesPerSecond` so ProMotion displays pace up to 120 Hz while 60 Hz external monitors cap cleanly at 60 Hz. `PlatformPower` / `DesktopPower` observes macOS Low Power Mode (`NSProcessInfoPowerStateDidChangeNotification`) and automatically throttles background/calm frame pacing to conserve battery while Low Power Mode is active.

### What Info.plist asks for

* `NSLocalNetworkUsageDescription` and `NSBonjourServices` (`_hex-link._tcp`): Hex Link finds the other Hexes on the network
  by multicast DNS, which macOS asks the person to allow once.
* Document types for folders, text and source code (role Editor, rank Alternate): Open With and `open -a`.
* The folder usage descriptions of Desktop, Documents and Downloads, which the terminal and the agents touch.
* No entitlements are needed to run (Native AOT: no JIT). The Developer ID signature adds the hardened runtime and
  `com.apple.security.cs.disable-library-validation` (`scripts/macos/Hex.entitlements`), so a library that isn't
  signed by the same team can still be loaded. Hex is not sandboxed: it runs git, compilers and the agents' programs.

## Signing and notarizing (for a Hex to hand out)

Apple's rule for an app downloaded from anywhere but the App Store: a Developer ID Application certificate, the
hardened runtime, a secure timestamp, every executable and library signed, and the notary service's ticket stapled.
The script does all of it when it is told who signs and where the notary credentials are:

```sh
xcrun notarytool store-credentials hex-notary --apple-id <id> --team-id <TEAMID> --password <app-specific password>
scripts/macos/make-app.sh --identity "Developer ID Application: Name (TEAMID)" --notary-profile hex-notary --dmg
```

That needs the owner's paid Apple Developer account: the certificate in the Mac's keychain, the Team ID and notary
credentials. Without them the script makes an ad hoc signature (`--identity -`), which passes
`codesign --verify --deep --strict` on any Mac and runs where it was built, but `spctl --assess` says *rejected* for it and a
copy downloaded to another Mac is quarantined and has to be opened by hand (above).

## GitHub release (CI)

`.github/workflows/build.yml` has a `macos-app` job with two legs, `macos-15` (arm64) and `macos-15-intel` (x64, with\n`-p:UseLdClassicXCodeLinker=false` because Apple's classic linker turns down an unaligned pointer in .NET's own\nSystem.Net.Security.Native on Intel). Each checks out the same commits as the other jobs (hex with its submodules, and the\nengine on development in `horizon/`), installs .NET 11 and runs `scripts/macos/make-app.sh --arch <arch> --zip --dmg`. The\npackages are `Hex-<version>-macos-<arch>.dmg` and `.zip`, uploaded as the workflow artifact `Hex-macos-<arch>-app` and\nattached to the GitHub release by the `release` job with the others, and to the nightly releases (`nightly.yml`). The\nreleases have no Hex Setup.app and no `Hex-osx-*.zip` any more: the unsigned Setup app, made by hand, was called damaged by\nmacOS, and the app updates itself. The site's links (`site/index.html`) name the version in the file, which\n`.github/site-release.py` makes the latest release's when the page is published. The job summary says whether the app was\nsigned with a Developer ID or only ad hoc.

Without the secrets below the job still runs and the app is ad hoc signed. With them it imports the certificate into a keychain
of its own, signs every library, the program and the bundle with the hardened runtime, has Apple's notary service check the
app (and the dmg) with an API key, staples the tickets, and the job fails if Apple turns the app down.

Secrets to add in the repository's Settings, Secrets and variables, Actions:

| Secret | What |
| --- | --- |
| `MACOS_CERT_P12_BASE64` | the Developer ID Application certificate with its private key exported from Keychain Access as .p12, then `base64 -i cert.p12` |
| `MACOS_CERT_PASSWORD` | the password the .p12 was exported with |
| `MACOS_TEAM_ID` | the ten character Team ID (developer.apple.com, Membership) |
| `MACOS_NOTARY_KEY_BASE64` | an App Store Connect API key (.p8, role Developer) as `base64 -i AuthKey_XXXX.p8` |
| `MACOS_NOTARY_KEY_ID` | that key's Key ID |
| `MACOS_NOTARY_ISSUER` | the Issuer ID of App Store Connect, Users and Access, Integrations |

The certificate secrets alone give a signed but not notarized app; all six give a notarized one. Both legs of the job (arm64 and x64) sign and notarize the same way. The same signing and notarizing by hand:
`scripts/macos/make-app.sh --identity "<name or hash>" --notary-key AuthKey.p8 --notary-key-id ID --notary-issuer ISSUER --zip --dmg`.

## Updates

The Windows installer and updater (Hex.Installation's installer handoff) don't apply here. A Hex in Hex.app replaces itself with
`BundleUpdate` (see Installing); the person's files stay where they are because they are never in the app. It does not sign
anything again: the app arrives with the signature it was made with, and a downloaded file made by Hex has no quarantine
flag (it is removed anyway when there is one).

## When there is a Developer ID

With a paid Apple Developer account, add the six `MACOS_*` secrets above and nothing else changes in the workflow: the job
signs with the hardened runtime, notarizes with Apple and staples the ticket, the dmg and the zip carry it, and macOS opens Hex
without the first-open steps. Then the site, README and the notes can drop those steps, and `codesign --verify` plus
`spctl --assess` in the Check the app step already run for a signed build. The updater needs no change: a notarized app
replaced by a notarized app is still accepted.
