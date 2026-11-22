using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed record HexLinkDraftPresence(int Count, IReadOnlyList<string> Paths)
{
    public const int MostNames = 50;

    public JsonObject ToJson() => new()
    {
        ["count"] = Count,
        ["paths"] = new JsonArray([.. Paths.Take(MostNames).Select(path => (JsonNode)JsonValue.Create(path)!)])
    };

    public static HexLinkDraftPresence Read(string folder, JsonObject? said)
    {
        if (HexLinkData.Int(said, "count") is not int count || count < 0 ||
            said?["paths"] is not JsonArray paths || paths.Count > Math.Min(MostNames, count))
            throw HexLinkException.Invalid("The private worktree presence is invalid.");
        var names = new List<string>(paths.Count);
        foreach (JsonNode? node in paths)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out string? path) || path is null ||
                path.Length > HexLinkFiles.MOST_PATH || !path.StartsWith(folder + "/", StringComparison.Ordinal) ||
                path.Any(char.IsControl))
                throw HexLinkException.Invalid("The private worktree presence contains a name outside its folder.");
            names.Add(path);
        }
        return new(count, names.AsReadOnly());
    }
}
