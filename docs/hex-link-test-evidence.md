# Hex Link test evidence

The tables separate isolated tests, deployed probes and benchmarks. Each row names the Hex revision it
actually ran. The three live devices used Hex `223dd02` with Horizon `0e01c6c` for the final shared-save
probe. The isolated device gates used Horizon `0e01c6c`, HIDL `bf0f436` and hex-extensions `0305852`.
`0d9d2e2` is the documentation baseline. The device gates below record their tested revisions separately.

## BOGZPC · Windows

| Run | Hex commit | Result | Notes |
| --- | --- | ---: | --- |
| All Hex.Tests | `0a2feb9` | 1,927/1,927 | Wave 2 integration gate, recorded in `fbbd05c` |
| Combined native Link suites | `0a2feb9` | 238/238 | Ten suites at the Wave 2 integration gate |
| Shared-save regression build | `2d76bd5` | 20/20 focused unit tests, 47/47 `link-documents` checks | Four isolated Hexes on loopback, including an authority without the file open and a one-hop replica. The patch merged as `63d8081`. |
| All Hex.Tests | `9ee5f55` | 1,851/1,851 | Earlier rollout gate |
| Horizon.Tests | Horizon `0e01c6c` | 2,047/2,047 | Matching engine |
| Combined native Link suites | `9ee5f55` | 162/162 | Earlier combined gate |
| `link-files`, `link-provision`, `link-missing-repository` | `9ee5f55` | 50/50 | Separate grouped repeat |
| Earlier combined native Link gate | `66eef98` | 151/151 | `link`, `link-files`, `link-documents`, `link-provision`, `link-missing-repository`, `link-ui` |
| Live LAN sync with BOGROG | `66eef98` | 5/5 probe locations each way | Reads observed within 2.7 s, including SSH polling. Deletions propagated. |

The LAN smoke probes covered the workspace root, Hex docs, Horizon, hex-extensions and Horizon.HIDL.
The working-tree benchmark below is a separate measurement.

## BOGROG · Windows x64

