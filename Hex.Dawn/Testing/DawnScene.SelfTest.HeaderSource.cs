namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestHeaderSource(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "header source");
        string header = Path.Combine(root, "sample file.h"), source = Path.Combine(root, "sample file.cpp"), lone = Path.Combine(root, "unmatched.h");
        void Switch() => AllCommands().First(command => command.Text.EndsWith(Said("code_server.switch_header_source"), StringComparison.Ordinal)).Picked?.Invoke();
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(header, "int answer();\n");
            File.WriteAllText(source, "#include \"sample file.h\"\nint answer() { return 42; }\n");
            File.WriteAllText(lone, "struct Unmatched {};\n");
            explorer.OpenFolder(root);
            Open(source);
        });
        test.WaitFor(() => languages.CanServe(document, "hoverProvider"), 30f);
        test.Check("Header/source: command available for clangd", () => AllCommands().Any(command => command.Text.EndsWith(Said("code_server.switch_header_source"), StringComparison.Ordinal)));
        test.Run(Switch);
        test.WaitFor(() => document.Path is {} path && PlatformPaths.Same(path, header), 10f);
        test.Check("Header/source: source opens matching header with spaces", () => document.Path is {} path && PlatformPaths.Same(path, header));
        test.WaitFor(() => languages.CanServe(document, "hoverProvider"), 10f);
        test.Run(Switch);
        test.WaitFor(() => document.Path is {} path && PlatformPaths.Same(path, source), 10f);
        test.Check("Header/source: header returns to source", () => document.Path is {} path && PlatformPaths.Same(path, source));
        test.Run(() => Open(lone));
        test.WaitFor(() => languages.CanServe(document, "hoverProvider"), 10f);
        test.Run(Switch);
        test.Settle(0.5f);
        test.Check("Header/source: unmatched header stays open", () => document.Path is {} path && PlatformPaths.Same(path, lone));
    }
}
