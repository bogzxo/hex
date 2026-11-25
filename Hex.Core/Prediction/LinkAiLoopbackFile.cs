using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>What the loopback file says: the address and token of the endpoint, and the linked models it lists.</summary>
internal sealed record LinkAiLoopbackInfo(int Pid, string Url, string Token, IReadOnlyList<LinkAiLoopbackModel> Models);

/// <summary>
/// The loopback endpoint's address, token and models (<c>link-ai/loopback.json</c> in Hex's data folder, readable by this user
/// only) kept for the processes Hex starts that can't hold a Link connection themselves: the Local agent reads it to list the
/// linked models and to ask them. It names the Hex that wrote it by process id, and a file whose Hex has gone is no file.
/// </summary>
internal static class LinkAiLoopbackFile
{
    /// <summary>Where the file is, in the data folder of this Hex (so two Hexes with their own homes keep their own).</summary>
    public static string Path => System.IO.Path.Combine(HexData.Root, "link-ai", "loopback.json");

    /// <summary>The file written for the endpoint and the models it lists now.</summary>
    public static void Write(LinkAiLoopback endpoint, IReadOnlyList<LinkAiLoopbackModel> models)
    {
        try
        {
            var written = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["url"] = endpoint.Url,
                ["token"] = endpoint.Token,
                ["models"] = new JsonArray([.. models.Select(model => (JsonNode)new JsonObject { ["id"] = model.Id, ["device"] = model.Device, ["name"] = model.Name })])
            };
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            HexLinkIdentity.WriteUserOnly(Path, Encoding.UTF8.GetBytes(written.ToJsonString()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without the file only the Local agent can't see the linked models, nothing else depends on it
        }
    }

    /// <summary>Takes the file away, when this Hex wrote it.</summary>
    public static void Delete()
    {
        try
        {
            if (Read(any: true) is { } info && info.Pid == Environment.ProcessId)
                File.Delete(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The file's content when the Hex that wrote it is still running, null otherwise.</summary>
    public static LinkAiLoopbackInfo? Read(bool any = false)
    {
        try
        {
            if (!File.Exists(Path) || JsonNode.Parse(File.ReadAllText(Path)) is not JsonObject root)
                return null;
            int pid = HexLinkData.Int(root, "pid") ?? 0;
            string url = HexLinkData.String(root, "url", 200) ?? string.Empty, token = HexLinkData.String(root, "token", 200) ?? string.Empty;
            if (pid <= 0 || url.Length == 0 || token.Length == 0)
                return null;
            if (!any && !Alive(pid))
                return null;
            var models = new List<LinkAiLoopbackModel>();
            foreach (JsonObject each in HexLinkData.Objects(root, "models", HexLinkAiModel.MOST_MODELS))
                if (HexLinkData.String(each, "id", HexLinkAiRequest.LONGEST_MODEL + 80) is { Length: > 0 } id)
                    models.Add(new LinkAiLoopbackModel(id, HexLinkData.String(each, "device", 120) ?? string.Empty, HexLinkData.String(each, "name", 120) ?? string.Empty));
            return new LinkAiLoopbackInfo(pid, url, token, models);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool Alive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
