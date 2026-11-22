# Hex on macOS: Hex.app

Hex is a native program (Native AOT), so the Mac app is a bundle with Hex's executable in it and nothing else to
install: no .NET, no runtime. `scripts/macos/make-app.sh` makes it from a checkout, on a Mac.

## Installing

1. Open the `.dmg` (or unzip the `.zip`) and drag **Hex** onto **Applications**.
2. A Hex that isn't signed with a Developer ID and notarized by Apple (every build made for a person's own Mac, see
   below) is stopped by Gatekeeper the first time when it was downloaded: *Control-click Hex, Open*, or in System Settings,
   Privacy & Security, "Open Anyway". A Hex built on the Mac it runs on has nothing to be stopped for.
3. Hex needs macOS 14 or newer (the minimum is read from the executable into Info.plist) and a Metal graphics card. The
   build made on Apple silicon is arm64 only; `--arch universal` adds Intel (not tried yet).

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
scripts/macos/make-app.sh --arch universal     # arm64 and x64 joined with lipo
HORIZON_ROOT=~/release/horizon scripts/macos/make-app.sh   # against a clean checkout of the engine, as releases are made
```

It needs Xcode (or the Command Line Tools: clang, `codesign`, `sips`, `iconutil`, `hdiutil`) and the .NET SDK the repository
asks for in `global.json`. The publish takes about two minutes (Native AOT). It does what `dotnet publish -c Release -r osx-arm64` does
and arranges the result:

```
Hex.app/Contents/Info.plist                 made from scripts/macos/Info.plist.in (version from Directory.Build.props)
Hex.app/Contents/MacOS/Hex                  the program
Hex.app/Contents/MacOS/*.dylib              SDL, MoltenVK, Skia, glfw, slang, HarfBuzz, oniguruma: the program loads these by name from its own folder
Hex.app/Contents/Resources/Hex.icns         from Assets/hex_icon.png
Hex.app/Contents/Resources/Assets, shaders  what Hex ships
```

In a bundle, `HexBundle.Inside` is true and `HexHost.ProgramFolder` is `Contents/Resources`: Hex reads what it ships from there
and writes nothing there (a signed bundle that changes fails `codesign --verify` and macOS then calls it damaged). The
rest of the engine already knew the layout (`PlatformPaths.Program`).

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

`.github/workflows/build.yml` has a `macos-app` job (runner `macos-15`, Apple silicon). It checks out the same commits as the
other jobs (hex with its submodules, and the engine on development in `horizon/`), installs .NET 11 and runs
`scripts/macos/make-app.sh --arch arm64 --zip --dmg`. The packages are `Hex-<version>-macos-arm64.dmg` and `.zip`, uploaded as
the workflow artifact `Hex-macos-arm64-app` and attached to the GitHub release by the `release` job with the others, and
to the nightly releases (`nightly.yml`). The older `Hex-osx-arm64.zip` of the `build` job (what Hex Setup and the update check
download) is unchanged. The job summary says whether the app was signed with a Developer ID or only ad hoc.

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

The certificate secrets alone give a signed but not notarized app; all six give a notarized one. An Intel or universal job is left
as a marked TODO in the workflow: it has not been run. The same signing and notarizing by hand:
`scripts/macos/make-app.sh --identity "<name or hash>" --notary-key AuthKey.p8 --notary-key-id ID --notary-issuer ISSUER --zip --dmg`.

## Updates

The Windows installer and updater (Hex.Installation) don't apply here: a new Hex.app replaces the old one in
Applications, and the person's files stay where they are. An updating Hex.app would be notarized and use the
Sparkle framework or replace itself the way the Windows one does; neither is done.
