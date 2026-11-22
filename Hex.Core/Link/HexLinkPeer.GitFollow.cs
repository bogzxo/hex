using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkPeer
{
    internal event Action? GitFollowHint;
    internal async Task<(Dictionary<string, HexLinkHeadTip> Tips, Dictionary<string, string> Issues)> GitFollowTips(
        string folder, IReadOnlyList<string> paths, CancellationToken cancel)
    {
        JsonObject response = await AskObject("git/bundle", new JsonObject
        {
            ["operation"] = "follow_tips", ["folder"] = folder,
            ["paths"] = new JsonArray([.. paths.Select(path => (JsonNode)path)])
        }, cancel, TimeSpan.FromSeconds(30));
        if (HexLinkData.Object(response, "tips") is not { } tips)
            throw HexLinkException.Invalid("The other device returned no Git tips.");
        var result = new Dictionary<string, HexLinkHeadTip>(StringComparer.Ordinal);
        var issues = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonObject? problems = HexLinkData.Object(response, "issues");
        foreach (string path in paths)
        {
            if (HexLinkHeadTip.Of(HexLinkData.Object(tips, path)) is { } tip)
                result[path] = tip;
            else if (problems is not null && HexLinkData.String(problems, path, 512) is { Length: > 0 } why)
                issues[path] = why;
        }
        return (result, issues);
    }

    internal Task<JsonObject> StopGitFollow(string folder, CancellationToken cancel) =>
        AskObject("git/bundle", new JsonObject { ["operation"] = "follow_stop", ["folder"] = folder }, cancel);

    internal Task<JsonObject> ConfirmGitFollow(string folder, string path, HexLinkHeadTip there, bool common, CancellationToken cancel) =>
        AskObject("git/bundle", new JsonObject
        {
            ["operation"] = "follow_confirm", ["folder"] = folder, ["path"] = path,
            ["expected"] = there.ToJson(), ["common"] = common
        }, cancel);

    internal async Task<HexLinkHeadMove> AdoptGitFollow(string folder, string path, HexLinkHeadTip there, HexLinkHeadTip here, CancellationToken cancel)
    {
        JsonObject response = await AskObject("git/bundle", new JsonObject
        {
            ["operation"] = "follow_adopt", ["folder"] = folder, ["path"] = path,
            ["expected"] = there.ToJson(), ["target"] = here.ToJson()
        }, cancel, TimeSpan.FromMinutes(15));
        return new(HexLinkData.String(response, "backup", 100), HexLinkData.Bool(response, "rewrite") == true,
            HexLinkData.String(response, "why", 1024));
    }
}
