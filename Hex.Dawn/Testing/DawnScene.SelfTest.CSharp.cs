using Horizon.UI.Scripting;

namespace Hex.Dawn;

// The self test of C# through the real language server (csharp-ls, see language_servers.hor), left out when it isn't on
// this machine. A file with a mistake in it is opened before there is a project for it, so the server is going when the
// project is made (a clone, dotnet new, while Hex is open) and has to hear of it from the workspace's watcher: then the
// mistake is underlined, and Console. offers Console's members as they're typed, the way the editor asks.
internal sealed partial class HexScene
{
    private const string CSHARP_PROJECT = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>\n";

    private void TestCSharp(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "selftest_csharp"), program = Path.Combine(folder, "Program.cs");
        bool there = HexServerCatalog.Locate("csharp-ls") is not null;
        test.Run(() =>
        {
            if (!there)
            {
                Console.WriteLine("[Hex self-test] csharp-ls isn't on this machine, the C# part is skipped");
                return;
            }

            Directory.CreateDirectory(folder);
            File.WriteAllText(program, "class Program\n{\n    static void Main()\n    {\n        int count = \"three\";\n        \n    }\n}\n");
            Open(program);
        });
        if (!there)
            return;

        test.WaitFor(() => languages.LinterOf(document) is not null, 10.0f);
        test.Check("csharp: a .cs file is opened in csharp-ls", () => languages.LinterOf(document) == "csharp");

        // The project after the server is going
        test.Settle(1.0f);
        test.Run(() => File.WriteAllText(Path.Combine(folder, "selftest_csharp.csproj"), CSHARP_PROJECT));

        // Loading the project is the server's slow part, a minute at the most
        test.WaitFor(() => code.Diagnostics.Any(found => found.Message.Contains("CS0029")), 90.0f);
        test.Check("csharp: a project made after the server started is heard of, and what's wrong is underlined the way it says", () =>
            code.Diagnostics.Any(found => found.Line == 4 && found.Message.Contains("CS0029") && !found.Warning));

        // Typed on the empty line as a key would, the dot asking for what can follow it
        test.Run(() =>
        {
            code.Focus();
            code.Document.MoveTo(new Horizon.UI.Components.UITextPosition(5, 8));
            code.Document.Insert("System.Console.");
            code.Complete(typed: true);
        });
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Text == "WriteLine"), 30.0f);
        test.Check("csharp: after Console. the list has its members, from the server", () =>
            code.CompletionOpen && code.Completions.Any(item => item.Text == "WriteLine"));

        test.Run(() =>
        {
            code.Document.Insert("Wri");
            code.Complete(typed: true);
        });
        test.WaitFor(() => code.CompletionOpen && code.Completions.Count > 0 && code.Completions.All(item => item.Text.Contains("Wri", StringComparison.OrdinalIgnoreCase)), 10.0f);
        test.Check("csharp: typing on narrows it down", () =>
            code.CompletionOpen && code.Completions.Any(item => item.Text == "WriteLine") && code.Completions.All(item => item.Text.Contains("Wri", StringComparison.OrdinalIgnoreCase)));

        test.Run(() =>
        {
            code.CloseCompletion();
            CloseNow();
            LanguageServers.StopAll();
        });
    }
}
