# Global Pyright integration

Hex's Python registration already uses `pyright-langserver --stdio`. On the tested Windows machine, Pyright 1.1.414 was installed under `%APPDATA%\npm`, but that directory was absent from the editor's inherited PATH. Node.js was present in the current machine PATH. Windows npm also installs an extensionless POSIX shell shim beside its `.cmd` shim; choosing that file or directly executing the batch file cannot establish the redirected LSP connection.

Language-server discovery and version probes now share one launcher in Horizon.Tooling. It reads the current machine/user PATH through the existing ProcessEnvironment helper, retains inherited development-tool paths, and checks Windows npm's default global bin directory. Windows discovery uses PATHEXT, avoiding the extensionless POSIX shim. Standard npm `.cmd` shims resolve to their JavaScript entry point and run directly through Node with redirected stdio. Arguments remain separate literal arguments; no command shell is involved. Explicit executable paths and Unix executable discovery remain supported.

Custom npm prefixes are found when present on PATH; otherwise configure the language server's path in Hex's language-server settings. This change recognizes npm JavaScript shims, rather than interpreting arbitrary batch scripts. No packages, user PATH values or global installation files were modified.

## Verification

- Full Hex suite: 481/481 tests passed, with JSON reflection serialization disabled.
- Focused engine suite: 27/27 tests passed, including LSP messages, process environment, Windows shim selection and literal arguments containing spaces, quotes, ampersands and percent signs.
- Managed graphical acceptance: 6/6 checks passed using the actual global Pyright installation. Opening a Python document starts the normal Python registration, reports an invalid typed assignment, resolves a float hover, completes `math.sqrt`, and clears diagnostics after the document is corrected.
- NativeAOT Windows x64 publish succeeds. The native executable passes the same 6/6 real-Pyright checks in 1.4 seconds. No launcher or JSON AOT diagnostics are reported; existing Silk.NET loader warnings remain.

The real-installation suite is opt-in so other machines do not need Pyright for unrelated graphical tests:

```powershell
./Hex.exe --selftest pyright --exit --size 1600x900
```

The suite uses temporary Python files and isolated Hex configuration. Global Pyright and Node remain installed and unchanged. Shared Horizon changes must accompany the Hex changes.

## npm installs through Hex

The language-server Install/Update runner still inherited Hex's original environment, independently of server discovery. A second Windows defect passed an entire shell command through `ArgumentList` quoting; quoted executable paths and shell chains then failed. Both defects were reproduced by focused tests before the fix: the environment test saw the old PATH, and the quoted batch command returned 1 instead of its expected 7.

The installer now receives the refreshed process environment. Windows shell commands use an explicit system `cmd.exe` with `/d /s /c` and one outer quote pair, preserving the command line's own quoting and shell operators. Unix uses its existing `/bin/sh -c` path after login-shell environment discovery. Output capture, cancellation, timeout and exit-code reporting remain in the existing work runner.

Verification uses a real npm install of a local dependency-free fixture with `--offline --ignore-scripts --no-audit --no-fund`. Global-style installs specify a temporary `--prefix` and cache. No user global packages are installed, updated or removed. Paths contain spaces and an ampersand. The checks cover the Install action, generated command shim, server discovery, launching that shim, and entering the same install in the integrated terminal.

- Full unit suite: 483/483 passed, including the two installer regressions.
- Managed graphical suites `work npminstall pyright`: 20/20 passed, including existing progress, cancellation, duplicate-work and failure reporting checks.
- NativeAOT publish succeeds; the native executable also passes all 20/20 checks in those suites. The user's reported failure was specifically the language-server Install/Update button.

Run the real npm proof explicitly with `./Hex.exe --selftest npminstall --exit`. Registry downloads, proxy credentials and package-specific install scripts are not exercised by this offline test.
