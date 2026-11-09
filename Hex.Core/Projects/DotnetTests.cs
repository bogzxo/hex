using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Hex.Core.Projects;

/// <summary>A test found in the source, its project, the class it's in (with its namespace) and where it's declared, lines from 0.</summary>
internal sealed record DotnetTest(string Project, string Namespace, string Class, string Method, string File, int Line)
{
    /// <summary>An opaque execution ID for native runners such as pytest.</summary>
    public string? NativeName { get; init; }
    /// <summary>The native execution ID, or Namespace.Class.Method for a .NET test.</summary>
    public string FullName => NativeName ?? (Namespace.Length > 0 ? $"{Namespace}.{Class}.{Method}" : $"{Class}.{Method}");

    /// <summary>The class with its namespace, what a filter for the whole class goes by.</summary>
    public string FullClass => Namespace.Length > 0 ? $"{Namespace}.{Class}" : Class;

    /// <summary>How many cases its source gives it (its InlineData, TestCase or DataRow), 0 when it doesn't say, a Fact or a theory of MemberData.</summary>
    public int Cases { get; init; }
}

/// <summary>How a test came out the last time it ran.</summary>
internal enum DotnetTestOutcome { NotRun, Passed, Failed, Skipped }

/// <summary>A test's result out of a .trx, what failed and where, and how long it took.</summary>
internal sealed record DotnetTestResult(string FullName, DotnetTestOutcome Outcome, TimeSpan Duration, string Message, string Stack, string Output)
{
    /// <summary>The evaluated target framework of this execution, empty for a runner with no framework variants.</summary>
    public string Framework { get; init; } = string.Empty;
    /// <summary>The class the .trx says the test is in, with its namespace (Outer+Inner for a nested one), empty when it didn't say.</summary>
    public string Class { get; init; } = string.Empty;

    /// <summary>Where a failed test failed, the first frame of its stack that's a file on the disk, the line from 0.</summary>
    public (string File, int Line)? At { get; init; }

    /// <summary>The test it's a result of, its full name without a theory case's arguments.</summary>
    public bool ExactName { get; init; }
    public string Test => ExactName ? FullName : DotnetTests.WithoutArguments(FullName);

    /// <summary>A theory's case, which has its arguments on its name.</summary>
    public bool IsCase => !ExactName && FullName.IndexOf('(') > 0;
}

// The tests of a workspace found the way VS Code's C# Dev Kit finds them before a build, off the source. A project that
// references a test framework is a test project, and in it a method with [Fact], [Theory], [Test], [TestCase] or
// [TestMethod] over it is a test. Reading the source is quick and needs no build, which is what the explorer wants to
// show at once, and dotnet test says how they came out in a .trx that's read back here
internal static partial class DotnetTests
{
    // What makes a project a test project, what it references
    private static readonly string[] Frameworks = ["xunit", "nunit", "MSTest", "Microsoft.NET.Test.Sdk", "TUnit"];

    /// <summary>Whether a project file references a test framework.</summary>
    public static bool IsTestProject(string projectText) =>
        Frameworks.Any(framework => projectText.Contains($"\"{framework}", StringComparison.OrdinalIgnoreCase));

    /// <summary>The test projects under some folders, skipping bin, obj and dot folders.</summary>
    public static List<string> TestProjects(IEnumerable<string> roots, string? solution = null)
    {
        var found = new List<string>();
        if (solution is not null)
        {
            // An explicit solution owns its project set. Never silently substitute a
            // recursive folder scan (which includes nested submodule test copies).
            foreach (string project in DotnetSolutionTree.Read(solution)?.Projects.Select(project => project.Path).Distinct(PlatformPaths.Comparer) ?? [])
                if (File.Exists(project) && IsTestProject(File.ReadAllText(project)))
                    found.Add(project);
            return found;
        }
        foreach (string root in roots)
        {
            foreach (string project in ProjectsUnder(root, 0))
            {
                try
                {
                    if (IsTestProject(File.ReadAllText(project)))
                        found.Add(project);
                }
                catch (IOException)
                {
                }
            }
        }

        return found;
    }

