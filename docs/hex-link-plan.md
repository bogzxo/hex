# Hex Link: design and plan

Hex Link joins Hex on one machine to Hex on others over an encrypted, mutually authenticated connection, so the
agents running on any of them can be watched and steered from any other, and the codebase one of them has open can
be browsed, edited and cloned from the rest. Any number of machines, any mix of Windows, Linux and macOS, x64 and
arm64. Nothing listens until the person turns Link on.

Decided with the person (2026-11-21):

- **Shared code, v1:** the remote workspace (browse, search, open, save back with a conflict check), presence
  (who has what open, follow them), and the repository handed over by git (clone or fetch over the link, a patch
  offered). Live character-by-character co-editing is a later phase (section 9).
- **Reach:** the LAN, found by mDNS, or an address typed in, which also works over a VPN the person runs
  (Tailscale, WireGuard). No relay, no server of ours.
- **Rights:** asked at every pairing, on both machines, changeable per device afterwards.

## 1. What rides on what exists

| Existing piece | Its part in Link |
| --- | --- |
| `HexAgentHub` and `HexHubSnapshot` (`Hex.Core/Agents/Hub`) | The fleet that's sent: the snapshot's rows, made again only when a chat moved, are what `fleet/changed` carries. Read off the editor's thread as `hex_agent_wait` does. |
| `HexAgentChatStore.Write/RowOf/EntryOf/Read` | A chat on the wire is a chat as it's kept on disk. The watching side reads it back into a `HexAgentChat` of its own, so every row, card and plan the Agents view and Agentic draw for a local chat they draw for a remote one. |
| `HexMcpTools.CallAs` and the tool registry (`DawnScene.McpWorkbench.McpToolList`) | Steering and reading are the tools that already do them (`hex_agent_send`, `hex_agent_stop`, `hex_agent_restart`, `hex_agent_approve`, `hex_agent_spawn`, `hex_read`, `hex_search`, `hex_symbols`, `hex_edit`, ...), called with no calling chat, behind a list of which tools each right allows. No second implementation of any of them. |
| `McpProtocol.WriteLine`, the line-of-JSON framing of `McpSession` | The link speaks JSON-RPC 2.0, a message a line, in both directions over one connection. |
| `McpPipeHost`, `McpGate`, `McpDiscovery` | Untouched. The pipe stays this user on this machine only. Link is a second door with its own, much stronger, lock; it never forwards to the pipe. |
| `HexSettings` rows, `EditorSettingsCatalog` | `link`, `link_port`, `link_name`, `link_discoverable`, user settings only, never a workspace's (a cloned repository must not open the door, as with `mcp_server`). |
| `HexData`, `TerminalFiles.UserFolder` | The device's key and the known devices live in the settings folder (`link/`), the audit log and the copies of remote files in the data folder (`data/link/`). |
| The persistent process | The listener is the scene's, like the MCP server's, and stays up while the window sleeps. |
| Status bar, Agentic (Noon), settings window | A status item, a Link window, a section of the fleet per connected device. |
| Native AOT | `SslStream`, `CertificateRequest`, `JsonNode`, sockets and P/Invoke only. No reflection, no serializer, no new packages. |

Spiked before choosing (scratch `link/spike`): mutual TLS between two self-signed P-256 certificates, each pinned
by the other by the SHA-256 of its public key, with `SslStream` on .NET 11. Windows 11 negotiates TLS 1.3
(`TLS_AES_256_GCM_SHA384`), macOS 27 on arm64 TLS 1.2 (`TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384`). A wrong pin and
a missing client certificate are both refused on both. So TLS, the platform's own audited implementation, rather
than a Noise handshake written by hand over primitives the BCL only half has (no X25519).

## 2. Architecture

