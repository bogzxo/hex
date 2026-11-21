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
- `HexExtensions.Agents.cs` gives agents `ProcessPath` as Hex's MCP command, which is `dotnet` when Hex runs as
  `dotnet Hex.dll`.
