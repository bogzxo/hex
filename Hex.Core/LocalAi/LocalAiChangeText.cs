namespace Hex.Core;

/// <summary>
/// The text a <see cref="LocalAiSetupChange"/> is shown as in the Local AI window (the "Use this setup" confirmation and the
/// "Custom (based on ...)" differences): the values are keys, ids and settings words, so each is resolved to what it means to a person.
/// </summary>
internal static class LocalAiChangeText
{
    /// <summary>The what-changes label, e.g. "Engine".</summary>
    public static string What(LocalAiSetupChange change, Func<string, string> said) => said(change.WhatKey);

    /// <summary>What it is now, readable.</summary>
    public static string FromText(LocalAiSetupChange change, Func<string, string> said) => Value(change.WhatKey, change.From, said);

    /// <summary>What it would become, readable.</summary>
    public static string ToText(LocalAiSetupChange change, Func<string, string> said) => Value(change.WhatKey, change.To, said);

    /// <summary>One line: "what: from → to" (the way the confirmation reads, from the current to the new).</summary>
    public static string Line(LocalAiSetupChange change, Func<string, string> said) =>
        $"{What(change, said)}: {FromText(change, said)} → {ToText(change, said)}";

    /// <summary>One line the way the custom list reads, what the setup would set, then what it is now.</summary>
    public static string ReversedLine(LocalAiSetupChange change, Func<string, string> said) =>
        $"{What(change, said)}: {ToText(change, said)} → {FromText(change, said)}";

    private static string Value(string whatKey, string value, Func<string, string> said)
    {
        // A text key (a profile's title, "Edited by hand") is looked up whatever the change is
        if (value.StartsWith("local_ai_profiles.", StringComparison.Ordinal) || value.StartsWith("local_ai_setups.", StringComparison.Ordinal))
            return said(value);
        return whatKey switch
        {
            "local_ai_setups.change_invocation" => said("local_ai.invocation_" + value),
            "local_ai_setups.change_mode" => said("local_ai.mode_" + value),
            "local_ai_setups.change_renames" or "local_ai_setups.change_far" or "local_ai_setups.change_autostart" => said(value == "on" ? "local_ai.on" : "local_ai.off"),
            "local_ai_setups.change_engine" => said("local_ai.engine_" + value),
            // Models are names already, and a role with no model says so
            _ => value.Length == 0 ? said("local_ai.none") : value,
        };
    }
}
