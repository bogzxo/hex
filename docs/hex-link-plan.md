# Hex Link design

Hex Link connects Hex installations over mutually authenticated TLS. Devices can share a workspace, work on the
same text buffer, and run agents where the right tools are available. Each device decides what the others may do
on it. The [test evidence](hex-link-test-evidence.md) distinguishes the deployed baseline from the Wave 2
integration gates.

## Status

- **What ships in Wave 2.** Devices and invite pairing, folder authority, checked working-tree sync and bulk
  transfers, workspace provisioning, HEAD follow, claims and clean text merges, shared documents, private
  worktrees and checked publication, and remote agent approvals. Real-device checks covered workspace Get,
  automatic reconnect and sync resumption, LAN and Tailscale file probes, HEAD follow, agent coordination
  and a three-editor save with the authority's tab closed.
  - [Held changes](hex-link-held-changes.md) keep working edits local until submission. Held changes and Git conflicts use the shared merge editor.
- **Managing another device's agents.** A linked device stops, restarts, renames, clears and deletes chats of the
  Agents panel on the device that has them (`hex_agent_stop`, `hex_agent_restart`, `hex_agent_rename`,
  `hex_agent_close`, or `HexLinkPeer.AgentAction`; rows say `started_by_you`). The device that has the chats decides.
  A chat the caller's own call started there takes **Steer**, as it always has. A chat it did not start (the
  device's own, another device's) takes **Manage agents**, a fourth per-device right (`manage`, needs Steer, off
  until the person ticks it in the Devices window). Stopping or deleting a chat also stops or deletes the chats
  under it, so a chat of the caller's with somebody else's chat under it takes Manage too. Deleting takes
  `confirm true` over MCP and a confirmation card in the caller's UI; clearing (`clear true`) keeps the chat. A
  running chat is stopped first. A refusal says whose chat it was and names Manage agents. Every action, done or
  refused, is written to the audit log (`manage`, `manage refused`, `manage failed`) and shown as a notice on the
  device that has the chats. All of it travels as ordinary `tools/call` requests, so it works on LAN, Tailscale
  and through the one-hop relay (`hex_link_call`). Chats started by a link remember the device that started them
  (`link_device` in the chat file).
- **Tester leftovers (fixed).**
  - A shared save no longer leaves a participant modified. Working-tree sync refusing a write while the shared tab had not
    caught up with the authority's latest receipt is a retry (a CONFLICT without `editor_dirty`), not a held conflict. Every
    participant also writes the acknowledged receipt to its own disk, not only through a relay. Save on an offline or
    read-only shared tab says why.
  - An editing claim made by MCP edits lapses once the buffer has been idle for the 35 s lease; a tab the person typed in keeps
    its claim while unsaved. An explicit claim is refused, naming the holder, when another device's editing claim or explicit
    claim covers the path. The check reads the local claim table, so two takers inside one propagation delay can still both
    succeed until the table converges; there is no authority round trip yet.
  - Devices with the same name show a short id tail (`BOGZPC · 3F9A2C`; the port for an id-less tailnet entry) in Nearby, the pairing
    dialog, device cards, the session banner, pickers and command titles. Names are read from the current device record, so a
    rename shows at once.
  - An empty folder that only shares a name with the other device's folder is not synced automatically: Hex asks
    "Get <device>'s <folder> here?" once per session (Get it / Not now). Get Missing Repository says it is checking and what it is getting.
  - Disconnect by hand sends `link/bye` before closing: the other device holds off dialling, its card says "Disconnected by <device>"
    with a Connect button, until the presser connects again. Any refusal caused by an open tab (shared or plain) carries
    `[shared_document_open]`, so Keep Mine / Keep Theirs offers "Close the tab and resolve". Release on an automatic editing claim
    drops it (unsaved edits stay local). Ports refusals are audited once per kind until the rights move.
  - "Changed on both … nothing was overwritten" cards close when the conflict is resolved by any path, or the sync stops.
- **Known limits.** One hop can relay permitted control RPC. Bulk publication requires a direct link to the
  authority. Handle case-only directory renames manually. BOGROG had no Tailscale, and the Mac's Claude CLI
  was signed out. Shared editing can exceed the 50 ms loopback target under CPU saturation.
