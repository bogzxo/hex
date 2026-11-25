using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    /// <summary>
    /// What a link needs of the editor (IHexLinkServices), over this scene. The links call it from their own threads:
    /// what's read there is a value the editor swaps in whole (the workspace, the fleet's extras, the hub's snapshot),
    /// and what has to touch the editor (a chat read, a question answered, a file written into an open tab) is posted to
    /// the editor's thread and handed back when it's run.
    /// </summary>
    private sealed class LinkServices(DawnScene hex) : IHexLinkServices, IHexLinkProvisionOffers, IHexLinkRepositoryReady, IHexLinkGitFollowing, IHexLinkSyncActivation, IHexLinkHeldSubmission
    {
        public Task<HexLinkSyncReport?> ActivateSync(HexLinkPeer peer, HexLinkFolder folder, string action, bool resume, CancellationToken cancel) =>
            hex.LinkReceiveSyncActivation(peer, folder, action, resume, cancel);

        public HexLinkAuthority? BindProvisionSource(HexLinkFolder folder)
        {
            if (FolderAuthorities.Of(folder) is { } known)
                return known.DeviceId == Device.Id ? known : null;
            HexLinkAuthority pin = FolderAuthorities.Bind(folder, new HexLinkAuthority(Device.Id, 1), HexLinkAuthorityProvenance.Provisioned);
            hex.link?.WorkspaceMoved();
            return pin;
        }
        public string GitFollowData => hex.linkData;
        public bool GitFollows(HexLinkFolder folder, HexLinkDevice device) => hex.LinkGitFollowPolicy(folder, device);

        private HexLinkWorkspace workspace = HexLinkWorkspace.Empty;
        private volatile IReadOnlyDictionary<int, HexLinkChatExtra> extras = new Dictionary<int, HexLinkChatExtra>();

        public HexLinkDevice Device => hex.linkIdentity?.Device ?? new HexLinkDevice(string.Empty, hex.LinkNameWanted);

        public IHexLinkPublicationService? Publication => hex.CurrentLinkPublication?.Publication;
        public HexLinkWorktreeGatePolicy? WorktreeGate(HexLinkFolder folder) => hex.LinkReadOwnedWorktreeGate(this, folder);

        internal Lock HoldServicesGate { get => LazyInitializer.EnsureInitialized(ref field, static () => new()); }
        internal Dictionary<HexLinkHoldStore, HexLinkHoldService> HoldServices { get => LazyInitializer.EnsureInitialized(ref field, static () => new()); }
        private readonly Lock holdStoresGate = new();
        private readonly Dictionary<(string Name, string Path), HexLinkHoldStore> holdStores = new();

        public HexLinkHoldStore Holds(HexLinkFolder folder)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
            if (PlatformPaths.Comparison == StringComparison.OrdinalIgnoreCase)
                full = full.ToUpperInvariant();
            var key = (folder.Name, full);
            lock (holdStoresGate)
            {
                if (!holdStores.TryGetValue(key, out HexLinkHoldStore? store))
                {
                    store = new HexLinkHoldStore(folder, Path.Combine(hex.linkData, LINK_SYNC_STATES));
                    holdStores.Add(key, store);
                    HexLinkHoldStore observed = store;
                    store.Changed += () => hex.PostLink(() => hex.LinkHoldMoved(this, folder, observed));
                    if (store.Count > 0)
                        hex.PostLink(() => hex.LinkHoldMoved(this, folder, observed));
                }
                return store;
            }
        }

        public HexLinkAuthorityStore FolderAuthorities { get; } = new(Path.Combine(hex.linkData, "folder-authority.json"));

          public HexLinkPorts Ports { get; } = new(Path.Combine(hex.linkData, "ports.json"));

        public HexLinkWorkspace Workspace
        {
            get
            {
                HexLinkWorkspace advertised = FolderAuthorities.Advertise(Volatile.Read(ref workspace));
                return hex.settings.LinkSystemFileFolders.Length == 0 ? advertised
                    : advertised with { Folders = [.. advertised.Folders.Select(hex.LinkSystemFilePolicy)] };
            }
            set => hex.LinkWorkspaceAdvertised(Interlocked.Exchange(ref workspace, value), value);
        }

        public void AuthorityChanged()
        {
            hex.link?.WorkspaceMoved();
            hex.LinkMoved();
        }

        public string? DeviceColour => DawnScene.LinkColourOf(hex.settings.LinkColour) is not null ? hex.settings.LinkColour : null;

        public IReadOnlyList<string> ChosenSyncFolders(string deviceId) => hex.LinkSyncedWith(deviceId);
        public IReadOnlyList<HexLinkSyncPause> PausedSyncFolders(string deviceId) => hex.LinkSyncPaused(deviceId);

        public Task ResumeLegacyPair(HexLinkPeer peer, HexLinkFolder folder, CancellationToken cancel) =>
            hex.LinkResumeLegacyPair(peer, folder, cancel);

        public void RepositoryReady(HexLinkDevice device, string folder, string path) => hex.LinkRepositoryReady(device, folder, path);

        public HexLinkDocuments Documents { get; } = new();
        public bool LegacyDocumentFixture => hex.linkByTest;

        public Task<HexLinkDocumentRoom> OpenSharedDocument(string path, string full, HexLinkDevice caller, bool automatic,
            CancellationToken cancel) => hex.LinkLiveOpenRoom(this, path, full, caller, automatic, cancel);

        public HexLinkPeer? SharedDocumentPeer(string id) => hex.link?.PeerOf(id);

        public bool OwnsSharedDocument(string wire)
        {
            int slash = wire.IndexOf('/');
            var folder = slash > 0 ? Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, wire[..slash])) : null;
            return folder?.Authority is not { } authority || authority.DeviceId == Device.Id;
        }

        public HexHubSnapshot Fleet => hex.hub.Snapshot;

        public IReadOnlyDictionary<int, HexLinkChatExtra> FleetExtras
        {
            get => extras;
            set => extras = value;
        }

        public Task<JsonObject?> Chat(int id, Func<HexAgentChat, JsonObject?> read) =>
            hex.OnEditorForLink(() => hex.ChatById(id) is { } chat ? read(chat) : null);

        // The tools as a client that isn't a chat's sees them; HexLinkAllowed keeps to the few a device may call
        public IMcpTools Tools => hex.McpTools;

        // As the question card's button does it
        public Task<bool> Answer(int chat, string answer) => hex.OnEditorForLink(() =>
            hex.ChatById(chat) is { } asked && asked.FirstPending(HexAgentEntryKind.Question) is { } question && asked.Reply(question, answer));

        // The disk always, whole or not at all, since the hash handed back says what's on it now (the working-tree sync
        // takes it as what both sides agreed), and an open tab of it made what it says as one step of its undo, changes
        // nobody saved a step back (DawnScene.LinkSync's LinkWriteLocal)
        public Task WriteFile(string fullPath, byte[] content) => hex.LinkWriteLocal(fullPath, content);

        public Task DeleteFile(string fullPath) => Task.Run(() => File.Delete(fullPath));

        public HexExcludes Excludes => HexExcludes.Of(hex.settings);

        public JsonObject RedialAdvertisement() => HexLinkRedial.Advertisement(hex.linkTransport?.Port ?? 0, hex.linkListenAddresses);

        public Task RenameCase(string from, string to, string expected, CancellationToken cancel) =>
            hex.LinkRenameCase(from, to, expected, cancel);

        public string? TreeCacheDirectory => hex.linkData is { Length: > 0 } data
            ? HexLinkPrivateStorage.ChooseTreeCache(data, Path.Combine(data, LINK_SYNC_STATES), hex.LinkProfileDataRoot, Workspace.Folders)
            : null;

        public Task<string?> CompareAndReplace(string full, string wire, string? baseHash, Stream? source,
            long length, string? expectedHash, CancellationToken cancel, bool? executable) =>
            hex.LinkCompareAndReplaceStream(full, wire, baseHash, source, length, expectedHash, cancel, executable);

        public Task<string?> CompareAndReplace(string full, string wire, string? baseHash, byte[]? bytes, CancellationToken cancel, bool? executable = null) =>
            hex.LinkCompareAndReplace(full, wire, baseHash, bytes, cancel, executable);

        public Task<string?> ApplyHeldSubmission(string full, string wire, string? basis, byte[]? bytes,
            CancellationToken cancel, bool? executable, HexLinkFiles.PackDirectories? directories) =>
            hex.LinkApplyHeldSubmission(this, full, wire, basis, bytes, cancel, executable, directories);

        public Task<string?> CompareAndReplacePacked(string full, string wire, string? baseHash, byte[] bytes,
            CancellationToken cancel, bool? executable, HexLinkFiles.PackDirectories directories) =>
            hex.LinkCompareAndReplace(full, wire, baseHash, bytes, cancel, executable, directories);

        public void Presence(HexLinkDevice device, HexLinkPresence? presence) => hex.PostLink(() =>
        {
            hex.linkPresence[device.Id] = new LinkPresenceSeen(device, presence, DateTime.UtcNow);
            hex.linkPresenceVersion++;
            hex.linkDirty = true;
            hex.LinkUpdateExplorerPresence();
        });

        // A card for the person, Apply, Keep as File or Discard, their answer going back (DawnScene.LinkGit)
        public Task<string> PatchOffered(HexLinkDevice device, string title, string diff) => hex.LinkPatchOffered(device, title, diff);

        public void WorkspaceOffered(HexLinkDevice device, string workspace) => hex.LinkWorkspaceOffered(device, workspace);
        public HexLinkWorkspace WorkspaceForProvision(string? folder) => hex.LinkProvisionExport(folder);

        public void Notice(string text) => hex.PostLink(() => hex.Say(text));

        public void Audit(string what, HexLinkDevice who, string detail)
        {
            hex.linkAudit?.Write(what, who.Id, detail);
            // A Steer request breathes the status item a while, so the person sees another machine is driving this one
            if (what == "steer")
                hex.LinkSteered();
        }

        // What the prediction side bound (DawnScene.LinkPredict), off the editor's thread
        public bool ServesPredictions => hex.LinkPredictServes();

        public IAsyncEnumerable<string> Predict(HexLinkPrediction request, CancellationToken cancel) => hex.LinkPredictMake(request, cancel);

        // Serve AI (DawnScene.LinkAi), off the editor's thread
        public bool ServesAi => hex.LinkAiServes();

        public Task<IReadOnlyList<HexLinkAiModel>> AiModels(CancellationToken cancel) => hex.LinkAiModelsListed();

        public IAsyncEnumerable<string> Ai(HexLinkDevice device, HexLinkAiRequest request, CancellationToken cancel) => hex.LinkAiServe(device, request, cancel);

        public void AiServing(HexLinkDevice device, int requests) => hex.LinkAiServingChanged(device, requests);
    }

    internal Task<string?> LinkCompareAndReplace(string full, string wire, string? baseHash, byte[]? bytes,
        CancellationToken cancel, bool? executable = null) =>
        LinkCompareAndReplace(full, wire, baseHash, bytes, cancel, executable, directories: null);

    private async Task<string?> LinkCompareAndReplace(string full, string wire, string? baseHash, byte[]? bytes,
        CancellationToken cancel, bool? executable, HexLinkFiles.PackDirectories? directories)
    {
        var (before, shared) = await OnEditorForLink(() =>
        {
            LinkTabStamp stamp = LinkTabBeforeWrite(full);
            return (stamp, LinkLiveOf(stamp.Document));
        }).WaitAsync(cancel);
        if (shared is not null)
        {
            if (bytes is not null && HexLinkFiles.Decode(bytes) is { } incoming)
            {
                string incomingHash = HexLinkFiles.Hash(bytes);
                var receipt = await LinkLiveKnownSyncSave(shared, full, incomingHash, HexDocSaved.TextDigest(incoming.Text), cancel).ConfigureAwait(false);
                if (receipt is not null)
                    return await LinkLiveWriteSaved(shared, receipt, full, wire, baseHash, bytes, incomingHash,
                        incoming.Text, before.DiskText, HexDocFileBasis.EncodingOf(incoming.Encoding),
                        cancel, executable, directories).ConfigureAwait(false);
            }
            if (bytes is null && !before.Dirty)
            {
                // The file was deleted or renamed away over the link while a saved tab has it open: the tab has nothing
                // that would be lost, so it closes with the file (a rename's new name arrives as its own file)
                string? deleted = await HexLinkFiles.CompareAndReplace(full, wire, baseHash, null, cancel, executable, directories).ConfigureAwait(false);
                await OnEditorForLink(() =>
                {
                    if (Context.Documents.Find(full) is { } open && !HasChanges(open))
                        CloseDocument(open);
                    return true;
                }).ConfigureAwait(false);
                return deleted;
            }

            // A shared tab that has simply not caught up with the authority's latest save is not a conflict to hold:
            // the next save (or the room's receipt) makes the same bytes acceptable, so ask for a later retry.
            if (bytes is not null && await OnEditorForLink(() => shared.Draft is null && LinkLiveOf(shared.Tab) == shared).ConfigureAwait(false))
                throw LinkLiveSaveLagging(wire);
            throw LinkLiveSaveConflict(full, wire);
        }
        if (before.Dirty)
            throw new HexLinkException(HexLinkErrors.CONFLICT, $"{wire} has unsaved changes in the editor.",
                new JsonObject { ["hash"] = HexLinkFiles.HashOf(full), ["editor_dirty"] = true });

        string? hash = await HexLinkFiles.CompareAndReplace(full, wire, baseHash, bytes, cancel, executable, directories).ConfigureAwait(false);
        if (bytes is not null && HexLinkFiles.Decode(bytes) is { } decoded &&
            await OnEditorForLink(() => LinkTakeIntoTab(full, decoded.Text, before)).ConfigureAwait(false))
        {
            throw new HexLinkException(HexLinkErrors.CONFLICT, $"{wire} was edited while the linked write was applying.",
                new JsonObject { ["hash"] = hash, ["editor_dirty"] = true });
        }
        QueuePrivateFollowForPath(full);
        return hash;
    }

    // Bytes as text when they're UTF-8 (a byte order mark let go of), null when they aren't, which goes to the disk as they are
    private static string? LinkText(byte[] content)
    {
        try
        {
            int skip = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content, skip, content.Length - skip);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
