# Hex Link design

Hex Link connects Hex installations over mutually authenticated TLS. Devices can share a workspace, work on the
same text buffer, and run agents where the right tools are available. Each device decides what the others may do
on it. The [test evidence](hex-link-test-evidence.md) distinguishes the deployed baseline from the Wave 2
integration gates.

## Status

- **What ships in Wave 2.** Devices and invite pairing, folder authority, checked working-tree sync and bulk
  transfers, workspace provisioning, HEAD follow, claims and clean text merges, shared documents, private
  worktrees and checked publication, remote agent approvals.
- **Known limits.** One relay may forward permitted control RPC. Bulk publication needs a direct link to the
  authority. Handle case-only directory renames manually. Native Keychain and `secret-tool` paths and native UI
  colour contrast have no recorded acceptance runs. Shared editing meets the 50 ms loopback target in the fast
  run and misses it at CPU saturation.
- **Not done.** A current Tailscale-only sync run, a controlled real-LAN throughput benchmark, and a live
  64-device GUI load test. The recorded throughput comparison uses pinned loopback TLS.

## 1. Architecture

`Hex.Core/Link` owns identity, transport, permission checks, peer state, sync and document rooms.
`Hex.Dawn/Link` adapts the editor, workspace and agent hub. A TLS connection is symmetric. Both ends serve
requests and send notifications, regardless of who dialled. Each endpoint applies its current local rights,
workspace rules and feature availability. Network and disk work run off the editor thread. The editor applies
posted changes and updates its UI. The local MCP pipe remains a separate entry point.

## 2. Devices and routes

Link is opt-in in this installation's settings. A repository cannot turn it on. The listener defaults to TCP
47833 and accepts private addresses, including private VPN ranges. Discovery is optional LAN mDNS. It advertises
the device name and a short identifier. Known devices remember eligible endpoints and can redial after reconnect
or restart with backoff. The Devices window shows the route, last failure, and retry state where known.
Disconnect holds off redial. Forget removes trust.

A device can have up to **64 active links**. The listener admits at most four handshakes at once. The initial
handshake budget is ten seconds before user confirmation. Each link bounds request rate and message sizes and
checks liveness with pings. A full or inaccessible listener can refuse the connection before sharing workspace
data.

## 3. Security model

### 3.1 Identity and storage

Each installation uses a P-256 key and a self-signed certificate. Its device ID is the SHA-256 of the
certificate's public key in SubjectPublicKeyInfo format. The name is a label. The fingerprint is a shortened
representation of the key ID.

Windows uses DPAPI for the current user. macOS and Linux can wrap the private key using a secret from the login
Keychain or `secret-tool`. The Unix file-only fallback uses mode `0600`. If the store cannot be read now, Link
reports that state and keeps the key for a retry. An explicit reset archives the old identity, records the reset
and creates a new identity that must pair again.

### 3.2 TLS and admission

Both ends present certificates. TLS 1.3 is used where supported, or TLS 1.2 with ECDHE and an AEAD cipher.
Direct trust checks the pinned key on each side. The protocol starts with
`HEX-LINK 1 link|pair|invite <device-json>`, where the middle field is one of the three mode words. Admission
can return `NO <reason>`. The hello also advertises capabilities such as bulk framing.

### 3.3 Pairing and invitations

For address or Nearby pairing, the host opens a short pairing window. The joiner commits to a fresh nonce before
it sees the host nonce, then reveals it. Both sides derive a six-digit code from the nonces and both TLS pins.
Confirm the code against the other screen and approve the grants on each device. A decline or a timeout ends the
pairing.

**Invite a device** makes a private `hex-link:v1.` token containing candidate private endpoints, the port, the
full host key pin, expiry and a random 256-bit secret. The joiner races endpoints and verifies the pinned host
before sending proof. If a VPN endpoint wins first, pending LAN candidates get up to 300 ms to finish. After the
`invite` hello, fresh 32-byte nonces travel in `nonce <hex>` lines. Each side proves possession with a
role-separated HMAC-SHA256 over both TLS pins and both nonces, in `proof <hex>` lines. The host consumes the
invite on one valid proof, even if approval is later declined. It expires after at most five minutes and is
invalidated after five wrong proofs.

