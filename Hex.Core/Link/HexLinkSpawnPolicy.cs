using System.Text.Json.Nodes;

namespace Hex.Core;

internal enum HexLinkPreApproval { Nothing, Edits, Everything }

internal static class HexLinkSpawnPolicy
{
    internal static string Word(HexLinkPreApproval policy) => policy switch
    {
        HexLinkPreApproval.Nothing => "nothing",
        HexLinkPreApproval.Everything => "everything",
        _ => "edits"
    };

    internal static HexLinkPreApproval Read(string? word) => word switch
    {
        "nothing" => HexLinkPreApproval.Nothing,
        "everything" => HexLinkPreApproval.Everything,
        _ => HexLinkPreApproval.Edits
    };

    internal static string? Apply(JsonObject arguments, string agent, HexLinkPreApproval policy)
    {
        if (policy == HexLinkPreApproval.Everything)
            return null;

        bool edits = policy == HexLinkPreApproval.Edits;
        var options = arguments["options"] as JsonObject ?? new JsonObject();
        foreach (var (key, value) in options)
        {
            string? chosen = value is JsonValue scalar && scalar.TryGetValue(out string? text) ? text : null;
            bool allowed = key switch
            {
                "model" or "effort" => chosen is not null,
                "permission_mode" => chosen is "default" or "plan" || edits && chosen == "acceptEdits",
                "sandbox" => chosen == "read-only" || edits && chosen == "workspace-write",
                "mode" => chosen == "plan" || edits && chosen == "accept-edits",
                _ => false
            };
            if (!allowed)
                return $"Remote starters may pre-approve {Word(policy)} here. This device does not allow that {key} option.";
        }

        if (arguments["permissions"] is JsonObject permissions && permissions["allow"] is JsonArray allow)
        {
            foreach (JsonNode? rule in allow)
            {
                string? name = rule is JsonValue scalar && scalar.TryGetValue(out string? text) ? text?.Trim() : null;
                if (!edits || name is not ("Edit" or "Write" or "MultiEdit" or "NotebookEdit"))
                    return $"Remote starters may pre-approve {Word(policy)} here. This device does not allow that allow rule.";
            }
        }

        // A provider's local default can be broader than the policy of the linked starter.
        switch (agent)
        {
            case "claude_code":
                options["permission_mode"] ??= edits ? "acceptEdits" : "default";
                break;
            case "codex":
                options["sandbox"] ??= edits ? "workspace-write" : "read-only";
                break;
            case "antigravity":
                options["mode"] ??= edits ? "accept-edits" : "plan";
                break;
        }

        if (options.Count > 0 && options.Parent is null)
            arguments["options"] = options;
        return null;
    }
}
