# Hex publish-build measurements — 4 November 2026

These are measurements of the local **Hex 2.8.0 “thickboy” development working tree**, including unpublished changes
to Hex and Horizon. They replace the Hex 2.0.0 figures formerly in the README. They are not measurements of the
existing `v2.8.0` download, a Debug build, or a self-test session.

## Results

Ten measured launches per mode, after **one discarded launch of Dawn and one discarded launch of Dusk** to populate
shader caches. No first-launch result is included in the statistics. “Warm” here means subsequent process launches
with caches left intact, not reopening a window in an already running process.

| Metric | Dawn median | Dawn min–max | Dusk median | Dusk min–max |
| --- | ---: | ---: | ---: | ---: |
| Process start → `main` | 11.5 ms | 11–13 ms | 11 ms | 11–13 ms |
| Process start → first rendered scene | **492 ms** | 472–527 ms | **474 ms** | 455–484 ms |
| Idle working set | **149.8 MiB** | 149.5–149.9 MiB | **114.1 MiB** | 113.9–114.3 MiB |
| Idle private bytes | 216.4 MiB | 215.4–216.8 MiB | 169.4 MiB | 167.9–170.5 MiB |
| Idle CPU, % of one logical core | 4.33% | 3.08–5.86% | 12.81% | 1.24–13.89% |

![First rendered scene after a discarded warmup: Dawn 492 ms median, Dusk 474 ms median; whiskers show the range](../images/readme/startup.svg)

Working set is resident process memory. Private bytes is a different Windows process metric, not another working-set
measurement and not managed-heap size. Each launch's memory result is the mean of its idle samples; the table then
takes the median and range across ten launches. CPU is process CPU time divided by elapsed time: **100% means one
logical core**, not the whole 12-thread CPU. For orientation, the medians normalize to approximately 0.36% and 1.07%
of total logical-processor capacity on this machine. Dusk's idle CPU varied considerably; the full range is retained.

Both modes share one distribution:

| Published package | Bytes | MiB |
| --- | ---: | ---: |
| Entire publish folder, 232 files | 106,237,076 | 101.3 |
| ZIP of that folder | 47,144,371 | 45.0 |
| `Hex.exe` within the folder | 45,579,776 | 43.5 |

MiB means 1,048,576 bytes. The ZIP was made with .NET's `ZipFile.CreateFromDirectory`, `CompressionLevel.Optimal`,
before launching the app. It includes the published assets and native dependencies, but no generated shader cache,
downloaded language servers, notebook kernels or user data. It is a locally generated archive, not a release-asset
download-size measurement. Other RIDs and archive tools can produce different sizes.

## Machine and build

- Windows 11 Pro, build **26300**, x64.
- AMD Ryzen 5 5600X, 6 physical cores / 12 logical processors; 32 GB installed RAM.
- NVIDIA GeForce RTX 3060, Windows driver `32.0.16.2101` (621.01).
- .NET SDK **11.0.100-rc.2.26504.105**.
- [`FolderProfile.pubxml`](../Hex/Properties/PublishProfiles/FolderProfile.pubxml): Release, `net11.0`, `win-x64`,
  self-contained NativeAOT. Only the output directory was overridden.
- Hex base commit: `e825f671b52afca52da08aaf8162ba8fe9bbf5ae`; local changes included.
- Horizon base commit: `12ee3a1762e504b0e03451767a61f4b34ccf9f70`; local changes included.
- Measured executable SHA-256:
  `E66A1C0CB7CEE5C287E724515FF83CD2CF5CABDBDCB80FE00BCF03EEA4488445`.

The base commits alone do **not** reconstruct this unpublished working tree. The executable hash identifies the
measured binary; the script below reproduces the procedure on whichever source revision you publish.

## Procedure and limits

1. Publish into a new directory and archive its contents before running Hex.
2. Give the run an isolated `HEX_HOME`. Set `HORIZON_LOG_STARTUP=1` and `HEX_PROFILE=0`.
3. Launch Dawn once, wait for its first scene and five seconds of settling, then close it. Do the same for Dusk.
   Discard both runs; retain the shader cache for all following launches.
4. Measure ten new processes of each mode. Alternate the order by pair: Dawn/Dusk, then Dusk/Dawn.
5. Use the startup trace's `first frame of the scene shown at … ms` marker. Its clock starts at process start;
   it does not start when the script notices the line. Do not add the discarded warmups to the dataset.