```
 Hex A (PC)                                              Hex B (Mac)
 ┌───────────────────────────────┐                      ┌───────────────────────────────┐
 │ DawnScene.Link*  (Hex.Dawn)   │                      │ DawnScene.Link*               │
 │  services: hub, tools, files  │                      │  UI: fleet section, Link window│
 │  UI: status item, Link window │                      │                               │
 ├───────────────────────────────┤                      ├───────────────────────────────┤
 │ HexLink          (Hex.Core)   │   one TLS connection │ HexLink                       │
 │  HexLinkHost   answers  ◄─────┼──── JSON-RPC both ───┼────►  HexLinkPeer  asks       │
 │  HexLinkPeer   asks     ──────┼──── ways ────────────┼────►  HexLinkHost  answers    │
 │  HexLinkSession (framing)     │                      │                               │
 │  HexLinkTransport (TLS, pins) │                      │                               │
 │  HexLinkPairing (SAS)         │                      │                               │
 │  HexLinkTrust (known devices) │                      │                               │
 │  HexLinkIdentity (key, cert)  │                      │                               │
 │  HexLinkDiscovery (mDNS)      │                      │                               │
 └───────────────────────────────┘                      └───────────────────────────────┘
```

- **A link is symmetric.** Whoever dialled, both ends ask and both answer over the one connection. What each may
  ask is what the *other* granted it, from the other's own list of known devices. The Mac watching the PC and the
  PC watching the Mac are the same link.
- **Many devices.** A Hex holds a link to each paired device that's up (8 at once at most). Presence and changes
  go to every one of them that may see them.
- **`Hex.Core/Link`** has no window and is unit tested alone: identity, trust, transport, pairing, discovery, the
  session, the host (which asks an `IHexLinkServices` for everything that's the editor's) and the peer (the other
  machine as this one sees it: its fleet, its chats mirrored as `HexAgentChat`, its files).
- **`Hex.Dawn/Link`** (`DawnScene.Link*.cs`) implements the services over the hub, the tools and the workspace,
  marshals to the editor's thread through the queue the MCP calls use, and draws the UI.
- **Threads.** The listener and each link's reader are background threads. Nothing of the editor's is touched off
  its thread: the fleet is read from the hub's snapshot, everything else is queued (`HexMcpCall`, or a queue of
  the link's own drained each tick), and what arrives for the UI waits in a queue the tick drains.

## 3. Security model

### 3.1 Identity
Each Hex installation has one P-256 key, made the first time Link is turned on, and a self-signed certificate over
it (20 years, the name is only a label). The **device id** is the SHA-256 of the certificate's
SubjectPublicKeyInfo; its first 80 bits in base32 groups are the fingerprint people can read out.

The private key never enters the repository or the workspace. It's kept in `<settings>/link/identity`, wrapped:

| Platform | Protection |
| --- | --- |
| Windows | DPAPI, `CryptProtectData` for the current user (P/Invoke, no package). |
| macOS | A random 256-bit wrapping key in the login Keychain (`SecKeychainAddGenericPassword` / `Find`, service `Hex Link`), the file AES-256-GCM under it. |
| Linux | The same with libsecret through `secret-tool` (the secret on stdin, never on a command line). |
| Fallback (no Keychain in an ssh session, no secret service) | The file alone, mode 0600, and Link's window says the key is protected by file permissions only. |

### 3.2 Transport
TLS 1.3 where the platform has it, TLS 1.2 with ECDHE and an AEAD suite otherwise, nothing older. Both ends
present their certificate. Neither is checked against a CA or a host name: the only question is whether the
SHA-256 of the peer's public key is a pin in the list of known devices (or, during pairing, what the code is
computed over). Revocation lists, renegotiation and session resumption are off. After the handshake each end
sends one line, `HEX-LINK 1 link|pair`, and an end that isn't let in is told `NO` and dropped having been shown
nothing.

### 3.3 Pairing
Pairing is the one moment two unknown keys are introduced, so it's the one moment a person has to look.

1. On the host: **Link › Pair a Device**. For five minutes it accepts one unknown device. Outside that window an
   unknown certificate is dropped straight after the handshake.
2. On the joiner: pick the host from **Nearby** (mDNS) or type its address, **Pair**.
3. TLS comes up between the two unknown certificates. The joiner sends a commitment, SHA-256 of a 32-byte nonce.
   The host answers with its own nonce. The joiner reveals its nonce, the host checks it against the commitment.
4. Both compute a six-digit code from SHA-256 over both public-key pins and both nonces, and show it with the
   other device's name and fingerprint.
