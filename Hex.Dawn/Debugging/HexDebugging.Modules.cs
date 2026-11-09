using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private bool modulesShown, modulesLoading, modulesExpanded = true;
    private readonly HashSet<DebugModuleId> expandedModules = [];
    private string moduleFilter = "", moduleError = "";
    private long shownModulesRevision = -1;
    private int modulesQuery;

    private bool CanShowModules() => debugging is { State: DebugState.Running or DebugState.Stopped } session
        && (session.CanRequestModules || session.LoadedModuleCount > 0);

    private void ShowModules()
    {
        if (!CanShowModules()) return;
        modulesShown = true;
        modulesExpanded = true;
        context.Panels.ShowSide(HexSide.Debug);
        RefreshModules();
        context.Panels.RevealSideRow("debug-modules");
    }

    private void UpdateModuleView()
    {
        if (!modulesShown || debugging is not {} session || session.ModulesRevision == shownModulesRevision) return;
        shownModulesRevision = session.ModulesRevision;
        context.Panels.RefreshSide(HexSide.Debug);
    }

    private void RefreshModules()
    {
        if (!modulesShown || !CanShowModules() || modulesLoading || debugging is not {} session) return;
        moduleError = "";
        modulesLoading = session.CanRequestModules;
        context.Panels.RefreshSide(HexSide.Debug);
        if (!session.CanRequestModules) return; // netcoredbg supplies the list through native module events.
        int query = ++modulesQuery;
        _ = Refresh();
        async Task Refresh()
        {
            string error = "";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await session.RefreshModules(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { error = context.Strings.Said("debugging.modules_timeout"); }
            catch (Exception failed) { error = failed.Message; }
            debugWork.Enqueue(() =>
            {
                if (!ReferenceEquals(debugging, session) || query != modulesQuery) return;
                (modulesLoading, moduleError) = (false, error);
                context.Panels.RefreshSide(HexSide.Debug);
            });
        }
    }

    private void FilterModules()
    {
        if (!modulesShown || !CanShowModules() || debugging is not {} session) return;
        context.Windows.Prompt(context.Strings.Said("debugging.modules_filter"), context.Strings.Said("debugging.modules_filter_text"), moduleFilter,
            context.Strings.Said("debugging.modules_filter_apply"), "", text =>
            {
                if (!ReferenceEquals(debugging, session) || !CanShowModules()) return;
                moduleFilter = text.Trim();
                context.Panels.RefreshSide(HexSide.Debug);
            });
    }

    private UITreeItem ModulesSection()
    {
        var session = debugging!;
        var modules = session.LoadedModules;
        bool Matches(DebugModule module) => moduleFilter.Length == 0 || new[] { module.Name, module.Path, module.SymbolStatus, module.SymbolFilePath, module.Version }
            .Any(value => value.Contains(moduleFilter, StringComparison.OrdinalIgnoreCase));
        var shown = modules.Where(Matches).ToArray();
        var section = new UITreeItem(context.Strings.Said("debugging.modules_count", ("shown", shown.Length.ToString()), ("total", modules.Count.ToString())), "library")
        {
            Expandable = true, Expanded = modulesExpanded, Children = [], Tag = new SideTag(Id: "debug-modules", Toggled: row =>
            {
                if (ReferenceEquals(debugging, session)) modulesExpanded = row.Expanded;
            }, Menu: _ =>
            {
                var menu = new Menu("modules");
                menu.Add(context.Strings.Said("debugging.modules_filter"), () => { if (ReferenceEquals(debugging, session)) FilterModules(); }, icon: "filter");
                if (session.CanRequestModules) menu.Add(context.Strings.Said("debugging.modules_refresh"), () => { if (ReferenceEquals(debugging, session)) RefreshModules(); }, icon: "refresh");
                context.Windows.Menu(menu);
            })
        };
        if (moduleFilter.Length > 0) section.Children.Add(NoteItem(context.Strings.Said("debugging.modules_filtered", ("filter", moduleFilter))));
        if (modulesLoading) section.Children.Add(NoteItem(context.Strings.Said("debugging.modules_loading")));
        if (moduleError.Length > 0) section.Children.Add(NoteItem(context.Strings.Said("debugging.modules_failed", ("why", moduleError))));
        if (shown.Length == 0 && !modulesLoading) section.Children.Add(NoteItem(context.Strings.Said(moduleFilter.Length == 0 ? "debugging.modules_empty" : "debugging.modules_no_match")));
        foreach (var module in shown)
        {
            var details = ModuleDetails(module);
            bool Current() => ReferenceEquals(debugging, session) && session.State != DebugState.Ended && session.LoadedModules.Contains(module);
            section.Children.Add(new UITreeItem(module.Name, "library")
            {
                Detail = module.SymbolStatus.Length > 0 ? module.SymbolStatus : context.Strings.Said("debugging.modules_symbols_unknown"),
                Tooltip = string.Join('\n', details), Expandable = true, Expanded = expandedModules.Contains(module.Id), Children = details.Select(NoteItem).ToList(),
                Tag = new SideTag(Toggled: row =>
                {
                    if (!Current()) return;
                    if (row.Expanded) expandedModules.Add(module.Id);
                    else expandedModules.Remove(module.Id);
                }, Menu: _ =>
                {
                    var menu = new Menu("module");
                    menu.Add(context.Strings.Said("debugging.modules_copy_details"), () => { if (Current()) UIClipboard.Copy(module.Name + "\n" + string.Join('\n', details)); }, icon: "copy");
                    if (module.Path.Length > 0) menu.Add(context.Strings.Said("debugging.modules_copy_path"), () => { if (Current()) UIClipboard.Copy(module.Path); }, icon: "copy");
                    if (module.SymbolFilePath.Length > 0) menu.Add(context.Strings.Said("debugging.modules_copy_symbols"), () => { if (Current()) UIClipboard.Copy(module.SymbolFilePath); }, icon: "copy");
                    context.Windows.Menu(menu);
                })
            });
        }
        return section;
    }

    private List<string> ModuleDetails(DebugModule module)
    {
        var lines = new List<string>();
        void Add(string key, string value)
        {
            if (value.Length > 0) lines.Add(context.Strings.Said("debugging.module_field", ("name", context.Strings.Said("debugging.module_" + key)), ("value", value)));
        }
        Add("path", module.Path);
        Add("symbols", module.SymbolStatus.Length > 0 ? module.SymbolStatus : context.Strings.Said("debugging.modules_symbols_unknown"));
        Add("symbol_file", module.SymbolFilePath);
        Add("version", module.Version);
        if (module.Optimized is {} optimized) Add("optimized", context.Strings.Said(optimized ? "debugging.module_yes" : "debugging.module_no"));
        if (module.UserCode is {} userCode) Add("user_code", context.Strings.Said(userCode ? "debugging.module_yes" : "debugging.module_no"));
        Add("address", module.AddressRange);
        Add("timestamp", module.DateTimeStamp);
        Add("id", module.Id.Value);
        return lines;
    }

    private void ClearModuleView()
    {
        modulesQuery++;
        expandedModules.Clear();
        modulesExpanded = true;
        (modulesShown, modulesLoading, shownModulesRevision, moduleFilter, moduleError) = (false, false, -1, "", "");
    }
}
