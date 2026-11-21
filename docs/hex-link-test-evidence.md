# Hex Link on two machines: test evidence

Phase 5 of `docs/hex-link-plan.md`, run 2026-11-21 on branch `feat/hex-link-test` (from `feat/hex-link` d9d7b32).

| Machine | OS | Arch | LAN | Tailscale |
| --- | --- | --- | --- | --- |
| PC | Windows 11 Pro 10.0.26300 | x64 | 192.168.0.50 | 100.81.32.118 |
| Mac | macOS 27.3.0 (MacBook, M1) | arm64 | 192.168.0.30 | 100.80.122.37 |

Every Hex in these runs had its own `HEX_HOME` (a temp folder), its own `HEX_REMOTE_PIPE`, `HORIZON_AUDIO=off` and
`HEX_LINK_KEYSTORE=file`, and was closed at the end. The Mac side ran from the rig (`scratch/link/mac`), a real window
on the Mac's desktop started over ssh, except where it says the GUI session. Engine 68b7e11, HIDL bf0f436.

## 1. One machine at a time

### BOGROG development checkout

The default Debug restore of `Hex/Hex.csproj` succeeded after adding NuGet.org to an untracked
`NuGet.Config` at the common workspace root. BOGROG previously had no configured sources or
package cache. A config under `hex/` alone left the sibling Horizon projects with NU1100.

`hex_dev_test` with `FullyQualifiedName~HexLink` reported 204 passed, 0 failed, and 0 skipped
in 33 seconds. The testhost stayed alive for about 203 seconds and had to be stopped before the
tool returned the buffered result. The assertions passed, but clean test runner shutdown is unverified.

| Run | Result |
| --- | --- |
| PC, `HexLink` unit tests | 176 of 176 (175 before, plus the shared-read test below) |
| PC, suite `link` (`Hex.exe`) | 22 of 22, TLS 1.3 |
| PC, suite `link` (`dotnet Hex.dll`) | 2 of 22 before the fix (B was started as `dotnet --selftest link-peer ...`), 22 of 22 after (b729d5a) |
| Mac, `HexLink` unit tests (`mac-test.sh`) | 174 of 175 before the fix, 175 of 175 after (f3850bc), 176 of 176 with the shared-read test |
| Mac, suite `link` alone (`mac-hex.sh -- --selftest link --exit --size 1200x800`) | 22 of 22, TLS 1.2 |

`dotnet Hex.dll` otherwise behaves as `Hex.exe` for the self tests (it finds `hex-extensions`, the shaders and its
data beside the dll). One more place still assumes the process is Hex: the MCP command written for agents
(`HexExtensions.Agents.cs`, `ProcessPath ?? ...`) would be `dotnet.exe` without the dll. Not fixed here; the agent run
below used `Hex.exe` on the PC, which only dialled out, so Windows Firewall was never asked.

## 2. Across the machines: suite `link-cross`

BOGROG subsequently ran `link-cross` over the LAN against the Apple M1 Mac, with the Mac listening and Windows dialing. Both processes passed 14/14 checks over TLS 1.2 using isolated profiles. Windows used the multiplayer validation build and the Mac used the existing `~/hexmac/link/hex` build (checkout `a2d64c0`). This interoperability run used the suite’s stand-in chat. The Tailscale and real-agent runs below are historical.

One command a side, the same build, the listener first:

```
# Listener (here the Mac, from the PC through the rig)
bash mac-hex.sh HEX_HOME=~/hexmac/link/run/<run> HEX_LINK_TEST_NAME="Hex Mac" HEX_LINK_TEST_LISTEN=47833 \
    -- --selftest link-cross --exit --size 1000x700
# Dialler (here the PC, from <worktree>/Hex/bin/Debug/net11.0)
HEX_HOME=<temp> HEX_REMOTE_PIPE=<unique> HORIZON_AUDIO=off HEX_LINK_KEYSTORE=file HEX_LINK_TEST_NAME="Hex PC" \
    HEX_LINK_TEST_DIAL=192.168.0.30:47833 [HEX_LINK_TEST_MITM=host:port] [HEX_LINK_TEST_AGENT=claude_code] \
    dotnet Hex.dll --selftest link-cross --exit --size 1000x700
```

`scratch/link/twomachine/cross.sh <run> <mac|pc> <address>` does both and prints the two logs' results.