5. The person checks the code is the same on both screens and, on each, ticks what that machine grants the
   other (View, Steer, Edit; nothing ticked beforehand), then **Pair**. Either side declining, a mismatch or two
   minutes passing ends it, and the pairing window with it.
6. Each stores the other's pin, name and what it granted.

Why this holds: somebody in the middle has to use keys of their own on each side, so the two sides hash different
pins. Because the joiner committed to its nonce before seeing the host's, and the host sent its nonce before
seeing the joiner's, neither nonce can be chosen to steer the code, so the codes agree by chance once in a
million, and a miss closes the window. No secret is typed, so there's nothing to guess offline.

A QR code of `hex-link://address:port#fingerprint` is shown beside the code for a device with a camera (a tablet
later); it saves typing the address and lets the joiner check the fingerprint by itself, the code still decides.

### 3.4 Rights, least privilege
What a device may do is the host's to say, per device, and is checked by the host on every request.

| Right | Allows | Tools let through |
| --- | --- | --- |
| **View** | The fleet, chats, plans, mail. The workspace's tree, files and search. Presence. Git's state, a clone or fetch of the repository. | `hex_agent_list`, `hex_agent_read`, `hex_plan_read`, `hex_map`, `hex_files`, `hex_symbols`, `hex_search`, `hex_refs`, `hex_read`, `hex_skim`, `hex_changes`, `hex_projects`, `hex_git_diff` |
| **Steer** | View, and: say something to an agent, answer its questions and approvals, stop, restart, start and close agents. | plus `hex_agent_send`, `hex_agent_message`, `hex_agent_stop`, `hex_agent_restart`, `hex_agent_spawn`, `hex_agent_approve`, `hex_agent_close` |
| **Edit** | View, and: write the workspace's files. | plus `hex_edit`, `hex_write`, `hex_edit_symbol`, `hex_rename` (and `fs/write`) |

Never over a link, whatever was granted: the terminal, tasks, clicking, typing and keys, screenshots, the
debugger, settings, `hex_dev_*`, the self test, notebooks' kernels. The list is an allow list, a tool added to
Hex later is not reachable until somebody adds it here.

Steer is said plainly in the pairing dialog to be as much as sitting at the machine: an agent told to run a
command runs it. Edit without Steer still can't run anything.

### 3.5 The listener
Off until `link` is on. One TCP port (`link_port`, 47833 unless changed). Connections from addresses that aren't
private (RFC 1918, link-local, loopback, CGNAT 100.64/10 which VPNs use, IPv6 unique-local and link-local) are
dropped before TLS. At most 8 links and 4 handshakes at once, 10 s to finish a handshake, a line of at most 8 MB,
60 requests a second a device, a ping every 20 s and a link that misses three is closed. mDNS announces only
while `link_discoverable` is on, and says the device's name and the first 8 characters of its id, nothing of the
workspace.

### 3.6 Revocation
**Forget** on a device removes its pin and cuts its link the same tick. It can't come back without pairing again.
Rights changed on a device apply to its next request. **Turn Link off** closes the listener and every link.

### 3.7 Audit
`data/link/audit.log`, a line an event: links up and down, pairings, refusals and why, every Steer and Edit
request (the device, the tool, the chat or file). Kept to 2 MB. Link's window shows the latest.

### 3.8 Threats

| Threat | Answer |
| --- | --- |
| Somebody on the LAN reads the traffic | TLS, AEAD, forward secret (ECDHE). |
| Somebody on the LAN pretends to be a known device | It hasn't the private key for the pin, the handshake fails. |
| In the middle of a pairing | The code differs (3.3). One try a window. |
| An unknown device probes the port | Dropped after the handshake with nothing shown, counted, rate limited. Nothing before pairing mode. |
| A paired device asks for more than it was granted | Checked per request on the host, refused and logged. |
| A paired device with View reads outside the workspace | Paths are relative to a workspace folder, made whole and checked to be inside it after links are followed, `.git` and what `files_exclude` hides are refused. |
| A paired device floods | Line, request-rate and link limits. |
| A stolen laptop that was paired | Forget it on the other machines. Its key is wrapped by its OS account. |
| A cloned repository turns Link on | `link*` are never workspace settings. |
| A hostile host sends a chat or file made to break the watcher | JSON is parsed with `JsonNode` and read field by field with limits, a remote chat's rows are data, never instructions, remote file copies are opened as files of an untrusted folder (no tasks run, no extensions loaded from them). |
| A remote agent's text steers a local agent | What a local agent reads through `hex_link_call` is marked as coming from another machine. |
| Downgrade | TLS 1.2 is the floor, the protocol version is in the first line and an older one is refused. |
| The key is copied off the disk | DPAPI, Keychain or libsecret wrapping; the fallback is said in the UI. |
| Steer is remote code execution | Said in the dialog, never granted by default, logged. |

