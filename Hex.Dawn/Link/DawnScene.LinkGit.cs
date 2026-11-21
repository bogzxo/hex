using System.Globalization;

using Horizon.Core.Threading;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The repository handed over a link by git's own means (docs/hex-link-plan.md, 5.4). A clone of another machine's
// repository into a folder picked here, a fetch of what it has into refs/remotes/<device>/* of the repository here, and
// this working tree's changes offered to it as a patch; and the other way round, a patch offered by another machine
// put to the person here as a card: apply it, keep it as a file, or throw it away. Bundles come over in chunks with a
// work card saying how far along; nothing here runs on the editor's thread but what's said.
internal sealed partial class DawnScene
{
    // How long a patch offered here waits for the person before it's taken as kept
    private static readonly TimeSpan LINK_PATCH_WAIT = TimeSpan.FromMinutes(50);

    /// <summary>
    /// A repository of a linked device cloned into a folder here (which mustn't be there yet, or be empty), with a work
    /// card saying how much has come. Null when it's cloned, why when it isn't. From any thread.
    /// </summary>
    internal async Task<string?> LinkClone(string deviceId, string repository, string into, WorkContext? work = null, CancellationToken cancel = default)
    {
        if (link?.PeerOf(deviceId) is not { } peer)
            return Said("link.sync_not_linked", ("device", deviceId));
        if (Directory.Exists(into) && Directory.EnumerateFileSystemEntries(into).Any())
            return Said("link.clone_not_empty", ("folder", into));
        string? bundle = null;
        try
        {
            bundle = await peer.Bundle(repository, [], LinkProgress(work, peer.Device.Name), cancel: cancel);
            if (bundle is null)
                return Said("link.clone_empty", ("repository", repository), ("device", peer.Device.Name));
            work?.Say(Said("link.clone_cloning", ("folder", into)));
            return await Task.Run(() => HexLinkGit.Clone(bundle, into), cancel);
        }
        catch (Exception e) when (e is HexLinkException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return e.Message;
        }
        finally
        {
            if (bundle is not null)
                HexLinkGit.TryDelete(bundle);
        }
    }

    /// <summary>
    /// What a linked device's repository has that the one here hasn't, fetched into refs/remotes/&lt;device&gt;/*: what came,
    /// a line a branch, or that nothing was new. Throws with why when it couldn't. From any thread.
    /// </summary>
    internal async Task<string> LinkFetch(string deviceId, string localRepository, string? repository = null, WorkContext? work = null, CancellationToken cancel = default)
    {
        HexLinkPeer peer = link?.PeerOf(deviceId) ?? throw new InvalidOperationException(Said("link.sync_not_linked", ("device", deviceId)));
        string named = repository ?? LinkRepositoryThere(peer, localRepository) ?? throw new InvalidOperationException(Said("link.fetch_which", ("device", peer.Device.Name)));
        List<string> haves = await Task.Run(() => HexLinkGit.Haves(localRepository), cancel);
        string? bundle = await peer.Bundle(named, haves, LinkProgress(work, peer.Device.Name), localRepository, cancel);
        if (bundle is null)
            return Said("link.fetch_nothing", ("device", peer.Device.Name));
        try
        {
            work?.Say(Said("link.fetch_fetching"));
            List<(string Name, string Head)> refs = await Task.Run(() => HexLinkGit.Refs(bundle), cancel);
            if (await Task.Run(() => HexLinkGit.Fetch(bundle, localRepository, peer.Device.Name), cancel) is { } why)
                throw new InvalidOperationException(why);
            string slug = HexLinkGit.Slug(peer.Device.Name);
            IEnumerable<string> branches = refs.Where(each => each.Name.StartsWith("refs/heads/", StringComparison.Ordinal))
                .Select(each => $"{slug}/{each.Name["refs/heads/".Length..]} at {each.Head[..10]}");
            return Said("link.fetched", ("device", peer.Device.Name), ("branches", string.Join(", ", branches)));
        }
        finally
        {
            HexLinkGit.TryDelete(bundle);
        }
    }

    /// <summary>
    /// The working tree's changes in a repository here offered to a linked device as a patch, with a title; what the
    /// person there did with it (applied, kept or discarded). Throws with why when it couldn't be offered.
    /// </summary>
    internal async Task<string> LinkOfferChanges(string deviceId, string localRepository, string title, CancellationToken cancel = default)
    {
        HexLinkPeer peer = link?.PeerOf(deviceId) ?? throw new InvalidOperationException(Said("link.sync_not_linked", ("device", deviceId)));
        string diff = await Task.Run(() => HexLinkGit.Diff(localRepository), cancel);
        if (diff.Length == 0)
            throw new InvalidOperationException(Said("link.offer_nothing", ("folder", Path.GetFileName(localRepository))));
        return await peer.OfferPatch(title, diff, cancel);
    }

