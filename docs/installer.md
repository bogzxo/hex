# Hex Setup

`Hex.Installer` is a native Horizon / Horizon.UI application using the same flat skin and Inter font as Hex. `Hex.Installation` owns release discovery, verification, installation, platform integration and the process handoff. `Hex.Installer.Tests` exercises those operations without changing the machine's launchers or registry. All three projects are in `Hex.sln` and the parent workspace solution.

Setup uses Hex's `ScaleToDisplay` / `UICompositor.DisplayScale` path, with a PerMonitorV2 manifest on Windows. The camera follows framebuffer resizing, including monitor changes. Layout fitting operates in logical units so 125%, 150%, 200% and Retina scaling apply exactly once; small windows fit the setup card without clipping.

The window takes its corner radius from the `card` in `setup.hor`, following DPI and resizing. Native clipping is supported on Windows, macOS and X11; Wayland decoration depends on the compositor. Maximized windows remain square. These native integrations need testing on the target OS.

**Browse** opens Horizon's folder picker to choose an empty installation directory or an existing managed Hex installation. On macOS, selecting a containing directory creates `Hex.app/Contents/MacOS` inside it; selecting an `.app` bundle uses that bundle. Invalid folders leave the previous destination intact. Update and uninstall destinations remain fixed. The selected destination is used for installation, shortcuts, registration and launch.

**Customize** configures Hex and Dusk desktop shortcuts, app-menu entries and (on Windows) App Paths registration for Windows Run. App Paths does not change the shell PATH. Only applicable choices are shown; macOS offers the Hex desktop link, with app bundle registration retained. Apply changes the pending install preferences; Cancel or closing the options window discards pending edits. Uninstall support is always retained.

Choices are stored under `integration` in `.hex-install.json`, carried forward by updates and restored with the installation on rollback. Existing records without preferences retain the original defaults. Choosing another managed installation loads its choices; choosing another empty destination preserves the current pending choices. Deselected entries belonging to this installation are removed during registration. The options UI is editable in `Assets/installer/options.hor`, with wording in the existing localisation file.

The editable UI is `Hex.Installer/Assets/installer/setup.hor`. Open it in Hex's designer to change the layout, colours, text, logo and control styling. Preserve the control names documented at the top of the file: `InstallerScene` uses them to bind the installer actions and progress updates. The layout is copied into both build and publish output; runtime install/uninstall state updates remain in C#.

All installer wording lives in `Hex.Installer/Assets/installer/localisation/en-gb.hor`, read with Horizon's `Localisation.ReadFile`. Layout text uses `@installer.*` keys resolved through the compositor's strings. Runtime states, progress, installer errors and Hex's update prompts use the same catalog. Edit the file and restart Setup; preserve `{placeholder}` names. Hex also reloads the catalog when checking for updates. The catalog is copied into installer and editor build/publish output. Executable names, command-line switches, shortcut filenames and platform identifiers remain stable technical identifiers; messages supplied by the OS or network libraries retain their own wording.

## Install