After the host proof it sends `terms <preset> <host-grants> <host-requests>`, with decimal fields, or `terms -`. Preset values 0–3 are Collaborator, Build and test worker, Viewer, and Custom. The masks contain View (1),
Steer (2), and Edit (4). The proposal carries no Predict bit. A valid invite replaces the code comparison. Both
devices still explicitly approve their own grants. The optional workspace share happens after pairing.

### 3.4 Rights and presets

| Right | Scope |
| --- | --- |
| View | Read the visible workspace, fleet, chat and plan data. Subscribe to presence, join a shared buffer read-only, and get a workspace or a Git bundle the host exposes. |
| Edit | With View, write visible files and edit or save shared buffers. Write requests still need a valid base and must respect the folder's authority and publication rules. |
| Steer | With View, start and steer agents and answer approvals. An agent can run commands on the host under that host's agent policy. Treat Steer as access to run work on the machine. |
| Predict | Use a model that the host explicitly shares for autocomplete. Independent of View and off by default. |

**Collaborator** suggests View + Edit. **Build and test worker** suggests View + Edit + Steer. **Viewer**
suggests View. **Custom** sets the rights individually. The two directions are independent. Only allowlisted MCP
tools are callable over Link. Direct terminal, debugger, UI input, screenshot, settings, and development-build
tools stay local.

Rights changes apply to new requests and ongoing work that depends on the revoked right. A loss of Edit makes an
existing sync pull-only. View regrant restores watches and refreshes their state. Forget removes the pin and
closes its connection. Turning off Link closes the listener and sessions. The bounded local audit records
pairings, refusals, Steer, edits, authority transfers and overrides. Relayed agent calls include the origin and
each authenticated hop. Invite secrets and model prompt text are kept out of the audit.

### 3.5 One-hop relay

A device with Steer on the relay can ask for supported control operations on a third device. The third device
applies the grants it gave the relay, and relayed document edits also require Edit at the first hop. The relay
scope carries the origin for attribution. It is no substitute for authenticating that origin directly. No second
relay hop, opaque tunnel, or relayed bulk transfer. Private Publish and authority transfer use the direct
authenticated identity of the relevant device.

## 4. Folder authority and wire transport

An exported folder has a persisted authority device ID and epoch. Share and Get bind the identity of the
exporter. Peers advertise it with folder metadata. Legacy two-device folders can use the existing sync-owner
rule until a binding is established. An explicit transfer is audited, advances the epoch and invalidates
operations expecting the old one. The old pinned authority can attest its successor. Recovery when the old owner
is permanently unavailable requires explicit local consent. An unreachable authority does not trigger an
election. Sync ownership, document ordering, claims, HEAD follow and publication use this binding where they
depend on an authority.

Request/response and notification messages are JSON-RPC over TLS. Capable endpoints negotiate raw or Zstandard
bulk framing, with JSON fallback for older peers. A bulk stream is associated with its authenticated connection
and operation. Length and SHA-256 are checked before applying bytes. Streams use 256 KiB frames, up to four
streams per direction, and an eight-frame (2 MiB) flow-control window per stream. A control message can go
between frames. Sync can pack up to 128 small files or 18 MiB and use resumable raw streams for larger files.
Legacy JSON file operations keep their 8 MiB limit. A resumable stream still requires a matching transfer
identity and file basis.

## 5. Shared work

### 5.1 Fleet and remote approvals

Remote chats display the machine running them. Fleet snapshots show status and pending approvals. A watched chat
follows versioned row changes and falls back to a snapshot if the patch base is unavailable. Use the **Runs on**
picker or `hex_link_spawn` to run an agent on a connected device that grants Steer. The remote machine owns the
process, workspace and permissions.

The spawning chat can receive the linked child's approval requests and answer through `hex_link_call` with
`hex_agent_approve`. The person can also answer in the remote chat card. The host binds routed approvals to the
device and chat that started the agent and rechecks Steer. A per-device pre-approval setting limits remote spawn
options to **Nothing**, **Edits** (the default), or **Everything**. These settings bound what a remote starter
may pre-approve. They do not grant permissions to a provider that would otherwise deny them.

### 5.2 Claims, guards and merge

The authority keeps advisory file and directory-prefix claims. The first edit starts an editing claim. Reading
alone leaves a file unclaimed. Planned claims can include context or an ETA. Private publication can reserve its
paths. View gates claim visibility. A live claim uses a 10-second heartbeat, a 35-second lease and a 15-second
disconnect grace period. The limits are 2,048 claims per device and 8,192 per folder.