- **Not done.** The live private-worktree Publish gate retest, a controlled real-LAN throughput run, the 1 GB
  benchmark and a live 64-device GUI load test are pending. Native Keychain and `secret-tool` validation
  and measured native UI contrast are pending. Linked prediction was not rerun on the real devices in Wave 2.

## 1. Architecture

`Hex.Core/Link` owns identity, transport, permission checks, peer state, sync and document rooms.
`Hex.Dawn/Link` adapts the editor, workspace and agent hub. A TLS connection is symmetric. Both ends serve
requests and send notifications, regardless of who dialled. Each endpoint applies its current local rights,
workspace rules and feature availability. Network and disk work run off the editor thread. The editor applies
posted changes and updates its UI. The local MCP pipe remains a separate entry point.

### Chat between linked people

Link Chat opens beside the editor when another device connects and there is room. The close button hides it,
and the status bar and **Link: Chat** command open it again. On a narrow window an opened chat floats along
the right edge, where it can be closed to give the editor its full width. Enter sends, and Shift+Enter adds a
line. The transcript and unread count are held in memory until Link stops.

The `talk/say` notification carries a message id, text and a Unix timestamp in milliseconds. The receiver
takes the sender from the authenticated connection. A receiver can relay the message to its other linked
viewers once, with the sender's device id and name. A relay ends at that hop. Incoming notifications require
View, with a 2,000-character text limit, a rate limit and room for 4,096 accepted messages per Link session.

## 2. Devices and routes

Link is opt-in in this installation's settings. A repository cannot turn it on. The listener defaults to TCP
47833 and accepts private addresses, including private VPN ranges. Discovery is optional LAN mDNS. It advertises
the device name and a short identifier. Known devices remember eligible endpoints and can redial after reconnect
or restart with backoff. The Devices window shows the route, last failure, and retry state where known.
Disconnect holds off redial. Forget removes trust.

A device can have up to **64 active links**. The listener admits at most twelve handshakes at once and six per source address (a device paired with, dialling from its last address, is not held back by the twelve); a connection that sends nothing is dropped after three seconds. A joiner dials an invite's addresses one after another (a quarter of a second apart, or as soon as one fails) and stops at the first that answers. The initial
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

**Collaborator** (shown as "Work together") suggests View + Edit when you invite a device, without agent control. In
the invite's **Change** section, **Use my AI agents** is an explicit tick that adds Steer; the last choice is kept per
user in `link_invite_rights` (`work_together` by default, or `use_agents`) and starts the next invite. When you join
with a code and the host proposes no terms, the preset still suggests View + Steer + Edit for what you grant. **Build
and test worker** suggests View + Edit + Steer. **Viewer**
suggests View. **Custom** sets the rights individually. The two directions are independent. Only allowlisted MCP
tools are callable over Link. Direct terminal, debugger, UI input, screenshot, settings, and development-build
tools stay local.

Rights changes apply to new requests and ongoing work that depends on the revoked right. A loss of Edit makes an
existing sync pull-only on the device that lost it: its saves stop landing on the device that revoked, and the
revoking device keeps writing to it. The revoking device also stops applying that device's changes (they are skipped,
its status line says so, and they arrive when Edit is granted again). View regrant restores watches and refreshes their state. Forget removes the pin and
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
Git-clean keeps the checkout's bytes. For older copies, live sync preserves saved line endings. Its file
manifest does not certify that a local change should be rewritten by the replica's Git checkout filters.

To repair a file copied by an older Get, pause sync and save or close any open editor buffer. Check that the two
repositories have the same `git rev-parse HEAD` and that `git status --porcelain -- "$path"` is empty on the
authority. From the replica's affected repository, set `path` to one tracked file and run:

```sh
path='relative/path/to/file'
if git --literal-pathspecs ls-files --error-unmatch -- "$path" >/dev/null &&
   git --literal-pathspecs diff --cached --quiet HEAD -- "$path" &&
   git --literal-pathspecs diff --no-ext-diff --no-textconv --ignore-cr-at-eol --exit-code HEAD -- "$path"
then
    git --literal-pathspecs restore --source=HEAD --worktree -- "$path" &&
        git --literal-pathspecs status --short -- "$path"
else
    printf 'Left %s untouched.\n' "$path"
fi
```

