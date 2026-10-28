using System.Diagnostics;
using Hex.Core.Notebooks;
using Hex.Dawn;
using Horizon.Graphics;

namespace Hex.Tests;

[Collection(Timed.NAME)]
public class NotebookEngineeringTests
{
    [Fact(Timeout = 180_000)]
    public async Task Engineering_notebook_executes_renders_and_roundtrips_in_a_real_kernel()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "hex", "notebooks", "engineering.ipynb");
        Notebook notebook = NotebookFile.Read(File.ReadAllText(path));
        KernelSpec? spec = KernelSpecs.Find().FirstOrDefault(spec => spec.Language == "python");
        Assert.NotNull(spec);
        await using var kernel = new Kernel(spec, Path.GetTempPath());
        await kernel.StartAsync(TimeSpan.FromSeconds(60));
        var run = new NotebookRun();
        foreach (var cell in notebook.Cells.Where(cell => cell.Kind == CellKind.Code))
        {
            run.Sent(cell, kernel.Execute(cell.Source));
            var timer = Stopwatch.StartNew();
            while (run.IsQueued(cell) && timer.Elapsed.TotalSeconds < 45)
            {
                while (kernel.Events.TryDequeue(out var message)) run.Take(message);
                await Task.Delay(10);
            }
            Assert.False(run.IsQueued(cell), "Engineering cell timed out: " + cell.Source);
            Assert.DoesNotContain(cell.Outputs, output => output["output_type"]?.GetValue<string>() == "error");
        }
        Assert.Contains(notebook.Cells.SelectMany(cell => cell.Outputs), output => NotebookFile.Text(output["text"]).Contains("ENGINEERING_SUITE_PASS"));
        var bundles = notebook.Cells.SelectMany(cell => cell.Outputs).Where(output => output["data"] is not null).Select(output => output["data"]!).ToList();
        string folder = Path.Combine(Path.GetTempPath(), "hex-engineering-proof");
        Directory.CreateDirectory(folder);
        int count = 0;
        foreach (var bundle in bundles)
        {
            ImagePixels? image = bundle["image/svg+xml"] is { } svg ? NotebookRender.Svg(NotebookFile.Text(svg))
                : bundle["text/latex"] is { } latex ? NotebookRender.Math(NotebookFile.Text(latex)) : null;
            if (image is { } pixels)
                ImageFile.SavePng(Path.Combine(folder, $"output-{count++}.png"), pixels.Pixels, pixels.Width, pixels.Height);
        }
        Assert.True(count >= 8);
        Assert.Contains(bundles, bundle => bundle["image/png"] is not null);
        Assert.Contains(bundles, bundle => bundle["text/html"] is not null);
        Assert.Contains(bundles, bundle => NotebookFile.Text(bundle["text/latex"]).Contains("1000"));
        string saved = NotebookFile.Write(notebook);
        File.WriteAllText(Path.Combine(folder, "engineering.executed.ipynb"), saved);
        Assert.Equal(saved, NotebookFile.Write(NotebookFile.Read(saved)));
        await kernel.ShutdownAsync();
    }
}
