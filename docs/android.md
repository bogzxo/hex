# Hex on Android

Dawn, the editor, runs on an Android tablet from `Hex.Android` (application id `dev.horizon.hex`). It's the same Dawn,
Core and Shell as on a desktop with an Android head under it, `Hex.Android/README.md` has how to build, install and
start it. Made on a Samsung SM-T725 (Android 17, API 37, Adreno 615 with Turnip's Vulkan 1.3) with its Book Cover
keyboard and a Bluetooth mouse, which is how it's meant to be used. Touch works too. Dusk, the terminal as a program of
its own, isn't in the APK, the terminal page inside Dawn is.

## How it's put together

- `HexHost` (Hex.Core) says where Hex's files are and what the place it runs on can do. A desktop leaves it as it is,
  everything on. The Android head sets it before the editor is made, and the parts that start programs, listen on
  pipes or download things ask it and say why not.
- `DawnProgram.Hosted`, `HostedScene`, `HostedSelfTest`, `HostedLeaving` and `HostedStopped` are Dawn's way in for a
  head that makes the engine itself. `DawnProgram.Run` is still the desktop's.
- Dawn doesn't reference `Horizon.Platform.Desktop` any more. The one thing it wanted from it, the native window's
  handle for the native keys self test, Hex.exe hands it through `DawnProgram.NativeWindow`.
- Content goes in the APK at the paths a desktop build copies it to, found from the projects' own copy items, so a file
  added to Dawn needs nothing done for the tablet. It's unpacked once per install into the app's files folder, which
  is Hex's program folder, the engine's content and the working directory (Dawn reads plenty of its files through
  File and Directory by path, and Skia wants real font files).
- Shaders come out of the engine's pack. A desktop Hex self test with `HORIZON_SHADERS=packed` used four modules, all
  in `horizon.hzshaders`, so Hex has no pack of its own.
- Settings, keys and sessions live in `files/config/Hex` (`HEX_HOME`), backups and history in the engine's state folder,
  new files in `files/hex/workspace`. No storage permission is asked for.
- Documents outside the app come through the system's picker, File, Open from Device and Open Folder from Device.
  What's picked is copied into `files/hex/picked` with a persisted grant, and saving the copy writes it back to the
  original (a new file saved inside a picked folder is made in the original folder). A file deleted or renamed in the
  copy isn't deleted or renamed in the original, and a change another app makes to the original isn't seen until it's
  picked again.
- The launcher icon is Hex's own, the adaptive icon in `Hex.Android/Resources` traced from
  `Hex.Shell/Assets/hex_icon.png` (the exe's icon). The orange and the darker horizon are the background layer, the
  white H the foreground and the themed icon's monochrome layer, all vectors, so they're sharp at every density.
- Settings, Appearance, Rendering is the render scale, the same rows on a desktop and a tablet. Auto is the platform's
  own, every pixel on a desktop and the shorter side at 720 pixels on a phone or a tablet (the engine's policy, Hex
  works nothing out itself). Native, or Manual at a percentage. What is scaled is Native UI (the default, the text and
  everything of Hex stay sharp, and as Hex is nearly all interface that saves little) or Whole frame (fewer pixels for
  all of it, softer text). The upscaler (CAS, bilinear, nearest) and CAS's sharpness, and a read only row saying what
  it came to (the share, the pixels drawn and the window's, what blows it up). It's live and kept in settings.hor, and
  `HORIZON_RENDER_SCALE` and the engine's other settings still win over it.
- Going to the back writes everything unsaved into the backups and keeps the workspace at once (`HostedLeaving` from
  `OnPause`), Android can end a process at the back without asking.

## What a tablet can and can't do

| Feature | On Android | Why |
|---|---|---|
| Editing, the code editor, syntax colouring (TextMate grammars, Oniguruma), find and replace, multiple carets, undo | Works | The same code, `libonigwrap.so` and `libSkiaSharp.so` ship for arm64 |
| Keyboard and mouse, shortcuts, wheel, right click menus | Works | Through the engine's Android input, the same keys as on Windows (Ctrl is the primary modifier) |
| Touch | Works | Horizon.UI's own touch handling, drag scrolls the code, long press is the context menu |
| Layouts, the designer, HIDL, the console and the values | Works | Nothing in them is desktop only |
| Files in the app, the explorer, the drawn file dialogs | Works | App storage, the drawn file browser instead of the desktop's dialogs |
| Files outside the app | Through the system's picker | Copies written back, see above. No all files permission |
| Terminal page | A real `/system/bin/sh` with toybox, Android 14 (API 34) or newer | Horizon's Unix pty on bionic. Older Androids say why in the terminal instead |
| Git | Not there | There's no git on Android, the git views say git isn't installed, the same as a desktop without it |
| Language servers | Not there, installing says why | Android only runs the programs it ships with (nothing an app writes can be executed since Android 10), and the downloads are desktop Linux builds |
| Debugging, running a project, tasks needing compilers | Not there | Same reason, no dotnet, Python, D or compilers to run. Tasks that are only shell lines run in the terminal |
| Notebooks' kernels | Not there | No Jupyter or Python, notebooks open and edit but can't run |
| Agents (Claude Code, Codex) | Not there | Their CLIs can't be installed or run |
| MCP server, another Hex handing files over | Not there, the server says why | Both are named pipes on the same machine |
| Dusk as a program, the drop down terminal, the frameless window's controls, the Windows shell integration | Not there | Desktop window things, Android has one fullscreen surface |
| Checking for Hex updates, the installer | Not there | An APK is updated by installing another |

## Evidence

The results of the port, the builds, the device runs, the screenshots and the saved and reopened files, are in the
workspace at `artifacts/android-platform-baseline/hex-android-results.md`.
