# Idle startup memory on BOGROG

Measured 22 November 2026 with a real Dawn editor window and Settings closed. The optimized managed Debug build used **240.1 MiB private** in the normal configuration and **211.6 MiB private** with No AI on and Git off. Link and AI Autocomplete were off in both cases.

## Managed desktop results

Each cell shows the median of three launches, followed by their range in parentheses. Windows private bytes measure private committed memory. Working set includes resident private and shareable pages. All sizes are MiB.

| Build | Config | Private bytes | Working set |
| --- | --- | ---: | ---: |
| Original | Normal | 236.3 (236.2–238.2) | 306.4 (306.2–306.5) |
| Before deferral | Normal | 238.7 (238.7–249.4) | 310.4 (309.8–320.9) |
| Optimized | Normal | 240.1 (238.9–240.5) | 311.4 (311.4–311.6) |
| Original | No AI | 236.8 (234.9–237.6) | 304.9 (304.6–306.6) |
| Before deferral | No AI | 238.2 (238.1–239.4) | 309.3 (308.6–310.0) |
| Optimized | No AI, Git off | 211.6 (208.9–212.7) | 282.6 (277.0–283.4) |
| Optimized | No AI, Git on | 211.3 (210.0–211.9) | 275.6 (273.7–276.1) |

With normal defaults, the optimized editor used 3.8 MiB more private memory and 5.0 MiB more working set than the original. Its low-memory configuration used 25.2 MiB less private memory and 22.3 MiB less working set than the original No AI configuration. The two older revisions always enabled Git. The comparison includes intervening changes to the editor, engine and extensions. It cannot assign that normal-profile difference to Link or autocomplete.

## Reproduce

| Build | Hex | Horizon | HIDL | hex-extensions |
| --- | --- | --- | --- | --- |
| Original | `590eb42` | `68b7e11` | `bf0f436` | `66022c3` |
| Before deferral | `d763c58` | `3369515` | `bf0f436` | `4bd3737` |
| Optimized | `8c44cdd` | `c9e12de` | `bf0f436` | `4bd3737` |

Both feature branches started at `590eb42`. It is the direct parent of the first Link commit `5f00eb6` and the first prediction commit `88e9687`. The original Hex revision did not pin Horizon. The comparison uses the latest first-parent engine commit before that Hex commit.

BOGROG ran Windows 11 build 26200, .NET SDK 11.0.100, Ryzen Z1 Extreme, Radeon integrated graphics and 15.2 GiB usable RAM. Build each variant in its own directory with its own engine and HIDL. Set `HorizonRoot` and `HidlRoot` to those directories, with trailing separators.

```powershell
dotnet build Hex/Hex.csproj -c Debug "-p:HorizonRoot=$engine/" "-p:HidlRoot=$engine/Horizon.HIDL/" -p:NuGetAudit=false -p:HexBuildNumber=158 --disable-build-servers
```

Seed a neutral folder outside any repository or .NET project with an `example.txt` containing `Matched Hex memory sample.` and a CRLF. Use a fresh `HEX_HOME`, `DUSK_HOME`, `TEMP`, `TMP` and `HEX_REMOTE_PIPE` per launch. Use the same available Git executable, here a private copy of Git 2.56.0.windows.4. Run on the logged-in desktop with `HORIZON_AUDIO=off`, using the built output directory as the working directory.

```powershell
.\Hex.exe --window --fresh --size 1100x720 --folder $fixture --open "$fixture/example.txt"
```

The app created a 1920×1044 swapchain after maximizing on this desktop. Start with warm per-build shader caches. Leave the editor idle with the plain text tab open. For a normal run, leave the new profile at defaults. For a low-memory run write this private user `settings.hor` before launch.

```text
#define dusk
#define hex_settings
let settings = { editor: { no_ai: true, git_enabled: false } }
```

Take `System.Diagnostics.Process.PrivateMemorySize64` and `WorkingSet64` at 18, 20, 22, 24 and 26 seconds after the native Hex window appears. Take the median of each five-sample series, then the median across launches. For each full profile use order original, before, optimized, optimized, before, original, original, optimized, before. Run each editor in its own process and stop it before starting the next. Shader-compilation launches, test windows, and launches with a CLR diagnostic hook were excluded from this table.

## What loads

Two separate process-start EventPipe traces observed the optimized build for 16 seconds, one normal and one No AI with Git off, using `Microsoft-Windows-DotNETRuntime:0x100003803D:5`. They reported 78 and 77 distinct managed assembly names, respectively. Neither loaded `Hex.Link.Transport` or `Hex.Prediction.Runtime`. The former contains the transport, discovery, DNS and router mapping. The latter contains the prediction engine, formatting, model providers and relay.

The shared assemblies `Hex.Core`, `Hex.Dawn` and `Hex.Shell` still map their metadata. This includes Link wire types, host, rooms, sync, UI methods, prediction settings and editor glue. JIT events showed settings row accessors and minimal discovery and on/off checks. They showed no host, room, sync, peer transport, model provider, inference, tab decoration or feature-window construction. Normal startup registers the Devices entry, one MCP discovery command, and autocomplete enable/settings commands. No AI omits autocomplete commands. Settings widgets and the Ollama picker are created when opened. The renderer still uses the GPU. The normal language-server version check made its own HTTPS connections on both old and new builds.

Separate private instrumented launches measured the managed heap and total managed bytes allocated about 14.5 seconds after process entry, before inspecting scene fields. The first assembly list was captured before reflection. Later reflection over field types can itself load optional assemblies, so the process-start EventPipe traces supply the assembly evidence.

| Build | Config | Cumulative managed allocations (MiB) | Estimated managed heap (MiB) |
| --- | --- | ---: | ---: |
| Original | Normal | 189.1 | 61.6 |
| Before deferral | Normal | 194.3 | 63.2 |
| Optimized | Normal | 192.7 | 62.3 |
| Original | No AI | 189.6 | 61.1 |
| Before deferral | No AI | 194.5 | 62.7 |
| Optimized | No AI, Git off | 110.6 | 32.9 |

The normal disabled scene went from 81 non-null Link, prediction or Ollama object fields to one discovery submenu and four empty-array references. In No AI the count fell from 73 to four empty arrays. Counted fields include test scaffolding. The separate Git-on No AI runs show no measurable private-memory saving from disabling Git in this non-repository. The native Git test used an actual repository and verified that off stopped discovery and on found files added while off.

On plain text, No AI skips synthetic code/Markdown grammar warm-up. The optimized low-memory process omitted `Onigwrap.dll` and `libonigwrap.DLL`, and retained less managed memory. In a separate project-containing fixture, an allocation trace also found .NET project evaluation while Properties was hidden. Properties now waits until opened. That project-containing fixture and its allocations were excluded from the figures above.

On the desktop, a saved opt-in restored Link and autocomplete. Turning both off released the prediction engine, closed the IPv4 and IPv6 listeners, and reduced the MCP catalog to discovery. Re-enabling them in the same process restored both. No AI with a saved autocomplete opt-in kept the prediction engine absent while an explicitly enabled Link continued to work. A fresh disabled profile created no Link identity. Once explicitly enabled, hidden status items and menu commands are retained for later use. An already loaded managed assembly stays in the default load context until process exit.

The Windows Release configuration compiles a single NativeAOT executable. The managed assembly and JIT results above describe Debug. NativeAOT links reachable implementation code into its executable, even when a feature is disabled at launch.