Git writes the verified file using this checkout's rules. Resume sync after checking that the repaired paths are clean.

**Clone from Device** and **Fetch from Device** remain available for repository history. A bundle can contain
committed versions of files excluded from the current working-tree view. Fetch places refs under the device's
remote-tracking namespace. **Offer Changes** sends a patch for review. Automatic **HEAD follow** is
a per-folder option to follow the pinned authority's branch, commits and index, including nested repositories
and submodules. File sync carries the working changes. The follower preserves its local edits, config and stash,
backs up its old HEAD and audits agreed rewrites. Divergence keeps both tips and file sync available. **Align**
is the explicit choice for a divergent checkout.

### 5.5 Working-tree sync

Link leaves out operating-system metadata at any depth, independent of Git's ignore rules. The default list is
`.DS_Store`, `._*` (AppleDouble), `.Spotlight-V100`, `.Trashes`, `.fseventsd`, `Thumbs.db`, `ehthumbs.db`,
`desktop.ini` and `$RECYCLE.BIN`. Names are matched case-insensitively. An excluded directory stays local with its
contents. Existing copies are left on disk and appear under ?doesn't travel? after the folder is compared. Link
continues to apply the ordinary workspace and Git exclusions. Editor suffixes like `*.swp` and `*~` can be added to
those rules when appropriate for a project.

Add a `.hex-link-ignore` file to a shared folder's root to leave local files and directories out of
working-tree sync in either direction. It works in plain folders and Git repositories, including for tracked
files. Use `.gitignore` patterns such as `*.pdb`, `/build/`, `cache/**/trace` and `!keep.pdb`. To re-include a
file, its parent directories must remain visible. The rules file itself travels and Link reloads changes while
sync keeps running. Existing files stay on disk when a rule starts ignoring them.

Hex offers to create the file when you enable Link or share a folder in Devices. **Create** opens a starter
file with common build-output rules. **Not now** remembers your choice for that folder.

To opt a folder out of Link's operating-system defaults, add its absolute local path to `link_system_file_folders`
under `editor` in your user settings, for example `link_system_file_folders = ["C:/source/special-workspace"]`.
Restart Link on each device after changing the setting. Both ends must opt in before working-tree sync carries
these paths. The setting leaves Git history intact, so committed content can still arrive in a Git checkout.

A pair runs one automatic sync for the selected folder. It compares certified, paged manifests and uses watches
for incremental changes. Directory digests and a shared scan and hash cache reduce repeat work. Git inventory
includes tracked files and untracked files eligible under ignore and workspace exclusion rules. Nested
repositories retain their own boundaries. Cached metadata or digests can avoid scans or reads. A transfer still
checks exact content and the agreed base before publication. When the usual cache directory overlaps an export, Hex
uses the profile's private app-data location. If that also overlaps an export, the cache stays in memory.

For a bound folder, the replica signals readiness over the authenticated link at the matching authority epoch.
When the authority has Edit back to the replica, it runs the engine. Without that grant, the replica pulls from
the authority. A change of role drains the current engine before its replacement starts. Devices with View can
see the owner's state, transfer counts and conflict or skip summaries in Devices and `hex_link_sync status`.
The pinned authority can call `hex_link_sync start` on the replica through `hex_link_call` with Steer.
The replica checks the caller against its recorded authority and uses its direct connection for sync activation.
Link audits the tool call and checks the current grants and epoch during activation. Third-device relay
requests cannot stand in for the direct authority.

A single-sided edit is sent or pulled against the last agreed value. A verified common Git base can seed the
first comparison. If both sides changed, a safe clean merge may apply. Otherwise, Hex preserves both versions and
offers a resolution. A delete requires a certified view and confirmation that the path is absent. A skipped
path, locked file, truncated page, broken repository or inaccessible directory is not proof of deletion.
Untrustworthy scope is held or quarantined and reported while safe scopes may continue. A working-tree write
uses a process-wide per-path guard, a fresh base check and atomic replacement as the OS permits. A dirty editor
or a lost race stops the write instead of clobbering it.

