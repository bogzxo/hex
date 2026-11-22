namespace Hex.Core;

/// <summary>The root .hex-link-ignore of a shared folder, refreshed when it changes on disk.</summary>
internal sealed class HexLinkIgnore(string root)
{
    internal const string FileName = ".hex-link-ignore";

    private static readonly Lock RootsGate = new();
    private static readonly Dictionary<string, WeakReference<HexLinkIgnore>> Roots = new(PlatformPaths.Comparer);

    private readonly Lock gate = new();
    private readonly string path = Path.Combine(root, FileName);
    private (long Length, DateTime Written, DateTime Created)? stamp;
    private HexIgnoreFile? rules;

    internal static HexLinkIgnore ForRoot(string root)
    {
        lock (RootsGate)
        {
            if (Roots.TryGetValue(root, out var found) && found.TryGetTarget(out HexLinkIgnore? existing))
                return existing;
            if (Roots.Count > 128)
            {
                foreach (var (key, value) in Roots.Where(entry => !entry.Value.TryGetTarget(out _)).ToArray())
                    Roots.Remove(key);
            }
            var policy = new HexLinkIgnore(root);
            Roots[root] = new(policy);
            return policy;
        }
    }

    internal static bool IsRulesFile(string relative) => relative.Equals(FileName, PlatformPaths.Comparison);

    internal static bool Matches(HexIgnoreFile? rules, string relative, bool folder = false) =>
        rules is not null && (folder || !IsRulesFile(relative)) && rules.Excludes(relative, folder);

    internal void Invalidate()
    {
        lock (gate)
            stamp = null;
    }

    internal HexIgnoreFile? Read()
    {
        lock (gate)
        {
            var info = new FileInfo(path);
            FileAttributes attributes = info.Attributes;
            if (attributes == (FileAttributes)(-1) || (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                stamp = null;
                return rules = null;
            }

            var current = (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
            if (stamp != current)
            {
                rules = HexIgnoreFile.Of(File.ReadLines(path), ignoreCase: PlatformPaths.Comparison == StringComparison.OrdinalIgnoreCase);
                stamp = current;
            }
            return rules;
        }
    }
}
