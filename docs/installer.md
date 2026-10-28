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

Download `Hex-Setup-<rid>.zip` from [the latest release](https://github.com/bogzxo/hex/releases/latest), extract the whole archive, and open `Hex.Installer` (`Hex.Installer.exe` on Windows, `Hex Setup.app` on macOS). Setup shows the destination and release before installing. No administrator access is required.

| Platform | Destination | Integration |
| --- | --- | --- |
| Windows | `%LOCALAPPDATA%/Programs/Hex` | Desktop shortcut; Hex, Dusk and uninstall Start menu shortcuts; per-user App Paths and Installed Apps registration |
| Linux | `~/.local/opt/hex` | XDG application launchers for Hex, Dusk and uninstall; desktop launcher |
| macOS | `~/Applications/Hex.app/Contents/MacOS` | Application bundle registered with Launch Services; desktop link; `~/Applications/Uninstall Hex.command` |

The release workflow builds x64 and ARM64 packages on native runners for each OS. Linux needs a Vulkan loader and driver, as Hex already does. macOS packages are currently unsigned and not notarized; normal macOS trust requirements still apply. Setup does not disable Gatekeeper. Packaging and native execution on Linux/macOS must be validated on those systems; Windows tests do not establish that.

There is one registered installation per user. `--target <path>` is available for controlled deployments; on macOS it must end in `.app/Contents/MacOS`. Choose an empty directory for a new installation. Portable directories are intentionally not adopted: move personal settings into the installed copy yourself if wanted.

## Updates and uninstall

In Dawn, choose **Help → Check for Updates** (also in the command palette). Checks are explicit; no background polling is added. When a newer stable version exists, **Install** copies the bundled installer to a private temporary directory and uses Hex's normal close prompt. Cancelling the prompt cancels the handoff. A failed process start keeps Hex open. The installer waits for that exact parent process to exit, then downloads the release itself. Other running Hex or Dusk windows must be closed. **Launch Hex** opens the installed version after completion; the editor restores its normal saved session.

Uninstall through Windows Installed Apps / the Start menu, the Linux Uninstall Hex launcher, or the macOS uninstall command. The bundled installer relocates itself before changing the installation. Its confirmation screen is always shown. Only files recorded in `.hex-install.json` are removed; settings, backups, extensions and personal files remain.

## Integrity and recovery

The GitHub stable release API supplies an exact `Hex-<rid>.zip` asset and its SHA-256 digest ([API reference](https://docs.github.com/en/rest/releases/releases)). Downloads are streamed with cancellation and verified against both size and digest before extraction. Releases without a digest or bundled installer are refused. A newly published release from the updated workflow is therefore required before the initial live install.

Archives must contain one `Hex/` directory. Traversal, symbolic links, special files, duplicate names, excessive expanded size and collisions with personal files are refused. Installation destinations containing symbolic links are refused. A per-destination file lock prevents concurrent installers.

Updates stage a complete new directory beside the old one, preserving unowned files and removing obsolete package files. The old directory is moved aside during commit and restored if registration fails. Cancellation is honored before commit; directory replacement finishes safely once it starts. Registration rollback is best effort. A power loss between directory renames can leave a `.previous-<id>` directory beside the destination; retain it for recovery. There is no claim of crash-atomic replacement or OS code signing. SHA-256 verifies the artifact served by GitHub, not an independent publisher signature.

Temporary installer copies are retained until the operating system cleans its temporary directory; this avoids deleting a running executable or a recovery copy. A failed startup records diagnostics in the system temporary directory as `hex-setup-error.txt`.

## Development

```sh
dotnet run --project Hex.Installer -- --preview
dotnet test Hex.Installer.Tests
dotnet publish Hex.Installer -c Release -r win-x64
```

`--preview` shows an offline, non-installing UI for screenshots. Normal setup reads GitHub when opened. Internal update handoff arguments are `--update --target <root> --version <tag> --wait-pid <pid> --wait-start <UTC ticks>`; `--uninstall --target <root>` opens removal confirmation. There is no silent destructive mode.

Release packaging publishes the installer separately, copies its complete output into `Hex/installer/`, and uploads both ZIPs. Tag versions must match `Directory.Build.props`. Older portable releases remain usable but cannot perform managed in-place updates.
