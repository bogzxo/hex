using Hex.Core.Notebooks;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestNotebookEngineering(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "engineering.ipynb");
        test.Run(() =>
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "hex", "notebooks", "engineering.ipynb"), file, true);
            Open(file);
        });
        test.Wait();
        test.Wait();
        test.Check("engineering: inline and display Markdown math have picture components", () =>
            cellViews[0].Written!.Children.OfType<NotebookPicture>().Count() >= 3);
        test.Run(() => RunCells(notebookShown!, notebookShown!.Model.Cells));
        test.WaitFor(() => notebookShown!.Model.Cells[^1].Outputs.Count > 0 && notebookShown.Run.Queued.Count == 0 && !notebookShown.Starting, 120);
        test.Settle(0.5f);
        test.WaitFor(() => notebookRenders.Count == 0, 30);
        test.Check("engineering: all numerical checks passed without kernel errors", () =>
            notebookShown!.Model.Cells.SelectMany(cell => cell.Outputs).Any(output => NotebookFile.Text(output["text"]).Contains("ENGINEERING_SUITE_PASS"))
            && !notebookShown.Model.Cells.SelectMany(cell => cell.Outputs).Any(output => output["output_type"]?.GetValue<string>() == "error"));
        test.Check("engineering: LaTeX overrides plain repr and uploads a valid texture", () =>
            cellViews[2].Outputs.Children.OfType<NotebookPicture>().Any(picture => picture.Texture is { IsValid: true }));
        test.Check("engineering: Matplotlib SVG and PNG upload valid textures", () =>
            cellViews[7].Outputs.Children.OfType<NotebookPicture>().Count(picture => picture.Texture is { IsValid: true }) == 2);
        test.Check("engineering: summary table remains native", () =>
            cellViews[^1].Outputs.Children.OfType<StackPanel>().Any(panel => Descendants(panel).OfType<NotebookTableView>().Any(table => table.Rows.Count == 7)));
        test.Run(() => { PickCell(notebookShown!, 2, false); Shot("engineering-equation"); });
        test.Wait();
        Horizon.Graphics.Texture? mathTexture = null;
        string theme = settings.EditorTheme;
        float scale = compositor.UIScale;
        test.Run(() => { mathTexture = cellViews[2].Outputs.Children.OfType<NotebookPicture>().Single().Texture; UseEditorTheme("light", save: false); });
        test.WaitFor(() => compositor.Skin?.Theme == "light", 3);
        test.Wait();
        test.Check("engineering: math follows light-theme ink without rerasterizing", () =>
            cellViews[2].Outputs.Children.OfType<NotebookPicture>().Single() is { MathInk: true } math
            && math.Tint == compositor.Skin!.TextColor && ReferenceEquals(math.Texture, mathTexture));
        test.Run(() => { UseEditorTheme(theme, save: false); SetScale(2, remember: false); });
        test.Settle(0.2f);
        test.Check("engineering: narrow equations remain inside cell width", () =>
            cellViews[2].Outputs.Children.OfType<NotebookPicture>().All(picture => picture.Size.X <= cellViews[2].Body.Size.X + 1));
        test.Run(() => SetScale(scale, remember: false));
        test.Run(() => { PickCell(notebookShown!, 7, false); Shot("engineering-plot"); });
        test.Wait();
        test.WaitFor(() => !notebookShown!.Changed, 3);
        test.Run(Save);
        test.Wait();
        test.Check("engineering: saved notebook retains vector and math MIME bundles", () =>
            !document.Modified && NotebookFile.Read(File.ReadAllText(file)).Cells.SelectMany(cell => cell.Outputs).Any(output => output["data"]?["image/svg+xml"] is not null));

        NotebookTablePanel? tools = null;
        string csvFile = Path.Combine(testFolder, "engineering-table.csv");
        test.Run(() =>
        {
            tools = Descendants(cellViews[^1].Outputs).OfType<NotebookTablePanel>().Single();
            PickCell(notebookShown!, notebookShown!.Model.Cells.Count - 1, false);
        });
        test.Settle(0.2f);
        test.Check("engineering: compact CSV strip sits above the table", () => tools is not null
            && tools.CopyCsv.OnPressed is not null && tools.ExportCsv.OnPressed is not null
            && tools.CopyCsv.Bounds.Min.Y >= tools.Table.Bounds.Max.Y);
        test.Click(() => tools!.Table.Module!.ToWorld(new System.Numerics.Vector2(
            tools.Table.Bounds.Min.X + tools.Table.Columns[0].Width + 30, tools.Table.Bounds.Max.Y - 12)));
        test.Wait();
        test.Check("engineering: numeric header sorts rows for CSV", () =>
            tools!.Table.RowOrder.Select(row => double.Parse(tools.Table.Rows[row][1].Text, System.Globalization.CultureInfo.InvariantCulture))
                .SequenceEqual(tools.Table.Rows.Select(row => double.Parse(row[1].Text, System.Globalization.CultureInfo.InvariantCulture)).Order()));
        test.Run(() => tools!.ExportTo(csvFile));
        test.Check("engineering: exported UTF-8 CSV matches copied text and includes every row", () =>
            File.ReadAllText(csvFile) == tools!.Csv() && tools.Csv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length == 8
            && tools.Csv().StartsWith("Calculation,Value,Unit\r\n", StringComparison.Ordinal));
        test.Hover(() => tools!.Table.Module!.ToWorld(tools.Table.CellBounds(tools.Table.RowOrder[1], 1)!.Value.Center));
        test.Run(() => Shot("engineering-table-hover"));
        bool compactTools = false;
        test.Run(() =>
        {
            FitWritten(cellViews[^1].Outputs, 120);
            compactTools = tools!.CopyCsv.Label.Length == 0 && tools.ExportCsv.Label.Length == 0
                && tools.CopyCsv.Size.X + tools.ExportCsv.Size.X + 4 <= 120 && !tools.Table.FreezeFirst;
        });
        test.Check("engineering: narrow CSV controls stay compact", () => compactTools);

        test.Run(() =>
        {
            var attachment = new NotebookCell { Kind = CellKind.Markdown, Source = "![beam](attachment:beam.svg)" };
            attachment.Rest["attachments"] = new System.Text.Json.Nodes.JsonObject
            {
                ["beam.svg"] = new System.Text.Json.Nodes.JsonObject { ["image/svg+xml"] = "<svg xmlns='http://www.w3.org/2000/svg' width='80' height='30'><rect width='80' height='30' fill='blue'/></svg>" }
            };
            var broken = new NotebookCell { Kind = CellKind.Code };
            broken.Outputs.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["output_type"] = "display_data", ["data"] = new System.Text.Json.Nodes.JsonObject { ["image/svg+xml"] = "<broken", ["text/plain"] = "Readable fallback" }
            });
            notebookShown!.Model.Cells.Add(attachment);
            notebookShown.Model.Cells.Add(broken);
            notebookShown.Remake = true;
        });
        test.Wait();
        test.WaitFor(() => notebookRenders.Count == 0, 20);
        test.Check("engineering: embedded SVG attachments upload a texture", () =>
            Descendants(cellViews[^2].Written!).OfType<NotebookPicture>().Any(picture => picture.Texture is { IsValid: true }));
        test.Check("engineering: malformed vector output shows its plain fallback", () =>
            cellViews[^1].Outputs.Children.OfType<NotebookPicture>().Single().RenderStatus?.Text == "Readable fallback");

        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        NotebookPicture? canceled = null;
        test.Run(() =>
        {
            var pictures = cellPictures[cellViews[^1]];
            canceled = RenderedPicture(System.Text.Json.Nodes.JsonValue.Create("cancel-test")!, () =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return NotebookRender.Svg("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'/>");
            }, pictures);
            cellViews[^1].Outputs.Add(canceled!);
        });
        test.WaitFor(() => started.IsSet, 5);
        test.Check("engineering: a blocked CPU renderer leaves UI steps responsive", () => canceled is { Texture: null } && started.IsSet);
        CloseNotebookTest(test, file);
        test.Run(() => release.Set());
        test.Settle(0.2f);
        test.Check("engineering: closing releases cell picture ownership", () => cellPictures.Count == 0 && markdownPictures.Count == 0 && notebookRenders.Count == 0);
        test.Check("engineering: canceled work never uploads a stale texture", () => canceled!.Texture is null);
    }
}