On Windows, download and run the single `Hex-Setup-win-x64.exe` or `Hex-Setup-win-arm64.exe` from [the latest release](https://github.com/bogzxo/hex/releases/latest). It extracts its native installer, libraries and editable assets into a unique `Hex.Setup-<id>` folder beside the downloaded EXE, runs setup, and removes that folder after the setup window closes, including cancellation or failure. The download stays in place. Its containing folder must be writable; move the EXE to Downloads if needed. Simultaneous launches use separate folders.

On Linux and macOS, download `Hex-Setup-<rid>.zip`, extract the whole archive, and open `Hex.Installer` or `Hex Setup.app`. Setup shows the destination and release before installing. No administrator access is required.

| Platform | Destination | Integration |
| --- | --- | --- |
| Windows | `%LOCALAPPDATA%/Programs/Hex` | Desktop shortcut; Hex, Dusk and uninstall Start menu shortcuts; per-user App Paths and Installed Apps registration |
| Linux | `~/.local/opt/hex` | XDG application launchers for Hex, Dusk and uninstall; desktop launcher |
| macOS | `~/Applications/Hex.app/Contents/MacOS` | Application bundle registered with Launch Services; desktop link; `~/Applications/Uninstall Hex.command` |

The release workflow builds x64 and ARM64 packages on native runners for each OS. Linux needs a Vulkan loader and driver, as Hex already does. macOS packages are currently unsigned and not notarized; normal macOS trust requirements still apply. Setup does not disable Gatekeeper. Packaging and native execution on Linux/macOS must be validated on those systems; Windows tests do not establish that.

There is one registered installation per user. `--target <path>` is available for controlled deployments; on macOS it must end in `.app/Contents/MacOS`. Choose an empty directory for a new installation. Portable directories are intentionally not adopted: move personal settings into the installed copy yourself if wanted.

## Updates and uninstall

In Dawn, choose **Help → Check for Updates** (also in the command palette). Checks are explicit; no background polling is added. When a newer stable version exists, **Install** downloads and verifies its complete package while Hex stays open. It validates the archive and relocates that release's own installer and dependencies to a private temporary directory, then uses Hex's normal close prompt. Cancelling the download or close prompt discards the prepared copy. A failed process start keeps Hex open. The new installer waits for that exact parent process to exit and re-verifies a private snapshot of the cached package before installing it; there is no second package download. Other running Hex or Dusk windows must be closed. **Launch Hex** opens the installed version after completion; the editor restores its normal saved session.

### Installer update plan and lifecycle

Hex, Setup and the uninstaller share the release version. An application update runs the **new release's installer**, rather than requiring the old installed Setup to understand how to install a newer release. The existing installation transaction replaces `installer/` with the release's files and rolls it back along with Hex if registration fails. The next uninstall therefore uses the updated code. A separate installer-only channel or network check during uninstall is unnecessary; uninstall remains available offline and always asks for confirmation.

The handoff adds `--package <absolute cached ZIP>` to the existing pinned-version and parent-process arguments. Setup accepts this only with `--update`. It checks the published release again, refuses a changed tag or downgrade, verifies the cached package's size and SHA-256, and stages it before changing the installation. Missing, modified, unsafe or incomplete packages fail before commit. The cached ZIP is kept with the temporary installer for retries and is removed by the normal temporary-copy cleanup after Setup exits. Cancelling the close prompt, a failed save, or a failed process start abandons the handoff and cleans up the prepared copy without changing the installed files.

Standalone Setup continues to install the complete release, including its matching installed updater/uninstaller. Its downloaded launcher is a distribution artifact and is not overwritten in place. This avoids modifying a running executable and preserves the existing Windows bundle, Linux and macOS launch behaviour.

Uninstall through Windows Installed Apps / the Start menu, the Linux Uninstall Hex launcher, or the macOS uninstall command. The bundled installer relocates itself before changing the installation. Its confirmation screen is always shown. Only files recorded in `.hex-install.json` are removed; settings, backups, extensions and personal files remain.

## Integrity and recovery

The GitHub stable release API supplies an exact `Hex-<rid>.zip` asset and its SHA-256 digest ([API reference](https://docs.github.com/en/rest/releases/releases)). Downloads are streamed with cancellation and verified against both size and digest before extraction. Releases without a digest or bundled installer are refused. A newly published release from the updated workflow is therefore required before the initial live install.

Archives must contain one `Hex/` directory. Traversal, symbolic links, special files, duplicate names, excessive expanded size and collisions with personal files are refused. Installation destinations containing symbolic links are refused. A per-destination file lock prevents concurrent installers.

Updates stage a complete new directory beside the old one, preserving unowned files and removing obsolete package files. The old directory is moved aside during commit and restored if registration fails. Cancellation is honored before commit; directory replacement finishes safely once it starts. Registration rollback is best effort. A power loss between directory renames can leave a `.previous-<id>` directory beside the destination; retain it for recovery. There is no claim of crash-atomic replacement or OS code signing. SHA-256 verifies the artifact served by GitHub, not an independent publisher signature.

On Windows, relocated update/uninstall copies also clean up after exiting: a hidden Windows PowerShell helper waits for the exact launcher process, then removes only its marked, uniquely named setup-copy folder in the system temporary directory. The installed launcher exits before its relocated copy proceeds, allowing uninstall to remove the installed executable. The downloaded launcher stays open until its extracted UI exits so it can remove the extraction folder itself. A killed launcher, power loss or an OS-denied cleanup can leave a `Hex.Setup-<id>` folder (or `hex-setup-<id>` temporary copy); it is safe to remove after setup has exited. Linux/macOS helper copies retain the existing OS-temporary-directory lifetime. A failed startup records diagnostics in the system temporary directory as `hex-setup-error.txt`.

## Development

```sh
dotnet run --project Hex.Installer -- --preview
dotnet test Hex.Installer.Tests
dotnet publish Hex.Installer -c Release -r win-x64
```

`--preview` shows an offline, non-installing UI for screenshots. Normal setup reads GitHub when opened. Internal update handoff arguments are `--update --target <root> --version <tag> --wait-pid <pid> --wait-start <UTC ticks>`; `--uninstall --target <root>` opens removal confirmation. There is no silent destructive mode.

Windows native `dotnet publish` automatically packages the entire installer output into `Hex.Installer.exe`, including native libraries, shaders, `.hor` layouts and localisation. The ZIP overlay includes a SHA-256 checksum checked before extraction; it detects corruption, not an independent publisher signature. Use a dedicated publish output directory. `-p:BundleWindowsInstaller=false` retains the loose native output for debugging; normal builds and `--preview` also remain usable with loose assets.

Release packaging copies the installer output into `Hex/installer/` and uploads `Hex-<rid>.zip` plus the standalone Windows `Hex-Setup-<rid>.exe` (or Linux/macOS setup ZIP). The same bundled Windows EXE serves as the updater and uninstaller. Tag versions must match `Directory.Build.props`. Older portable releases remain usable but cannot perform managed in-place updates.
