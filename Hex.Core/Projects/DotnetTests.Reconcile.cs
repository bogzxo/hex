namespace Hex.Core.Projects;

// What a run says the tests are, against what reading the source said. The source is read so there's something to show
// before anything's built, but dotnet test knows for certain, so a test the .trx names that wasn't found goes in under the
// class the .trx says it's in. One found under the wrong namespace (the source fooled discovery somehow) is that one,
// moved to where it really is, rather than a second row of it next to one that never runs
internal static partial class DotnetTests
{
    /// <summary>
    /// The tests of a project with what a run's results say put in. Null when they had nothing to add, the list as it was
    /// is right.
    /// </summary>
    public static List<DotnetTest>? Reconcile(string project, List<DotnetTest> known, IEnumerable<DotnetTestResult> results)
    {
        List<DotnetTestResult> all = [.. results];
        HashSet<string> names = [.. known.Select(one => one.FullName)], ran = [.. all.Select(result => result.Test)];
        List<DotnetTest>? reconciled = null;
        foreach (DotnetTestResult result in all)
        {
            string test = result.Test;
            if (names.Contains(test) || result.Class.Length == 0 || !test.StartsWith(result.Class + ".", StringComparison.Ordinal))
                continue;

            var (ns, inClass) = SplitClass(result.Class);
            string method = test[(result.Class.Length + 1)..];
            reconciled ??= [.. known];

            // The same class and method found in another namespace, and nothing ran by that name, is this one misplaced
            int misplaced = reconciled.FindIndex(one => one.Class == inClass && one.Method == method && !ran.Contains(one.FullName));
            if (misplaced >= 0)
            {
                names.Remove(reconciled[misplaced].FullName);
                reconciled[misplaced] = reconciled[misplaced] with { Namespace = ns };
            }
            else
            {
                // Where it is, the file of its class's other tests or where it failed, the line it failed on if that's the file
                DotnetTest? sibling = reconciled.FirstOrDefault(one => one.FullClass == result.Class);
                string file = sibling?.File ?? result.At?.File ?? string.Empty;
                int line = result.At is { } at && PlatformPaths.Same(at.File, file) ? at.Line : sibling?.Line ?? 0;
                reconciled.Add(new DotnetTest(project, ns, inClass, method, file, line));
            }

            names.Add(test);
        }

        return reconciled;
    }

    /// <summary>A class's full name split into its namespace and its name, Outer+Inner for a nested one.</summary>
    public static (string Namespace, string Class) SplitClass(string fullClass)
    {
        int plus = fullClass.IndexOf('+');
        int dot = (plus < 0 ? fullClass : fullClass[..plus]).LastIndexOf('.');
        return dot < 0 ? (string.Empty, fullClass) : (fullClass[..dot], fullClass[(dot + 1)..]);
    }
}
