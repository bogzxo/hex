namespace Hex.Dawn;

internal static class HexBuildProblems
{
    internal static List<TerminalProblem> Read(string output, int exitCode, string? builtFile = null)
    {
        List<TerminalProblem> problems = [];
        var reader = new TerminalProblemReader(TerminalProblemMatcher.Shipped, problems.Add);
        reader.Feed(output.ReplaceLineEndings("\n") + "\n\n\n");
        problems.AddRange(CMakeProblems(output, builtFile));
        if (exitCode != 0) problems.AddRange(LinkerSources(output, builtFile));
        if (exitCode != 0 && DotnetSdkProblem(output, builtFile) is { } sdk) problems.Add(sdk);
        if (exitCode != 0 && builtFile is not null && !problems.Any(problem => !problem.Warning))
            foreach (string line in HexDebugging.LinkErrorsIn(output))
                problems.Add(new TerminalProblem(builtFile, 1, 1, line, false, "link"));
        return problems;
    }

    private static readonly System.Text.RegularExpressions.Regex LinkerHeader = new(
        @"^(?:lld-link|ld\.lld)(?:\.exe)?: error: (?<message>.+)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex LinkerReference = new(
        @"^>>> referenced by (?<file>.+):(?<line>\d+)(?: \((?<full>.+):\d+\))?$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static IEnumerable<TerminalProblem> LinkerSources(string output, string? builtFile)
    {
        string[] lines = output.ReplaceLineEndings("\n").Split('\n');
        HashSet<TerminalProblem> found = [];
        for (int i = 0; i < lines.Length; i++)
        {
            var header = LinkerHeader.Match(lines[i]);
            if (!header.Success) continue;
            string message = header.Groups["message"].Value;
            bool located = false;
            for (int next = i + 1; next < lines.Length && lines[next].StartsWith(">>>", StringComparison.Ordinal); next++)
            {
                var reference = LinkerReference.Match(lines[next]);
                if (!reference.Success || !int.TryParse(reference.Groups["line"].Value, out int number) || number < 1) continue;
                located = true;
                string file = reference.Groups["full"].Success ? reference.Groups["full"].Value : reference.Groups["file"].Value;
                var problem = new TerminalProblem(file, number, 1, message, false, "link");
                if (found.Add(problem)) yield return problem;
            }
            if (!located && builtFile is not null)
            {
                var problem = new TerminalProblem(builtFile, 1, 1, lines[i], false, "link");
                if (found.Add(problem)) yield return problem;
            }
        }
    }

    private static TerminalProblem? DotnetSdkProblem(string output, string? builtFile)
    {
        string[] lines = output.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()).ToArray();
        const string missing = "A compatible .NET SDK was not found.";
        if (!lines.Contains(missing)) return null;
        const string global = "global.json file: ";
        string? file = lines.FirstOrDefault(line => line.StartsWith(global, StringComparison.Ordinal))?[global.Length..];
        if (file is null || !file.EndsWith("global.json", StringComparison.OrdinalIgnoreCase)) file = builtFile;
        if (file is null) return null;
        var details = lines.Where(line => line == missing || line.StartsWith("Requested SDK version:", StringComparison.Ordinal)
            || line.StartsWith("Install the [", StringComparison.Ordinal));
        return new(file, 1, 1, string.Join("\n", details), false, "dotnet-sdk");
    }

    private static readonly System.Text.RegularExpressions.Regex CMakeHeader = new(
        @"^CMake (?<kind>Error|Warning(?: \(dev\))?)(?: at (?<file>.+):(?<line>\d+)(?: \([^)]*\))?| in (?<file>.+))?:\s*(?<message>.*)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static IEnumerable<TerminalProblem> CMakeProblems(string output, string? builtFile)
    {
        string[] lines = output.ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var header = CMakeHeader.Match(lines[i]);
            if (!header.Success) continue;
            string file = header.Groups["file"].Success ? header.Groups["file"].Value : builtFile ?? "";
            if (file.Length == 0) continue;
            // CMake's configuration locations are relative to the source tree, even
            // when regeneration was triggered by a build running in the binary tree.
            if (!Path.IsPathRooted(file) && builtFile is not null && Path.IsPathFullyQualified(builtFile)
                && Path.GetFileName(builtFile).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
                file = Path.GetFullPath(file, Path.GetDirectoryName(builtFile)!);
            List<string> message = [];
            if (header.Groups["message"].Value is { Length: > 0 } first) message.Add(first);
            // CMake indents the diagnostic body. Blank lines can separate paragraphs;
            // unindented status and call-stack headings belong to the following output.
            while (i + 1 < lines.Length && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                message.Add(lines[++i].Trim());
            string text = string.Join("\n", message).Trim();
            if (text.Length == 0) text = header.Value.Trim();
            int line = int.TryParse(header.Groups["line"].Value, out int number) ? number : 1;
            yield return new(file, line, 1, text, header.Groups["kind"].Value.StartsWith("Warning", StringComparison.Ordinal), "cmake");
        }
    }

    internal static void Show(DawnContext context, IReadOnlyList<TerminalProblem> problems, string folder, bool failed)
    {
        context.Terminals.ClearProblems();
        foreach (TerminalProblem problem in problems)
            context.Terminals.AddProblem(Path.IsPathRooted(problem.File) ? problem.File : Path.Combine(folder, problem.File),
                problem.Line, problem.Column, problem.Message, problem.Warning);
        if (failed && problems.Any(problem => !problem.Warning)) context.Panels.ShowProblems();
    }
}
