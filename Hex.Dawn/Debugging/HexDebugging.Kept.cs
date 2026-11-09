using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// The breakpoints kept between runs of Hex, Visual Studio's way (it keeps them in the .vs folder of a solution). Each
// workspace has a file of its own in the settings folder, named by its folders, written whenever a breakpoint changes
// and read back the first time a file of that workspace opens. A breakpoint whose file has gone is dropped as it's read.
internal sealed partial class HexDebugging
{
    // The workspace the breakpoints are of now, by the key of its folders, null before any were read
    private string? keptFor;

    // Whether the breakpoints are being read back, so reading them doesn't write them out again a breakpoint at a time
    private bool readingKept;

    // The key of the workspace's folders, what its file is named by
    private string WorkspaceKey()
    {
        string folders = string.Join('\n', context.Workspace.Roots.Select(root => Path.GetFullPath(root).ToLowerInvariant()).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folders)))[..16];
    }

    private string? testBreakpointStore;
    private string KeptFile(string key) => Path.Combine(testBreakpointStore ?? Path.Combine(TerminalFiles.UserFolder, "breakpoints"), key + ".json");

    internal void UseTestBreakpointStore(string? directory)
    {
        if (!context.Options.SelfTest) throw new InvalidOperationException("Breakpoint store overrides are only available to self-tests.");
        testBreakpointStore = directory;
        keptFor = null;
        ReadKeptBreakpoints();
    }

    /// <summary>The workspace's breakpoints read back if they're another workspace's than what's in memory, as a file opens.</summary>
    private void ReadKeptBreakpoints()
    {
        if (context.Workspace.Roots.Count == 0)
            return;

        string key = WorkspaceKey();
        if (key == keptFor)
            return;

        readingKept = true;
        try
        {
            // Clearing the old workspace must not overwrite the destination before it is read.
            keptFor = key;
            foreach (var (was, lines) in breakpoints.ToArray())
            {
                lines.Clear();
                breakpointSettings.Remove(was);
                BreakpointsChanged(was);
            }
            functionBreakpoints.Clear();
            if (context.Options.SelfTest && testBreakpointStore is null) return;
            string file = KeptFile(key);
            if (!File.Exists(file)) return;
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonArray kept)
                return;

            foreach (var (name, enabled) in ReadFunctionBreakpoints(kept)) functionBreakpoints[name] = enabled;

            foreach (JsonNode? node in kept)
            {
                if (node?["file"]?.GetValue<string>() is not { } path || !File.Exists(path) || node["line"] is not { } at)
                    continue;

                int line = at.GetValue<int>();
                DebugHitCondition? hits = node["hits"] is JsonObject count && Enum.TryParse(count["kind"]?.GetValue<string>(), out DebugHitKind kind)
                    ? new DebugHitCondition(kind, count["count"]?.GetValue<int>() ?? 1)
                    : null;
                var settings = new HexBreakpoint(node["condition"]?.GetValue<string>(), hits, node["log"]?.GetValue<string>(), node["enabled"]?.GetValue<bool>() ?? true);
                if (settings.Plain)
                {
                    BreakpointsOf(path).Add(line);
                    BreakpointsChanged(path);
                }
                else
                {
                    Settle(path, line, settings);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Hex] The breakpoints kept for this workspace couldn't be read, {e.Message}");
        }
        finally
        {
            Volatile.Write(ref activeFunctionBreakpoints, functionBreakpoints.Where(pair => pair.Value).Select(pair => pair.Key).ToArray());
            readingKept = false;
            context.Panels.RefreshSide(HexSide.Debug);
        }
    }

    /// <summary>The workspace's breakpoints written out, after any of them changed.</summary>
    private void KeepBreakpoints()
    {
        if (readingKept || keptFor is not { } key || context.Options.SelfTest && testBreakpointStore is null)
            return;

        var kept = new JsonArray();
        WriteFunctionBreakpoints(kept, functionBreakpoints);
        foreach (var (file, lines) in breakpoints)
        {
            foreach (int line in lines)
            {
                HexBreakpoint settings = SettingsOf(file, line);
                var node = new JsonObject { ["file"] = file, ["line"] = line };
                if (settings.Condition is { } condition)
                    node["condition"] = condition;
                if (settings.Hits is { } hits)
                    node["hits"] = new JsonObject { ["kind"] = hits.Kind.ToString(), ["count"] = hits.Count };
                if (settings.LogMessage is { } log)
                    node["log"] = log;
                if (!settings.Enabled)
                    node["enabled"] = false;
                kept.Add((JsonNode)node);
            }
        }

        try
        {
            string path = KeptFile(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, kept.ToJsonString());
        }
        catch (IOException e)
        {
            Console.WriteLine($"[Hex] The breakpoints couldn't be kept, {e.Message}");
        }
    }
}