6. After that marker, allow five seconds to settle. Sample working set and private bytes every 250 ms for five seconds.
   Measure process CPU-time growth over the same interval, then close that process before starting the next one.

Dawn ran with `--window --fresh --size 1600x900`, the local Hex repository open, and
`Hex.Dawn/Shell/DawnScene.cs` open. Dusk ran with `--dusk --size 1600x900 --cwd <Hex repository>` and its default shell.
Default display scaling and default appearance were used. Screenshot-only theme and font changes were made afterward
in separate settings folders, not during the benchmark.

These are **normal application launches**, not `--selftest` launches. First rendered scene is not a measurement of
keyboard responsiveness, language-server readiness, complete repository indexing, or a ready shell prompt. The
startup trace is enabled during measurement. The first warmup can also populate OS and application caches beyond
shaders; caches were not flushed between runs.

CPU and memory cover the **Hex process only**. Shells, language servers and other child processes are excluded.
The workload was a live development workstation, not an otherwise empty laboratory machine; network availability,
background tasks, focus, drivers, refresh rate, installed tools and repository size can affect results. No other
editor was measured. No cross-platform speed claim or statistical significance is inferred from this small sample.
Managed-heap size and allocations were not measured in this run.

## Reproduce on Windows

From the Hex repository, with the engine available and native publishing prerequisites installed:

```powershell
# Use new output folders; keep the publish output separate from the measurement output.
$publish = Join-Path $PWD 'artifacts/readme-publish'
dotnet publish Hex/Hex.csproj -p:PublishProfile=FolderProfile "-p:PublishDir=$publish/"

# Optional: measure the distribution before it creates caches.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = Join-Path $PWD 'artifacts/Hex-win-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($publish, $archive,
    [IO.Compression.CompressionLevel]::Optimal, $false)
Get-Item $archive | Select-Object Length
Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum

./scripts/measure-readme.ps1 -PublishedDirectory $publish -Repository $PWD `
    -OutputDirectory (Join-Path $PWD 'artifacts/readme-results') -Samples 10
