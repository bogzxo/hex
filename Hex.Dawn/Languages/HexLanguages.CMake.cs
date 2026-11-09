namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    private static bool UsesClangd(LanguageServers.Registration? entry) => entry is not null
        && Path.GetFileNameWithoutExtension(entry.Command).Equals("clangd", StringComparison.OrdinalIgnoreCase);

    internal static string[] ClangdArguments(LanguageServers.Registration? entry, string root)
    {
        if (!UsesClangd(entry) || entry!.Arguments.Any(argument => argument.TrimStart('-').StartsWith("compile-commands-dir", StringComparison.Ordinal))) return [];
        return CMakeProject.CompilationDirectory(root) is { } build ? ["--compile-commands-dir=" + build] : [];
    }

    internal void CMakeConfigured(string root, string build)
    {
        if (!File.Exists(Path.Combine(build, "compile_commands.json"))) return;
        CMakeProject.RememberCompilationDirectory(root, build);
        var affected = linters.Where(pair => pair.Key.Path is { } path && PlatformPaths.IsUnder(path, root)
            && UsesClangd(LanguageServers.RegistrationFor(ServerPathFor(pair.Key, path)))).ToArray();
        foreach (var (document, _) in affected) StopLinting(document);
        foreach (var server in affected.Select(pair => pair.Value).Distinct())
            if (!linters.Values.Contains(server)) _ = LanguageServers.Stop(server);
        foreach (var (document, _) in affected)
        {
            StartLinting(document);
            context.Documents.DiagnosticsChanged(document);
        }
    }
}
