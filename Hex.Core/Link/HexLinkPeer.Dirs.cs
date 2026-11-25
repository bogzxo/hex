using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>What the other machine said of a folder's empty directories.</summary>
internal sealed record HexLinkRemoteDirs(IReadOnlyList<string> Empty, IReadOnlyList<string> Present, IReadOnlyList<string> Made, IReadOnlyList<string> Removed);

internal sealed partial class HexLinkPeer
{
    /// <summary>
    /// A folder of the other machine's empty directories (View), which of <paramref name="check"/> exist there (View), and
    /// what <paramref name="make"/> and <paramref name="remove"/> did (Edit, never more than what it leaves out of sync).
    /// </summary>
    internal async Task<HexLinkRemoteDirs> Directories(string folder, bool list, IReadOnlyCollection<string> check,
        IReadOnlyCollection<string> make, IReadOnlyCollection<string> remove, CancellationToken cancel)
    {
        var parameters = new JsonObject { ["folder"] = folder };
        if (list)
            parameters["list"] = true;
        Put("check", check);
        Put("make", make);
        Put("remove", remove);
        JsonObject said = await AskObject("sync/dirs", parameters, cancel, TimeSpan.FromSeconds(60));
        return new HexLinkRemoteDirs(
            HexLinkData.Strings(said, "empty", HexLinkDirs.MOST, HexLinkFiles.MOST_PATH),
            HexLinkData.Strings(said, "present", HexLinkDirs.MOST, HexLinkFiles.MOST_PATH),
            HexLinkData.Strings(said, "made", HexLinkDirs.MOST, HexLinkFiles.MOST_PATH),
            HexLinkData.Strings(said, "removed", HexLinkDirs.MOST, HexLinkFiles.MOST_PATH));

        void Put(string name, IReadOnlyCollection<string> paths)
        {
            if (paths.Count > 0)
                parameters[name] = new JsonArray([.. paths.Take(HexLinkDirs.MOST).Select(path => (JsonNode)path)]);
        }
    }
}
