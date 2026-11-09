using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestRenameSafety(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "rename_safety"), file = Path.Combine(folder, "Api", "Counter.cs"), other = Path.Combine(folder, "App", "Usage.cs");
        const string source = "namespace Review;\npublic class Counter {\n    public int Length = 1;\n    public int Read(string text) => text.Length + Length;\n}\n";
        const string usage = "namespace Review;\npublic class Usage { public int Read(Counter counter) => counter.Length; }\n";
        HexDocument? counter = null;
        int before = 0;
        test.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            File.WriteAllText(Path.Combine(folder, "Api", "Api.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(folder, "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Api/Api.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(folder, "RenameSafety.slnx"), "<Solution><Project Path=\"Api/Api.csproj\" /><Project Path=\"App/App.csproj\" /></Solution>");
            File.WriteAllText(file, source);
            File.WriteAllText(other, usage);
            explorer.OpenFolder(folder);
            Open(file);
            counter = document;
        });
        test.WaitFor(() => languages.CanServe(counter!, "renameProvider") && code.SemanticSpans.Count > 0, 90f);
        test.Check("rename safety: real C# server is ready", () => languages.CanServe(counter!, "renameProvider") && code.SemanticSpans.Count > 0);

        void RenameAt(int line, int column, string name)
        {
            test.Run(() =>
            {
                Show(counter!);
                code.GoTo(line, column);
                before = said.Count;
                codeEditing.RenameSymbol();
            });
            test.WaitFor(() => textDialog is { IsOpen: true } || said.Skip(before).Any(entry => entry.Error), 10f);
            test.Run(() =>
            {
                if (textDialog is not { IsOpen: true } dialog) return;
                var input = Descendants(dialog.Body).OfType<TextBox>().First();
                input.Text = name;
                input.OnSubmitted?.Invoke(name);
            });
            test.WaitFor(() => textDialog is not { IsOpen: true }, 3f);
        }
        RenameAt(3, 42, "Magnitude");
        test.WaitFor(() => said.Skip(before).Any(entry => entry.Error) || counter!.Editor.Text != source, 20f);
        test.Check("rename safety: framework property rejection preserves unrelated names", () => counter!.Editor.Text == source);
        test.Check("rename safety: rejected rename explains its outcome", () => said.Skip(before).Any(entry => entry.Error && entry.Text.Contains("Length", StringComparison.Ordinal)));
        test.Check("rename safety: rejected rename leaves both disk files unchanged", () => File.ReadAllText(file) == source && File.ReadAllText(other) == usage);

        RenameAt(2, 16, "Size");
        test.WaitFor(() => counter!.Editor.Text.Contains("int Size", StringComparison.Ordinal), 20f);
        test.Check("rename safety: valid symbol rename changes declaration and own reference only", () =>
            counter!.Editor.Text.Contains("int Size", StringComparison.Ordinal) && counter.Editor.Text.Contains("text.Length + Size", StringComparison.Ordinal));
        test.Check("rename safety: valid rename updates a reference in the other project", () =>
            OpenDocument(other)?.Editor.Text.Contains("counter.Size", StringComparison.Ordinal) == true);
        test.Check("rename safety: semantic rename never implicitly saves", () => File.ReadAllText(file) == source && File.ReadAllText(other) == usage);
        test.Run(() =>
        {
            foreach (string path in new[] { file, other })
                if (OpenDocument(path) is { } open) { Show(open); CloseNow(); }
        });
        test.Wait();
    }
}