    // Project files a few folders down, the way a solution lays them out
    private static IEnumerable<string> ProjectsUnder(string folder, int depth)
    {
        string[] projects, folders;
        try
        {
            projects = Directory.GetFiles(folder, "*.csproj");
            folders = depth < 4 ? Directory.GetDirectories(folder) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string project in projects)
            yield return project;

        foreach (string under in folders)
        {
            string name = Path.GetFileName(under);
            if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules")
                continue;

            foreach (string project in ProjectsUnder(under, depth + 1))
                yield return project;
        }
    }

    /// <summary>The tests of a project, out of every .cs file under its folder.</summary>
    public static List<DotnetTest> Discover(string project)
    {
        var tests = new List<DotnetTest>();
        string folder = Path.GetDirectoryName(project)!;
        foreach (string file in SourcesUnder(folder))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            if (TestAttribute().IsMatch(text))
                tests.AddRange(Find(project, file, text));
        }

        return tests;
    }

    // The .cs files of a project's folder, not its bin or obj
    private static IEnumerable<string> SourcesUnder(string folder)
    {
        var stack = new Stack<string>([folder]);
        while (stack.Count > 0)
        {
            string at = stack.Pop();
            string[] files, folders;
            try
            {
                files = Directory.GetFiles(at, "*.cs");
                folders = Directory.GetDirectories(at);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
                yield return file;

            foreach (string under in folders)
            {
                string name = Path.GetFileName(under);
                if (!name.StartsWith('.') && name is not ("bin" or "obj"))
                    stack.Push(under);
            }
        }
    }

    /// <summary>
    /// The tests one file declares. The namespace (file scoped or blocks of them), the class each method is in and the
    /// line it's on, by reading the code with its strings and comments blanked (CodeOnly), so a namespace line in a test's
    /// raw string fixture is nobody's namespace. A nested class goes by Outer+Inner, the name dotnet test gives it. Every
    /// declaration, attribute and brace of a line is taken in the order it comes, two tests on one line are two tests.
    /// </summary>
    public static List<DotnetTest> Find(string project, string file, string text)
    {
        var tests = new List<DotnetTest>();
        string fileNamespace = string.Empty;

        // The namespaces and classes the code is in, each with the depth of braces its block opened at, and the one
        // declared whose block hasn't opened yet (a record that ends at its ; never gets one)
        var blocks = new List<(string Name, bool IsClass, int Depth)>();
        (string Name, bool IsClass)? declared = null;
        int depth = 0;
        bool testNext = false;
        int cases = 0;
        string[] lines = CodeOnly(text).Split('\n');
        var marks = new List<(int At, int Kind, string Name)>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith('#'))
                continue;

            marks.Clear();
            foreach (Match match in NamespaceLine().Matches(line))
                marks.Add((match.Index, NAMESPACE, match.Groups[1].Value));
            foreach (Match match in ClassLine().Matches(line))
                marks.Add((match.Index, CLASS, match.Groups[1].Value));
            foreach (Match match in TestAttribute().Matches(line))
                marks.Add((match.Index, ATTRIBUTE, string.Empty));
            foreach (Match match in CaseAttribute().Matches(line))
                marks.Add((match.Index, CASE, string.Empty));
            foreach (Match match in MethodLine().Matches(line))
                marks.Add((match.Index, METHOD, match.Groups[1].Value));
            for (int c = 0; c < line.Length; c++)
            {
                if (line[c] is '{' or '}' or ';')
                    marks.Add((c, line[c], string.Empty));
            }

            marks.Sort((a, b) => a.At.CompareTo(b.At));
            foreach (var (_, kind, name) in marks)
            {
                switch (kind)
                {
                    case NAMESPACE or CLASS:
                        declared = (name, kind == CLASS);
                        break;
                    case ATTRIBUTE:
                        testNext = true;
                        break;
                    case CASE:
                        cases++;
                        break;
                    case METHOD when testNext:
                        // The method an attribute is over, the next one declared after it (the attribute can share its line)
                        testNext = false;
                        if (blocks.Any(block => block.IsClass))
                            tests.Add(new DotnetTest(project, NamespaceOf(fileNamespace, blocks), ClassOf(blocks), name, file, i) { Cases = cases });
                        cases = 0;
                        break;
                    case METHOD:
                        cases = 0;
                        break;
                    case '{':
                        if (declared is { } opens)
                            blocks.Add((opens.Name, opens.IsClass, depth));
                        declared = null;
                        depth++;
                        break;
                    case '}':
                        depth--;
                        // A block that closed is out of it, back at the depth it was declared at
                        while (blocks.Count > 0 && depth <= blocks[^1].Depth)
                            blocks.RemoveAt(blocks.Count - 1);
                        break;
                    case ';':
                        // A file scoped namespace, or a record that's all said in its line
                        if (declared is { IsClass: false } scoped && blocks.Count == 0)
                            fileNamespace = scoped.Name;
                        declared = null;
                        break;
                }
            }
        }

        return tests;
    }

    private const int NAMESPACE = 1, CLASS = 2, ATTRIBUTE = 3, METHOD = 4, CASE = 5;

    // The namespace of where the code is, the file's and the blocks of it in one another
    private static string NamespaceOf(string fileNamespace, List<(string Name, bool IsClass, int Depth)> blocks)
    {
        IEnumerable<string> names = blocks.Where(block => !block.IsClass).Select(block => block.Name);
        return string.Join('.', fileNamespace.Length > 0 ? names.Prepend(fileNamespace) : names);
    }

    // The class the code is in, a nested one after the ones it's in with a + the way reflection names it
    private static string ClassOf(List<(string Name, bool IsClass, int Depth)> blocks) =>
        string.Join('+', blocks.Where(block => block.IsClass).Select(block => block.Name));

    [GeneratedRegex(@"\[\s*(?:Xunit\.)?(?:Fact|Theory|Test|TestCase|TestCaseSource|TestMethod|DataTestMethod)\s*[\]\(,]")]
    private static partial Regex TestAttribute();

    // A case a theory's source gives it, each one of its rows of arguments
    [GeneratedRegex(@"(?<![\w.])(?:Xunit\.)?(?:InlineData|TestCase|DataRow)\s*\(")]
    private static partial Regex CaseAttribute();

    [GeneratedRegex(@"(?<![\w.])namespace\s+([\w.]+)")]
    private static partial Regex NamespaceLine();

    [GeneratedRegex(@"\b(?:class|record(?:\s+(?:class|struct))?|struct)\s+(\w+)")]
    private static partial Regex ClassLine();

    // What a test returns, then its name, its type parameters if it's generic, and its parameters
    [GeneratedRegex(@"\b(?:void|Task|ValueTask)(?:<[^>]*>)?\s+(\w+)\s*(?:<[^>()]*>)?\s*\(")]
    private static partial Regex MethodLine();

    /// <summary>
    /// A dotnet test filter for some tests by their names. Whole classes go as one clause each, so running a class of
    /// two hundred tests isn't a command line of two hundred names. A theory's case (its arguments on its name) goes by
    /// its display name, the one case and not the rest. One with a quote in its arguments goes by what comes before the
    /// quote when the filter goes on a command line, a quote would end it in every shell there is, and by all of its
    /// name when it doesn't (a .runsettings, RunSettings, or arguments passed one by one).
    /// </summary>
    public static string Filter(IEnumerable<string> fullNames, bool quotes = false) => string.Join('|', fullNames.Select(name => Clause(name, quotes)).Distinct());

    private static string Clause(string name, bool quotes)
    {
        if (name.IndexOf('(') <= 0)
            return $"FullyQualifiedName{(name.EndsWith('.') ? '~' : '=')}{Escape(name)}";

        int quote = name.IndexOf('"');
        return quote < 0 || quotes ? $"DisplayName={Escape(name)}" : $"DisplayName~{Escape(name[..quote].TrimEnd())}";
    }

    /// <summary>
    /// A .runsettings with nothing in it but a filter, what dotnet test is given with --settings for a filter a command
    /// line can't carry, a case of a theory with a string among its arguments. It ran every case of the theory.
    /// </summary>
    public static string RunSettings(string filter) =>
        new XDocument(new XElement("RunSettings", new XElement("RunConfiguration", new XElement("TestCaseFilter", filter)))).ToString();

    // Characters a filter reads as its own escaped, a name with them in still matches
    private static string Escape(string name)
    {
        var escaped = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c is '\\' or '(' or ')' or '&' or '|' or '=' or '!' or '~')
                escaped.Append('\\');
            escaped.Append(c);
        }

        return escaped.ToString();
    }

    /// <summary>
    /// The results of a .trx dotnet test wrote, by the test's full name. A theory's cases come back as one result each
    /// with their arguments on the name (Method(x: 1)), and are kept that way, the explorer rolls them up.
    /// </summary>
    public static List<DotnetTestResult> ReadTrx(string trx)
    {
        var results = new List<DotnetTestResult>();
        XDocument document = XDocument.Parse(trx);
        XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;

        // The test's class and method are in its definition, the result has only the name the adapter gave it, which is
        // the full name for xUnit and the method alone for NUnit and MSTest
        var definitions = new Dictionary<string, string>();
        var classes = new Dictionary<string, string>();
        foreach (XElement test in document.Descendants(ns + "UnitTest"))
        {
            if (test.Attribute("id")?.Value is { } id && test.Element(ns + "TestMethod") is { } method)
            {
                string className = method.Attribute("className")?.Value ?? string.Empty;
                string name = method.Attribute("name")?.Value ?? string.Empty;
                classes[id] = className;
                definitions[id] = name.StartsWith(className + ".", StringComparison.Ordinal) || className.Length == 0 ? name : $"{className}.{name}";
            }
        }

        foreach (XElement result in document.Descendants(ns + "UnitTestResult"))
        {
            string testName = result.Attribute("testName")?.Value ?? string.Empty;
            string fullName = result.Attribute("testId")?.Value is { } id && definitions.TryGetValue(id, out string? defined) ? defined : testName;

            // A theory's case keeps its arguments, from the result's own name
            int arguments = testName.IndexOf('(');
            if (arguments > 0 && !fullName.Contains('('))
                fullName += testName[arguments..];

            DotnetTestOutcome outcome = result.Attribute("outcome")?.Value switch
            {
                "Passed" => DotnetTestOutcome.Passed,
                "Failed" or "Error" or "Timeout" or "Aborted" => DotnetTestOutcome.Failed,
                "NotExecuted" or "Inconclusive" or "Skipped" => DotnetTestOutcome.Skipped,
                _ => DotnetTestOutcome.NotRun
            };

            TimeSpan.TryParse(result.Attribute("duration")?.Value, System.Globalization.CultureInfo.InvariantCulture, out TimeSpan duration);
            XElement? output = result.Element(ns + "Output");
            XElement? error = output?.Element(ns + "ErrorInfo");
            results.Add(new DotnetTestResult(fullName, outcome, duration,
                error?.Element(ns + "Message")?.Value.Trim() ?? string.Empty,
                error?.Element(ns + "StackTrace")?.Value.Trim() ?? string.Empty,
                string.Join("\n", new[] { output?.Element(ns + "StdOut")?.Value.Trim(), output?.Element(ns + "StdErr")?.Value.Trim() }
                    .Where(text => !string.IsNullOrEmpty(text))))
            {
                Class = result.Attribute("testId")?.Value is { } of && classes.TryGetValue(of, out string? inClass) ? inClass : string.Empty
            });
        }

        return results;
    }

    /// <summary>The name of a result without a theory case's arguments, the test it's a case of.</summary>
    public static string WithoutArguments(string fullName) =>
        fullName.IndexOf('(') is var at and > 0 ? fullName[..at] : fullName;

    /// <summary>
    /// The first place in a stack trace that's a file of the workspace, "at X in C:\...\File.cs:line 42", the line from
    /// 0. Where a failed test failed, what clicking it goes to.
    /// </summary>
    public static (string File, int Line)? FailedAt(string stack, IReadOnlyList<string>? workspaceRoots = null)
    {
        (string File, int Line)? fallback = null;
        foreach (Match match in StackPlace().Matches(stack))
        {
            string file = match.Groups[1].Value.Trim();
            if (File.Exists(file) && int.TryParse(match.Groups[2].Value, out int line))
            {
                var location = (file, Math.Max(0, line - 1));
                fallback ??= location;
                if (workspaceRoots is null || workspaceRoots.Any(root => PlatformPaths.IsUnder(file, root))) return location;
            }
        }

        return fallback;
    }

    [GeneratedRegex(@" in (.+?\.cs):line (\d+)")]
    private static partial Regex StackPlace();
}