```

The script refuses to overwrite an existing measurement directory. It creates `samples.json` with measured runs
only, `machine.json`, and per-launch stdout/stderr logs. Launch `0` is the discarded warmup for that mode. It restores
the caller's environment variables afterward. Close unrelated workloads for a quieter run; do not compare results
from different machines as though they came from the same conditions.

The recorded run used the shared workspace's Horizon checkout and wrote to
`artifacts/readme-refresh/publish` and `artifacts/readme-refresh/benchmarks` at workspace level.

## Agentic under load — 17 November 2026

The `agentic-perf` self test (`--selftest agentic-perf --exit`) puts a dozen chats of 360 rows each into Agentic and
measures five phases. Idle stands still on one chat. Switching goes through every chat twice. Scrolling goes from the
end to the top and back. Streaming adds 120 lines and calls to the chat on screen, one an update. Groups opens and
folds a group of tool calls twenty times. Render and update times are the engine's own loop averages for a turn, in
milliseconds; allocation is what the update loop allocated per turn. The window was asked for frames throughout, so
both builds drew at the display's 240 Hz. Same machine as above, Hex 3.0.0 at d816f30 with local changes.

| Phase | Render ms (debug / release) | Update ms (debug / release) | Update KB a turn (debug / release) | CPU % of a core (debug / release) |
| --- | ---: | ---: | ---: | ---: |
| Idle | 0.21 / 0.19 | 0.62 / 0.21 | 13.2 / 6.3 | 22.6 / 26.5 |
| Switching | 0.22 / 0.26 | 0.94 / 0.34 | 258.7 / 162.3 | 33.9 / 10.6 |
| Scrolling | 0.32 / 0.22 | 0.45 / 0.15 | 10.5 / 9.6 | 44.0 / 11.7 |
| Streaming | 0.49 / 0.27 | 0.48 / 0.21 | 42.9 / 42.1 | 73.7 / 24.6 |
| Groups | 0.48 / 0.26 | 0.66 / 0.10 | 9.8 / 5.9 | 100.7 / 37.2 |

Both are NativeAOT, the dev build Debug and the published one Release (`FolderProfile.pubxml`). Release updates two
to six times faster. Its working set was 406 to 414 MiB against the dev build's 241 to 248 MiB with a similar
managed heap (58 to 73 MiB), which wants looking into. Idle still allocates every update (6.3 KB a turn in release,
about 1.5 MB a second at 240 Hz), the next thing to bring down. Results go to `hex-agentic-perf-<kind>.json` in the
temp folder, one per kind of build.

### The chat in a VirtualStack — 18 November 2026

The same test on the dev build once the chat's rows went into a `VirtualStack` (only the rows near what's in sight
measured, laid out and painted), then with a chat put on screen making its rows only as they come into sight. The
release build wasn't measured again.

| Phase | Update ms (before / stack / made in sight) | Update KB a turn (before / stack / made in sight) |
| --- | ---: | ---: |
| Idle | 0.62 / 0.41 / 0.40 | 13.2 / 13.3 / 14.1 |
| Switching | 0.94 / 1.62 / 1.07 | 258.7 / 140.7 / 44.6 |
| Scrolling | 0.45 / 0.18 / 0.18 | 10.5 / 9.0 / 10.6 |
| Streaming | 0.48 / 0.40 / 0.25 | 42.9 / 26.2 / 14.2 |
| Groups | 0.66 / 0.17 / 0.17 | 9.8 / 5.5 / 8.6 |

### Standing still — 18 November 2026

What idle allocated was weighed step by step (`HexTicks.Weighing`, agentic-perf prints the heaviest steps). Nearly all
of it was the Agents panel and Agentic's fleet drawing nothing new a few times a second: keys made as strings (the
chips, the list of chats, the fleet, the systems panel), the order of the chats and a set of their ids made again,
LINQ over the hub's chats, every card's words written anew, and the agent programs looked for on PATH every two
seconds. Each now goes by a number worked out with nothing allocated and writes its words only when they moved.

| Phase | Update ms (made in sight / now) | Update KB a turn (made in sight / now) |
| --- | ---: | ---: |
| Idle | 0.40 / 0.17 | 14.1 / 0.4 |
| Switching | 1.07 / 0.24 | 44.6 / 12.2 |
| Scrolling | 0.18 / 0.21 | 10.6 / 9.8 |
| Streaming | 0.25 / 0.28 | 14.2 / 13.3 |
| Groups | 0.17 / 0.17 | 8.6 / 6.9 |

What idle still makes, a few hundred bytes an update, is the agents panel's buttons and the notebooks' and the
extensions' own steps, under 0.2 KB each.

## Release against Debug, 3.1.0 — 18 November 2026

Hex 3.1.0 Amphetamine at 4d00117 (Horizon b8f14f4), same machine as above. Release is `dotnet publish Hex/Hex.csproj
-c Release -r win-x64` into a folder of its own, NativeAOT. Debug is the dev build out of `Hex/bin/Debug/net11.0`,
which is **not** NativeAOT. It is `Hex.dll` under the JIT from the shared runtime, with the AOT project's switches
(dynamic code off) in its runtimeconfig. That is why the agentic-perf files called it `nativeaot-debug`, now `jit-debug`.
Another agent was building the repository and three dev Hexes were running throughout, so CPU percentages are noisy
(the machine sat at 30 to 60% load) and frame times are best compared between runs taken minutes apart, as here.

### The 400 against 240 MiB

It was not Release being bigger. A Release Hex started in a folder with no shader cache (a new publish, a new install,
an update, which swaps the folder) found no compiled shaders and compiled them all with Slang. Slangc.NET keeps its
Slang session in a static field for the rest of the process, about 250 MB of native memory, so that run sat at 406 to
450 MiB working set and 510 to 546 MiB private bytes for its whole life, while every later start was 198 MiB. The
4 November measurements threw that first launch away, the 17 November ones did not. Server GC, GC settings and trimming
played no part, Hex has none of them set and both builds use workstation concurrent GC. The publish already carries
`shaders/packed/horizon.hzshaders` (84 modules), read only when Slang was missing. Now an ordinary run looks in the
cache, then in the pack, then compiles (Horizon c04f3e2).

| First launch in a new folder, agentic-perf | Before | After |
| --- | ---: | ---: |
| Working set, MiB | 406 / 450 | 212 / 217 |
| Private bytes, MiB | 510 / 546 | 274 / 276 |
| First frame of the scene, ms | 4856 | 3538 |

### Agentic under load (agentic-perf, medians of 4 Release and 3 Debug runs)

| Phase | Render ms (debug / release) | Update ms (debug / release) | Update KB a turn (debug / release) | Working set MiB (debug / release) |
| --- | ---: | ---: | ---: | ---: |
| Idle | 0.24 / 0.21 | 0.33 / 0.14 | 0.6 / 0.5 | 255 / 198 |
| Switching | 0.25 / 0.21 | 0.39 / 0.18 | 37.9 / 37.6 | 256 / 198 |
| Scrolling | 0.42 / 0.23 | 0.24 / 0.08 | 7.5 / 7.4 | 258 / 199 |
| Streaming | 0.51 / 0.25 | 0.31 / 0.15 | 12.9 / 12.4 | 260 / 199 |
| Groups | 0.51 / 0.25 | 0.21 / 0.08 | 6.2 / 6.4 | 260 / 199 |

Managed heap is 69 to 86 MB in Release and 78 to 94 MB in Debug. Allocation is the same code in both, so it matches.

### Typing and UI cost (one run each)

| Measure | Debug | Release |
| --- | ---: | ---: |
| Typing a 19,000 line C# file, ms a key, median / 95th / worst | 4.4 / 9.0 / 23.2 | 4.2 / 5.4 / 17.8 |
| Typing a 5,000 line .hor, ms a key, median / 95th / worst | 5.2 / 7.4 / 20.4 | 4.2 / 4.6 / 5.9 |
| uicost paint ms, idle / typing / wheel / drag / notebook | 1.52 / 1.82 / 1.56 / 4.57 / 1.54 | 0.39 / 0.51 / 0.37 / 1.07 / 0.38 |
| Working set after both suites, MiB | 488 | 372 |

### Normal start, warm caches (three launches each, the 4 November way)

| Measure | Debug | Release |
| --- | ---: | ---: |
| Process start to `main`, ms | 54 | 14 |
| First frame of the scene, ms (median, range) | 2057 (2027–3417) | 683 (663–741) |
| Idle working set, MiB | 251 | 202 |
| Idle private bytes, MiB | 260 | 251 |
| Idle CPU, % of a core | 5.1 | 3.2 |

Release's first frame was 492 ms on 4 November. Of today's 683, the device takes about 240 ms (the driver went from
621.01 to 621.14) and setting the scene up 250, of which 51 is the collection `SceneManager` takes under the loading
screen. With the machine busy as it was, that is not yet a regression anybody can point at.

### Leaks

Idle in the Hex repository with a file open, the handle count climbed by about 60 a minute in both builds, 757 to
1212 in ten minutes in Release. Nearly all were pipes. `GitProcess.Run` (status and blame every few seconds, fetch every few minutes) let
`Process.Dispose` leave git's redirected output and error to the finalizer, and an idle Hex makes so little garbage
the collector hardly ever came round. They're closed after reading now (0e0c03b).

| Release, idle in the repository | 2 min | 10 min | 15 min |
| --- | ---: | ---: | ---: |
| Handles before | 801 | 1212 | |
| Handles after | 739 / 564 | 691 / 569 | 566 |
| Private bytes after, MiB | 254 | 266 | 267 |

The two after rows are a busy run (three Hexes at once) and a quiet one. Private bytes level off after the first few
minutes. `HEX_SELFTEST_REPEAT=8` with agentic-perf (a dozen 360 row chats made, measured and closed eight times in one
process) ended every round from the third at a 42.5 MB heap after a full collection, 93.4 MB committed, 458 MiB
working set (a first launch, see above) and 636 to 639 handles, so the chats leave nothing behind.

Raw logs and the measuring scripts were kept outside the repository. `run-hex.ps1` samples working set, private bytes,
CPU, handles and threads every half second from the outside, the self-tests say the rest.

## Evidence

- [All 20 measured samples](benchmarks/2026-11-04/samples.json)
- [Recorded machine metadata and binary hash](benchmarks/2026-11-04/machine.json)
- [Package byte counts](benchmarks/2026-11-04/package.json)
- [Startup markers, including separately labeled discarded warmups](benchmarks/2026-11-04/startup-markers.txt)
- [Measurement script](../scripts/measure-readme.ps1)
- [Screenshot provenance](../images/readme/README.md)
