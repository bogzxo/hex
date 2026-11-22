using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    private volatile HexLinkHostedProvision? hostedProvision;
    private long lastProvisionOffer;
    private readonly Lock bundleGate = new();
    private readonly Dictionary<string, CancellationTokenSource> outgoingBundles = new(StringComparer.Ordinal);
    private readonly Queue<string> earlyBundleCancellations = new();

    /// <summary>
    /// Returns the workspace's checkout plan or bundles one of its repositories. The asker chooses the bundle id
    /// so it can receive chunks even before the response arrives.
    /// </summary>
    private async Task<JsonNode?> GitBundle(JsonObject parameters, CancellationToken cancel)
    {
        string? operation = HexLinkData.String(parameters, "operation", 20);
        if (operation == "offer")
            return OfferProvision(parameters);

        Need(HexLinkRights.View, "git/bundle");
        if (operation == "repository_ready")
            return RepositoryReady(parameters);
        if (operation == "follow_tips")
            return await GitFollowTips(parameters, cancel);
        if (operation is "follow_adopt" or "follow_confirm" or "follow_stop")
            return await GitFollowRequest(operation, parameters, cancel);
        string id = HexLinkData.Need(parameters, "id", 64);
        if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw HexLinkException.Invalid("A bundle's id is letters, digits and dashes.");
        CancellationTokenSource transfer = BeginBundle(id, cancel);
        bool sending = false;
        try
        {
            if (operation == "plan")
            {
                string? folder = HexLinkData.String(parameters, "folder", 200);
                string? repositoryPath = HexLinkData.String(parameters, "repository_path", HexLinkFiles.MOST_PATH + 1);
                HexLinkWorkspace workspace = services is IHexLinkProvisionOffers offers && folder is null
                    ? offers.WorkspaceForProvision(folder) : services.Workspace;
                bool defer = folder is null && HexLinkData.Bool(parameters, "defer_worktree") == true;
                HexLinkHostedProvision hosted = await Task.Run(() => HexLinkWorkspaces.Provision(workspace, folder, transfer.Token, repositoryPath, defer,
                    path => HoldOf(path)?.Entry), transfer.Token);
                hostedProvision = hosted;
                return hosted.Plan.ToJson();
            }

            if (operation == "deleted")
                return await ProvisionDeleted(parameters, transfer.Token);
            string name = HexLinkData.Need(parameters, "repository", 200);
            if (await Task.Run(HexLinkGit.Why, transfer.Token) is { } unavailable)
                throw HexLinkException.NotFound(unavailable);
            string repository;
            HexLinkProvisionRepository? planned = null;
            string? followWire = HexLinkData.String(parameters, "follow_wire", HexLinkFiles.MOST_PATH);
            string? followHead = null;
            if (followWire is not null)
            {
                followHead = HexLinkData.Need(parameters, "follow_head", 65);
                if (!HexLinkGit.IsCommitId(followHead))
                    throw HexLinkException.Invalid("HEAD follow requires a full commit id.");
                repository = ResolveGitFollowRepository(followWire);
            }
            else if (HexLinkProvisionPlan.IsBundleId(name))
            {
                HexLinkHostedProvision hosted = hostedProvision ?? throw HexLinkException.NotFound("Ask for a workspace plan before its bundles.");
                if (!hosted.WirePaths.TryGetValue(name, out string? wire))
                    throw HexLinkException.NotFound("The workspace bundle plan changed. Request it again.");
                planned = hosted.Plan.Repositories.First(repo => repo.Id == name);
                repository = HexLinkFiles.Resolve(Folders, wire);
            }
            else
            {
                repository = (services.Workspace.Repositories.FirstOrDefault(repo => repo.Name == name)
                    ?? throw HexLinkException.NotFound($"The workspace has no repository called {name}.")).Path;
            }

            List<string> have = HexLinkData.Strings(parameters, "have", HexLinkGit.MOST_HAVE, 64);
            RequirePublishableGit(repository);
            services.Audit("git", Device, $"git/bundle {followWire ?? planned?.Path ?? name}");
            HexLinkGit.Made? made = await Task.Run(() =>
            {
                if (planned is not null && HexLinkProvision.ReadHead(repository, transfer.Token) != planned.Head)
                    throw new InvalidOperationException("A repository changed on the host. Get a new workspace plan.");
                if (followHead is null)
                    return HexLinkGit.Bundle(repository, have, transfer.Token, includeHead: planned is not null);
                if (HexLinkGitFollow.ReadCheckout(repository, transfer.Token).Head != followHead)
                    throw new HexLinkException(HexLinkErrors.CONFLICT, "The other device's Git tip changed. Try again.");
                HexLinkGit.Made? bundle = HexLinkGit.Bundle(repository, have, transfer.Token, onlyHead: true);
                if (bundle is not null && !bundle.Refs.Contains(("HEAD", followHead)))
                {
                    HexLinkGit.TryDelete(bundle.File);
                    throw new HexLinkException(HexLinkErrors.CONFLICT, "The other device's Git tip changed while bundling. Try again.");
                }
                return bundle;
            }, transfer.Token);
            var answer = new JsonObject
            {
                ["id"] = id,
                ["size"] = made?.Size ?? 0,
                ["refs"] = new JsonArray([.. (made?.Refs ?? []).Select(each => (JsonNode)new JsonObject { ["name"] = each.Name, ["head"] = each.Head })])
            };

            if (made is null)
            {
                await session.TellAsync("git/done", new JsonObject { ["id"] = id, ["ok"] = true, ["size"] = 0 });
                return answer;
            }

            sending = true;
            _ = Task.Run(() => SendBundle(id, made.File, transfer));
            return answer;
        }
        finally
        {
            if (!sending)
                EndBundle(id, transfer);
        }
    }

    private JsonObject OfferProvision(JsonObject parameters)
    {
        if (services is not IHexLinkProvisionOffers offers)
            throw new HexLinkException(HexLinkErrors.METHOD_NOT_FOUND, "This Hex cannot receive workspace offers.");
        long now = Environment.TickCount64, last = Volatile.Read(ref lastProvisionOffer);
        if ((last != 0 && now - last < 5_000) || Interlocked.CompareExchange(ref lastProvisionOffer, now, last) != last)
            throw new HexLinkException(HexLinkErrors.BUSY, "A workspace was just offered to this device.");
        string workspace = HexLinkData.Need(parameters, "workspace", 200);
        offers.WorkspaceOffered(Device, workspace);
        return new JsonObject { ["ok"] = true };
    }

    private CancellationTokenSource BeginBundle(string id, CancellationToken cancel)
    {
        lock (bundleGate)
        {
            if (outgoingBundles.ContainsKey(id))
                throw HexLinkException.Invalid("That bundle id is already in use.");
            if (earlyBundleCancellations.Contains(id))
                throw new OperationCanceledException("That bundle request was cancelled.");
            var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            outgoingBundles.Add(id, transfer);
            return transfer;
        }
    }

    private void EndBundle(string id, CancellationTokenSource transfer)
    {
        lock (bundleGate)
            outgoingBundles.Remove(id);
        transfer.Dispose();
    }

    partial void GitBundleCancel(JsonObject parameters)
    {
        if (HexLinkData.String(parameters, "id", 64) is not { Length: > 0 } id)
            return;

        CancelPreparedProvisionBundle(id);
        lock (bundleGate)
        {
            if (outgoingBundles.TryGetValue(id, out CancellationTokenSource? transfer))
            {
                _ = transfer.CancelAsync();
            }
            else
            {
                // A notification can reach the reader before the request's worker has started.
                if (earlyBundleCancellations.Count == 128)
                    earlyBundleCancellations.Dequeue();
                earlyBundleCancellations.Enqueue(id);
            }
        }
    }

    // The bundle's bytes in chunks, in order, then done, and the temp file deleted whatever happened
    private async Task SendBundle(string id, string file, CancellationTokenSource transfer)
    {
        long at = 0;
        try
        {
            using var read = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, HexLinkGit.CHUNK, FileOptions.Asynchronous);
            byte[] chunk = new byte[HexLinkGit.CHUNK];
            int got;
            while ((got = await read.ReadAtLeastAsync(chunk, chunk.Length, throwOnEndOfStream: false, cancellationToken: transfer.Token)) > 0)
            {
                transfer.Token.ThrowIfCancellationRequested();
                Need(HexLinkRights.View, "git/chunk");
                if (!await session.TellAsync("git/chunk", new JsonObject { ["id"] = id, ["at"] = at, ["data"] = Convert.ToBase64String(chunk, 0, got) }, transfer.Token))
                    return;
                at += got;
            }

            await session.TellAsync("git/done", new JsonObject { ["id"] = id, ["ok"] = true, ["size"] = at });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HexLinkException or OperationCanceledException)
        {
            await session.TellAsync("git/done", new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = e.Message });
        }
        finally
        {
            HexLinkGit.TryDelete(file);
            EndBundle(id, transfer);
        }
    }
}
