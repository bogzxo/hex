using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    // The empty directories of a folder, which of those asked about exist, and the ones asked made or removed (Edit)
    private async Task<JsonNode?> SyncDirs(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.View, "sync/dirs");
        HexLinkTree tree = TreeOf(parameters);
        HexLinkFolder folder = tree.Folder;
        var answer = new JsonObject();
        List<string> make = HexLinkData.Strings(parameters, "make", HexLinkDirs.MOST, HexLinkFiles.MOST_PATH);
        List<string> remove = HexLinkData.Strings(parameters, "remove", HexLinkDirs.MOST, HexLinkFiles.MOST_PATH);
        if (make.Count + remove.Count > 0)
        {
            Need(HexLinkRights.Edit, "sync/dirs");
            if (!HexLinkAllowed.Has(Rights, HexLinkRights.Edit))
                throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Making and removing directories needs Edit.");
            await using var lease = await HexLinkPublicationFence.ForRoot(tree.Root).EnterLive(cancel);
            var made = new JsonArray();
            var removed = new JsonArray();
            foreach (string path in make)
            {
                if (HexLinkDirs.Make(folder, tree, path, () => services.Excludes))
                    made.Add((JsonNode)path);
            }

            foreach (string path in remove)
            {
                if (HexLinkDirs.Remove(folder, tree, path, () => services.Excludes))
                    removed.Add((JsonNode)path);
            }

            answer["made"] = made;
            answer["removed"] = removed;
        }

        List<string> check = HexLinkData.Strings(parameters, "check", HexLinkDirs.MOST, HexLinkFiles.MOST_PATH);
        if (check.Count > 0)
            answer["present"] = new JsonArray([.. check.Where(path => HexLinkDirs.Exists(folder, path)).Select(path => (JsonNode)path)]);
        if (HexLinkData.Bool(parameters, "list") == true)
        {
            List<string> empty = await Task.Run(() => HexLinkDirs.Empty(tree, cancel), cancel);
            answer["empty"] = new JsonArray([.. empty.Select(path => (JsonNode)path)]);
        }

        return answer;
    }
}