| Run | Hex commit | Result | Notes |
| --- | --- | ---: | --- |
| Isolated build | `5a27611` | Passed in 96.40 s | Five warnings |
| All Hex.Tests | `5a27611` | 1,937/1,939 | Two failures, zero skipped, 156 s |
| Eleven native Link suites | `5a27611` | 238/244 | Six failures, about 230 s. Child fixtures passed 42/42 separately. |
| Isolated rebuild | `223dd02` | Passed in 51.7 s | Two warnings |
| `link-git-follow` and `link-claims` rerun | `223dd02` | 18/18 parent, 14/14 child | 41.3 s for the gate. The full managed suite was not repeated at this revision. |
| Release bulk/JSON benchmark | `043b0d3` | [Measurements below](#working-tree-benchmark) | Two private workspaces over pinned loopback TLS |

| Native suite | `5a27611` parent checks | Time | `223dd02` parent + child |
| --- | ---: | ---: | --- |
| `link` | 23/23 | 6.5 s | — |
| `link-files` | 33/33 | 19.0 s | — |
| `link-documents` | 38/38 | 12.8 s | — |
| `link-provision` | 17/17 | 33.2 s | — |
| `link-missing-repository` | 6/6 | 16.6 s | — |
| `link-git-follow` | 3/5 | 18.2 s | 5/5 + 3/3, 20.3 s |
| `link-claims` | 9/13 | 34.3 s | 13/13 + 11/11, 15.9 s |
| `link-worktree` | 8/8 | 28.6 s | — |
| `link-editor-notices` | 12/12 | 2.6 s | — |
| `link-ui` | 83/83 | 37.9 s | — |
| `link-sync-upgrade` | 6/6 | 8.5 s | — |

At `5a27611`, the bundle incremental-fetch test `A_bundle_clones_on_the_other_side_and_a_second_fetch_brings_only_whats_new`
failed its empty-collection assertion. The real-kernel notebook test
`Engineering_notebook_executes_renders_and_roundtrips_in_a_real_kernel` failed its non-null assertion.
The Git-follow fixture missed the joiner's commit and working file returning, and agreement between history,
index and checkout. The claims fixture missed disjoint convergence and three checks in the overlapping-edit
scenario, covering preserved originals, the diff3 preview and the accepted resolution. The two native reruns at
`223dd02` passed after the fixture and Git handling updates. BOGROG did not have Tailscale.

## bogz-mac · Apple M1

| Run | Hex commit | Result | Notes |
| --- | --- | ---: | --- |
| Get workspace from BOGZPC | `9ee5f55` | 9 repos at source HEADs | About 413 MB of Git bundles, 170–224 s end to end, 1.29 GB on disk. Skipped an oversized tar file and the uninitialized nested Horizon.HIDL checkout. |
| Build in provisioned tree | `9ee5f55` | Passed in 50 s | Zero build errors |
| HexLink filter in provisioned tree | `9ee5f55` | 348/348 | File keystore |
| Live sync with BOGZPC | `9ee5f55` | Idle | 3,322 line-ending-only differences. Five mixed-EOL tracked files appeared modified after the older Get. The later provisioning correction applies to future Gets. |
| Isolated device gate | `5a27611` | 1,928/1,939 Hex.Tests, partial GUI 78 passed and 7 failed | The first launch could neither bind the unit test listener nor open an SDL display. The GUI retry was stopped for rollout before completing all suites. |
| Isolated rebuild | `223dd02` | Passed in 26.20 s | Fourteen warnings |
| All Hex.Tests | `223dd02` | 1,944/1,946 | Two failures, zero skipped, 131.27 s |
| Eleven native Link suites | `223dd02` | 255/255 | 200.79 s in the logged-in Aqua session, with the default Darwin temp path |

| Native suite at `223dd02` | Checks passed | Time |
| --- | ---: | ---: |
| `link` | 23/23 | 10.2 s |
| `link-files` | 33/33 | 15.4 s |
| `link-documents` | 47/47 | 34.8 s |
| `link-provision` | 19/19 | 18.5 s |
| `link-missing-repository` | 6/6 | 11.5 s |
| `link-git-follow` | 5/5 | 13.7 s |
| `link-claims` | 13/13 | 14.9 s |
| `link-worktree` | 8/8 | 16.1 s |
| `link-editor-notices` | 12/12 | 4.0 s |
| `link-ui` | 83/83 | 48.6 s |
| `link-sync-upgrade` | 6/6 | 10.1 s |

Eight Git-follow unit tests failed at `5a27611` because the tests could not resolve the selected Git checkout.
The checkout-root fix in `223dd02` cleared those failures. Two managed failures remained at `223dd02`.
`HexLinkSyncTests.A_hostile_peer_can_never_make_this_side_write_outside_the_folder_into_git_or_over_the_state`
saw an extra temporary sync-state file in its snapshot. The real-kernel notebook test again failed its non-null
assertion. Codex was available. The Mac's Claude CLI was signed out.

## Deployed device probes

The timings below use operator observations, command replies or local reflogs as noted. They are smoke
measurements. The [working-tree benchmark](#working-tree-benchmark) uses a controlled fixture.

| Probe | Build | What the devices showed |
| --- | --- | --- |
| Sync after restart | `7ba581f` | After BOGZPC restarted, the Mac resumed without a manual start and reported `horizon managed by BOGZPC [idle]: 0 sent, 0 received, 0 conflicts, 0 skipped`. After the later `223dd02` rebuilds and restarts, both remotes relinked on LAN and sync was idle with zero conflicts. |
| Tailscale-only file sync | `7ba581f` | Used `hex_link_route` to pin the Mac endpoint `100.80.122.37:47833` on BOGZPC and the BOGZPC endpoint `100.81.32.118:47833` on the Mac. Polling confirmed reads in both directions within 8.6 s. Deletions propagated. Both ends returned to Automatic and reconnected on LAN. |
| HEAD follow | `5a27611` | Local reflogs recorded the authority at 07:08:15 +02, the Mac at 07:08:17 +02 and BOGROG at 07:08:19 +02, apparent delays of 2 s and 4 s. Clock skew was not measured. BOGROG was clean. The Mac retained only the five known EOL-only changes. |
| Shared save | `223dd02` | Two Mac profiles, one relayed through the other, and BOGROG edited together while the authority had no tab open. A single Mac save cleared all three modified indicators and updated every disk. Both links were idle with zero conflicts. |
| Private worktree spawn | `223dd02` | The real request returned in 30 ms with “Preparing its private worktree; the agent runs when it is ready.” The reply measures acknowledgement, before checkout preparation completes. |
| Private worktree Publish | `223dd02` | Pending. The first live attempt stopped before the compile gate with “Files changed outside the exact gate candidate. Retry the publication.” The comparison used authority byte hashes for the replica's files, which can have different line endings. A real gate refusal followed by successful publication still needs verification. |

### Claims, remote approval and steering

The BOGZPC–BOGROG agent checks used a `7ba581f`-era build. The operator recorded the following replies.

- **Claim refusal.** BOGROG chat #31's edit returned in 37 ms with “Claimed file horizon/hex/docs/_linkpass/claimed.md
  by #133 '#133 Impl D: Devices window' on BOGZPC (planned, claimed 22 s ago). ETA 06:44:18.” The reply offered
  waiting for release, contacting the holder with `hex_agent_message` or using a private worktree. It said
  “Use override_claim=true only after coordination. Overrides are audited.” The holder received “Can you let
  me know when this file is free?” on BOGZPC. After release, the edit succeeded in 57 ms and reported one edit
  at `claimed.md:1`. BOGZPC read `link-pass from BOGROG`.
- **Remote approval.** BOGZPC started BOGROG Claude chat #32 with default permissions. Within 9.5 s it saw
  “A chat you started on BOGROG, #32, asks leave: Bash wants to run (request 1).” The approval reply was
  “#32's request 1 allowed: Bash wants to run”. The shell marker reached BOGZPC and the chat finished by 12.4 s.
  A request to pre-approve the exact Bash rule was refused with “Remote starters may pre-approve edits here.
  This device does not allow that allow rule.”
- **Steering stop.** BOGROG #31 acknowledged the follow-up. “Stopped #31.” appeared at 14.5 s and the agent
  readback showed it stopped at 15.1 s.

### Shared-save byte checks

Overlapping inserts in `shared.md` converged to `oMTverlap`. BOGZPC and the Mac stored LF
(SHA-256 `d5218b3097ba52111149668c1a33f1d3e3c9e981131aa9e61fb3d554867b6d82`). BOGROG stored CRLF
(`ff125b773bf63fde84583e911fd24d3eb8e2722121db0edb011596736f0485d9`). A second probe in `shared-bytes.md`
started from the seven-byte, newline-free `overlap`. After further overlapping inserts and one Mac save, all
three disks held the same nine bytes, `oTMverlap`, with SHA-256
`7cc700197344e2e83e189a3ee62422f9a912e8a2b0ccc51287851bba21d4ec13`. All editors were clean. The readback
includes agent reporting delay, so it gives no save-latency measurement.

## Found only on real machines

| What the existing suites missed | Fix or follow-up |
| --- | --- |
| A live upgrade could retain an elected authority and a persisted stop, leaving sync off after reconnect. | `5af4f78` restores sync choices and lets explicit Start override an old stop. Verified in the `7ba581f` rollout and upgrade fixture. |
| A replica installed before folder authority could reconnect without a pin and fail to resume sync. | The `7ba581f` rollout includes authority migration from `5af4f78` and the trusted legacy-source check in `f85cded`. The Mac resumed automatically after the authority restarted. |
| A live room could save on the authority and Mac while BOGROG treated the CRLF/LF difference as unsaved editor changes. The authority had no tab open. | `63d8081` verifies the saved revision by content across line endings and updates each replica's disk baseline. The four-process fixture and the three-machine save now cover this topology. |
| Publish could not scope the compile gate to the authority's selected project. | The range `ed31f4f` through `5a27611` added an authority-owned scoped gate and local timeout handling. The end-to-end live Publish remains pending. |
| Private worktree preparation took 83.05 s for 4,292 files and 223 MiB, past the 30 s spawn timeout. | `19bcd46` acknowledges spawn while preparing and reuses required repositories. A nine-repository scratch clone measured 19.945 s cold and 0.869 s reused, opening four repositories and preparing 3,066 files and 47.2 MiB. |
| Approvals could be invisible to the device that started a remote agent. | `957170c` corrected the stored starter chat number. The live Claude prompt and approval then reached the starter. |
| Redial picked LAN even when the operator needed to test a VPN-only route. | `7d1a3d8` added a remembered route choice. The Tailscale-only probe used it on both ends. |
| One unavailable repository or oversized file could abort the entire Get. | `6342e01` and `7d720da` let provisioning continue with a skip reason for individual repositories or paths. |
| A commit could arrive as working-file edits while the remote HEAD stayed behind. | `fb8933a` and `433b08d` added Git ref and index follow alongside file sync. The series ends at `3c2c9d1`, which closes the reverse-follow fixture timing gap. The live reflogs confirmed movement on both replicas. |
| Slower devices exposed fixtures that checked before sync, Git or the editor had finished. | `223dd02` improves the waits and Git diagnostics. The two BOGROG reruns and all eleven Mac suites passed. |
| Dev builds collided with DLLs held by running agent bridge helpers. | `0d9d2e2` gives each build's move-aside files unique names. |
| Concurrent agent worktrees filled the build disk. | Operational follow-up. Remove generated `bin` and `obj` contents from merged scratch worktrees and stop work below 15 GB free. |

## Wave 2 feature and integration suites

| Integration checkpoint | Hex commit | Focused gate |
| --- | --- | ---: |
| Immediate document replay | `0a79cf2` | 20/20 Release document tests |
| Bulk sync and pairing decline | `22c4df1` | 96/96 focused Release tests |
| Publication fence, private worktrees and separate storage | `73db327` | 31/31 focused Release tests |
| Claims, HEAD follow and streamed provisioning | `74754da` | 39/39 focused Release tests |

The integrated code at `0a2feb9` passed the Windows Release build, all 1,927 Hex.Tests cases, and 238/238 checks
across the ten combined app suites. The main unit run passed 1,925 cases. The bundle cleanup and checkpoint
discovery fixtures passed in a second run with serial collections and a scratch drive boundary. The bundle
fixture inspects the process-wide temp directory. The checkpoint fixture needs a path outside an enclosing
Git repository. Horizon remained at `0e01c6c`.

| Combined app suite | Passed |
| --- | ---: |
| `link` | 23/23 |
| `link-files` | 33/33 |
| `link-worktree` | 8/8 |
| `link-provision` | 17/17 |
| `link-git-follow` | 5/5 |
| `link-missing-repository` | 6/6 |
| `link-documents` | 38/38 |
| `link-editor-notices` | 12/12 |
| `link-claims` | 13/13 |
| `link-ui` | 83/83 |

The companion processes that printed totals passed their checks. Provisioning exercised one-way View,
handoff when reverse Edit arrived, and both sides restarting. Link Files also passed alone on this revision.

The feature rows retain the original tested commit IDs, even where integration picked them under new IDs.

| Suite or filter | Tested revision | Exact result | What it exercised |
| --- | --- | ---: | --- |
| `link` | `99d7aae` | 23/23 + 3/3 companion | Bulk/legacy integration candidate |
| `link-files` | `99d7aae` | 33/33 + 3/3 companion, twice | Checked sync, Git and Follow on the bulk candidate |
| `link-files` Follow repeat | `f5485d9` | 33/33 three times | The shared-file announcement carries no placeholder caret. Follow waits for the real position and local movement releases it. |
| `link-provision` | `cafea35` | 12/12 + 5/5 companion | Streamed Get, nested history and resumed sync |
| `link-missing-repository` | `9ee5f55` | Included in the 50/50 grouped repeat | Common files continue while a missing nested checkout can be fetched separately. |
| `link-documents` | `b50b658` | 38/38 twice | Three native editors, overlap, View-only reader, selective undo, named agent edit, and exact save to all three roots |
| Document convergence | `0c363be` | 4,096/4,096 seeds | 16 editors, 160 ticks each, inserts/deletes, undo, reordering, disconnected edits and equality at quiescence |
| 16 remote carets | Horizon `0e01c6c` | 256 frames per configuration | Mean CPU paint 0.035 ms without remote carets, 0.042 ms with 16 |
| `link-claims` | `a3fa2ac` | 13/13 + 11/11 companion | Claims, guard and explicit resolution. Focused claims 8/8 and `conflicts` 14/14. |
| `link-worktree` | `5af857e` | 8/8 + 4/4 replica | Isolated edit, checked publication and receipt |
| Publication fence | `9022263` | 5/5 | Group projection, exclusion and receipts |
| Private storage fallback | `a3a2057` | 4/4 | Focused Windows tests |
| `link-git-follow` | `3c2c9d1` | 5/5 + 3/3 companion | Branch and HEAD changes, then file sync. Focused core 8/8 at `433b08d`. |
| Mixed EOL provision | `b3b0573` | 1/1 | Git-clean checkout bytes and genuine mixed-EOL edits |
| `link-editor-notices` | `f15877c` preview, Horizon `0e01c6c` | 18/18 | Native UI at 150%. The preview exercised the UI before the production bindings were integrated. |
| `link-ui` approvals branch | `957170c` | 77/77 | Remote approval routing and pre-approval limits |
| HexLink | `99d7aae` | 372/372 | Bulk sync worker gate |
| HexLink and GitProcess | `cafea35` | 377/377 | Bulk provisioning worker gate |
| HexLink | `5af857e` | 367/367 | Private worktree candidate |
| HexLink | `b50b658` | 349/349 | Document latency and Follow wire-fixture correction. `8300a77` rebases the same patch onto `9ee5f55`. |

The claims branch's earlier broad run was 300/302 and the HEAD-follow branch's was 343/345. Each pair of
failures passed 2/2 in isolation. The focused and native rows above record their own runs.

### Shared-document timing

Twenty-four actual editor inputs on loopback from the authority to the second native editor, at BelowNormal
priority. The end point is the remote editor's text change. The test reports the later fixture observation
separately.

| Stage | 50 ms exchange timer baseline, median / p95 | Immediate dispatch and replay, fast run, median / p95 |
| --- | ---: | ---: |
| Input dispatch to remote editor | 107.3 / 132.9 ms | 11.7 / 31.1 ms |
| Local edit to dispatch | 70.8 / 79.2 ms | 0.0 / 0.0 ms |
| Dispatch to authority worker | — | 0.2 / 3.1 ms |
| Authority order to wire replay | — | 2.6 / 11.6 ms |
| Prepared edit to remote editor | — | 3.9 / 4.9 ms |

The earlier four-key baseline was 202.0 ms median. The final build returned 91.3 / 502.3 ms in one repeat and
100.8 / 377.6 ms at 99.8% mean host CPU in another repeat. In the latter run authority-worker scheduling reached
27.5 / 210.0 ms, while editor application was 3.6 / 4.3 ms. The fast run meets the 50 ms loopback target. The
saturated machine still misses it.

## Working-tree benchmark

BOGROG, `043b0d3`, Release, pinned loopback TLS. Eight repositories at recorded fixture commits, 3,951 paths
and 72.6 MB in the cold comparison. Bulk transferred all 3,951 without a skip. The warm comparison had zero
transfers. These are working-tree measurements. The cold Mac Get above also provisions Git history.

| Operation | JSON fallback | Bulk |
| --- | ---: | ---: |
| Cold first sync | 93.560 s | **12.060 s** |
| Warm unchanged comparison | 0.933 s | **0.885 s** |
| One saved file | 282 ms | **284 ms** |
| 500 changed paths | 15.728 s | **5.989 s** |
| 200 MiB file | Skipped by legacy limit | **2.466 s** |

The two modes use the same fixture shape and exact-byte checks. A controlled real-LAN repeat, the 1 GB benchmark and the same-HEAD
blob ID shortcut are pending.

## Rerun

Use a disposable Hex and Horizon checkout at the revisions for the desired row, initialize submodules, and build
with the matching engine. Run one native suite per invocation for its own count. On a shared machine keep build
parallelism at four and run heavy jobs serially at lower priority. Use `Start-Process -PassThru` and set the
process `PriorityClass` to `BelowNormal` on Windows, or use `nice -n 10` on Unix.

```sh
dotnet build Hex/Hex.csproj -m:4 -p:HorizonRoot=/path/to/engine/ -p:HidlRoot=/path/to/engine/Horizon.HIDL/
dotnet test Hex.Tests/Hex.Tests.csproj -m:4 --filter FullyQualifiedName~HexLink -p:HorizonRoot=/path/to/engine/ -p:HidlRoot=/path/to/engine/Horizon.HIDL/
dotnet test /path/to/engine/Horizon.Tests/Horizon.Tests.csproj -m:4
```

For all Hex.Tests omit `--filter`. For the combined provisioning and process gate use
`--filter "FullyQualifiedName~HexLink|FullyQualifiedName~GitProcess"`. For a native run, set a fresh `HEX_HOME`
in the fixture area, a unique `HEX_REMOTE_PIPE`, and `HORIZON_AUDIO=off`. Set `HEX_LINK_KEYSTORE=file` for
a disposable Unix profile that should avoid the system key store. Use the built executable in
`Hex/bin/Debug/net11.0/` (with `.exe` on Windows). For example:

```sh
./Hex --selftest link-documents --exit --size 1200x800
```

Substitute `link`, `link-files`, `link-provision`, `link-missing-repository`, `link-claims`,
`link-worktree`, `link-git-follow`, `link-ui`, `link-editor-notices`, `link-sync-upgrade` or `conflicts` for the corresponding
row. Use `--scale 1.5` for the editor-notice preview. The local Link suites create their own disposable peers
and exercise IPv4 loopback with discovery off. Collect the parent total, companion totals and exit code
separately. The command catalogue is in [testing and shipping](context/testing-and-shipping.md#hex-link-suites).

For focused convergence, set `HEX_LINK_CRDT_SEEDS=4096` and use
`--filter FullyQualifiedName~Sixteen_editors_converge` on Hex.Tests. For the 256-frame sample set
`HEX_LINK_CARETS_PERF=1` and use `--filter FullyQualifiedName~CodeEditorRemoteCaretsTests` on Horizon.Tests with
`--logger "console;verbosity=detailed"`.

For the working-tree benchmark, set `HEX_LINK_SYNC_BENCH_ROOT` to the source fixture workspace and
`HEX_LINK_SYNC_BENCH_MODE=both`. The benchmark clones input repositories at their recorded HEADs into its own
temp trees. Keep the printed fixture commit list. Optional `HEX_LINK_SYNC_BENCH_LOG` captures progress and
`HEX_LINK_SYNC_BENCH_PROFILE=1` records internal timings. Run:

```sh
dotnet test Hex.Tests/Hex.Tests.csproj -c Release -m:4 --filter FullyQualifiedName~HexLinkSyncTlsBenchmark --logger "console;verbosity=detailed"
```

### Paired-device checks

Use two isolated profiles with `--selftest link-cross --exit` for paired TLS, permissions, chat and agent
checks. Set `HEX_LINK_TEST_LISTEN` to a private listener port on one device and `HEX_LINK_TEST_DIAL` to its
`host:port` on the other. For a forwarded loopback route, set `HEX_LINK_TEST_LOOPBACK=1` on both ends. Use
`link-nearby` on two devices for LAN discovery. For linked prediction or other opt-in modes, see the
[pairing instructions](context/testing-and-shipping.md#linked-prediction-across-two-devices). The legacy
`HEX_LINK_TEST_DOCUMENTS` and `HEX_LINK_TEST_DOCUMENT_RECOVERY` modes prove the old two-editor protocol. Use
native `link-documents` to exercise the current sequence protocol.

For a live provisioning repeat, share a disposable workspace from a host granting View. On the joiner use **Get
workspace** or call `hex_link_provision` with
`{"device":"host-name","action":"get","destination":"/absolute/fixture/path"}`. Record elapsed time,
transferred bytes, disk size, each repository's HEAD and any skip notes. Build from the result and run the
HexLink filter. Time probes from both ends in the root, a nested repo and a submodule, check saved bytes and
line endings, then remove the fixture probes and confirm deletion convergence. Record the route for pairing
**and** for the subsequent transfer.

To repeat the Tailscale-only probe, use `hex_link_devices` to find each VPN endpoint and set it on the opposite
device with `hex_link_route` using `{"device":"peer-name","address":"vpn-ip:port"}`. Check the active route
before writing and deleting a probe file in each direction. Record polling bounds separately from transfer
timings. Restore the route with `{"device":"peer-name","address":"automatic"}`.

For a real shared-save repeat, leave the authority's tab closed and open the fixture file in three participants,
including one relayed through another. Type overlapping changes, save once, then check clean indicators, conflict
counts and each disk. Record both the line-ending-preserving case and the exact-byte case without a newline.
For private Publish, use a disposable candidate with a gate scoped by the authority. Verify that a failing build
leaves every live copy unchanged, then fix it and verify the same approved revision on all three disks.

The historical report at `9ee5f55` (`git show 9ee5f55:docs/hex-link-test-evidence.md`) covers the original
pairing, wire inspection and agent probes. Native Keychain and `secret-tool` acceptance, measured native UI
contrast, a controlled real-LAN throughput run and the 1 GB benchmark remain pending. Linked prediction was
not rerun on the real devices during Wave 2.