`hex_link_claims` lists, takes or releases claims. Agent file edits and renames check the local claim index
before applying changes. A refusal identifies the holder and can offer waiting, contacting the holder or using a
private worktree. An explicit `override_claim` is audited. `hex_plan_update` reports overlapping plans and
`hex_agent_list` can show claimed paths. A linked agent can address `hex_agent_message` to `<device>:<chat>`
through a Steer-authorized route. A claim warns about intent and cannot replace the content check at write time.

Sync and Publish share a Git three-way text merge helper. A clean merge can be applied with a verified base and
compare-and-replace. Ordinary sync caps auto-merge inputs at 256 KiB and retains up to 32 MiB of recent bases. The shared helper accepts up to 1 MiB per input. Overlapping hunks, unavailable bases and unsupported or binary
content use the conflict path, which keeps both versions for resolution.

### 5.3 Remote files

Browse or search a peer's shared files with View. **Go to Symbol** opens a declaration at its line. A file
already in a synced folder opens locally. Otherwise, Hex opens a remote tab. Path resolution stays inside the
selected workspace root. The workspace's exclusions apply to names, reads, writes, watches and search results.
Linked edit tools reject a multi-file change if any target is outside the allowed workspace.

A remote save carries the hash the tab read. A stale base opens a conflict with the other version available for
comparison or explicit resolution. Watched clean tabs can refresh. Dirty buffers and concurrent disk changes are
protected.

### 5.4 Provisioning, repositories and HEAD follow

**Get workspace** and `hex_link_provision` can populate an empty destination or update an eligible older
checkout with View. Get transfers Git objects, restores branch and origin information, recreates initialized
submodules and nested repositories, and hydrates visible tracked and nonignored untracked files. It adds the
folder and starts sync. Uninitialized submodules keep their gitlink and URL. Bad individual paths or nested
repositories may be skipped with reasons while the rest copies. A failed root repository, unusable destination,
revoked rights, disk full or cancellation stops the operation. A skipped nested checkout can be fetched
separately without stopping sync of the rest.

Bulk-capable peers stream bundles and working files using checked transfers. On an older host, the legacy
working-file limit still applies. Provisioning preserves genuine mixed-line-ending edits. A tracked file that is
Git-clean keeps the checkout's bytes.

**Clone from Device** and **Fetch from Device** remain available for repository history. A bundle can contain
committed versions of files excluded from the current working-tree view. Fetch places refs under the device's
remote-tracking namespace. **Offer Changes** sends a patch for review. Automatic **HEAD follow** is
a per-folder option to follow the pinned authority's branch, commits and index, including nested repositories
and submodules. File sync carries the working changes. The follower preserves its local edits, config and stash,
backs up its old HEAD and audits agreed rewrites. Divergence keeps both tips and file sync available. **Align**
is the explicit choice for a divergent checkout.

### 5.5 Working-tree sync

A pair runs one automatic sync for the selected folder. It compares certified, paged manifests and uses watches
for incremental changes. Directory digests and a shared scan and hash cache reduce repeat work. Git inventory
includes tracked files and untracked files eligible under ignore and workspace exclusion rules. Nested
repositories retain their own boundaries. Cached metadata or digests can avoid scans or reads. A transfer still
checks exact content and the agreed base before publication. Cache data lives outside the exported tree.

A single-sided edit is sent or pulled against the last agreed value. A verified common Git base can seed the
first comparison. If both sides changed, a safe clean merge may apply. Otherwise, Hex preserves both versions and
offers a resolution. A delete requires a certified view and confirmation that the path is absent. A skipped
path, locked file, truncated page, broken repository or inaccessible directory is not proof of deletion.
Untrustworthy scope is held or quarantined and reported while safe scopes may continue. A working-tree write
uses a process-wide per-path guard, a fresh base check and atomic replacement as the OS permits. A dirty editor
or a lost race stops the write instead of clobbering it.

The sync engine recognizes equivalent LF and CRLF text and keeps the destination's line-ending style where
applicable. Exact byte hashes still protect writes and receipts. `hex_link_sync` can start, stop, report or
flush a folder. A flush can report remaining conflicts, skipped paths or pull-only rights. An explicit alignment
or recovery decision is required when repository history cannot be reconciled automatically.

