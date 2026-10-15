using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Horizon.Hex;

// What Hex can say is wrong with a file from what it comes to, without anybody writing down what it should look like.
// There is no schema for a move list, the shape is in a comment at the top and in the moves themselves, so the moves are
// asked. The objects in one object are taken to be the same kind of thing, and a key only one of them has that is a
// letter or two off a key most of them have is a typo nine times in ten. And a text that looks like the path of a file
// (a slash and an extension) that isn't there from the file's folder or any folder over it is a warning, the game reads
// them from its own folder and Fighter2D's characters name their sprites and moves that way.
internal sealed partial class HexScene
{
    // How many objects an object needs before its keys say what its objects look like
    private const int SIBLINGS = 3;

    // How far up from a file a path in it is looked for
    private const int PATH_LEVELS = 8;

    /// <summary>What the last run of a file says is wrong with it, as warnings under its code.</summary>
    private List<UICodeDiagnostic> Lint(HexValues ran, HexDocument of)
    {
        var found = new List<UICodeDiagnostic>();
        string? folder = of.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path)) : null;
        string? file = of.Path is { } at ? Path.GetFullPath(at) : null;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        bool Here(SourceLocation at) => at.File is null || (file is not null && string.Equals(Path.GetFullPath(at.File), file, StringComparison.OrdinalIgnoreCase));

        void Warn(SourceLocation at, string message)
        {
            if (Here(at) && at.Span.IsKnown && found.Count < 200)
                found.Add(new UICodeDiagnostic(at.Span.Line - 1, at.Span.Column - 1, Math.Max(1, at.Span.Length), message, Warning: true));
        }

        void Walk(IRuntimeValue value, SourceLocation? at)
        {
            switch (value)
            {
                case ObjectValue obj when seen.Add(obj.Properties):
                    Siblings(ran, obj, Warn);
                    foreach (var (key, inner) in obj.Properties)
                        Walk(inner, ran.Sources.TryGetKey(obj, key, out var keyAt) ? keyAt : null);
                    break;

                case ListValue list when seen.Add(list):
                    for (int i = 0; i < list.Count; i++)
                        Walk(list[i], ran.Sources.TryGetItem(list, i, out var itemAt) ? itemAt : null);
                    break;

                case StringValue text when at is { Literal: true } written && folder is not null && LooksLikePath(text.Value) && !PathExists(folder, text.Value):
                    // The text itself is underlined, not its key
                    Warn(written with { Span = written.Value }, Said("lint.no_file", ("path", text.Value)));
                    break;
            }
        }

        foreach (var (name, value) in ran.Declared)
            Walk(value, ran.Sources.TryGetDeclaration(name, out var declared) ? declared : null);

        return found;
    }

    /// <summary>
    /// Helper method to warn about a key only one of the objects in an object has, when it is a letter or two off one most
    /// of them have.
    /// </summary>
    private void Siblings(HexValues ran, ObjectValue parent, Action<SourceLocation, string> warn)
    {
        int objects = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (IRuntimeValue child in parent.Properties.Values)
        {
            if (child is not ObjectValue obj)
                continue;

            objects++;
            foreach (string key in obj.Properties.Keys)
                counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        if (objects < SIBLINGS)
            return;

        var common = counts.Where(pair => pair.Value * 2 >= objects).Select(pair => pair.Key).ToList();
        foreach (var (childName, child) in parent.Properties)
        {
            if (child is not ObjectValue obj)
                continue;

            foreach (string key in obj.Properties.Keys)
            {
                if (counts[key] != 1 || common.Contains(key))
                    continue;

                // Close to a key most of the others have and this one hasn't
                string? meant = common.FirstOrDefault(other => !obj.Properties.ContainsKey(other) && Distance(key, other) <= (other.Length >= 6 ? 2 : 1));
                if (meant is not null && ran.Sources.TryGetKey(obj, key, out var at))
                    warn(at, Said("lint.typo", ("key", key), ("meant", meant), ("count", counts[meant].ToString()), ("of", objects.ToString()), ("name", childName)));
            }
        }
    }

    /// <summary>Whether a text is the path of a file by the look of it, a slash and an extension and no spaces.</summary>
    private static bool LooksLikePath(string text) =>
        text.Length is > 3 and < 260 && (text.Contains('/') || text.Contains('\\')) && !text.Contains(' ') && !text.Contains("://")
        && Path.HasExtension(text) && !text.StartsWith('@');

    /// <summary>Whether a path is there from a folder or any folder over it, the way a game finds its files from wherever it runs.</summary>
    private static bool PathExists(string folder, string path)
    {
        if (Path.IsPathRooted(path))
            return File.Exists(path) || Directory.Exists(path);

        string? at = folder;
        for (int level = 0; at is not null && level < PATH_LEVELS; level++, at = Path.GetDirectoryName(at))
        {
            string full = Path.Combine(at, path);
            if (File.Exists(full) || Directory.Exists(full))
                return true;
        }

        return false;
    }

    /// <summary>How many letters have to change to turn one word into another (Levenshtein), stopping early past three.</summary>
    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 3)
            return 4;

        Span<int> previous = stackalloc int[b.Length + 1];
        Span<int> current = stackalloc int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            current.CopyTo(previous);
        }

        return previous[b.Length];
    }
}
