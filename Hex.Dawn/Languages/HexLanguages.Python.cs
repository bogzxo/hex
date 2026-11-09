using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    internal Action<string>? PythonInterpreterChanged { get; set; }
    private void SelectPythonInterpreter()
    {
        if (context.Documents.Current.Path is not { } file) return;
        string root = ServerRootFor(file);
        var choices = new List<(string, Action)> {
            (context.Strings.Said("languages.python_auto"), () => UsePythonInterpreter(root, null)) };
        var found = Hex.Core.Notebooks.KernelSpecs.Pythons().ToList();
        if (PythonEnvironment.FindLocal(Path.GetDirectoryName(file)!, root) is { } local) found.Insert(0, local);
        foreach (string python in found.Distinct(PlatformPaths.Comparer))
            choices.Add((python, () => UsePythonInterpreter(root, python)));
        choices.Add((context.Strings.Said("languages.python_enter_path"), () => context.Windows.Prompt(
            context.Strings.Said("languages.python_interpreter"), context.Strings.Said("languages.python_path_text"),
            PythonEnvironment.Selected(root) ?? string.Empty, context.Strings.Said("languages.python_use"), string.Empty,
            path => UsePythonInterpreter(root, path.Trim().Trim('"')))));
        context.Windows.Choose(context.Strings.Said("languages.python_interpreter"), root, choices);
    }

    internal void UsePythonInterpreter(string root, string? interpreter)
    {
        try { PythonEnvironment.Select(root, interpreter); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            context.Notices.Say(error.Message, error: true);
            return;
        }
        var affected = linters.Where(pair => pair.Key.Path is { } path && PlatformPaths.IsUnder(path, root)
            && HexDebugging.IsPython(path)).ToArray();
        foreach (var (document, _) in affected) StopLinting(document);
        foreach (var server in affected.Select(pair => pair.Value).Distinct())
            if (!linters.Values.Contains(server)) _ = LanguageServers.Stop(server);
        foreach (var (document, _) in affected)
        {
            StartLinting(document);
            context.Documents.DiagnosticsChanged(document);
        }
        PythonInterpreterChanged?.Invoke(root);
        context.Notices.Say(context.Strings.Said("languages.python_selected", ("interpreter", interpreter ?? context.Strings.Said("languages.python_auto"))));
    }

    internal static JsonObject? PythonServerSettings(LanguageServers.Registration? entry, string file, string root)
    {
        if (entry is null || !Path.GetFileNameWithoutExtension(entry.Command).Equals("pyright-langserver", StringComparison.OrdinalIgnoreCase)) return null;
        if (entry.Settings?["python"] is { } existing && existing is not JsonObject) return null;
        if (entry.Settings?["python"]?["pythonPath"] is JsonValue configured
            && configured.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path)
            && PythonEnvironment.Selected(root) is null) return null;
        if (PythonEnvironment.FindLocal(Path.GetDirectoryName(file)!, root) is not { } python) return null;
        var settings = entry.Settings?.DeepClone().AsObject() ?? new JsonObject();
        if (settings["python"] is not JsonObject section) settings["python"] = section = new JsonObject();
        section["pythonPath"] = python;
        return settings;
    }
}