The sync engine recognizes equivalent LF and CRLF text and keeps the destination's line-ending style where
applicable. A file that is new on the receiving side arrives byte for byte as sent, whatever line ends most files
there use (an LF script stays LF in a CRLF checkout). Exact byte hashes still protect writes and receipts. A clean
tab open in a shared document closes when the file is deleted or renamed away by the other device; a refusal
because a shared document hasn't saved a write ends with the marker `[shared_document_open]`
(`HexLinkSync.IsSharedOpenRefusal`). A device's own editor claims never block its own agents. `hex_link_sync` can start, stop, report or
flush a folder. A flush can report remaining conflicts, skipped paths or pull-only rights. An explicit alignment
or recovery decision is required when repository history cannot be reconciled automatically.

**Undo after a resolve.** Keep mine and Use theirs send or write the chosen version, so the other one would be lost.
Each resolve therefore keeps both versions (bytes and hashes) with the path, time and device in a resolution record
under the sync's state folder (`resolutions/<device and folder>/<id>`; none when that state sits inside the shared
folder). The last 50 are kept for 7 days. The resolve notice shows **Undo** for about 30 seconds, and the Devices window
lists the settled conflicts with **Undo** for later. Undo restores the other version as a normal local change: it goes
through the same guarded local write (fresh base check, dirty-editor guard, per-path lock) and the sync then sends it
like any edit. If the file is no longer what the resolve left (edited, deleted or changed since), nothing is written
and the merge editor opens with the current file, the version that was set aside and the one the resolve kept.
`hex_link_sync resolutions` lists the records and `hex_link_sync undo {id}` answers `undone`, `needs_merge` (the merge
editor opens) or an error. Known limit: a file open in a shared document is settled through the room first, so the version kept as "set aside" is what the file held when the sync's resolve ran, not always the original.

### 5.5.1 Empty directories

Empty folders travel both ways beside the files: made, deleted and renamed on one side, the other follows. A
directory is empty when no file is under it that sync sees; folders left out by `.hex-link-ignore`, Git, the workspace
excludes or operating-system metadata are never made, removed or counted, and a folder holding any of those is not
empty. It is a request of its own (`sync/dirs`: `list`, `check`, `make` and `remove`, the last two needing Edit) so the
file manifests are unchanged and an older peer without it is simply left alone. While the file sync is settled, every
few seconds each side lists its empty directories and `HexLinkDirs.Plan` compares them with what both had last time
(kept in `<state>.dirs`): one side's new directory is made on the other; one missing from a side that had it agreed is a
delete to follow, and so is one that files were taken out of (seen by either watcher), so a folder deleted with its
files is not brought back by the empty folder its files' deletion left on the other side. A rename is a delete and a
create. A pull-only side makes directories only from the other. Removing never touches a directory that has anything in it.

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
eligible nested repositories. `hex_agent_spawn` and `hex_link_spawn` accept `worktree: private` and a shared
folder name or absolute path. The agent's file tools and publish status use paths relative to the private root.
File tools also accept the shared folder name as a prefix. Reading or editing a file opens its repository when
needed. Searches and declaration reads can include the workspace's other folders. The agent's Hex file edits
stay inside its private tree, and `hex_terminal_run` starts a fresh shell there.

A private draft can follow incoming changes to untouched paths or hold them. Peers see names of unpublished paths without
receiving draft contents. If profile storage overlaps an export, Hex keeps drafts in a stable per-profile
directory outside the share, using the platform's user data location.

**Publish** checks the draft in an isolated copy using the folder's configured build task or `dotnet build`. It
can run a requested `dotnet test` filter. If upstream moved, a clean merge is checked again. Overlapping edits
preserve both sides for resolution. A failed gate leaves the draft available and shows its output. **Publish
anyway** explicitly bypasses the build gate and is audited. It cannot bypass authority, rights, path or base
checks.

A path covered by `.hex-link-ignore` in the private draft or at the authority stays out of Publish. Adding a
rule does not delete the file. A replica also keeps files covered by its local rules when a checked set arrives.
Claims follow the local rules. Publish refuses a file over its size limit and names the path.

