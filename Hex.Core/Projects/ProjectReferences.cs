using System.Text.RegularExpressions;

namespace Hex.Core.Projects;

/// <summary>Edits reference expressions without replacing repository path properties with machine paths.</summary>
internal static partial class ProjectReferences
{
    [GeneratedRegex(@"\$\(([A-Za-z_][A-Za-z0-9_]*)\)")]
    private static partial Regex PropertyNames();

    public static async Task Set(string project, string reference, bool add, string configuration, CancellationToken cancel = default)
    {
        project = Path.GetFullPath(project);
        reference = Path.GetFullPath(reference);
        byte[] originalBytes = File.ReadAllBytes(project);
        using var reader = new StreamReader(new MemoryStream(originalBytes), new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        string original = reader.ReadToEnd();
        var encoding = reader.CurrentEncoding;
        string[] properties = PropertyNames().Matches(original).Select(match => match.Groups[1].Value)
            .Concat(["TargetFramework", "TargetFrameworks"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        async Task<List<DotnetEvaluation>> Evaluate()
        {
            async Task<DotnetEvaluation> Read(string? framework = null)
            {
                var args = new List<string> { "msbuild", project, "-nologo", "-p:Configuration=" + configuration, "-getItem:ProjectReference", "-getProperty:" + string.Join(',', properties) };
                if (framework is not null) args.Add("-p:TargetFramework=" + framework);
                var (exit, output) = await DotnetCli.Run(Path.GetDirectoryName(project)!, args, TimeSpan.FromSeconds(90), cancel);
                if (exit != 0) throw new InvalidDataException(output.Trim());
                return DotnetEvaluation.Parse(project, output);
            }
            DotnetEvaluation outer = await Read();
            var states = new List<DotnetEvaluation> { outer };
            if (string.IsNullOrEmpty(outer.Properties.GetValueOrDefault("TargetFramework"))
                && outer.Properties.GetValueOrDefault("TargetFrameworks") is { Length: > 0 } frameworks)
                foreach (string framework in frameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    states.Add(await Read(framework));
            return states;
        }
        List<DotnetEvaluation> before = await Evaluate();
        bool Has(List<DotnetEvaluation> evaluation) => evaluation.SelectMany(state => state.Items).Any(item => item.Type == "ProjectReference" && PlatformPaths.Same(item.FullPath, reference));
        if (Has(before) == add) return;
        if (PlatformPaths.Same(project, reference)) throw new InvalidOperationException("A project cannot reference itself.");
        bool Matches(string include) => before.Any(state => MatchesIn(include, state));
        bool MatchesIn(string include, DotnetEvaluation state)
        {
            string expanded = PropertyNames().Replace(include, match => state.Properties.GetValueOrDefault(match.Groups[1].Value) ?? match.Value);
            if (expanded.Contains("$(", StringComparison.Ordinal) || expanded.IndexOfAny(['*', '?', ';']) >= 0) return false;
            return PlatformPaths.Same(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, expanded.Replace('\\', Path.DirectorySeparatorChar))), reference);
        }
        string changed;
        if (add)
        {
            // Reuse a directory property already used by this project's reference
            // expressions. Its alternate value (e.g. UseSubmodules) stays meaningful.
            var names = ProjectFileEdits.ReferenceItems(ProjectXml.Read(original)!)
                .SelectMany(item => PropertyNames().Matches(item.Attribute("Include")?.Value ?? string.Empty).Select(match => match.Groups[1].Value))
                .Concat(properties.Where(name => name.EndsWith("Root", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Directory", StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            string include = DotnetProjects.RelativeTo(project, reference);
            int longest = 0;
            foreach (string name in names)
            foreach (var state in before)
            {
                string value = state.Properties.GetValueOrDefault(name) ?? string.Empty;
                if (!value.EndsWith('/') && !value.EndsWith('\\')) continue;
                string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, value.Replace('\\', Path.DirectorySeparatorChar)));
                if (root.Length <= longest || !PlatformPaths.IsUnder(reference, root)) continue;
                longest = root.Length;
                include = "$" + "(" + name + ")" + Path.GetRelativePath(root, reference).Replace('/', '\\');
            }
            changed = ProjectFileEdits.AddProjectReference(original, include);
        }
        else
        {
            changed = ProjectFileEdits.RemoveProjectReference(original, Matches);
            if (changed == original)
                throw new InvalidOperationException("This reference is supplied by an import or a computed item list. Edit its defining project instead; no files were changed.");
        }
        cancel.ThrowIfCancellationRequested();
        if (!File.ReadAllBytes(project).SequenceEqual(originalBytes))
            throw new IOException("The project changed while the reference was being evaluated. Retry with the current file.");
        DocumentFile.Write(project, changed, encoding);
        byte[] written = [.. encoding.GetPreamble(), .. encoding.GetBytes(changed)];
        try
        {
            List<DotnetEvaluation> after = await Evaluate();
            var others = before.SelectMany(state => state.Items).Where(item => item.Type == "ProjectReference" && !PlatformPaths.Same(item.FullPath, reference))
                .Select(item => item.FullPath).ToHashSet(PlatformPaths.Comparer);
            if (Has(after) != add || !others.SetEquals(after.SelectMany(state => state.Items).Where(item => item.Type == "ProjectReference" && !PlatformPaths.Same(item.FullPath, reference)).Select(item => item.FullPath)))
                throw new InvalidOperationException("MSBuild did not accept the requested reference change.");
        }
        catch
        {
            // Never overwrite an intervening editor/external change during rollback.
            if (File.ReadAllBytes(project).SequenceEqual(written)) DocumentFile.Write(project, original, encoding);
            throw;
        }
        finally { DotnetProjects.Shared.Forget(project); }
    }
}
