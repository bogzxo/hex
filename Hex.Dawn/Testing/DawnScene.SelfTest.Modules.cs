using System.Diagnostics;
using Hex.Core.Projects;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDebugModules(HexPointerScript test, string language)
    {
        bool cpp = language == "cpp", python = language == "python";
        string root = Path.Combine(testFolder, "modules " + language), file = Path.Combine(root, cpp ? "main.cpp" : python ? "main.py" : "Program.cs");
        string dependency = Path.Combine(testFolder, "module dependency " + language);
        string plugin = cpp ? Path.Combine(dependency, OperatingSystem.IsWindows() ? "HexModulePlugin.dll" : "HexModulePlugin.so")
            : Path.Combine(dependency, "bin", "Debug", "net11.0", "HexModulePlugin.dll");
        string name = cpp ? "main" + (OperatingSystem.IsWindows() ? ".exe" : "") : "Probe.dll";
        string escaped = plugin.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        string source = python ? "answer = 42\nprint(answer)\nanswer += 1\nprint(answer)\n"
            : cpp ? "#ifdef _WIN32\n#include <windows.h>\n#else\n#include <dlfcn.h>\n#endif\n#include <cstdio>\nint main() {\n int answer = 42;\n std::printf(\"modules %d\\n\", answer);\n"
                + "#ifdef _WIN32\n auto module = LoadLibraryA(\"" + escaped + "\");\n#else\n auto module = dlopen(\"" + escaped + "\", RTLD_NOW);\n#endif\n"
                + " std::printf(\"loaded %d\\n\", module != nullptr);\n return 0;\n}\n"
            : "int answer = 42;\nSystem.Console.WriteLine(answer);\nvar module = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(\"" + escaped + "\");\nSystem.Console.WriteLine(module.GetName().Name);\nSystem.Console.WriteLine(answer + 1);\n";
        Task<(int Code, string Output)>? build = null;
        Action<string>? staleFilter = null;
        object? focusBeforeModules = null;
        int first = cpp ? 8 : 1, loaded = cpp ? 14 : 3;
        bool Has(string key) => PaletteChoices().Any(choice => choice.Text == Said(key));
        void Command(string key) => PaletteChoices().FirstOrDefault(choice => choice.Text == Said(key))?.Picked?.Invoke();
        UITreeItem? Section() => sideTree.Items.FirstOrDefault(section => section.Icon == "library");
        UITreeItem? Row(string module) => Section()?.Children?.FirstOrDefault(row => row.Icon == "library" && row.Text == module);
        bool Ready() => Section() is {} section && section.Children?.Any(row => row.Text == Said("debugging.modules_loading")) != true;
        void Filter(string value)
        {
            Command("debugging.modules_filter");
            if (textDialog is { IsOpen: true } dialog) Descendants(dialog.Body).OfType<TextBox>().First().OnSubmitted?.Invoke(value);
        }
        async Task<(int Code, string Output)> BuildPlugin()
        {
            if (!cpp) return await DotnetCli.Run(dependency, ["build", Path.Combine(dependency, "HexModulePlugin.csproj"), "--nologo"], TimeSpan.FromMinutes(2), CancellationToken.None);
            var info = new ProcessStartInfo("clang++") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-shared");
            if (!OperatingSystem.IsWindows()) info.ArgumentList.Add("-fPIC");
            foreach (string argument in new[] { Path.Combine(dependency, "plugin.cpp"), "-o", plugin }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            ChildProcesses.Adopt(process);
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await output + await errors);
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(dependency);
            if (!python)
            {
                if (cpp) File.WriteAllText(Path.Combine(dependency, "plugin.cpp"), "#ifdef _WIN32\n#define API __declspec(dllexport)\n#else\n#define API\n#endif\nextern \"C\" API int HexModulePlugin() { return 42; }\n");
                else WriteSelfTestProject(Path.Combine(dependency, "HexModulePlugin.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><DebugType>none</DebugType><DebugSymbols>false</DebugSymbols></PropertyGroup></Project>");
                if (!cpp) File.WriteAllText(Path.Combine(dependency, "Plugin.cs"), "public static class Plugin { public static int Answer => 42; }\n");
                build = BuildPlugin();
            }
            if (!cpp && !python) WriteSelfTestProject(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
            debugger.breakpoints.Clear();
            debugger.ToggleBreakpoint(file, first);
            if (!python) debugger.ToggleBreakpoint(file, loaded);
        });
        if (!python)
        {
            test.WaitFor(() => build?.IsCompleted == true, 60);
            test.Run(() => { if (build?.IsCompletedSuccessfully == true && build.Result.Code != 0) Console.WriteLine("[Modules plugin] " + build.Result.Output); });
            test.Check("Modules: " + language + " dependency builds without debug symbols", () => build?.IsCompletedSuccessfully == true && build.Result.Code == 0 && File.Exists(plugin) && !File.Exists(Path.ChangeExtension(plugin, ".pdb")));
        }
        test.Run(RunOrPlay);
        test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == first, 60);
        test.Check("Modules: " + language + " starts at the source breakpoint", () => debugger.DebugStopped && code.ExecutionLine == first);
        test.Check("Modules: " + language + " exposes the native module command", () => Has("debugging.modules"));
        test.Run(() => { focusBeforeModules = compositor.Focus; Command("debugging.modules"); });
        test.WaitFor(() => Ready() && Row(python ? "__main__" : name) is not null, 15);
        test.WaitFor(() => Section() is {} section && sideTree.RowBounds(section) is not null, 5);
        test.Check("Modules: " + language + " command scrolls its section into view without taking keyboard focus", () => Section() is {} section
            && sideTree.RowBounds(section) is not null && ReferenceEquals(compositor.Focus, focusBeforeModules));
        if (python)
        {
            test.Check("Modules: Python reports its main source with an explicit unknown symbol status", () => Ready()
                && Row("__main__")?.Detail == Said("debugging.modules_symbols_unknown")
                && Row("__main__")?.Children?.Any(row => row.Text.StartsWith(Said("debugging.module_path") + ": ", StringComparison.Ordinal)
                    && PlatformPaths.Same(row.Text[(Said("debugging.module_path").Length + 2)..], file)) == true);
            test.Run(() => Filter("__main__"));
            test.Check("Modules: Python modules can be filtered by their native name", () => Row("__main__") is not null && Row("runpy") is null);
            test.Run(() => Command("debugging.modules_refresh"));
            test.WaitFor(Ready, 15);
            test.Check("Modules: refreshing Python leaves its source pause intact", () => Ready() && debugger.DebugStopped && code.ExecutionLine == first);
        }
        else
        {
            test.Check("Modules: " + language + " shows loaded symbols for the main program", () => Row(name)?.Detail?.Contains("Symbols loaded", StringComparison.OrdinalIgnoreCase) == true);
            test.Check("Modules: " + language + " displays the native binary path and module ID", () => Row(name)?.Children?.Any(row => row.Text.Contains(name, StringComparison.Ordinal)) == true
                && Row(name)?.Children?.Any(row => row.Text.StartsWith(Said("debugging.module_id") + ":", StringComparison.Ordinal)) == true);
            test.Check("Modules: " + language + " refresh availability follows native query support", () => Has("debugging.modules_refresh") == cpp);
            test.Run(() =>
            {
                if (Row(name) is {} row) { row.Expanded = true; sideTree.OnToggled?.Invoke(row); }
                Command("debugging.modules");
            });
            test.WaitFor(Ready, 15);
            test.Check("Modules: " + language + " retains expanded module details across refresh", () => Row(name)?.Expanded == true);
            if (cpp)
            {
                test.Check("Modules: LLDB exposes the native symbol file and address", () => Row(name)?.Children?.Any(row => row.Text.Contains("main.pdb", StringComparison.OrdinalIgnoreCase)) == OperatingSystem.IsWindows()
                    && Row(name)?.Children?.Any(row => row.Text.StartsWith(Said("debugging.module_address") + ":", StringComparison.Ordinal)) == true);
                test.Run(() => Command("debugging.modules_refresh"));
                test.WaitFor(Ready, 15);
                test.Check("Modules: refresh preserves the paused frame and source", () => debugger.DebugStopped && code.ExecutionLine == first && Row(name) is not null);
            }
            test.Run(() =>
            {
                if (Row(name) is {} row) sideTree.OnItemContextMenu?.Invoke(row, System.Numerics.Vector2.Zero);
            });
            test.Check("Modules: " + language + " exposes module details and path copy actions", () => contextMenu?.Showing?.Items.Any(item => item.Label == Said("debugging.modules_copy_details")) == true
                && contextMenu.Showing.Items.Any(item => item.Label == Said("debugging.modules_copy_path")));
            test.Check("Modules: " + language + " offers a symbol path only when the adapter reports it", () => contextMenu?.Showing?.Items.Any(item => item.Label == Said("debugging.modules_copy_symbols"))
                == (Row(name)?.Children?.Any(row => row.Text.StartsWith(Said("debugging.module_symbol_file") + ": ", StringComparison.Ordinal)) == true));
            test.Run(() => { contextMenu?.Close(); Filter("HexModulePlugin"); });
            test.Check("Modules: filter shows an explicit empty match before the library loads", () => Row(name) is null && Section()?.Children?.Any(row => row.Text == Said("debugging.modules_no_match")) == true);
            test.Run(() => { if (Section() is {} section) { section.Expanded = false; sideTree.OnToggled?.Invoke(section); } });
            test.Run(RunOrPlay);
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == loaded && Row(Path.GetFileName(plugin)) is not null, 20);
            test.Check("Modules: " + language + " native load events update the filtered list", () => debugger.DebugStopped && code.ExecutionLine == loaded && Row(Path.GetFileName(plugin)) is not null);
            test.Check("Modules: " + language + " native events preserve the collapsed module section", () => Section()?.Expanded == false);
            test.Check("Modules: " + language + " reports the dependency's missing symbols", () => Row(Path.GetFileName(plugin))?.Detail?.Contains("Symbols not found", StringComparison.OrdinalIgnoreCase) == true);
            test.Run(() => { if (Section() is {} section) { section.Expanded = true; sideTree.OnToggled?.Invoke(section); } Filter(""); });
            test.Check("Modules: clearing the filter restores both modules without duplicates", () => Row(name) is not null
                && Section()?.Children?.Count(row => row.Text == Path.GetFileName(plugin) && row.Icon == "library") == 1);
            test.Run(debugger.RestartDebugging);
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == first, 60);
            test.Run(() => Command("debugging.modules"));
            test.WaitFor(() => Ready() && Row(name) is not null, 15);
            test.Check("Modules: " + language + " Restart starts a fresh list before loading the dependency", () => Row(name) is not null && Row(Path.GetFileName(plugin)) is null);
        }
        test.Run(() =>
        {
            Command("debugging.modules_filter");
            if (textDialog is { IsOpen: true } dialog) staleFilter = Descendants(dialog.Body).OfType<TextBox>().First().OnSubmitted;
            string replacement = Path.Combine(testFolder, "modules replacement " + language);
            Directory.CreateDirectory(replacement);
            explorer.OpenFolder(replacement);
            staleFilter?.Invoke("stale module filter");
        });
        test.WaitFor(() => !debugger.Debugging, 10);
        test.Check("Modules: " + language + " workspace replacement retires the view and stale filter", () => !debugger.Debugging && Section() is null && !Has("debugging.modules") && !Has("debugging.modules_filter"));
        test.Check("Modules: " + language + " inspection preserves source text", () => File.ReadAllText(file) == source);
        test.Run(() => { textDialog?.Close(); debugger.breakpoints.Clear(); });
    }
}
