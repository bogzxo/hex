namespace Hex.Dawn;

/// <summary>
/// What language servers last said is wrong with each file for the agent tools, how many times they've said it and when
/// (a tick, Environment.TickCount64). A check after an edit waits for something said after the edit was sent, not merely
/// anything new, so what a server says of the text before an edit isn't taken for what it says of the edit.
/// </summary>
internal sealed class HexHeardLog
{
    public readonly record struct Heard(int Count, IReadOnlyList<UICodeDiagnostic>? Found, string? Server, long At);

    private readonly Dictionary<string, Heard> heard = new(PlatformPaths.Comparer);

    public bool Has(string path) => heard.ContainsKey(path);

    public Heard Of(string path) => heard.TryGetValue(path, out Heard said) ? said : default;

    /// <summary>A server said what's wrong with a file at a moment.</summary>
    public void Record(string path, IReadOnlyList<UICodeDiagnostic> found, string server, long now) =>
        heard[path] = new Heard(Of(path).Count + 1, found, server, now);

    /// <summary>Whether a server's said anything of a file at or after a moment, the moment an edit was sent to it.</summary>
    public bool SaidSince(string path, long since) => heard.TryGetValue(path, out Heard said) && said.Count > 0 && said.At >= since;
}
