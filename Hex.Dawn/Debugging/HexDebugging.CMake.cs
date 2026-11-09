namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private readonly Dictionary<string, CMakeTarget> cmakeTargets = new(PlatformPaths.Comparer);
    internal Action<string, string>? NativeProjectConfigured { get; set; }
    internal Action<string, CMakeBuildSelection>? NativeBuildChanged { get; set; }
    internal Action<string, CMakeBuildSelection>? NativeBuildReconfigured { get; set; }

    internal bool CanConfigureCMake => !Debugging && context.Documents.Current.Path is { } path
        && (IsNativeSource(path) || Path.GetFileName(path).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Equals("CMakePresets.json", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Equals("CMakeUserPresets.json", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).ToLowerInvariant() is ".h" or ".hpp" or ".hh" or ".hxx" or ".cmake");

    internal void ConfigureCMake()
        => ConfigureCMake(false);

    internal void SelectCMakePreset()
        => ConfigureCMake(true);

    private void ConfigureCMake(bool selectPreset)
    {
        if (Debugging) return;
        if (context.Documents.Current.Path is { } file && CMakeProject.RootOf(file, context.Workspace.Roots) is { } root)
            StartDebuggingCMake(file, root, preset: selectPreset ? string.Empty : null, configureOnly: true);
        else context.Notices.Say(context.Strings.Said("debugging.cmake_no_project"));
    }

    internal void BuildCMake()
    {
        if (Debugging) return;
        if (context.Documents.Current.Path is { } file && CMakeProject.RootOf(file, context.Workspace.Roots) is { } root)
            StartDebuggingCMake(file, root, configuration: CMakeProject.SelectedBuild(root)?.Configuration, buildOnly: true);
        else context.Notices.Say(context.Strings.Said("debugging.cmake_no_project"));
    }

    private void StartDebuggingCMake(string file, string root, CMakeTarget? selected = null, string? preset = null, bool configureOnly = false, string? configuration = null, bool buildOnly = false)
    {
        bool needsDebugger = !configureOnly && !buildOnly;
        string? cmake = CMakeProject.Tool("cmake"), adapter = needsDebugger ? LldbDap() : null;
        if (cmake is null || needsDebugger && adapter is null)
        {
            context.Notices.Say(context.Strings.Said(needsDebugger ? "debugging.cmake_tools_required" : "debugging.cmake_configure_tools_required"), error: true);
            return;
        }
        SaveAllCode();
        CancelDebugBuild();
        var attempt = new CancellationTokenSource();
        debugBuild = attempt;
        building = true;
        conditionFailures.Clear();
        debuggedProject = root;
        context.Panels.ShowOutput();
        if (needsDebugger) context.Panels.ShowSide(HexSide.Debug);
        context.Notices.Say(context.Strings.Said(configureOnly ? "debugging.cmake_configuring" : "debugging.building", ("project", Path.GetFileName(root))));
        if (selected is null) cmakeTargets.TryGetValue(root, out selected);
        preset ??= CMakeProject.SelectedBuild(root)?.Preset;
        string build = Path.Combine(root, "build", "hex-debug");
        var breakpointsNow = BreakpointsNow();
        _ = Task.Run(async () =>
        {
            try
            {
                CMakeResult configured;
                if (CMakeProject.HasPresets(root))
                {
                    var presets = await CMakeProject.Presets(cmake, root, attempt.Token);
                    var picked = presets.FirstOrDefault(p => p.Name == preset) ?? (presets.Count == 1 ? presets[0] : null);
                    if (picked is null)
                    {
                        PostBuild(attempt.Token, () =>
                        {
                            building = false;
                            if (presets.Count == 0) context.Notices.Say(context.Strings.Said("debugging.cmake_no_preset"), error: true);
                            else context.Windows.Choose(context.Strings.Said("debugging.cmake_preset"), context.Strings.Said("debugging.cmake_preset_text"),
                                presets.Select(p => (p.DisplayName == p.Name ? p.Name : $"{p.DisplayName} ({p.Name})", (Action)(() => { if (!Debugging) StartDebuggingCMake(file, root, selected, p.Name, configureOnly, configuration, buildOnly); }))).ToList());
                        });
                        return;
                    }
                    preset = picked.Name;
                    var result = await CMakeProject.ConfigurePreset(cmake, root, preset, attempt.Token);
                    configured = result.Result;
                    if (result.BuildDirectory is { } directory) build = directory;
                }
                else
                {
                    preset = null;
                    configured = await CMakeProject.Configure(cmake, root, build, attempt.Token);
                }
                if (!ReportBuild(configured.Output, root, configured.ExitCode, Path.GetFileName(root), Path.Combine(root, "CMakeLists.txt"), attempt.Token)) return;
                if (configureOnly || buildOnly)
                {
                    var configurations = CMakeProject.Configurations(build);
                    string? chosen = configurations.FirstOrDefault(value => value == configuration)
                        ?? (configurations.Count == 1 ? configurations[0] : null);
                    if (chosen is null)
                    {
                        PostBuild(attempt.Token, () =>
                        {
                            building = false;
                            context.Windows.Choose(context.Strings.Said("debugging.cmake_configuration"), context.Strings.Said("debugging.cmake_configuration_text"),
                                configurations.Select(value => (value, (Action)(() =>
                                {
                                    if (!Debugging) StartDebuggingCMake(file, root, selected, preset, configureOnly, value, buildOnly);
                                }))).ToList());
                        });
                        return;
                    }
                    string configuredCommands = CMakeProject.CompilationForConfiguration(build, chosen);
                    CMakeProject.RememberBuildConfiguration(root, build, chosen, preset);
                    if (buildOnly)
                    {
                        var result = await CMakeProject.BuildAll(cmake, root, build, chosen, attempt.Token);
                        if (!ReportBuild(result.Output, build, result.ExitCode, Path.GetFileName(root), Path.Combine(root, "CMakeLists.txt"), attempt.Token)) return;
                    }
                    PostBuild(attempt.Token, () =>
                    {
                        if (cmakeTargets.TryGetValue(root, out var previous) && previous.Configuration != chosen) cmakeTargets.Remove(root);
                        NativeBuildReconfigured?.Invoke(root, new(build, chosen, preset));
                        NativeProjectConfigured?.Invoke(root, configuredCommands);
                        building = false;
                        context.Notices.Say(context.Strings.Said(buildOnly ? "debugging.cmake_built" : "debugging.cmake_configured", ("project", Path.GetFileName(root))));
                    });
                    return;
                }
                var targets = CMakeProject.Targets(build, configuration: null);
                var target = targets.FirstOrDefault(t => selected is not null && t.Name == selected.Name && t.Configuration == selected.Configuration) ?? (targets.Count == 1 ? targets[0] : null);
                bool multipleConfigurations = targets.Select(t => t.Configuration).Distinct().Skip(1).Any();
                if (target is null)
                {
                    PostBuild(attempt.Token, () =>
                    {
                        building = false;
                        if (targets.Count == 0) context.Notices.Say(context.Strings.Said("debugging.cmake_no_executable"), error: true);
                        else context.Windows.Choose(context.Strings.Said("debugging.cmake_target"), context.Strings.Said("debugging.cmake_target_text"),
                            targets.Select(t => (multipleConfigurations ? $"{t.Name} ({t.Configuration})" : t.Name, (Action)(() => { if (!Debugging) StartDebuggingCMake(file, root, t, preset); }))).ToList());
                    });
                    return;
                }
                string compilation = CMakeProject.CompilationForConfiguration(build, target.Configuration);
                CMakeProject.RememberBuildConfiguration(root, build, target.Configuration, preset);
                PostBuild(attempt.Token, () =>
                {
                    NativeBuildChanged?.Invoke(root, new(build, target.Configuration, preset));
                    NativeProjectConfigured?.Invoke(root, compilation);
                    cmakeTargets[root] = target;
                    restart = () => StartDebuggingCMake(file, root, target, preset);
                });
                var built = await CMakeProject.Build(cmake, root, build, target, attempt.Token);
                if (!ReportBuild(built.Output, build, built.ExitCode, target.Name, Path.Combine(root, "CMakeLists.txt"), attempt.Token)) return;
                if (!File.Exists(target.Program)) throw new IOException(context.Strings.Said("debugging.cmake_missing_program"));
                await StartUnder(DebugSession.Start(adapter!, []), target.Name, LLDB,
                    session => session.Launch(target.Program, [], root, breakpointsNow, LLDB, ProgramOutputFile()), attempt.Token);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
            catch (Exception error)
            {
                PostBuild(attempt.Token, () =>
                {
                    building = false;
                    context.Notices.Say(context.Strings.Said(buildOnly ? "debugging.cmake_build_failed" : configureOnly ? "debugging.cmake_configure_failed" : "debugging.launch_failed", ("why", error.Message)), error: true);
                });
            }
            finally
            {
                debugWork.Enqueue(() => { if (ReferenceEquals(debugBuild, attempt)) debugBuild = null; attempt.Dispose(); });
            }
        });
    }
}