## 4. Protocol

JSON-RPC 2.0, a message a line of UTF-8 JSON, after the TLS handshake and the `HEX-LINK 1 link` line. Both ends
send requests, responses and notifications. Errors use JSON-RPC's codes plus `-32001` not allowed (the right it
would take is in `data.needs`), `-32002` conflict, `-32003` not found, `-32004` too big.

| Method | Right | What |
| --- | --- | --- |
| `link/hello` | none | `{protocol, device{id,name,os,arch,version}}` → the same of the other, with `granted` (what it lets the asker do) and `workspace{name, folders[{name}], repositories[{name, branch, head}]}`. |
| `link/ping` | none | Keeps the link up. |
| `link/rights` (notification) | none | What the asker may do changed. |
| `fleet/get` | View | `{version, rows[{id,name,agent,model,parent,depth,state,reply,seconds,cost,version,waiting,plan_step}]}`. |
| `fleet/changed` (notification) | View | The same, sent when the hub's snapshot moved, at most 5 a second, to links that asked `fleet/get`. |
| `chat/get` | View | `{id, since}` → a chat as `HexAgentChatStore.Write` says it, with `version`; with `since` only the rows whose version is newer, each with its index, and the count, so the mirror is patched rather than sent whole. |
| `chat/watch`, `chat/unwatch` | View | `chat/changed` notifications for a chat while somebody has it open, the same patch. |
| `tools/list`, `tools/call` | by tool | The tools 3.4 lets this device call, as MCP describes and answers them. |
| `fs/list` | View | `{path}` → entries `{name, kind, size}` of a folder of the workspace. |
| `fs/read` | View | `{path}` → `{text or base64, hash, encoding, eol, size, modified}`, 8 MB at most. |
| `fs/write` | Edit | `{path, base, text or base64}` → `{hash}`, or conflict with the file's hash now when it isn't `base`. `base` null makes a new file and conflicts when there is one. |
| `fs/watch`, `fs/unwatch` | View | `fs/changed {path, hash}` for files the asker has open. |
| `presence/set` (notification) | View | `{file, line, column, selection}` of the sender; the receiver shows it and passes it on to its other links. |
| `git/bundle` | View | `{repository, have[]}` → `{id, size, refs}` then `git/chunk {id, data}` notifications and `git/done`. What `git bundle create` wrote of the branches, less what the asker has. |
| `patch/offer` | View | `{title, diff}` → a card on the receiver, which applies it, keeps it as a file or throws it away. The answer says which. |

Paths are `folder-name/relative/path` with forward slashes, the folder one of the workspace's by name.

## 5. What's shared and how

### 5.1 The fleet
A connected device granted View sends `fleet/get` and then hears `fleet/changed`. Agentic's fleet gets a section
per device under the local one, headed by the device's name and what this machine may do there, its cards the
same cards. Picking one opens its chat: `chat/get` then `chat/watch`, read into a mirrored `HexAgentChat` held by
the `HexLinkPeer` (never added to the local hub, so local ids, runs and limits never see it). The box under a
remote chat sends with `hex_agent_send` over `tools/call` when Steer was granted, and says what's missing when it
wasn't. Approval and question cards answer through `hex_agent_approve` and `chat/answer`. Stop and Restart are
the cards' usual buttons. The plan is the chat's stored plan, shown in the Plan tab read-only.

