using Hex.Core.Projects;

using Horizon.HIDL.Runtime;
using Horizon.UI;

namespace Hex.Dawn;

// The self test of the Properties panel, one panel for whatever is selected. A file of a C# project made for it with its
// Copy to Output Directory flipped (the .csproj changed by the one item and the SDK agreeing), two files picked in the
// explorer with Ctrl given a Build Action together, a folder, a picture, a component of a layout with the designer's rows
// filtered, and a file renamed from its name row.
internal sealed partial class DawnScene
{
    private void TestProperties(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "props"), project = Path.Combine(folder, "Props.csproj");
        string data = Path.Combine(folder, "data.txt"), assets = Path.Combine(folder, "assets");
        string first = Path.Combine(assets, "a.json"), second = Path.Combine(assets, "b.json"), picture = Path.Combine(folder, "pic.png");
        string original = $"<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <!-- made by the self test -->\r\n  <PropertyGroup>\r\n    <OutputType>Exe</OutputType>\r\n    <TargetFramework>net{System.Environment.Version.Major}.0</TargetFramework>\r\n  </PropertyGroup>\r\n\r\n</Project>\r\n";
        string? shots = System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } asked ? asked : null;
        void Shot(string name)
        {
            if (shots is not null)
                Engine.CaptureScreenshot(Path.Combine(shots, name));
        }

        string Value(string key) => properties.Page.ValueText(key);

        // Whether a component is on screen, it and everything it's in visible
        static bool Shown(UIComponent component)
        {
            for (UIComponent? at = component; at is not null; at = at.Parent)
            {
                if (!at.Visible)
                    return false;
            }

            return true;
        }

        test.Run(() =>
        {
            Directory.CreateDirectory(assets);
            File.WriteAllText(project, original);
            File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"hi\");\n");
            File.WriteAllText(data, "some data\r\nmore\r\n");
            File.WriteAllText(first, "{}\n");
            File.WriteAllText(second, "{}\n");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "hex", "images", "background_icon.png"), picture, overwrite: true);
            explorer.OpenFolder(folder);
            Open(data);
        });
        test.Wait();

        /* A file of a project, the panel up with Alt+Enter's command */

        test.Run(() => ShowPropertiesOf(null));
        test.WaitFor(() => properties.Shows == HexPropertiesShows.Files && properties.ShownProject is not null && !properties.ProjectBusy, 90.0f);
        test.Settle(0.3f);
        test.Check("properties: Alt+Enter brings the panel up with the file on screen", () =>
            right.Shown && properties.Page.Visible && !inspectorScroll.Visible && properties.Shown.SequenceEqual([data])
            && Value("path") == data && Value("name") == "data.txt" && CommandKey("file_properties.command") == "Alt+Enter");
        test.Check("properties: how its text is written, CRLF, and the project it's in", () =>
            Value("line_endings") == "CRLF (\\r\\n)" && Value("encoding").StartsWith("UTF-8") && Value("lines") == "3"
            && PlatformPaths.Same(properties.ShownProject!, project) && Value("build_action") == "None" && Value("copy_to_output") == "Do not copy");
        test.Check("properties: a value's right click copies it", () =>
            properties.Page.MenuOf("path") is { } menu && menu.Items.Any(item => item.Label == Said("file_properties.copy_value")));

        test.Run(() => properties.Page.Enter("copy_to_output", new StringValue(DotnetEvaluation.IF_NEWER)));
        test.WaitFor(() => !properties.ProjectBusy && File.ReadAllText(project).Contains("PreserveNewest"), 60.0f);
        test.Settle(0.5f);
        test.Run(() => Shot("properties_csproj.png"));
        test.Settle(1.0f);
        test.Check("properties: Copy if newer is one Update item in the .csproj, the rest of it as it was", () =>
            File.ReadAllText(project) == ProjectFileEdits.SetMetadata(original, "None", "data.txt", ProjectFileEdits.COPY_TO_OUTPUT, DotnetEvaluation.IF_NEWER)
            && File.ReadAllText(project).Contains("\r\n    <None Update=\"data.txt\">\r\n      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>\r\n    </None>\r\n"));
        test.Check("properties: and the SDK says so too, where it lands is said", () =>
            DotnetProjects.Shared.Known(project)?.BuildItemOf(data)?.CopyToOutput == DotnetEvaluation.IF_NEWER
            && Value("copy_to_output") == "Copy if newer" && properties.Page.IsModified?.Invoke("copy_to_output") == true
            && Value("output").EndsWith(Path.Combine("bin", "Debug", $"net{System.Environment.Version.Major}.0", "data.txt"), StringComparison.OrdinalIgnoreCase));

        test.Run(() => properties.Page.Reset("copy_to_output"));
        test.WaitFor(() => !properties.ProjectBusy && !File.ReadAllText(project).Contains("PreserveNewest"), 60.0f);
        test.Check("properties: reset to Do not copy and the .csproj is what it was", () => File.ReadAllText(project) == original);

        /* Two files picked with Ctrl in the explorer, given a Build Action together */

        test.Run(() =>
        {
            explorer.workspace.Expanded.Add(Path.GetFullPath(assets));
            treeDirty = true;
        });
        test.Wait();
        test.Run(() =>
        {
            ShowSide(HexSide.Explorer);
            var rows = explorer.explorerRows.Where(row => PlatformPaths.Same(row.Path, first) || PlatformPaths.Same(row.Path, second)).Select(row => row.Row).ToList();
            sideTree.SelectMany(rows);
        });
        test.WaitFor(() => properties.Shown.Count == 2 && properties.ShownProject is not null && !properties.ProjectBusy, 60.0f);
        test.Check("properties: two files picked in the explorer are shown together", () =>
            sideTree.SelectedItems.Count == 2 && Value("name") == Said("file_properties.count_items", ("count", "2")) && Value("build_action") == "None");
        test.Run(() => properties.Page.Enter("build_action", new StringValue("Content")));
        test.WaitFor(() => !properties.ProjectBusy && File.ReadAllText(project).Contains("Content Include"), 60.0f);
        test.Check("properties: a Build Action picked for both is the SDK's for both", () =>
            DotnetProjects.Shared.Known(project) is { } known && known.BuildItemOf(first)?.Type == "Content" && known.BuildItemOf(second)?.Type == "Content"
            && Value("build_action") == "Content");

        /* The folder they're in, Copy always for everything in it */

        test.Run(() => properties.Follow([assets]));
        test.WaitFor(() => properties.Shown.SequenceEqual([Path.GetFullPath(assets)]) && !properties.ProjectBusy, 30.0f);
        test.Run(() => properties.Page.Enter("copy_to_output", new StringValue(DotnetEvaluation.ALWAYS)));
        test.WaitFor(() => !properties.ProjectBusy && File.ReadAllText(project).Contains("Always"), 60.0f);
        test.Check("properties: a folder's setting goes to every file of the project in it", () =>
            DotnetProjects.Shared.Known(project) is { } known && known.BuildItemOf(first)?.CopyToOutput == DotnetEvaluation.ALWAYS
            && known.BuildItemOf(second)?.CopyToOutput == DotnetEvaluation.ALWAYS && Value("contents").Contains('2'));
        int rowsBefore = 0;
        test.Run(() =>
        {
            rowsBefore = properties.Page.RowsShown;
            properties.Page.ToggleSection(Said("file_properties.folder"));
        });
        test.Check("properties: a heading folds its section away", () =>
            properties.Page.IsFolded(Said("file_properties.folder")) && properties.Page.RowsShown < rowsBefore);
        test.Run(() => properties.Page.ToggleSection(Said("file_properties.folder")));

        /* A picture */

        test.Run(() => Open(picture));
        test.Wait();
        test.Run(() => ShowPropertiesOf(null));
        test.WaitFor(() => properties.Shown.SequenceEqual([picture]) && !properties.ProjectBusy, 30.0f);
        test.Settle(0.5f);
        test.Run(() => Shot("properties_image.png"));
        test.Settle(1.0f);
        test.Check("properties: a picture says how big it is and what it is, and nothing of text", () =>
            right.Shown && imageView.Visible && properties.Page.Visible && Value("format") == "PNG" && Value("dimensions").Contains(" x ") && Value("dimensions").EndsWith(" pixels") && properties.Page.RowBounds("encoding") is null);

        /* A file renamed from its name row */

        test.Run(() =>
        {
            Open(data);
            properties.Follow([data]);
        });
        test.Wait();
        test.Run(() => properties.Page.Enter("name", new StringValue("renamed.txt")));
        test.Wait();
        test.Check("properties: a new name typed in the name row renames the file", () =>
            File.Exists(Path.Combine(folder, "renamed.txt")) && !File.Exists(data) && document.Path is { } now && PlatformPaths.Same(now, Path.Combine(folder, "renamed.txt")));

        /* A component of a layout, the designer's rows in the same panel, filtered */

        UIComponent? picked = null;
        test.Run(() =>
        {
            Open(savedFile);
            SwitchView(HexView.Design);
        });
        test.Wait();
        test.Run(() =>
        {
            picked = Descendants(document.Module.Root).FirstOrDefault(component => component is Horizon.UI.Components.Button) ?? Descendants(document.Module.Root).FirstOrDefault();
            designer.Select(picked);
        });
        test.Wait();
        test.Check("properties: a component picked in the designer has its own rows in the panel", () =>
            picked is not null && properties.Shows == HexPropertiesShows.Component && inspectorScroll.Visible && !properties.Page.Visible
            && properties.Filter.Visible && designer.editors.ContainsKey("size"));
        test.Run(() => properties.Filter.Text = "size");
        test.Run(() => designer.FilterRows(properties.Filter.Text));
        test.Wait();
        test.Run(() => Shot("properties_component.png"));
        test.Settle(1.0f);
        test.Check("properties: the filter narrows the rows down to what's typed", () =>
            Shown(designer.editors["size"]) && !Shown(designer.editors["name"]));
        test.Run(() =>
        {
            properties.Filter.Text = string.Empty;
            designer.FilterRows(string.Empty);
            designer.Select(null);
        });
        test.Wait();
        test.Check("properties: nothing picked in a saved layout and it's the layout file's properties", () =>
            properties.Shows == HexPropertiesShows.Files && properties.Shown.SequenceEqual([Path.GetFullPath(savedFile)]));

        test.Run(() =>
        {
            properties.Follow(null);
            CloseAll();
        });
        test.Wait();
    }
}
