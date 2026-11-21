using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    /// <summary>
    /// A repository bundled for the device: answered with the bundle's id (the asker's own, so it's ready for the chunks
    /// before the answer comes), its size and refs, then sent as git/chunk notifications of base64 and git/done. A
    /// repository with nothing the asker hasn't got is answered with size 0 and no chunks.
    /// </summary>
    private async Task<JsonNode?> GitBundle(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.View, "git/bundle");
        string name = HexLinkData.Need(parameters, "repository", 200);
        string id = HexLinkData.Need(parameters, "id", 64);
        if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw HexLinkException.Invalid("A bundle's id is letters, digits and dashes.");
        HexLinkRepository repository = services.Workspace.Repositories.FirstOrDefault(each => each.Name == name)
            ?? throw HexLinkException.NotFound($"The workspace has no repository called {name}.");
        if (HexLinkGit.Why() is { } why)
            throw new HexLinkException(HexLinkErrors.INTERNAL_ERROR, why);

        List<string> have = HexLinkData.Strings(parameters, "have", HexLinkGit.MOST_HAVE, 64);
        services.Audit("git", Device, $"git/bundle {name}");
        HexLinkGit.Made? made = await Task.Run(() => HexLinkGit.Bundle(repository.Path, have), cancel);
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

        _ = Task.Run(() => SendBundle(id, made.File));
        return answer;
    }

    // The bundle's bytes in chunks, in order, then done, and the temp file deleted whatever happened
    private void SendBundle(string id, string file)
    {
        long at = 0;
        try
        {
            using var read = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, HexLinkGit.CHUNK);
            byte[] chunk = new byte[HexLinkGit.CHUNK];
            int got;
            while ((got = read.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false)) > 0)
            {
                if (session.IsClosed || !session.Tell("git/chunk", new JsonObject { ["id"] = id, ["at"] = at, ["data"] = Convert.ToBase64String(chunk, 0, got) }))
                    return;
                at += got;
            }

            session.Tell("git/done", new JsonObject { ["id"] = id, ["ok"] = true, ["size"] = at });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            session.Tell("git/done", new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = e.Message });
        }
        finally
        {
            HexLinkGit.TryDelete(file);
        }
    }
}
