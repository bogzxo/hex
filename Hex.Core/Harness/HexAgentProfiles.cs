namespace Hex.Core;

/// <summary>
/// An agent system as an extension says it is with hex.agent, Claude Code or Codex or whatever's next. It's picked for a
/// connection by what the client calls itself in its initialize, any of <paramref name="Clients"/> being in that name.
/// </summary>
/// <param name="Extension">The id of the extension that said it, which takes it away again when it's switched off.</param>
/// <param name="Budget">The characters a harness answer stays under for it when a call doesn't say, 0 for the usual.</param>
/// <param name="Instructions">What its agent is told as it connects, after what Hex tells every agent.</param>
internal sealed record HexAgentProfile(string Id, string Name, string[] Clients, int Budget, string Instructions, string Extension);

/// <summary>
/// The agent profiles there are, said by extensions on the editor's thread and read from the pipe's as clients connect
/// and call, so it's swapped whole under a lock and read without one.
/// </summary>
internal static class HexAgentProfiles
{
    /// <summary>What a harness answer stays under when neither the call nor a profile says.</summary>
    public const int USUAL_BUDGET = 8000;

    private static readonly Lock changing = new();
    private static volatile HexAgentProfile[] all = [];

    public static IReadOnlyList<HexAgentProfile> All => all;

    /// <summary>A profile said, one with the same id put in its place (where it was, so the panel's chips don't move about).</summary>
    public static void Set(HexAgentProfile profile)
    {
        lock (changing)
            all = all.Any(each => each.Id == profile.Id) ? [.. all.Select(each => each.Id == profile.Id ? profile : each)] : [.. all, profile];
    }

    /// <summary>Every profile an extension said taken away, as it's switched off or read again.</summary>
    public static void Forget(string extension)
    {
        lock (changing)
            all = [.. all.Where(each => each.Extension != extension)];
    }

    /// <summary>The profile for a client by what it said it was ("claude-code 2.1.0"), null for one nobody said.</summary>
    public static HexAgentProfile? For(string? client)
    {
        if (string.IsNullOrEmpty(client))
            return null;

        return all.FirstOrDefault(profile => profile.Clients.Any(name => name.Length > 0 && client.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The budget for a call, what it asked for, the client's profile's, or the usual.</summary>
    public static int BudgetFor(string? client, int? asked) =>
        Math.Clamp(asked ?? (For(client) is { Budget: > 0 } profile ? profile.Budget : USUAL_BUDGET), 400, 200_000);

    /// <summary>What a client is told as it connects, Hex's own and its profile's after it.</summary>
    public static string InstructionsFor(string? client, string usual) =>
        For(client) is { Instructions.Length: > 0 } profile ? $"{usual}\n\n{profile.Name}: {profile.Instructions}" : usual;
}