### 5.6 Linked predictions

A device can borrow autocomplete from a consenting peer. The provider must be local to that peer and available.
Link sends the prediction context only when Predict and serving are enabled. Keystrokes, disconnects, rights
loss, turning off sharing and provider loss cancel work. Streams are bounded. A prediction grant gives no file
access.

### 5.7 Shared documents

Open the same text file on devices with that folder synced to join its shared session. Commands can also host,
join or leave explicitly. View allows read-only participation, including unsaved text. Edit allows typing and
saving. Device-coloured carets and selections, the tab roster and Follow use positions in the shared buffer.
Only room viewers receive its scoped caret updates, capped at 20 Hz. A file announcement outside the room has no
caret coordinates. `hex_edit`, `hex_write` and `hex_edit_symbol` enter active shared buffers as named agent
edits.

`doc2` uses a run-based sequence CRDT. Inserts and deletions refer to stable atom positions. The authority
orders and acknowledges a bounded operation tail for replay. Overlapping concurrent operations converge. Edits
leave the change handler immediately, with pending edits batched behind the exchange in flight. A waiting reader
receives ordered replay in its wake response. Undo and Redo affect only that author's edits. A quiet
acknowledged checkpoint compacts history and resets old undo history. A late joiner gets a checkpoint and tail.
A room can hold 64 remote participants and a checkpoint can hold 2,097,152 UTF-16 code units.

If the authority is offline, editors can keep typing locally. On return, same-epoch replicas replay missing
operations and retry pending IDs. When an epoch change cannot apply pending operations, Hex retains a draft and
offers comparison. The editor continues to identify a View-only participant as read-only.

An Edit participant can save once its operations are acknowledged. The authority compares the saved file's disk
basis before writing through checked replacement and retains the file's encoding and line endings. The resulting
receipt lets working-tree sync distribute the saved bytes without replacing a newer unsaved room buffer.
[Loopback latency and convergence evidence](hex-link-test-evidence.md#shared-document-timing) records where the
time went.

### 5.8 Private worktrees and Publish

The normal mode writes to the live workspace. People and agents can choose an isolated Git worktree, including
eligible nested repositories. `hex_agent_spawn` and `hex_link_spawn` accept `worktree: private`. A private
draft can follow incoming changes to untouched paths or hold them. Peers see names of unpublished paths without
receiving draft contents. If profile storage overlaps an export, Hex keeps drafts in a stable per-profile
directory outside the share, using the platform's user data location.

**Publish** checks the draft in an isolated copy using the folder's configured build task or `dotnet build`. It
can run a requested `dotnet test` filter. If upstream moved, a clean merge is checked again. Overlapping edits
preserve both sides for resolution. A failed gate leaves the draft available and shows its output. **Publish
anyway** explicitly bypasses the build gate and is audited. It cannot bypass authority, rights, path or base
checks.

Publication to another device uses direct pinned TLS and bulk transport. The authority validates the
epoch and exact bases, fences the affected paths from live sync, journals and projects the checked set, and
returns a durable receipt. Retrying a completed request returns the receipt. A failed or interrupted projection
is recovered before ordinary sync proceeds. Manual reapplication of individual files is unnecessary.
`hex_link_publish` offers `status`, `publish` and `discard` for the calling agent's draft.

## 6. Devices and editor UI

The Devices window shows this installation's identity, Invite and Join, nearby devices and cards for known
peers. Cards show connection and route, grants in both directions, sync progress and conflicts. Each device uses
the same tint in its card and caret. State and rights have text labels and Steer has a warning. The window
offers retry, disconnect, Forget and recovery for a temporarily unavailable or damaged identity. A destructive
reset asks for confirmation.

In a file, the editor and Explorer show claims and shared-session states. A claim notice can offer
**Open shared** or explain why that action is unavailable. Session notices and the tab roster identify
readers, offline sessions and collaborators. **Follow** tracks a named collaborator and local caret movement releases it. Remote
name tags fade while carets remain. The native review covered narrow and scaled layouts. Colour contrast
measurement remains open.

## 7. Verification

See the [Wave 2 test evidence](hex-link-test-evidence.md) for commit IDs, per-machine counts, and rerun
commands. Local suites use isolated profiles and IPv4 loopback without discovery. `link-cross` and `link-nearby`
are separate, explicit paired runs for network and discovery checks.