    // The repository of a device's that's this one: the same name, or the only one it has
    private static string? LinkRepositoryThere(HexLinkPeer peer, string localRepository)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(localRepository));
        IReadOnlyList<HexLinkRepository> there = peer.Workspace.Repositories;
        return there.FirstOrDefault(each => each.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name ?? (there.Count == 1 ? there[0].Name : null);
    }

    // How much of a bundle has come, on the work card
    private static Action<long, long>? LinkProgress(WorkContext? work, string device) => work is null ? null : (done, all) =>
    {
        string said = Said("link.bundle_progress", ("device", device), ("done", LinkSize(done)), ("all", all > 0 ? LinkSize(all) : "?"));
        if (all > 0)
            work.Report((float)done / all, said);
        else
            work.Say(said);
    };

    private static string LinkSize(long bytes) => bytes >= 1024 * 1024
        ? (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";

    // The repositories of the workspace, by their top folders here
    private IReadOnlyList<HexLinkRepository> LinkLocalRepositories => linkServices?.Workspace.Repositories ?? [];

    /* The palette's commands */

    // A device's repository picked, a folder picked, and it cloned there with a card; then offered to be opened
    private void LinkCloneCommand(HexLinkPeer peer)
    {
        List<(string Text, Action Picked)> repositories = [.. peer.Workspace.Repositories.Select(repository => (
            $"{repository.Name}  ({(repository.Branch.Length > 0 ? repository.Branch : "?")})",
            (Action)(() => ((IWindows)this).ChooseFolder(parent => LinkCloneInto(peer, repository.Name, Path.Combine(parent, repository.Name))))))];
        if (repositories.Count == 0)
        {
            Say(Said("link.clone_none", ("device", peer.Device.Name)));
            return;
        }

        ChooseFrom(Said("link.clone_pick_title", ("device", peer.Device.Name)), Said("link.clone_pick_text"), repositories);
    }

    // A device's repository cloned into a folder with a work card, then a card to open it
    private void LinkCloneInto(HexLinkPeer peer, string repository, string into)
    {
        Context.Work.Start($"link-clone-{peer.Device.Id}-{repository}", Said("link.clone_title", ("repository", repository), ("device", peer.Device.Name)),
            async work =>
            {
                if (await LinkClone(peer.Device.Id, repository, into, work, work.Token) is { } why)
                    throw new InvalidOperationException(why);
            },
            done =>
            {
                if (done.State != WorkState.Succeeded)
                {
                    Say(Said("link.clone_failed", ("repository", repository), ("why", done.Error ?? string.Empty)), error: true);
                    return;
                }

                UINotification? card = null;
                card = toasts.Show(Said("link.cloned", ("repository", repository), ("device", peer.Device.Name), ("folder", into)), "check", null, 0.0f,
                    (Said("link.clone_open"), () =>
                    {
                        card?.Close();
                        explorer.OpenFolder(into);
                    }));
            });
    }

    /// <summary>A fetch from a device into the repository of a workspace folder, with a card, what came said.</summary>
    private void LinkFetchFrom(string deviceId, string folder)
    {
        string? repository = linkServices?.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, folder)) is { } local
            ? LinkLocalRepositories.FirstOrDefault(each => PlatformPaths.IsUnder(local.Path, each.Path) || PlatformPaths.Same(local.Path, each.Path))?.Path
            : null;
        if (repository is null)
        {
            Say(Said("link.fetch_no_repository", ("folder", folder)), error: true);
            return;
        }

        LinkFetchInto(deviceId, repository);
    }

    private void LinkFetchInto(string deviceId, string repository)
    {
        string device = link?.PeerOf(deviceId)?.Device.Name ?? deviceId;
        Context.Work.Start($"link-fetch-{deviceId}-{repository}", Said("link.fetch_title", ("device", device)), async work =>
        {
            string said = await LinkFetch(deviceId, repository, null, work, work.Token);
            PostLink(() => Say(said));
        }, done =>
        {
            if (done.State == WorkState.Failed)
                Say(Said("link.fetch_failed", ("device", device), ("why", done.Error ?? string.Empty)), error: true);
        });
    }

    // Which repository here a fetch or an offer is of: the only one, or picked
    private void LinkPickRepository(Action<string> picked)
    {
        IReadOnlyList<HexLinkRepository> repositories = LinkLocalRepositories;
        if (repositories.Count == 1)
        {
            picked(repositories[0].Path);
            return;
        }

        if (repositories.Count == 0)
        {
            Say(Said("link.no_repository_here"), error: true);
            return;
        }

        ChooseFrom(Said("link.repository_pick_title"), Said("link.repository_pick_text"),
            [.. repositories.Select(repository => (repository.Name, (Action)(() => picked(repository.Path))))]);
    }

    // The working tree's changes offered to a device, with a title asked for
    private void LinkOfferCommand(HexLinkPeer peer) => LinkPickRepository(repository =>
        AskText(Said("link.offer_title", ("device", peer.Device.Name)), Said("link.offer_text"), string.Empty, Said("link.offer_button"), title =>
        {
            string named = title.Trim().Length > 0 ? title.Trim() : Path.GetFileName(repository);
            Say(Said("link.offer_sent", ("device", peer.Device.Name)));
            _ = Task.Run(async () =>
            {
                try
                {
                    string answer = await LinkOfferChanges(peer.Device.Id, repository, named);
                    PostLink(() => Say(Said("link.offer_answer_" + answer, ("device", peer.Device.Name), ("title", named))));
                }
                catch (Exception e)
                {
                    PostLink(() => Say(Said("link.offer_failed", ("device", peer.Device.Name), ("why", e.Message)), error: true));
                }
            });
        }));

    /* A patch offered here */

    /// <summary>The patches other devices offered that wait on the person here, oldest first, for the Link window.</summary>
    internal IReadOnlyList<LinkPatchOffer> LinkPatchesOffered
    {
        get
        {
            lock (linkPatches)
                return [.. linkPatches];
        }
    }

    private readonly List<LinkPatchOffer> linkPatches = [];

    /// <summary>
    /// A patch a device offered, put to the person as a card: Apply (git apply --check first, into the repository here it
    /// fits), Keep as File (in the link data folder), Discard. What they chose goes back as the answer; a card nobody
    /// answers is kept as a file. Called off the editor's thread by the link.
    /// </summary>
    private Task<string> LinkPatchOffered(HexLinkDevice device, string title, string diff)
    {
        var offer = new LinkPatchOffer(device, title, diff);
        lock (linkPatches)
            linkPatches.Add(offer);
        PostLink(() => LinkShowPatch(offer));
        LinkMoved();
        return Task.Run(async () =>
        {
            try
            {
                return await offer.Answer.WaitAsync(LINK_PATCH_WAIT);
            }
            catch (TimeoutException)
            {
                return LinkDecidePatch(offer, "kept");
            }
        });
    }

    private void LinkShowPatch(LinkPatchOffer offer)
    {
        int lines = offer.Diff.Count(c => c == '\n');
        UINotification? card = null;
        card = toasts.Show(Said("link.patch_offered", ("device", offer.Device.Name), ("title", offer.Title), ("lines", lines.ToString(CultureInfo.InvariantCulture))), "git-pull-request", null, 0.0f,
            (Said("link.patch_apply"), () =>
            {
                card?.Close();
                _ = Task.Run(() => LinkDecidePatch(offer, "applied"));
            }),
            (Said("link.patch_keep"), () =>
            {
                card?.Close();
                _ = Task.Run(() => LinkDecidePatch(offer, "kept"));
            }),
            (Said("link.patch_discard"), () =>
            {
                card?.Close();
                LinkDecidePatch(offer, "discarded");
            }));
        offer.Card = card;
    }

    /// <summary>
    /// A patch offered here decided, as the card's buttons do: applied to the repository here it fits (kept as a file
    /// when none does, said why), kept as a file, or discarded. What the device is told. From any thread.
    /// </summary>
    internal string LinkDecidePatch(LinkPatchOffer offer, string decision)
    {
        string answer = decision;
        if (decision == "applied")
        {
            string? why = Said("link.no_repository_here");
            string? into = null;
            foreach (HexLinkRepository repository in LinkLocalRepositories)
            {
                why = HexLinkGit.Apply(repository.Path, offer.Diff);
                if (why is null)
                {
                    into = repository.Path;
                    break;
                }
            }

            if (into is not null)
            {
                PostLink(() => Say(Said("link.patch_applied", ("device", offer.Device.Name), ("title", offer.Title), ("repository", Path.GetFileName(into)))));
            }
            else
            {
                string file = LinkKeepPatch(offer);
                PostLink(() => Say(Said("link.patch_not_applied", ("title", offer.Title), ("why", why ?? string.Empty), ("file", file)), error: true));
                answer = "kept";
            }
        }
        else if (decision == "kept")
        {
            string file = LinkKeepPatch(offer);
            PostLink(() => Say(Said("link.patch_kept", ("device", offer.Device.Name), ("title", offer.Title), ("file", file))));
        }

        lock (linkPatches)
            linkPatches.Remove(offer);
        if (offer.Card is { } card)
            PostLink(card.Close);
        offer.Decide(answer);
        LinkMoved();
        return answer;
    }

    // A patch kept as a file in the link data folder, where it is
    private string LinkKeepPatch(LinkPatchOffer offer)
    {
        string folder = Path.Combine(linkData, "patches");
        string safe = new([.. offer.Device.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
        string file = Path.Combine(folder, $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{(safe.Length > 0 ? safe : "device")}.diff");
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, offer.Diff, new System.Text.UTF8Encoding(false));
        return file;
    }
}

/// <summary>A patch another device offered, waiting on the person here: who, its title and the diff. Decided once.</summary>
internal sealed class LinkPatchOffer(HexLinkDevice device, string title, string diff)
{
    private readonly TaskCompletionSource<string> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HexLinkDevice Device { get; } = device;

    public string Title { get; } = title;

    public string Diff { get; } = diff;

    internal Task<string> Answer => answer.Task;

    internal UINotification? Card { get; set; }

    internal void Decide(string said) => answer.TrySetResult(said);
}