Publication to another device uses direct pinned TLS and bulk transport. When syncing an existing checkout
from a paired device that advertises itself as the folder authority, Hex pins that authenticated authority for
the local folder. The authority validates the
epoch and exact bases, fences the affected paths from live sync, journals and projects the checked set, and
returns a durable receipt. Retrying a completed request returns the receipt. A receipt advances the draft's
publication base and preserves edits made while the request was in flight. If the authority has no receipt and
the draft has changed, Publish prepares a fresh set. A failed or interrupted authority projection is recovered
before ordinary sync proceeds.

`hex_link_publish` offers `status`, `materialize`, `publish` and `discard` for the calling agent's draft. Discard removes the local
draft and its pending retry, including when the authority is offline. Snapshot cleanup retains bases used by other
drafts and reclaims obsolete objects as the store grows.

### 5.9 Shared ports

A device shares a local TCP port (a dev server on 127.0.0.1:5173) with one linked device or all of them, like SSH -L
or Live Share's shared servers. **Link: Share a Port…**, the **Share a port…** button on a device card and
`hex_link_ports` (`list`, `share`, `unshare`, `open`, `close`) make a share: a name, a port and a target host. Only
loopback targets are accepted unless the person explicitly allows a LAN host (`allow_lan`, or `lan` in the command).
At most 16 shares; they are kept in `ports.json` beside the other Link data.

**Right.** A new per-device right **Ports** (`ports`, "Open shared ports", off until ticked in the Devices window's
rights dialog, takes View with it). It is separate from Steer on purpose: a tunnel runs nothing on the sharer, but it
reaches whatever listens on the shared port, which Steer does not imply. The sharer decides which ports. A port is
reachable only when it is shared with that device **and** the device holds Ports, both checked again on every request;
taking the right away or unsharing closes the live tunnels at once.

