using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

// What an extension finds wrong with a file, in the problems panel and underlined in the code with the rest of what is
// wrong with it. hex.problems(file, [{ path: ["moves", "jab", "hit"], message, warning }, { line, column, message }])
// replaces whatever that extension said about that file before, [] says nothing's wrong. A path is placed where its key
// is written, a line and a column are counted from 1.
internal sealed partial class HexScene
{
    // What the extensions said, by the full path of the file and then by who said it
    private readonly Dictionary<string, Dictionary<HexExtension, List<UICodeDiagnostic>>> extensionFound = new(StringComparer.OrdinalIgnoreCase);

    private (string, IRuntimeValue)[] Problems(HexExtension extension) =>
    [
        ("problems", Natives.Function("problems", args =>
        {
            string? why = SetProblems(extension, args[0], args[1]);
            return why is null ? Values.Null : new StringValue(why);
        }))
    ];

    /// <summary>Helper method for hex.problems, null when they were put up, why not otherwise.</summary>
    private string? SetProblems(HexExtension extension, IRuntimeValue file, IRuntimeValue said)
    {
        string? full = file is StringValue { Value.Length: > 0 } named ? PathFor(extension, named.Value) : document.Path is { } here ? Path.GetFullPath(here) : null;
        if (full is null)
            return "the file on screen has never been saved, its problems have nowhere to go";
        if (said is not ListValue list)
            return "hex.problems takes a list of problems";

        string text = OpenDocument(full)?.Editor.Text ?? (File.Exists(full) ? File.ReadAllText(full).ReplaceLineEndings("\n") : string.Empty);
        HexSourceTree? tree = null;

        var found = new List<UICodeDiagnostic>();
        foreach (IRuntimeValue item in list.Items)
        {
            if (item is not ObjectValue problem)
                continue;

            string message = $"{extension.Name}: {Text(problem, "message") ?? "something's wrong"}";
            bool warning = Flag(problem, "warning") ?? false;
            if (problem.Properties.TryGetValue("path", out IRuntimeValue? path))
            {
                try
                {
                    tree ??= HexSourceTree.Read(text, full, ReadInclude);
                }
                catch (Exception)
                {
                    // A file that doesn't run says so itself, its problems wait for it to run again
                    return null;
                }

                if (tree.Find(HexValueEdits.PathOf(path), out _) is { } node && (node.At.Span.IsKnown ? node.At.Span : node.At.Value) is { IsKnown: true } at)
                    found.Add(new UICodeDiagnostic(at.Line - 1, at.Column - 1, Math.Max(1, at.Length), message, warning));
            }
            else
            {
                int line = (int)(Number(problem, "line") ?? 1), column = (int)(Number(problem, "column") ?? 1);
                found.Add(new UICodeDiagnostic(Math.Max(0, line - 1), Math.Max(0, column - 1), 1, message, warning));
            }
        }

        if (!extensionFound.TryGetValue(full, out var byExtension))
            extensionFound[full] = byExtension = [];
        byExtension[extension] = found;

        if (OpenDocument(full) is { } open)
            ShowDiagnostics(open);
        problemsDirty = true;
        return null;
    }

    /// <summary>What the extensions found wrong with a file, for its diagnostics and the problems panel.</summary>
    private IEnumerable<UICodeDiagnostic> ExtensionProblemsOf(string? path)
    {
        if (path is null || !extensionFound.TryGetValue(Path.GetFullPath(path), out var byExtension))
            yield break;

        foreach (var (by, found) in byExtension)
        {
            if (!by.Enabled || by.Failed is not null)
                continue;

            foreach (UICodeDiagnostic problem in found)
                yield return problem;
        }
    }

    /// <summary>What the extensions found wrong with the files that aren't on screen, for the problems panel.</summary>
    private IEnumerable<(string Path, UICodeDiagnostic Problem)> ExtensionProblemsElsewhere()
    {
        string? here = document.Path is { } path ? Path.GetFullPath(path) : null;
        foreach (string file in extensionFound.Keys)
        {
            if (here is not null && string.Equals(file, here, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (UICodeDiagnostic problem in ExtensionProblemsOf(file))
                yield return (file, problem);
        }
    }
}
