using System.Text.Json.Nodes;

namespace Hex.Core;

internal interface IHexLinkRepositoryReady
{
    void RepositoryReady(HexLinkDevice device, string folder, string path);
}

internal sealed partial class HexLinkPeer
{
    internal Task TellRepositoryReady(string folder, string path, CancellationToken cancel) =>
        AskObject("git/bundle", new JsonObject { ["operation"] = "repository_ready", ["folder"] = folder, ["path"] = path }, cancel);
}

internal sealed partial class HexLinkHost
{
    private JsonObject RepositoryReady(JsonObject parameters)
    {
        string folder = HexLinkData.Need(parameters, "folder", 200);
        string path = HexLinkData.Need(parameters, "path", HexLinkFiles.MOST_PATH);
        if (services is IHexLinkRepositoryReady listener)
            listener.RepositoryReady(Device, folder, path);
        return new JsonObject { ["ok"] = true };
    }
}