The link is symmetric, so both sides run the same checks of the other over the one connection: the dialler watches
and steers the listener, and the listener watches and steers the dialler. The two agree on when to go on through a
status file each keeps in its own workspace and the other reads over the link (`fs/read`); each prints what the other
compares (its code, the hash of its stand-in chat's rows, the SHA-256 of the file it got).

### Results, the four routes

PC side / Mac side, checks passed. Every check is listed with its result in both logs.

| Check (from each side, of the other) | Mac listens, LAN | Mac listens, Tailscale | PC listens, LAN | PC listens, Tailscale |
| --- | --- | --- | --- | --- |
| Paired and linked | pass / pass | pass / pass | pass / pass | pass / pass |
| The same six-digit code on both | 561710 = 561710 | 327644 = 327644 | 161076 = 161076 | 718763 = 718763 |
| Hello: name, OS, arch, version, workspace | pass / pass | pass / pass | pass / pass | pass / pass |
| TLS negotiated (both ends print it) | 1.2 ECDHE-ECDSA-AES256-GCM-SHA384 | the same | the same | the same |
| Fleet shows the stand-in chat, waiting | pass / pass | pass / pass | pass / pass | pass / pass |
| Mirrored chat = the other's rows (hash) | pass / pass | pass / pass | pass / pass | pass / pass |
| `hex_agent_list` through the link | pass / pass | pass / pass | pass / pass | pass / pass |
| `hex_link_devices`, `hex_link_call` | pass / pass | pass / pass | pass / pass | pass / pass |
| `hex_terminal_run` refused, "never" | pass / pass | pass / pass | pass / pass | pass / pass |
| File written: same SHA-256 on its disk | pass / pass | pass / pass | pass / pass | pass / pass |
| Stale base conflicts, with the hash there | pass / pass | pass / pass | pass / pass | pass / pass |
| Approval answered from here, seen allowed there (steer) | pass / pass | pass / pass | pass / pass | pass / pass |
| Dropped to View there: write refused, needs edit | pass / pass | pass / pass | pass / pass | pass / pass |
| Unpaired identity from the dialler's machine refused "unknown" (dialler) and seen in the listener's audit | pass / pass | pass / pass | pass / pass | pass / pass |
| Through a man in the middle (fresh key) on a paired link: refused "pin" (dialler) | pass | pass | pass | pass |
| Forgotten by the listener: link cut, redial refused "unknown" (both ends) | pass / pass | pass / pass | pass / pass | pass / pass |
| Total | 15/15, 14/14 | 15/15, 14/14 | 14/14, 15/15 | 14/14, 15/15 |

The first two runs failed one check each, both fixed: a bug in the suite (each side dropped the other only if the other
hadn't dropped it first), and a real one (section 6, the shared read).

Side by side, Mac listens on the LAN (run `c1c`, trimmed):

```
PC  dialler, up as Hex PC (2EF6-1C5C-0C1B-F4E1) on port 61993, Microsoft Windows 10.0.26300, X64
Mac listener, up as Hex Mac (406C-AEB3-5AFB-96AA) on port 47833, macOS 27.3.0, Arm64
PC  linked to Hex Mac (406C-AEB3-5AFB-96AA) os macos arch arm64 version 3.3.0, at 192.168.0.30, over Tls12 TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384
Mac linked to Hex PC (2EF6-1C5C-0C1B-F4E1) os windows arch x64 version 3.3.0, at 192.168.0.50, over Tls12 TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384
PC  code here 561710, there 561710                 Mac code here 561710, there 561710
PC  mirror hash 6d16aa841d9a79ff, theirs 6d16aa841d9a79ff     (Mac: status rows 6d16aa841d9a79ff)
Mac mirror hash 5658984e697a5b32, theirs 5658984e697a5b32     (PC:  status rows 5658984e697a5b32)
PC  wrote from-Hex-PC.txt, 77 bytes, sha256 7a7b2d250a76d199282bfef30ab3c5ca254411505d9fb1c18c1533ed963dc048
Mac from-Hex-PC.txt arrived, 77 bytes, sha256 on this disk 7a7b2d250a76d199282bfef30ab3c5ca254411505d9fb1c18c1533ed963dc048
Mac wrote from-Hex-Mac.txt, 78 bytes, sha256 00264f5b707b4bfbaf4469dc9e98874081fe0258010e2f9547b6c2392bb449f9
PC  from-Hex-Mac.txt arrived, 78 bytes, sha256 on this disk 00264f5b707b4bfbaf4469dc9e98874081fe0258010e2f9547b6c2392bb449f9
PC  stranger: the other device doesn't know this one, pair first
Mac audit refused FA87... 192.168.0.50, unknown device "Stranger"
PC  through the middle: pin 127.0.0.1 answered as another device than the one dialled
Mac audit forgotten 2EF6... its link cut
PC  link to Hex Mac closed, the other end hung up / redial refused: unknown
Mac audit refused 2EF6... 192.168.0.50, unknown device "Hex PC"
```

The other three routes print the same with their own addresses (`100.80.122.37` / `100.81.32.118` over Tailscale) and
codes. TLS is 1.2 on every pair, as the spike said: .NET on macOS has no TLS 1.3 server or client, Windows to Windows
negotiates 1.3 (`TLS_AES_256_GCM_SHA384`, the one-machine runs).

## 3. A real agent across the machines

`link-cross` with `HEX_LINK_TEST_AGENT=claude_code HEX_LINK_TEST_MODEL=haiku`, Mac listening on the LAN, PC dialling
(run `agent2`). Each side, once linked with Steer granted, calls its own `hex_link_spawn` for the other device with the
prompt "Run the shell command `uname -sm` exactly once. Then reply with exactly one line and nothing else: pong from
<this machine's host name>: <what uname printed>", then `hex_link_call` → `hex_agent_wait {"ids": [n], "timeout": 15}`
until it's done, answering any approval it asks with `hex_agent_approve` over the link, and reads the reply with
`hex_agent_read`. Both at once, so each machine runs the other's agent while its own runs there.

```
PC  hex_link_spawn: [From Hex Mac (3DDF7866), another machine ...] Started #2 "Link pong" with Claude Code haiku.
PC  waited: done ... #2 "Link pong" claude_code claude-haiku-5-5 done 2.7s $0.004
PC  agent reply: pong from bogz-mac.local: Darwin arm64
PC  pass: link-cross: a real claude_code agent started on the other machine replies with that machine's uname

Mac hex_link_spawn: [From Hex PC (446DEDD5), another machine ...] Started #2 "Link pong" with Claude Code haiku.
Mac waited: running waiting ... Timed out, 1 still running.
Mac approved over the link: #2's request 1 allowed: PowerShell wants to run
Mac waited: done ... #2 "Link pong" claude_code claude-haiku-5-5 done 33s $0.007
Mac agent reply: pong from BOGZPC: MINGW64_NT-10.0-26300 x86_64
Mac pass: link-cross: a real claude_code agent started on the other machine replies with that machine's uname
```

Each host's audit has the spawn and the approval as Steer requests from the other device:
`audit steer 446DEDD5 tool hex_agent_spawn {"prompt":"Run the shell command ...","agent":"claude_code","model":"haiku"}`.
15 of 15 checks on both sides. The PC's agent asked leave for its shell and the Mac gave it over the link; the Mac's
ran its command under the Mac's own Claude Code settings without asking.

Two things had to be right first. The first attempt failed on both machines at once ("Claude Code, There is no item 0
in a list of 0. (line 93), failed"): the Claude Code extension doesn't start in a Hex with no folder open, fixed below.
And Claude Code on the Mac is only signed in from the Mac's GUI session (the login Keychain is locked to ssh), so the
Mac side ran through `scratch/link/twomachine/mac-hex-gui.sh`, which starts the same Hex in a Terminal window opened by
`open -a Terminal`. The PC side ran as `Hex.exe` (it only dialled). Two short haiku turns, $0.011 in all.

## 4. On the wire

### Nothing readable
A logging relay (`scratch/link/twomachine/relay`, `relay log 47833 192.168.0.30:47833 capture.bin`) on the PC between
the PC dialling `127.0.0.1:47833` and the Mac listening. A whole `link-cross` ran through it (14 of 14 both sides), the
marker `HEXLINK-PLAINTEXT-MARKER-7731` in the stand-in chat's first row (read by `chat/get`) and in both files written.
Three connections: the link, the stranger, the redial after forgetting.

```
conn 1 dialler->listener: 38101 bytes, every byte in TLS records: True, records 0x14 0x0303=1, 0x16 0x0301=1, 0x16 0x0303=2, 0x17 0x0303=82
   first records: 16 03 01 len 426 | 16 03 03 len 444 | 14 03 03 len 1 | 16 03 03 len 40 | 17 03 03 len 104 | 17 03 03 len 96
conn 1 listener->dialler: 35925 bytes, every byte in TLS records: True, records 0x14 0x0303=1, 0x16 0x0303=6, 0x17 0x0303=78
conn 2 (the stranger) and conn 3 (the redial): handshake, one 0x17 record each way, closed
needle "HEXLINK-PLAINTEXT-MARKER-7731": 0 occurrences (UTF-8 or UTF-16) in 78487 captured bytes
needle "HEX-LINK": 0      needle "jsonrpc": 0      needle "link/hello": 0      needle "fleet/get": 0
needle "Build the thing": 0      needle "Hex Mac": 0      needle "Hex PC": 0      needle "Written over Hex Link": 0
```

Every byte each way is inside a TLS record (handshake `0x16 0x03`, then application data `0x17 0x03 0x03`).

What a watcher on the LAN does read, because TLS 1.2 sends the certificates in the clear: the certificates' subject
`Hex Link`, their validity dates, and the SNI `hex-link`. So the session can be told to be Hex Link, and each device's
public key (its id) can be followed from one network to another. None of the content is. TLS 1.3 (Windows to Windows)
encrypts the certificates. Left as a note for the design (section 7).

### Integrity
The same relay told to flip one bit in the 40th application-data record from the dialler (`relay log ... 40`):

```
[1] C flipping byte a1 of app-data record #40 at stream offset 30847
Mac link-cross: link to Hex PC closed, the connection broke
PC  link-cross: link to Hex Mac closed, the other end hung up
[3] C flipping byte b8 of app-data record #40 ...    (the redial, cut the same way)
[4] C flipping byte 7b of app-data record #40 ...
```

The receiver drops the link at the first altered record (AES-GCM's tag fails), every time; nothing altered is acted on.
The suite's later checks fail as they should (7 of 14 each side), and the stranger and forgetting still pass.

### A man in the middle with keys of its own
`relay mitm` terminates TLS with a fresh P-256 key towards each side and copies the plaintext between.

On a paired link (every route above, `HEX_LINK_TEST_MITM`): the dialler, expecting the listener's pin, is refused
before it says a word: `through the middle: pin ... answered as another device than the one dialled`; the relay saw
0 plaintext bytes.

During pairing (both sides `link-peer`, the Mac listening, the PC dialling the relay, both accepting by themselves as
only a self test does):

```
mitm on 47970 -> 192.168.0.30:47833, front pin 91f298c80f03bff5, back pin 93956f4c54d29c4e
PC  pairing with Hex B (91F2-98C8-0F03-BFF5) at 127.0.0.1, code 107694
Mac pairing with Hex B (9395-6F4C-54D2-9C4E) at 192.168.0.50, code 452586
```

The codes differ and each side shows the middle's fingerprint, not the other machine's, so a person comparing the two
screens declines. (The self test accepts whatever it's shown, which is why it exists only in self tests.)

## 5. Finding each other on the LAN (mDNS)

Suite `link-nearby` (Link up with `link_discoverable` on, what's heard printed) on both machines at once, the Mac's
from plain ssh (`mac-hex.sh`), the PC's as `dotnet Hex.dll`, and `dns-sd -B _hex-link._tcp` on the Mac beside them:

```
PC  link-nearby: up as Hex PC (E4D6A35E) on port 47833, looking for 60 s
PC  link-nearby: heard Hex Mac (D517E47F) port 47833 at 192.168.0.30,100.80.122.37
PC  pass: link-nearby: another Hex is heard on the LAN
Mac link-nearby: up as Hex Mac (D517E47F) on port 47833, looking for 60 s
Mac link-nearby: heard Hex PC (E4D6A35E) port 47833 at 100.81.32.118,192.168.0.50,172.25.64.1
Mac pass: link-nearby: another Hex is heard on the LAN
dns-sd  20:10:06.270  Add  11 local.  _hex-link._tcp.  Hex Mac (D517E47F)
dns-sd  20:10:12.507  Add  11 local.  _hex-link._tcp.  Hex PC (E4D6A35E)
dns-sd  20:10:28.774  Rmv  11 local.  _hex-link._tcp.  Hex Mac (D517E47F)   (goodbye as Link went down)
dns-sd  20:10:29.384  Rmv  11 local.  _hex-link._tcp.  Hex PC (E4D6A35E)
```

Each finds the other both ways, including the Mac's Hex started over ssh, and the system's own browser sees both
announcements and their goodbyes. (The plan's earlier note that the Mac over ssh didn't hear the PC was from a run
whose PC side may have been a firewalled `Hex.exe`; a worktree `Hex.exe` has no Windows Firewall rule, so its mDNS
answers would be blocked until someone allows it.) The PC announces a virtual adapter's address too (`172.25.64.1`,
Hyper-V's), which another machine can't reach; a redial goes through the addresses in turn, so it only costs a try.

## 6. Bugs found and fixed

| Commit | What |
| --- | --- |
| f3850bc | macOS: git spells a temp folder's repository `/private/var/...` while the folder is `/var/...`, so the repository of a folder wasn't the folder's (unit test `TheRepositoryOfAFolderSaysItsBranchAndHead` failed on the Mac). The top folder is now spelled as the folder asked about. |
| b729d5a | Suite `link` run as `dotnet Hex.dll` started its second Hex as `dotnet --selftest ...` (2 of 22). It now passes the dll. |
| f719980 | Windows: `fs/read` read with `File.ReadAllBytes`, which shares the file for reading only, so the person saving it (or a program appending to it) failed "being used by another process" while a device read it. Seen live in a PC to Mac run. Reads now share read, write and delete; unit test `A_file_being_written_here_is_read_for_a_device_and_the_writer_carries_on`. |
| hex-extensions 2238e72, pointer 62e0702 | Claude Code's extension failed to start in a Hex with no folder open (`[][0]` at `extension.hor:93`), so every `claude_code` agent there failed at once with "There is no item 0 in a list of 0". On the submodule's branch `feat/hex-link-test`, to be merged into hex-extensions. |

Suite fixes: 8803ecc.

## 7. Left open

- TLS 1.2 on every link with a Mac shows the certificates (section 4): Hex Link sessions and device keys are visible
  to a watcher on the network, not what's said. A fix would be design work (TLS 1.3 when .NET on macOS has it, or a
  certificate that says nothing and is changed, with the pin carried inside the encrypted channel).
- On the Mac, Claude Code started from a Hex launched over ssh can't read its sign-in: the login Keychain answers
  "User interaction is not allowed" to an ssh session (`claude auth status`: loggedIn false), while from the GUI session
  it's signed in. Hex launched normally on the Mac is fine; the rig's `mac-hex-gui.sh` starts it in the GUI session.
## 8. Development checkout follow-up

Agent MCP configurations now put the absolute path to `Hex.dll` before `--mcp-connect` when Hex runs under
`dotnet`. The shared MCP proxy uses the same command builder. Both keep their existing arguments when Hex
runs as an executable. The built-in agent launchers already consume the command and argument list.

### Reproducing the Windows loopback coding check

Run from a Hex checkout with its first-party `hex-extensions` submodule present and an authenticated Codex CLI on PATH. This uses two real Codex turns. Build once with `dotnet build Hex/Hex.csproj`. In two PowerShell terminals start in that checkout. Set `$role = 'listener'` in the first and `$role = 'dialler'` in the second. Run this block in each, starting the listener first and waiting for its “up as” log line. Choose the same available TCP port in both.

```powershell
$port = 47834
$profilePath = Join-Path ([IO.Path]::GetTempPath()) ("hex-coding-" + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $profilePath
$env:HEX_HOME = $profilePath
$env:HEX_REMOTE_PIPE = "hex-coding-" + [guid]::NewGuid().ToString('N')
$env:HORIZON_AUDIO = 'off'
$env:HEX_LINK_KEYSTORE = 'file'
$env:HEX_LINK_TEST_NAME = "Fixture-$role"
$env:HEX_LINK_TEST_AGENT = 'codex'
$env:HEX_LINK_TEST_MODEL = 'gpt-6.2-astra'
$env:HEX_LINK_TEST_LOOPBACK = '1'
$env:HEX_LINK_TEST_CODING = '1'
$env:HEX_LINK_TEST_MITM = $null
$env:HEX_LINK_TEST_DIAL = $null
$env:HEX_LINK_TEST_LISTEN = $null
if ($role -eq 'listener') { $env:HEX_LINK_TEST_LISTEN = "$port" }
else { $env:HEX_LINK_TEST_DIAL = "127.0.0.1:$port" }
dotnet Hex/bin/Debug/net11.0/Hex.dll --mcp --selftest link-cross --exit --size 1000x700 |
    Tee-Object -FilePath "$profilePath/stdout.log"
$bytes = [IO.File]::ReadAllBytes("$profilePath/link-cross-work/LinkProbe.cs")
$expected = [Text.Encoding]::UTF8.GetBytes("// Fixture-$role`n")
[Convert]::ToBase64String($bytes) -ceq [Convert]::ToBase64String($expected)
```

Expect 15/15 and `True` in both terminals. Each caller starts an agent on its peer, waits for it and reads its reply. Each receiving process checks its own file against the expected UTF-8 bytes, including one LF. The final PowerShell comparison reads that file again after exit. The live editor's Link and MCP settings are separate from these disposable profiles.

The original §2 suite uses a stand-in chat. The optional §3 real-agent check uses `uname -sm` and can approve a remote shell request. The explicit coding mode uses a source edit, requires IPv4 loopback, and leaves agent approval requests unanswered. For self-tests, `--mcp` starts the per-profile MCP server. The first-party Codex extension forwards `HEX_HOME` to its stdio bridge so the bridge can find that server's discovery listing. Earlier scratch variants used a separate test source and a copied extension to diagnose this routing problem. The procedure above uses the repository suite and extension.

On BOGROG, run `4c7b45fd74e24ac69c7b71f5398e77d4` built the repository suite without a source override. Self-test catalog discovery loaded the first-party Codex extension from the checkout, with no private catalog or bundled Codex copy in the test output. The Windows processes connected at `127.0.0.1` using TLS 1.3 and `TLS_AES_256_GCM_SHA384`. The listener passed 15/15 and read its peer agent's completed reply after 14.3 s. The dialler passed 15/15 and read its peer agent's completed reply after 15.4 s. After both processes exited, each profile's file matched the expected bytes, including LF. The listener's Base64 was `Ly8gRml4dHVyZS1saXN0ZW5lcgo=` and the dialler's was `Ly8gRml4dHVyZS1kaWFsbGVyCg==`. Logs are under `scratch/agent12/<run>/{listener,dialler}/stdout.log` in the Hex agents temp directory.

For this run, the Mac's documented LAN address answered SSH but BOGROG had no trusted host key for it. Its Tailscale address timed out on port 22. No Tailscale CLI on PATH or at its standard install path, service, or Tailscale IPv4 adapter was found on BOGROG. The two-machine results in §§2–3 remain historical.

## 9. Current Windows and Mac QA

Each Windows–macOS claim requires a current Mac run with the matching Windows source snapshot, including uncommitted and untracked inputs and the engine and extension revisions. Record the snapshot, build identity, OS and architecture, route, listener role, session type, check counts, exit codes and logs. The results in §§2–5 establish earlier coverage.

The forward paired run used the Mac as listener and document host and Windows as dialler and guest. Both suites negotiated TLS 1.2 and both processes exited cleanly. The staged Mac checkout was `~/hexmac/review.3NDcYx`. The combined Link suites passed 100/100 on the current Mac and 100/100 on the preceding Windows build. They include scripted checks for rendered carets, selection and pointer, Follow, and remote symbol navigation. Live native input remains a separate acceptance check. A direct LAN connection from the Mac to the Windows listener at TCP 47834 was unreachable. An authenticated SSH reverse forward can exercise reversed application roles through Windows loopback. That route gives no Windows LAN listener coverage.

| Current paired scenario | Direct LAN, Mac host / Windows guest | SSH forward, Windows host / Mac guest | What passed |
| --- | --- | --- | --- |
| `HEX_LINK_TEST_DOCUMENTS=1` | `d2`, 7/7 and 13/13 | `drev2`, 7/7 and 13/13 | Disjoint edits converge, same-offset inserts follow acceptance order, overlapping changes conflict, document edits are denied after Edit is removed, host close is detected, and caret, anchor and pointer payloads propagate within the session. |
| `HEX_LINK_TEST_DOCUMENT_RECOVERY=1` | `r2`, 5/5 and 6/6 | `rrev2`, 5/5 and 6/6 | Exact Unicode round-trip, detached guest draft survives disconnect, known device reconnects, old session conflicts, explicit reconciliation advances the new session and the host disk keeps its original CRLF bytes. |

The final `d2`, `drev2`, `r2` and `rrev2` runs verified exactly View and Edit on both ends and supersede the initial runs that reported Steer. Windows logs use `mac-review-01/<run>/run.log` under the Hex agents scratch directory. Mac logs use `<run>.log` in the staged review checkout. The initial fixture archive SHA-256 was `638940d3aa30df4f19c89ad8e53c98e047bab4fefd3ba437bab1a6e91ab94e90`. Mac combined output is in `local.log` in the staged checkout. Current baseline `base4` (direct LAN) and `baser4` (SSH-forwarded Windows listener) each passed 14/14 on both peers. Current LAN discovery `near2` passed 2/2 on both. Baseline audit logs show Edit save, Steer approval, blocked terminal, post-downgrade write denial, unknown identity, Forget and revoked redial. A separate native M1 Link and Prediction test run passed 410/410 with zero skipped, and the targeted native engine run exited zero.

In `coding2`, Windows dialled its loopback SSH forward to a Mac GUI listener. Link negotiated TLS 1.2. The Mac initiated a real Codex source edit on Windows. The retained Windows fixture `mac-review-01/coding2/link-cross-work/LinkProbe.cs` contained `// WindowsQA` followed by LF and its SHA-256 was `404FC0CC759D0AF1286C52A5A2A09D263BAA7CA9BAC320FD40F502CDAE87BF92`. The reverse Claude turn hit quota and the Windows process log has no terminal suite total. The follow-up `directed5` used the same SSH-forwarded application roles with one real Codex turn initiated from the Mac GUI and executed on Windows. Both sides passed 15/15 and exited cleanly. The fixture confirmed the expected bytes and audit. The Windows-initiated source edit on the Mac remains open because of the Claude quota.

### Feature ratings

An 8.5 requires applicable current happy-path, denial or failure, and recovery or integrity evidence, with no open high-impact defect. For a Windows–macOS claim the ceiling is 7.5 without a matching current Mac run. Where the feature includes native interaction, inspect that interaction on both platforms. Record the test route and roles when limiting a claim to an exercised configuration.

| Feature | Rating / 10 | Evidence and remaining acceptance check |
| --- | ---: | --- |
| TLS, trust, pairing and revocation | 8.5 scripted | Current baseline passed 14/14 on each host in both application roles. Covers unknown identity, Forget, revocation and re-dial refusal over the exercised routes. Manual consent and native keystores remain open. |
| Discovery and loopback isolation | 8.5 | Current LAN mDNS passed 2/2 on each platform. Native suite assertions cover loopback isolation. Windows direct LAN TCP listener reachability remains open. |
| Rights, MCP tools and audit | 8.5 scripted | Current baseline validated allowed Edit and Steer operations, blocked terminal, View downgrade denial and matching audit trail. Current document and worktree runs verified their minimal grants. |
| Link window, fleet and Runs on | 6.5 | `link-ui` fixture covers switches, mirrors, naming and choice availability. Verify current native visuals, actual picker launch and loss of Steer on both platforms. |
| Remote agents and coding | 8.5 Mac-initiated / 7.0 Windows-initiated | `directed5` completed a real Mac-initiated Codex edit on Windows, 15/15 on each peer, with exact-byte and audit proof. The earlier reverse Claude turn hit quota. Verify a current Windows-initiated source edit on the Mac. |
| Remote browse, open, save, conflict and Git | 6.5 | `link-files` includes stale save, clone, fetch and patch. Run current native suites and verify real paired save and refusal with exact disk bytes. |
| Go to Symbol | 7.5 | Current Mac native fixture passed indexed root, declaration line and opening the remote file. Verify matching current Windows evidence, View denial, synced local opening and ambiguous root labels. |
| Working-tree sync and flush | 8.5 direct LAN | Current Mac-host/Windows-guest run passed 7/7 and 17/17 after the rights fix. It covered exact bytes and flush, dirty Undo, offline replay, conflict preservation and resolution, downgrade without a push, and regrant replay. Reverse roles remain pending. |
| Presence and Follow | 8.0 | Current paired document presence payloads and current Mac rendered caret, selection, pointer and Follow fixture passed. Inspect native Windows behavior and live paired disconnect cleanup. |
| Shared document merge protocol | 8.5 | Current paired concurrency, conflict, Edit downgrade and host close passed with exact bilateral View and Edit in both application roles. The Windows listener role used an SSH forward. |
| Shared document editor workflow | 8.0 | Current Mac native editor suite and forward paired collaboration passed. Verify native Windows evidence, advertised View-only behavior and the full manual conflict/rejoin/save path. |
| Shared document reconnect and integrity | 8.5 automated | Current paired recovery passed in both application roles with exact View and Edit, including Unicode and disk integrity. Native manual acceptance still includes rejoin, draft recovery and separate host and guest saves. |
| Local autocomplete editor behavior | 8.5 scripted | Current native prediction suite passed 35/35 on Windows and Mac. Targeted Windows engine tests passed 17/17. |
| Model provider failure and cancellation | 8.5 scripted | Current native 35/35 on both systems and Windows 187/187 targeted prediction and relay tests. Real Zeta 2 and Qwen2.5-Coder quality and latency remain unscored. |
| Autocomplete privacy and settings | 8.5 scripted | Current prediction QA reports native suite coverage on both systems. |
| Autocomplete responsiveness | 8.5 scripted | Current 33-key samples were 4.7 to 4.5 ms p95 with prediction enabled on Windows and 6.8 to 9.4 ms on Mac. Both met the suite's threshold. Real model load remains open. |
| Linked autocomplete | 8.5 scripted | `netpred4` passed Mac listener 6/6 and Windows client 7/7 over direct LAN. `predreverse5` passed Windows listener 6/6 and Mac client 7/7 over SSH forwarding. Both used exactly Predict and verified editor acceptance, excluded buffers, provider cancellation, live revocation and wire-level denial. In-memory tests cover cache switching. Real model quality remains open. |

The paired shared-document fixtures exercise the protocol and part of the editor lifecycle. Native manual acceptance should open the host and guest tabs from the palette, observe caret, selection and pointer independently, use Compare and Resume on an overlap, disconnect with a dirty draft, then rejoin and reconcile from the detached tab. Verify Undo and separate host and guest saves. The document commands currently require Edit to join a tab while View can read the buffer through the protocol. Verify the intended View-only user experience and align the command or the plan.

### Paired working-tree check

The opt-in `HEX_LINK_TEST_WORKTREE=1` fixture creates disposable repositories at the same deterministic commit. Run it on both peers through `link-cross` with a fresh profile and no other scenario selected. The listener runs the host checkpoints, and the dialler drives Sync, editor Save and Undo, reconnect and conflict resolution. The first direct LAN run, `work1`, passed 5/6 on the Mac host and 12/14 on Windows. Initial CRLF/LF comparison, editor Save and flush, dirty Undo, offline replay, conflict preservation, tool reporting for a conflict and explicit resolution passed. The first failure was the Windows wait for the live sync to enter pull-only after a remote rights change. The host timed out waiting for the guest, and the late Windows tool check lost its connection. The follow-up updates live sync state when new rights arrive and requeues held local paths on explicit Edit regrant. The focused Windows `HexLinkSyncTests` run passed 36/36, with zero skipped and exit code 0. It verified immediate revocation before a push, incoming reads, held edit replay after regrant and the fallback when the server denies a write before sending a grant update. The current direct LAN rerun `work4` passed 7/7 on the Mac listener and 17/17 on the Windows dialler with View and Edit as the initial grant. It verified the state changes before the first local push, remote changes still arrive, the denied edit stays local until regrant, and the held edit then reaches the peer without a second save. Logs are `mac-review-01/work4/run.log` on Windows and `work4.log` in the staged Mac checkout. In the SSH-forwarded reverse attempt, `workr4`, the Windows listener passed 6/8. The fixture called a restrictive disk read while the incoming save was replacing the file. It aborted the first wait and its corresponding assertion. The fixture now waits for the peer's flush marker and uses a shared read that retries transient I/O failures. The reverse rerun is pending.

A separate Windows unit regression held a writable handle that allowed read, write and delete sharing. The old working-tree manifest omitted the file because its own restrictive read could not coexist with that handle. The tree and sync reads now allow all three sharing modes. The focused test failed before this change, then the full Windows `HexLinkSyncTests` suite passed 37/37 with zero skipped and exit code 0. The regression verified the previously omitted file's initial transfer, then an atomic save and exact destination bytes.

Use disposable clones of the same commit. Give both peers Edit, start sync and confirm the initial manifests converge with Windows CRLF and Mac LF checkouts. Save from each side and wait for flush, then compare expected exact bytes on both disks. On one host keep an unsaved tab, receive a remote write, and verify the local draft is still recoverable with one Undo. Disconnect, edit different files on each side and create a delete-versus-edit conflict on the same file. Reconnect and verify nonconflicting changes converge, the conflicting bytes are preserved on both sides, and the conflict clears only after an explicit resolution. Drop to View and verify pulls still arrive while writes are denied. The fixture also calls `hex_link_sync` start and flush with an unresolved conflict and again in pull-only mode. Both replies must identify the incomplete sync or say that local changes stay local. Record roles, effective rights, commit, path, exact hashes and log markers.

## 10. Shared-document stress on Apple M1

The native Apple M1 run passed **16/16** protocol and stress cases with **0 skips** in **103 ms**. The target included six document protocol cases and all ten cases in `HexLinkMultiplayerStressTests`.

| Stress cases | Coverage |
| --- | --- |
| Seven seeded races with 2–8 writers | 624 synchronized rounds and 3,024 committed revisions. Writers submit disjoint edits from a shared base. An independent model checks each reply against the submitting writer, then replays the commit order and compares every revision and full buffer ordinally. Each round also verifies that retrying an accepted proposal preserves the final snapshot and that concurrent reads agree. |
| Near-limit Unicode | Eight insertions at the same offset follow host commit order while preserving the full Unicode buffer, including combining marks and emoji sequences. The merged buffer reaches the 131,072 UTF-16 code unit limit. A valid stale proposal that would overflow the merged buffer fails atomically, and writing succeeds again after space is freed. |
| History and reopen | Revision zero is usable at the edge of the 32-entry history, where a duplicate retry is idempotent. Both a retry and a new proposal from that base conflict after eviction. Reopening the same path and text rejects an old epoch at revision zero and accepts a proposal in the new epoch. |
| Genuine overlap | In 32 three-writer races, exactly one overlapping replacement commits, the disjoint stale append survives, and retrying the losing proposal leaves the final buffer and revision intact. |

The stress cases verify 3,132 commits per pass. The seeds fix the proposals, while task scheduling determines their commit order. Ten additional runs of the same binary on Apple M1 passed **100/100** stress cases and verified **31,320 commits** across the repeats. Each run exited with code 0, and the repeat loop reported `TEN_ROUNDS_OK`.
