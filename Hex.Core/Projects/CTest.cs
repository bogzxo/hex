using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Hex.Core;

internal sealed record CTestCase(string Name, string File, int Line)
{
    public string[] Command { get; init; } = [];
    public string WorkingDirectory { get; init; } = "";
    public Dictionary<string, string> Environment { get; init; } = [];
    public string? DebugUnavailable { get; init; }
    public int? SkipReturnCode { get; init; }
    public bool DebugResultSupported { get; init; } = true;

    // CTest owns output-expression and expected-failure semantics. A direct debugger
    // launch cannot reliably distinguish every signal/exception from an ordinary exit.
    public CTestResult? DebugResult(int exitCode, double seconds) =>
        !DebugResultSupported || exitCode < 0 ? null : new(Name,
            exitCode == SkipReturnCode ? "skipped" : exitCode == 0 ? "passed" : "failed",
            seconds, $"Debugged process exited with code {exitCode}.");
}
internal sealed record CTestResult(string Name, string Outcome, double Seconds, string Output);
internal sealed record CTestRun(int ExitCode, string Output, IReadOnlyList<CTestResult> Results);

internal static class CTest
{
    internal static async Task<IReadOnlyList<CTestCase>> Discover(string executable, string build, string configuration, CancellationToken cancel = default)
    {
        var result = await CMakeProject.Run(executable, build, ["--show-only=json-v1", "-C", configuration], cancel);
        if (result.ExitCode != 0) throw new IOException(result.Output);
        var data = JsonNode.Parse(result.StandardOutput ?? result.Output)!;
        var graph = data["backtraceGraph"];
        List<CTestCase> cases = [];
        foreach (var row in data["tests"]!.AsArray())
        {
            string file = "";
            int line = 0;
            if (row?["backtrace"] is { } trace && graph is not null)
            {
                var node = graph["nodes"]![trace.GetValue<int>()];
                if (node?["file"] is { } at) file = graph["files"]![at.GetValue<int>()]!.GetValue<string>();
                line = Math.Max(0, (node?["line"]?.GetValue<int>() ?? 1) - 1);
            }
            var properties = (row?["properties"]?.AsArray() ?? []).ToDictionary(property => property!["name"]!.GetValue<string>(), property => property!["value"]);
            var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (properties.GetValueOrDefault("ENVIRONMENT") is JsonArray variables)
                foreach (var variable in variables)
                {
                    string value = variable!.GetValue<string>();
                    int equals = value.IndexOf('=');
                    if (equals > 0) environment[value[..equals]] = value[(equals + 1)..];
                }
            string? unavailable = properties.GetValueOrDefault("DISABLED")?.GetValue<bool>() == true ? "This CTest test is disabled." : null;
            foreach (string property in new[] { "FIXTURES_REQUIRED", "FIXTURES_SETUP", "FIXTURES_CLEANUP", "RESOURCE_GROUPS", "ENVIRONMENT_MODIFICATION" })
                if (properties.GetValueOrDefault(property) is JsonArray { Count: > 0 }) unavailable = $"Direct test debugging does not yet support CTest's {property} property. Run this test through CTest instead.";
            cases.Add(new(row!["name"]!.GetValue<string>(), file, line)
            {
                Command = row["command"]?.AsArray().Select(value => value!.GetValue<string>()).ToArray() ?? [],
                WorkingDirectory = properties.GetValueOrDefault("WORKING_DIRECTORY")?.GetValue<string>() ?? build,
                Environment = environment,
                DebugUnavailable = unavailable,
                SkipReturnCode = properties.GetValueOrDefault("SKIP_RETURN_CODE")?.GetValue<int>(),
                DebugResultSupported = properties.GetValueOrDefault("WILL_FAIL")?.GetValue<bool>() != true
                    && !new[] { "PASS_REGULAR_EXPRESSION", "FAIL_REGULAR_EXPRESSION", "SKIP_REGULAR_EXPRESSION" }
                        .Any(property => properties.GetValueOrDefault(property) is JsonArray { Count: > 0 })
            });
        }
        return cases;
    }

    internal static async Task<CTestRun> Run(string executable, string build, string configuration, IReadOnlyList<string>? selected = null, CancellationToken cancel = default)
    {
        if (selected is { Count: 0 }) return new(0, "", []);
        string report = Path.Combine(Path.GetTempPath(), "hex-ctest-" + Guid.NewGuid() + ".xml");
        string selection = report + ".txt";
        try
        {
            List<string> args = ["-C", configuration, "--output-on-failure", "--output-junit", report];
            if (selected is not null)
            {
                var help = await CMakeProject.Run(executable, build, ["--help"], cancel);
                if (!help.Output.Contains("--tests-from-file", StringComparison.Ordinal))
                    throw new IOException("Selected test runs require CTest 3.29 or newer. Update CMake to run this selection.");
                if (selected.Any(name => name.IndexOfAny(['\r', '\n']) >= 0)) throw new ArgumentException("CTest names cannot contain newlines.");
                await File.WriteAllLinesAsync(selection, selected, cancel);
                args.AddRange(["--tests-from-file", selection]);
            }
            var result = await CMakeProject.Run(executable, build, args, cancel);
            if (!File.Exists(report)) throw new IOException(result.Output);
            var xml = XDocument.Load(report);
            var results = xml.Descendants("testcase").Select(test => new CTestResult(
                (string?)test.Attribute("name") ?? "",
                test.Element("failure") is not null || test.Element("error") is not null ? "failed" : test.Element("skipped") is not null ? "skipped" : "passed",
                double.TryParse((string?)test.Attribute("time"), CultureInfo.InvariantCulture, out double seconds) ? seconds : 0,
                string.Join("\n", test.Elements().Where(element => element.Name.LocalName is "failure" or "error" or "skipped" or "system-out" or "system-err").Select(element => element.Value)))).ToArray();
            return new(result.ExitCode, result.Output, results);
        }
        finally
        {
            if (File.Exists(report)) File.Delete(report);
            if (File.Exists(selection)) File.Delete(selection);
        }
    }
}
