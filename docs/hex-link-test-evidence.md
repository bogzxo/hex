# Hex Link test evidence

The rollout and integration runs below are different gates. On 2026-11-22 the three deployed devices ran Hex
`9ee5f55`, Horizon `0e01c6c`, HIDL `bf0f436` and hex-extensions `0305852`. Wave 2 integration and
feature-branch checks were run in isolated worktrees. A grouped self-test total is the total printed by that
run. Companion checks are shown separately where the runner reported them. A dash indicates a breakdown the
recorded run did not provide.

## BOGZPC · Windows

| Run | Hex commit | Result | Notes |
| --- | --- | ---: | --- |
| All Hex.Tests | `9ee5f55` | 1,851/1,851 | Rollout gate |
| Horizon.Tests | Horizon `0e01c6c` | 2,047/2,047 | Matching engine |
| Combined native Link suites | `9ee5f55` rollout | 162/162 | Deputy's combined gate. Per-suite counts are not available from this summary. |
| `link-files`, `link-provision`, `link-missing-repository` | `9ee5f55` | 50/50 | Separate grouped repeat |
| Earlier combined native Link gate | `66eef98` | 151/151 | `link`, `link-files`, `link-documents`, `link-provision`, `link-missing-repository` and `link-ui` |
| Live sync with BOGROG | `66eef98` rollout, idle at `9ee5f55` | 5/5 probe locations each way | Arrived within 2.7 s in the smoke run. Deletes propagated. Sync was idle with zero conflicts on the recorded rollout. |

The probes covered the workspace root, Hex docs, the Horizon engine, hex-extensions and Horizon.HIDL. These are
write-and-observe smoke measurements. The BOGROG side was checked by SSH polling, so the number includes
observation delay.

## BOGROG · Windows

| Run | Hex commit | Result | Notes |
| --- | --- | ---: | --- |
| In-tree Hex.Tests | Rollout around `679cd89` | 1,821/1,823 | Exact tested HEAD was not recorded. Listener probe failure was fixed in the later rollout. The other failure needed a real notebook kernel. |
| Native self tests | Same tester run around `679cd89` | 70/70 | Per-suite counts were not recorded in the handoff. |
| Build | Same tester run around `679cd89` | Passed, 40 s | In-tree build |
| Live sync with BOGZPC | `9ee5f55` deployed | Idle | Both working trees were checked before aligning Git HEADs manually. This was before automatic HEAD follow on these devices. |
| Release bulk/JSON benchmark | `043b0d3` | [Measurements below](#working-tree-benchmark) | Two private fixture workspaces on BOGROG over pinned loopback TLS |

## bogz-mac · Apple M1

| Run | Hex commit | Result | Notes |
| --- | --- | ---: | --- |
| In-tree Hex.Tests | `679cd89` | 1,832/1,835 | Two Git alternates cases fixed in the later rollout and one notebook environment failure |
| Native Link self tests | `679cd89` | 87/87 | Grouped run through `link-missing-repository`. |
| Get workspace from BOGZPC | `9ee5f55` | 9 repos at source HEADs | About 413 MB of Git bundles, 170–224 s end to end, 1.29 GB on disk. Oversized tar and the uninitialized nested Horizon.HIDL checkout were skipped. |
| Build in provisioned tree | `9ee5f55` | Passed, 50 s | Zero build errors |
| HexLink filter in provisioned tree | `9ee5f55` | 348/348 | File keystore |
| Live sync with BOGZPC | `9ee5f55` deployed | Idle | 3,322 line-ending-only differences. Five mixed-EOL tracked files appeared modified from the older Get. The later provisioning correction applies to future Gets. |

The Mac paired via Tailscale and redialled via LAN. That establishes invite pairing over the VPN. A
Tailscale-only working-tree run after redial is pending. The Get time includes Git object transfer, checkout and
working-file hydration. The BOGROG benchmark measures working-tree sync. The recorded Mac handoff does not
give completed probe timings. Codex was available on the Mac and the Claude CLI was not logged in during this
rollout.

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

The two modes use the same fixture shape and exact-byte checks. A controlled real-LAN repeat and the same-HEAD
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
`link-worktree`, `link-git-follow`, `link-ui`, `link-editor-notices` or `conflicts` for the corresponding
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

The historical report at `9ee5f55` (`git show 9ee5f55:docs/hex-link-test-evidence.md`) covers the original
LAN/Tailscale tests, wire inspection and real-agent probes. Wave 2 still needs an isolated Tailscale-only sync
leg, a real-LAN performance run, native keystore validation and measured UI contrast.
