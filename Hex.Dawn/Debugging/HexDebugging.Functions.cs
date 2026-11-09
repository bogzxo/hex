using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private readonly Dictionary<string, bool> functionBreakpoints = new(StringComparer.Ordinal);
    private string[] activeFunctionBreakpoints = [];

    internal DebugFunctionBreakpoint? FunctionBreakpointStatus(string name) => debugging?.FunctionBreakpoint(name);

    private void AddFunctionBreakpoint()
    {
        ReadKeptBreakpoints();
        var workspace = context.Workspace.WatchExpressions;
        context.Windows.Prompt(context.Strings.Said("debugging.add_function_breakpoint"), context.Strings.Said("debugging.function_name"), "",
            context.Strings.Said("debugging.add_button"), "Program.Calculate", name =>
            {
                if (ReferenceEquals(workspace, context.Workspace.WatchExpressions)) SetFunctionBreakpoint(name, true);
            });
    }

    internal void SetFunctionBreakpoint(string name, bool enabled)
    {
        name = name.Trim();
        if (building || name.Length == 0) return;
        functionBreakpoints[name] = enabled;
        FunctionBreakpointsChanged();
        context.Panels.ShowSide(HexSide.Debug);
    }

    private void FunctionBreakpointsChanged()
    {
        string[] names = functionBreakpoints.Where(pair => pair.Value).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
        Volatile.Write(ref activeFunctionBreakpoints, names);
        KeepBreakpoints();
        context.Panels.RefreshSide(HexSide.Debug);
        if (debugging is not { State: not DebugState.Starting and not DebugState.Ended } session || !session.Can("supportsFunctionBreakpoints")) return;
        _ = Send();
        async Task Send()
        {
            try { await session.SetFunctionBreakpoints(names).ConfigureAwait(false); }
            catch (Exception error)
            {
                debugWork.Enqueue(() =>
                {
                    if (ReferenceEquals(debugging, session)) context.Notices.Say(context.Strings.Said("debugging.function_failed", ("why", error.Message)), error: true);
                });
            }
        }
    }

    private void FunctionBreakpointMenu(string name)
    {
        if (!functionBreakpoints.TryGetValue(name, out bool enabled)) return;
        var menu = new Menu("function-breakpoint");
        menu.Add(context.Strings.Said(enabled ? "debugging.disable_breakpoint" : "debugging.enable_breakpoint"), () => SetFunctionBreakpoint(name, !enabled),
            icon: enabled ? "circle" : "circle-filled").IsEnabled = () => !building;
        menu.Add(context.Strings.Said("debugging.delete_breakpoint"), () =>
        {
            if (building) return;
            functionBreakpoints.Remove(name);
            FunctionBreakpointsChanged();
        }, icon: "trash").IsEnabled = () => !building;
        context.Windows.Menu(menu);
    }

    private void AddFunctionBreakpointRows(UITreeItem section)
    {
        foreach (var (name, enabled) in functionBreakpoints.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var placed = debugging?.FunctionBreakpoint(name);
            string detail = !enabled ? context.Strings.Said("debugging.off")
                : debugging is not null && !debugging.Can("supportsFunctionBreakpoints") ? context.Strings.Said("debugging.function_unsupported")
                : placed?.Verified == true ? placed.File is {} file && placed.Line >= 0 ? context.Workspace.Relative(file) + ":" + (placed.Line + 1) : context.Strings.Said("debugging.function_verified")
                : placed?.Message ?? context.Strings.Said("debugging.function_pending");
            section.Children!.Add(new UITreeItem(name, !enabled ? "circle" : placed?.Verified == true ? "debug-breakpoint-function" : "debug-breakpoint-function-unverified")
            {
                Detail = detail, Tooltip = name + "\n" + detail, Color = enabled ? null : context.Panels.Dim,
                IconColor = enabled ? context.Panels.Error : context.Panels.Dim,
                Tag = new SideTag(Menu: _ => FunctionBreakpointMenu(name), Pressed: () =>
                {
                    if (placed?.File is {} path && placed.Line >= 0 && File.Exists(path)) context.Documents.OpenAt(path, placed.Line, 0);
                })
            });
        }
    }

    internal static Dictionary<string, bool> ReadFunctionBreakpoints(JsonArray kept)
    {
        var found = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (JsonNode? node in kept)
            if (node is JsonObject point && point["function"] is JsonValue value && value.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name))
                found[name.Trim()] = point["enabled"] is not JsonValue on || !on.TryGetValue<bool>(out bool enabled) || enabled;
        return found;
    }

    internal static void WriteFunctionBreakpoints(JsonArray kept, IReadOnlyDictionary<string, bool> functions)
    {
        foreach (var (name, enabled) in functions)
            kept.Add((JsonNode)new JsonObject { ["function"] = name, ["enabled"] = enabled });
    }
}