**Tunnel.** Bulk streams are one-way and fixed-length, so tunnels ride the same authenticated connection as ordinary
requests instead (`HexLinkTunnels`): `ports/list`, `ports/connect` (the sharer dials its local port),
`ports/ready`, `ports/data` (up to 96 KiB, base64) and `ports/close` (half or full), the last two in both directions.
A chunk is answered once the other end has written it to its socket and the next is not sent before, so a slow
reader slows its writer. Nothing new is opened, so route, relay rules, admission and pings are the link's own.
**Open** starts a listener on 127.0.0.1 (the share's port when free, else the next free one) and tunnels each
accepted connection, full duplex and several at once. The Devices card has Open, Open in browser, Copy address and
Close. A close at either end, an unshare, a lost right or a disconnect closes the tunnels, and a disconnect removes
the listeners. The opens are remembered and come back when the link does, on the same local port when free.

**Limits and audit.** At most 64 tunnelled connections per link (the 65th is closed at once), and 24 MiB/s per link
for tunnels. Link's 60 requests a second still applies, so tunnels wait when it is busy. The audit log records `ports
share`, `ports open`, `ports use`, `ports connect`, `ports close`, `ports refused` and `ports failed` with the
device, share and byte counts. Only loopback by default; the tool is local and not on the allow list for linked callers.

### 5.10 Serve AI (AI Edit, agents and @ai over Link)

A device without a model uses the chat models of a linked device that has them, for everything that asks the **Chat role**:
AI Edit (Ctrl+I) and its quick actions, Explain / Change with Agent, the Local agent and Link Chat's `@ai`.

**Right and gate.** New per-device right **Use my AI models** (`ai`; the one name for it in the Devices dialog, errors and tool output; `hex_link_devices` says it "use AI models", `HexLinkRights.Ai = 64`, off until ticked in the Devices window's
rights dialog beside Autocomplete and Open shared ports). Like Predict it stands alone: no View needed, the device sees no
files. The host serves when the device holds the right **and** the host has a chat model it can reach (`ServesAi`; with No
AI it serves nothing). Hello says `"serves": ["ai"]` and `link/serves` says it again on any change, so the client's
`HexLinkPeer.ServesAi` follows the right and the host's models. Every request re-checks both.

**Wire.** `ai/models` (the host's chat models: id, name, caps, context, size/quant line, at most 64), `ai/start`
(`{id, model, body}`, `body` an OpenAI chat-completions request as JSON text, at most 1 MB), notifications `ai/chunk`
(`{id, data}`, one server-sent `chat.completion.chunk` as the server wrote it, so content, `reasoning_content` and
`tool_calls` all arrive untouched), `ai/queued` (`{id, place}`), `ai/done` (`{id, ok, error, status}`) and `ai/cancel`. They
reuse the prediction stream shape (bounded channels, back-pressure, cancel on dispose, the 1024/s legacy-notice budget) and
the new error code `AI_FAILED` with `data.status` (`refused`, `busy`, `nomodel`, `cancelled`, `failed`).

**Bounds and audit.** The host bounds every body (`HexLinkAiRequest.Bound`): streaming forced, one choice, answer tokens clamped
to 8192 (2048 when unset), 1 MB request. A device has one chat running; up to two more queue behind it in the order asked and
are told their place (`ai/queued`: 2 is next); a fourth is refused as busy. Chats of all devices also go through one gate
on the host's server, and the host's own use shares the same llama-server/Ollama, which queues. Stopping to serve (the right
taken away, the host's models gone, the link closing) cancels what runs; the asker hears "stopped serving AI" rather than a
silent cancel. Each start is an audit line (`ai`, `refused`); the card says "Serving AI to Alpha: 1 request".

**Host side.** `DawnScene.LinkAi.cs` forwards the bounded body to the model's own `/v1/chat/completions` (llama-server and
Ollama both speak it; Hex's llama.cpp is started if needed) and passes each `data:` line on. The models served are the
host's `AiEditModels.LocalAgentModels` (its chat-capable library models).

**Client side.** Linked models are `LocalModel`s of source `Linked`, ids `link:<device fingerprint>:<model>`, listed after the
scanned ones (`LocalModelLibrary.SetLinked`) with the host's badges and the fit "runs on <device>". The picker groups them under
**On <device>**; pick one as the Chat role. `LinkAiRouter` + `LinkChatHandler` make an HTTP handler that sends the request over
Link and returns the chunks as server-sent events, so `OpenAiChat`/`OpenAiAgentChat` read them with the same code as a local
server: `LinkChatStreamer : IChatStreamer` and `LinkAgentChat : IAgentChat` (provider `linkai`, `ChatStreams.Make`/`AgentChats.Make`).
With no model here and exactly one device serving AI, `AiEditModels.Resolve` falls back to it; the AI menu says
"Model: qwen3.5-9b via Beta". Errors name the device and the next step: "Beta stopped serving AI. Ask Beta to tick Use my AI models
for this device…", "Beta is busy (queued 2nd)", "Lost the link to Beta. Reconnect it in Devices…".

**Local agent and other tools.** The agent is a process of its own with no Link connection. While linked models exist the
client Hex runs `LinkAiLoopback`, an OpenAI-compatible endpoint on **127.0.0.1** (random port, bearer token, refuses an `Origin`
header or a non-loopback `Host`, 1 MB bodies, 8 connections) with `GET /v1/models` and `POST /v1/chat/completions` (streamed
or whole, tool calls included). Its address, token and models are written to `link-ai/loopback.json` (this user only; named
by the Hex's process id, ignored once it is gone). `--local-agent` reads it, lists the linked models as agent models and talks to
the endpoint as to any OpenAI server, so any OpenAI-compatible tool can use a linked model too.

**Explain / Change with Agent.** They use the chosen agent system; with Local and a linked model they work through the loopback.
With no agent system here but a linked device that may be steered, they offer to run it there (**Run on <device>**: the box's
Runs on, started with `hex_link_spawn` when sent).

## 6. Devices and editor UI

The Devices window shows this installation's identity, Invite and Join, nearby devices and cards for known
peers. Cards show connection and route, grants in both directions, sync progress and conflicts. Each device uses
the same tint in its card and caret. State and rights have text labels and Steer has a warning. The window
offers retry, disconnect, Forget and recovery for a temporarily unavailable or damaged identity. A destructive
reset asks for confirmation.

**One-click controls.** A connected device's card has **Pause sync** and **Disconnect**. Pause stops the syncs running
with that device for every folder (nothing is deleted and the link stays up) and the card says **Paused**; the same
button then reads **Resume sync** and starts the paused folders again (`HexLinkDevicePause` picks what it acts on).
Disconnect cuts the link now, the card says **Disconnected by you**, and it is not dialled again until **Connect** is
pressed. The **More** menu opens under its button, not over it: opened at the button's corner the release of the same
click landed on the list and closed it at once.

**Rights granted by a peer.** When a peer changes the rights it grants this device, a notice names it and says what
changes ("Beta no longer lets this device edit there: your saves stay here until it does."), and the Devices window's
activity list gets a `rights from` line (the audit log has it too).

**Who holds or edits a file.** The Explorer marks each file with an open claim or a hold, by any device, with a dot in
that device's colour (the presence dots carry them, see `LinkUpdateExplorerPresence`). The tooltip says "Held by Beta"
or "Being edited by Beta (bogdan)" with the age ("2m ago"; a peer's hold has no age). The file's menu offers **Share my changes**
for what this device holds (its hold goes through Submit), **Let others edit** for what it has explicitly claimed, and
**Ask Beta to finish editing** for a peer's: it sends a Link Chat message naming the file. `HexLinkFileMark` is the model.

**Conflicts with the tab open.** The conflict toast, the Link: Sync Conflicts command and the Devices conflict list offer
Keep mine, Use Beta's and Compare. Compare, Review and the command all open the one Link merge editor, which saves to the
real file (**Use yours / Use Beta's / Use both**, **Save merged file**, **Close without saving**, the same words the git
merge editor uses with "Use theirs"); there is no marker-file preview tab. When a resolve is refused and the file is open in a tab, the notice adds **Close the tab and resolve**
(closing asks about unsaved changes as usual, then the same choice is applied). Once settled, the notice offers
**Undo** (see 5.5) and the Devices window keeps a short list of settled conflicts with Undo.

In a file, the editor and Explorer show claims and shared-session states. A claim notice can offer
**Open shared** or explain why that action is unavailable. Session notices and the tab roster identify
readers, offline sessions and collaborators. **Follow** tracks a named collaborator and local caret movement releases it. Remote
name tags fade while carets remain. The native review covered narrow and scaled layouts. Colour contrast
measurement remains open.

### 6.1 Link Chat for code

Link Chat carries code, not only words. All of it rides optional fields of the `talk/say` message, so an older peer
ignores them and shows the plain `text`, which always says the same in words.

- **Share Selection in Link Chat** (editor context menu, palette, `Ctrl+K L`) sends a `ref` of kind `code`: the wire
  path (`folder-name/relative/path`), 1-based `line` and `toLine`, a `snippet` capped at 60 lines and 4 KB, and an
  optional `note`. The chat draws a code card: `path:lines`, the snippet coloured by the file's language, and **Open**.
- **Ask `<device>` to Look Here** (context menu and palette, one entry per device) sends a `ref` of kind `look` with `to`
  set to the device. That device gets a notice, "`<device>` asks you to look at `file:line`" with **Go** and
  **Dismiss**, chat open or not. Every device shows it as a chat row.
- **Open** and **Go** use one path, `LinkOpenReference`: this device's own file, the local file when the folder is
  synced, otherwise the remote file as a remote tab (`LinkOpenRemote`), so it needs View like remote files. A path
  without a workspace folder in front is looked for in the sender's folders, then in this device's.
- `path/to/file.ext:123` and `file.ext:12-20` in any message are links to the same path, with `/` or `\`.
- `@DeviceName` is picked out in the chat and notifies that device even with its panel closed. Typing `@` in the
  input offers the linked devices and `ai`.
- The last 500 messages are kept in `talk-history.json` in the Link data folder and read back when Link starts.

Code: `HexLinkTalkRef.cs` (wire, caps, text parsing, history), `DawnScene.LinkTalk.Code.cs` (UI). Tests:
`HexLinkTalkCodeTests`.

## 7. Verification

See the [Wave 2 test evidence](hex-link-test-evidence.md) for commit IDs, per-machine counts, and rerun
commands. Local suites use isolated profiles and IPv4 loopback without discovery. `link-cross` and `link-nearby`
are separate, explicit paired runs for network and discovery checks.
