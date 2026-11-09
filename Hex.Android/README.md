# Hex.Android

Dawn, Hex's editor, on an Android tablet. Made to be used with a keyboard and a mouse plugged into the tablet (a Book
Cover keyboard and a Bluetooth mouse on the SM-T725 it was made on), touch works as well. Application id
`dev.horizon.hex`, activity `dev.horizon.hex/dev.horizon.hex.HexActivity`. It's the same Hex.Dawn, Hex.Core and Hex.Shell
a desktop Hex is made of, with no desktop project referenced. Not in `Hex.sln`, a desktop build never needs the Android
workload. What works on a tablet and what doesn't is in `../docs/android.md`.

## Optimized tablet publish

Release defaults to NativeAOT for arm64. From the workspace root, with the engine's verified shader pack present:

```powershell
dotnet publish hex/Hex.Android/Hex.Android.csproj -c Release -r android-arm64 `
  -p:AndroidSdkDirectory=C:/Users/bogdan/AppData/Local/Android/Sdk `
  -p:JavaSdkDirectory=D:/Microsoft/VSShared/Android/openjdk/jdk-21.0.8
```

The installable output is `bin/Release/net11.0-android/android-arm64/publish/dev.horizon.hex-Signed.apk`.
`PublishAot=true`, `OptimizationPreference=Speed` and `UseSizeOptimizedLinq=false` select native compilation and
speed-oriented code generation. NativeAOT uses ILC; `EnableLLVM` belongs to Mono AOT and must not be combined with it.
The APK should contain `libHex.Android.so`, without `libcoreclr.so`, `libclrjit.so`, a Mono runtime, or an assembly store.
The ILC response file under `obj/Release/net11.0-android/android-arm64/native/` should contain `--Ot` and
`System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported=false`. At startup Hex logs the actual runtime
and dynamic-code flags. Verify those outputs and exercise the editor on device; publish success alone does not prove it runs.

`-p:PublishAot=false` is an explicit diagnostic opt-out to CoreCLR/JIT, not the optimized release. Debug remains CoreCLR.
NativeAOT in this .NET 11 Android SDK is experimental. Settings, sessions, workspace files and picked documents survive
`adb install -r`; never use `pm clear` to recover a rendering problem.

The engine checks a requested upscaler before using it and falls back if its shaders are unavailable. Hex keeps the
saved choice and reports the fallback in a notification and **Render scale now**, including a native-resolution fallback.
For recovery with an older package, force-stop only Hex and start with `--es HORIZON_UPSCALER cas --es HORIZON_RENDER_SCALE native`.
Those extras apply to that process; an ordinary launch uses the saved settings again.

## Build

The engine's shader pack has to exist (`horizon/Horizon/shaders/packed/*.hzshaders`), the build stops otherwise. From
the workspace (the engine checkout next to `hex/`, not `hex/horizon`).

```
dotnet build hex/Hex.Android/Hex.Android.csproj -c Debug -r android-arm64 ^
  -p:HorizonRoot=C:/Users/bogdan/source/repos/bogzxo/horizon/horizon/ ^
  -p:AndroidSdkDirectory=C:/Users/bogdan/AppData/Local/Android/Sdk ^
  -p:JavaSdkDirectory=D:/Microsoft/VSShared/Android/openjdk/jdk-21.0.8
```

The APK is `bin/Debug/net11.0-android/android-arm64/dev.horizon.hex-Signed.apk`. Its content is whatever the projects
copy next to Hex.exe under `Assets\` and `shaders\`, found the way the desktop build finds it, plus any
`shaders/packed/*.hzshaders` next to this file.

## Install and run

```
adb -s R52M416M2TH install -r dev.horizon.hex-Signed.apk
adb -s R52M416M2TH shell am start -n dev.horizon.hex/dev.horizon.hex.HexActivity
adb -s R52M416M2TH logcat -s Horizon AndroidRuntime DOTNET
```

Settings are `HORIZON_*` string extras or lines of `files/horizon.env`, the same as Horizon.Testing.Android. Hex's own
are these, a process takes them once, so `am force-stop` first.

| Setting | What |
|---|---|
| `HORIZON_HEX_FOLDER` | A folder inside the app's folders to open as the workspace, relative to `files/hex/workspace` |
| `HORIZON_HEX_OPEN` | A file inside the app's folders to open, relative the same way |
| `HORIZON_HEX_SELFTEST` | `code,workspace` (or `all`), the editor tests itself in `cache/hex-selftest` |
| `HORIZON_HEX_EXIT=on` | With the self test, leave once it's done |
| `HORIZON_SCREENSHOT` | `shot.png@5`, into `/sdcard/Android/data/dev.horizon.hex/files/screenshots` |

`HORIZON_AUDIO` is `off` unless it's set, Hex makes no sound.

## Where things are on the tablet

| Folder | What |
|---|---|
| `files/hex/program` | The APK's Assets and shaders, unpacked once per install. Hex's program folder, the engine's content and the working directory |
| `files/config/Hex` | `HEX_HOME`, settings.hor, keys.hor, the sessions |
| `files/hex/workspace` | Where new files and layouts go, the explorer's first folder |
| `files/hex/picked` | Copies of documents and folders picked with the system's picker (File, Open from Device), saved back through it |
| `files/debug.log` | The log, `adb exec-out run-as dev.horizon.hex cat files/debug.log` |

Scripts that build, install, run and pull the evidence are `artifacts/android-platform-baseline/hex-android-*.ps1` in
the workspace.