### 5.2 Agents across machines
With a link up, local agents get `hex_link_devices` (who's connected and what they allow) and
`hex_link_call {device, tool, arguments}`, which calls one of the other machine's allowed tools and marks the
answer as another machine's. So an agent on the Mac can read and search the PC's workspace, list its agents,
message one or start one there, as far as the PC allowed the Mac.

### 5.3 The remote workspace
**Link › Open Remote File** lists the other workspace from its index (`hex_files`), a pick reads it with
`fs/read` into `data/link/remote/<device>/<folder>/<path>` and opens that as a tab whose title says whose it is.
Search and go-to-symbol over the remote workspace are `hex_search` and `hex_symbols` over `tools/call`.

**Saving** a remote tab sends `fs/write` with the hash the file had when it was read. The host writes it through
the same path `hex_write` takes (an open tab with unsaved changes is changed in the editor, undo kept).

**Conflicts.** The host answers conflict when the file's hash isn't the base. The saver is shown the three ways
out: open the difference (theirs against mine), take theirs (the tab reloads, what was typed stays in undo), or
overwrite (sent again with the new hash as base). Nothing is ever merged silently. While a remote tab is open,
`fs/watch` says when the file moves on the host: an unmodified tab reloads, a modified one says so in its bar.

**Presence.** Each end says where its caret is at most twice a second while it moves. The Link window lists who's
in which file. In a file two people have open the other's caret is a thin coloured mark with their name, and
**Follow** keeps the view on it.

### 5.4 The repository
**Link › Clone from Device** asks `git/bundle`, writes the chunks to a temp file, `git clone`s it into a folder
the person picks and sets nothing as its remote. In a clone that already exists, **Fetch from Device** sends what
it has and fetches the rest into `refs/remotes/<device>/*`, so work goes back and forth by ordinary merges with no
server between. **Offer Changes** sends the working tree's diff as `patch/offer`.

### 5.5 Agents started on another device, and the code kept the same (added 2026-11-21)
An agent, or the person, on one machine starts an agent on another: the PC's agent tells an agent on the Mac to
build, test or change what only a Mac can. Two things make that work.

**Where it runs is a choice.** The box of the Agents view and Agentic has a **Runs on** picker beside the model,
this device or any connected device that granted Steer. A chat started on another device is that device's chat
(its process, its tools, its limits), and it shows in the chat lists of both machines, like every other chat of a
linked device, with a badge naming the device it runs on. Agents get the same through `hex_link_call` with
`hex_agent_spawn`, `hex_agent_send`, `hex_agent_wait` and `hex_agent_read` on the other device, and the shorter
`hex_link_spawn {device, prompt, ...}`.

**The working tree is synced.** With **Sync** on for a workspace folder both machines have open (the same
repository, matched by its first commit, each side having granted the other Edit), a change saved on one is
written on the other within a moment, both ways:

- Each side knows, per file, the hash both last agreed on (the base). `sync/manifest` gives every file git would
  carry (tracked, and untracked but not ignored, so no build output), `sync/watch` then says what changes.
- Local changed, remote still at base: sent with `fs/write` and the base. Remote changed, local at base: read and
  written here. Both changed to different things: nothing is overwritten, the other machine's version is kept
  beside the file as `name.theirs-<device>.ext`, both sides are told and the Link window lists it. The same for a
  delete against a change.
- Before the first sync the two trees are compared. Different commits checked out are brought together by git
  over the link first (5.4), then files that differ are listed for the person to pick a side, once.
- `sync/flush`, and `hex_link_sync {device, wait: true}` for agents, answer when everything written here is
  there, so "now build it on the Mac" is said after the code arrived, not before.
- Files over 8 MB, `.git` and ignored files never travel.

## 6. UI

- **Status bar.** A link item while Link is on: how many devices are connected, breathing while one is steering.
  Its tooltip names them and what each may do. A press opens the Link window.
- **Link window** (View › Link, the palette's "Link: ..."). This device's name and fingerprint and the switch.
  **Devices**: each known device with whether it's connected, its address, three toggles for what it may do
  here, what this machine may do there, and Forget. **Nearby**: Hex's found on the LAN, Pair or Connect.
  **Connect to address**. **Pair a Device** with its countdown. The latest of the audit log.
- **Pairing dialog** on both machines: the other's name and fingerprint, the code in large type, the three
  rights with a line each on what they mean (Steer's in the warning colour), Pair and Cancel.
- **Agentic.** A section per device in the fleet, remote chats in the middle, a banner over the box of a remote
  chat saying whose machine it runs on.
- **Notices.** A device connecting, a refusal, a conflict.
- All text in `localisation/en-gb.hor` under `link.*`.

## 7. Platforms

| Part | Windows | macOS | Linux |
| --- | --- | --- | --- |
| TLS | SChannel, 1.3 | Apple's, 1.2 with .NET 11 | OpenSSL, 1.3 |
| Key protection | DPAPI | Keychain, else 0600 file | libsecret, else 0600 file |
| mDNS | Own responder on 5353 with address reuse, beside the system's | The same beside mDNSResponder | The same beside Avahi |
| Firewall | Windows asks once when the listener opens; said in the window | Local Network permission for a bundled app | Whatever the distribution runs |
| Everything else | Shared | Shared | Shared |

Android (`HexHost`): can dial and watch, never listens (`HexHost.Listens` false).

## 8. Phases and acceptance

| Phase | Work | Accepted when |
| --- | --- | --- |
| 1a Transport | Identity, key protection, trust store, TLS listener and dialer, address rule, pairing, mDNS. | Unit tests: two identities link over loopback; an unknown pin is refused; no certificate is refused; pairing gives both the same code and stores both pins; a changed nonce or a different key gives different codes; a forgotten device is cut and refused; a public address is refused; the key file doesn't hold the key in the clear. |
| 1b Protocol | Session, host with the rights check, peer with the fleet and chat mirrors, files with the path jail and conflict, git bundle, patch. Over any pair of streams. | Unit tests over an in-memory pair: fleet and chat mirror follow a changing hub; every method refuses without its right; a tool outside the list is refused whatever was granted; path escapes are refused; a stale base conflicts; a bundle clones. |
| 2 Wiring | `DawnScene.Link`, settings, CLI and environment for tests, services over the hub, tools and workspace, `hex_link_*` tools, the audit log. | Self test `link`: two Hex on one machine with their own pipes, settings and data pair, link, one sees the other's chat and steers it, a file edit goes across, forgetting cuts the link. |
| 3 UI | Status item, Link window, pairing dialog, Agentic's sections and remote chats. | Self test `link-ui`; screenshots looked at. |
| 4 Sharing | Open Remote File, save back, conflicts, presence, clone, fetch, offer changes. | Self test `link-files`; unit tests of the conflict paths. |
| 5 Two machines | PC and Mac. | The Mac watches and steers an agent on the PC and the PC one on the Mac; pairing; revocation; an unpaired peer refused; a capture of the wire shows TLS and none of the text sent; a file edited on one is saved on the other. |

## 9. Later

Live co-editing of one buffer (operations transformed against the host's order, the host's buffer the truth), a
relay for machines with no route between them, the debugger shared read-only, a tablet as a watcher, terminals.

## 10. Risks

| Risk | What's done about it |
| --- | --- |
| TLS behaves differently per platform (1.2 on macOS, when a refusal is seen) | Spiked on both first; the first line after the handshake is the real admission, not the handshake's exceptions. |
| No Keychain in an ssh session on the Mac | The 0600 fallback, said in the UI; the Mac test runs with it. |
| mDNS beside the system's responder, firewalls | An address typed in always works; discovery is a convenience. |
| A remote chat mirrored at the cost of a big chat every change | Patches by row version, only for the chat on screen, the fleet is rows only. |
| Editor-thread stalls from a busy link | Rate limits; everything off the editor's thread but the queued calls. |
| The UI's size | Remote chats reuse the local rows; the Link window is one layout. |

## Status

Kept current by the orchestrator (chat #107) at every phase change, so another agent can take over.
Last updated 2026-11-21 19:40, phases 3, 4 and 5 running.

**Where things are**
- Integration branch: hex `feat/hex-link`, worktree `../hexwt-link` (beside `hex/`). No engine changes so far (engine
  stays at development 68b7e11, HIDL bf0f436). Nothing is pushed.
- Engine copies to build against (never the live engine), in
  `hex/Hex/bin/Debug/net11.0/data/agents/temp/scratch/link/`: `engine` (orchestrator), `engine-transport`
  (wiring worker), `engine-protocol` (sync worker). Build: `dotnet build Hex/Hex.csproj -p:HorizonRoot=<engine>/
  -p:HidlRoot=<engine>/Horizon.HIDL/`.
- Mac rig: `scratch/link/mac/` (`mac-sync.sh [branch]`, `mac-build.sh`, `mac-test.sh [filter]`,
  `mac-hex.sh ENV=.. -- args`, `mac-kill.sh`, README.md). Mac clones in `~/hexmac/link/{hex,horizon}`. PC is
  192.168.0.50 / Tailscale 100.81.32.118, Mac 192.168.0.30 / 100.80.122.37. A windowed Hex runs on the Mac from
  plain ssh while the person is logged in there. On the PC a worktree `Hex.exe` listening needs the Windows
  Firewall prompt allowed once; `dotnet Hex.dll` uses the existing .NET Host rule.

**Done**
- Design, decisions, TLS spike (this document).
- Phase 1a transport + pairing: merged (5c9b371, bdc9722). Phase 1b protocol + remote hub + sync wire pieces: merged
  (106c471, f340236). Glue 1d1cb46. 141 `HexLink` unit tests pass on Windows and macOS. PC and Mac paired and
  linked over LAN and Tailscale with the transport's console harness (TLS 1.2 to the Mac).

**Merged since** (`feat/hex-link` d9d7b32): phase 2 wiring (#111: 768604f, 50deefc, 94e2b75, e37dbe6) and the sync
engine (#112: bb03029, c61191b). 408 unit tests in the Link, settings, MCP and agents filters; self test `link`
22 of 22 (two Hex on the PC).

**Running** (each commits work in progress often and messages the orchestrator its commit and what is left)
- #118 `feat/hex-link-ui` (`../hexwt-link-ui`, engine-transport): phase 3. Link window, pairing dialog, device
  sections and badges in the fleet and chat list, a remote chat on screen, the Runs on picker, suite `link-ui`.
  Owns `DawnScene.LinkWindow*/LinkPairing*/LinkFleet*/LinkChats*/LinkRunsOn*.cs`.
- #119 `feat/hex-link-share` (`../hexwt-link-share`, engine-protocol): phase 4 and 4b glue. Working-tree sync
  per device and folder with `hex_link_sync` (first), open remote file with save back and conflicts, presence,
  clone, fetch and offered changes, suite `link-files`. Owns `DawnScene.LinkFiles*/LinkSync*/LinkGit*/
  LinkPresence*.cs` and `DawnScene.McpLink.cs`.
- #120 `feat/hex-link-test` (`../hexwt-link-test`, engine-test): phase 5 on what is merged. `link` on the Mac,
  the four route and direction combinations (LAN, Tailscale; who listens), a real agent started across machines
  both ways (haiku), the wire through a logging relay, mDNS, fixes, `docs/hex-link-test-evidence.md`.

**Next, in order**
1. Merge #120's fixes as they come, then #119, then #118 (the UI shows #119's `LinkSyncs` and `LinkConflicts`;
   a small follow-up for the UI worker once #119 says their signatures). Unit tests, `link`, `link-ui`,
   `link-files` green on the PC; `mac-sync`, `mac-build`, `mac-test`.
2. Phase 6: predictions over Link with chat #110 (`feat/edit-prediction`): right `Predict = 8`,
   `predict/start|chunk|done|cancel`, glue to their `HexPredictionRelay` seam. Waits on their seam commit.
3. A last two-machine pass on the whole (sync between PC and Mac with an agent on each, over LAN and Tailscale),
   the evidence gathered, then the report to chat #3.

**Open**
- The Keychain path on macOS and `secret-tool` on Linux are untested (ssh has no unlocked Keychain; the file
  fallback is what runs there).
- mDNS from the Mac to the PC wasn't heard when the Mac's process was started over ssh; an address typed in works.
- The `mcp` self test fails 6 of 44 on the Mac (keys and file writes), not a Link suite, not looked into.
- Later than v1: live co-editing, a relay, the debugger shared.
