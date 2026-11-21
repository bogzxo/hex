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
                HexLinkWorkspace workspace = services.Workspace;
                HexLinkHostedProvision hosted = await Task.Run(() => HexLinkWorkspaces.Provision(workspace, folder, transfer.Token), transfer.Token);
                hostedProvision = hosted;
                return hosted.Plan.ToJson();
            }

            string name = HexLinkData.Need(parameters, "repository", 200);
            string repository;
            HexLinkProvisionRepository? planned = null;
            if (HexLinkProvisionPlan.IsBundleId(name))
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
            services.Audit("git", Device, $"git/bundle {planned?.Path ?? name}");
            HexLinkGit.Made? made = await Task.Run(() =>
            {
                if (planned is not null && HexLinkProvision.ReadHead(repository, transfer.Token) != planned.Head)
                    throw new InvalidOperationException("A repository changed on the host. Get a new workspace plan.");
                return HexLinkGit.Bundle(repository, have, transfer.Token, includeHead: planned is not null);
            }, transfer.Token);
            var answer = new JsonObject
            {
                ["id"] = id,
                ["size"] = made?.Size ?? 0,
                ["refs"] = new JsonArray([.. (made?.Refs ?? []).Select(each => (JsonNode)new JsonObject { ["name"] = each.Name, ["head"] = each.Head })])
            };

            if (made is null)
            {
                session.Tell("git/done", new JsonObject { ["id"] = id, ["ok"] = true, ["size"] = 0 });
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
    private void SendBundle(string id, string file, CancellationTokenSource transfer)
    {
        long at = 0;
        try
        {
            using var read = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, HexLinkGit.CHUNK);
            byte[] chunk = new byte[HexLinkGit.CHUNK];
            int got;
            while ((got = read.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false)) > 0)
            {
                transfer.Token.ThrowIfCancellationRequested();
                Need(HexLinkRights.View, "git/chunk");
                if (!session.Tell("git/chunk", new JsonObject { ["id"] = id, ["at"] = at, ["data"] = Convert.ToBase64String(chunk, 0, got) }))
                    return;
                at += got;
            }

            session.Tell("git/done", new JsonObject { ["id"] = id, ["ok"] = true, ["size"] = at });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HexLinkException or OperationCanceledException)
        {
            session.Tell("git/done", new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = e.Message });
        }
        finally
        {
            HexLinkGit.TryDelete(file);
            EndBundle(id, transfer);
        }
    }
}
