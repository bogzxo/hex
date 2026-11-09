using Hex.Core.Notebooks;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test, a notebook exported as HTML the way it's done by hand. The command picked from the palette, the save
// dialog's own Save pressed with the pointer, a second export over the first only once Save is pressed again, and a
// cancelled one writing nothing. The notebook has an edit that isn't saved, which the page has and the notebook keeps
// unsaved. HEX_NOTEBOOK_HTML_SAMPLE names the notebook (the engineering one in Hex's assets otherwise) and
// HEX_NOTEBOOK_HTML_OUT a folder the page is copied to, for looking at it afterwards
internal sealed partial class DawnScene
{
    private void TestNotebookHtml(HexPointerScript test)
    {
        string sample = Environment.GetEnvironmentVariable("HEX_NOTEBOOK_HTML_SAMPLE") is { Length: > 0 } named && File.Exists(named)
            ? named : Path.Combine(Hex.Core.HexHost.ProgramFolder, "Assets", "hex", "notebooks", "engineering.ipynb");
        string file = Path.Combine(testFolder, "export sample.ipynb");
        string page = Path.Combine(testFolder, "export sample.html");
        const string MARKER = "Unsaved edit, exported as it is ✓";
        string onDisk = string.Empty;


        test.Run(() =>
        {
            File.Copy(sample, file, true);
            if (File.Exists(page))
                File.Delete(page);
            Open(file);
        });
        test.Wait();
        test.Wait();
        test.WaitFor(() => notebookShown is not null && cellViews.Count > 0, 20);

        // An edit not saved, the page should have it and the notebook should still be unsaved after
        test.Run(() =>
        {
            onDisk = File.ReadAllText(file);
            notebookShown!.Model.Cells.Add(new NotebookCell { Kind = CellKind.Markdown, Source = "## " + MARKER });
            notebookShown.Remake = true;
            NotebookChanged(notebookShown, now: true);
        });
        test.WaitFor(() => !notebookShown!.Changed, 5);

        test.Check("notebook html: the edit is in the notebook and not saved", () => document.Modified && File.ReadAllText(file) == onDisk);

        test.Check("notebook html: Export as HTML is in the Notebook menu and the palette", () =>
            menu.Menus.Any(bar => bar.Title == Said("notebook.menu") && bar.Everything().Any(pair => pair.Item.Label == Said("notebook.export_html")))
            && AllCommands().Any(command => command.Text.EndsWith(Said("notebook.export_html"), StringComparison.Ordinal)));

        void Export() => test.Run(() => AllCommands().First(command => command.Text.EndsWith(Said("notebook.export_html"), StringComparison.Ordinal)).Picked());
        Button DialogButton(string label) => Descendants(chrome.Root).OfType<WindowPanel>().SelectMany(window => Descendants(window)).OfType<Button>().Last(button => button.Label == label && button.EnabledInHierarchy);
        void Press(string label) => test.Click(() => DialogButton(label).Module!.ToWorld(DialogButton(label).Bounds.Center));

        Export();
        test.Wait();
        test.Run(() => Shot("notebook-html-dialog"));
        Press("Save");
        test.WaitFor(() => htmlExecution is null && htmlExport is null && File.Exists(page), 60);
        test.Settle(0.3f);
        test.Run(() => Shot("notebook-html-exported"));

        string html = string.Empty;
        test.Run(() => html = File.ReadAllText(page));
        test.Check("notebook html: the page is next to the notebook, named after it, with the unsaved edit in it", () =>
            html.StartsWith("<!DOCTYPE html>", StringComparison.Ordinal) && System.Net.WebUtility.HtmlDecode(html).Contains(MARKER, StringComparison.Ordinal)
            && html.Contains("<title>export sample</title>", StringComparison.Ordinal));
        test.Check("notebook html: exporting leaves the notebook unsaved", () =>
            document.Modified && File.ReadAllText(file) == onDisk);
        test.Check("notebook html: the page runs nothing and fetches nothing", () =>
            !html.Contains("<script", StringComparison.OrdinalIgnoreCase) && !html.Contains(" src=\"http", StringComparison.OrdinalIgnoreCase)
            && !html.Contains("<link", StringComparison.OrdinalIgnoreCase));
        test.Check("notebook html: every cell is in it in order", () =>
        {
            int code = notebookShown!.Model.Cells.Count(cell => cell.Kind == CellKind.Code);
            int markdown = notebookShown.Model.Cells.Count(cell => cell.Kind == CellKind.Markdown);
            return Occurrences(html, "<section class=\"cell code\">") == code && Occurrences(html, "<section class=\"cell markdown\">") == markdown
                && html.LastIndexOf("<section class=\"cell markdown\">", StringComparison.Ordinal) > html.LastIndexOf("<section class=\"cell code\">", StringComparison.Ordinal);
        });
        test.Check("notebook html: SVG outputs stay vector, never a picture of the screen", () =>
        {
            bool hasSvg = notebookShown!.Model.Cells.SelectMany(cell => cell.Outputs).Any(output => output["data"]?["image/svg+xml"] is not null);
            return !hasSvg || html.Contains("data:image/svg+xml;base64,", StringComparison.Ordinal);
        });

        // Again over the page that's there, only written once Save is pressed a second time
        DateTime first = DateTime.MinValue;
        test.Run(() =>
        {
            File.SetLastWriteTimeUtc(page, DateTime.UtcNow.AddMinutes(-5));
            first = File.GetLastWriteTimeUtc(page);
        });
        Export();
        test.Wait();
        Press("Save");
        test.Settle(0.3f);
        test.Check("notebook html: a page that's there isn't replaced on the first Save", () => File.GetLastWriteTimeUtc(page) == first && htmlExport is null);
        Press("Save");
        test.WaitFor(() => htmlExecution is null && htmlExport is null && File.GetLastWriteTimeUtc(page) != first, 60);
        test.Check("notebook html: and is on the second", () => File.GetLastWriteTimeUtc(page) != first && File.ReadAllText(page).Contains("<!DOCTYPE html>", StringComparison.Ordinal));

        // Cancelled, nothing written
        DateTime second = DateTime.MinValue;
        test.Run(() => second = File.GetLastWriteTimeUtc(page));
        Export();
        test.Wait();
        Press("Cancel");
        test.Settle(0.3f);
        test.Check("notebook html: a cancelled export writes nothing", () => File.GetLastWriteTimeUtc(page) == second && htmlExport is null);

        // Save Image As on an SVG output writes the SVG it came as
        test.Run(() =>
        {
            if (Environment.GetEnvironmentVariable("HEX_NOTEBOOK_HTML_OUT") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                File.Copy(page, Path.Combine(output, Path.GetFileName(page)), true);
            }
        });

        string svgFile = Path.Combine(testFolder, "export sample-output.svg");
        string? original = null;
        test.Run(() =>
        {
            if (File.Exists(svgFile))
                File.Delete(svgFile);
            if (notebookShown!.Model.Cells.SelectMany(cell => cell.Outputs).Select(output => output["data"]?["image/svg+xml"]).FirstOrDefault(svg => svg is not null) is { } svg)
            {
                original = NotebookFile.Text(svg);
                SaveOutputImage("image/svg+xml", svg);
            }
        });
        test.Wait();
        test.Run(() =>
        {
            if (original is not null)
                DialogButton("Save").OnPressed?.Invoke();
        });
        test.WaitFor(() => original is null || File.Exists(svgFile), 10);
        test.Check("notebook html: Save Image As writes an SVG output byte for byte", () => original is null || File.ReadAllText(svgFile) == original);

        // Execution failure must not replace a previously exported page.
        string previous = string.Empty;
        NotebookCell failure = new() { Kind = CellKind.Code, Source = "raise ValueError('HTML_EXPORT_FAILURE')" };
        test.Run(() =>
        {
            previous = File.ReadAllText(page);
            notebookShown!.Model.Cells.Add(failure);
            NotebookChanged(notebookShown, now: true);
            BeginHtmlExport(notebookShown, "export sample", page);
        });
        test.WaitFor(() => htmlExecution is null && htmlExport is null, 60);
        test.Check("notebook html: failed execution preserves the previous HTML", () =>
            failure.Outputs.Any(output => output["output_type"]?.GetValue<string>() == "error") && File.ReadAllText(page) == previous);
        test.Run(() =>
        {
            failure.Source = "print(6 * 7)";
            NotebookChanged(notebookShown!, now: true);
            BeginHtmlExport(notebookShown!, "export sample", page);
        });
        test.WaitFor(() => htmlExecution is null && htmlExport is null, 60);
        test.Check("notebook html: corrected code executes and exports fresh output", () =>
            notebookShown!.Run.Succeeded(failure) && failure.Outputs.Any(output => output["text"]?.GetValue<string>()?.Trim() == "42") &&
            File.ReadAllText(page) != previous);
        test.Run(() => notebookShown!.Model.Cells.Remove(failure));

        NotebookCell? slow = null;
        string originalSource = string.Empty;
        test.Run(() =>
        {
            previous = File.ReadAllText(page);
            slow = notebookShown!.Model.Cells.First(cell => cell.Kind == CellKind.Code);
            originalSource = slow.Source;
            slow.Source = "import time; time.sleep(10)";
            NotebookChanged(notebookShown, now: true);
            BeginHtmlExport(notebookShown, "export sample", page);
        });
        test.WaitFor(() => slow is not null && notebookShown!.Run.IsQueued(slow), 10);
        test.Run(() => InterruptKernel(notebookShown!));
        test.WaitFor(() => notebookShown!.Run.Queued.Count == 0, 10);
        test.Check("notebook html: interrupt cancels export and preserves the page", () =>
            htmlExecution is null && htmlExport is null && File.ReadAllText(page) == previous);
        test.Run(() =>
        {
            slow!.Source = originalSource;
            NotebookChanged(notebookShown!, now: true);
            BeginHtmlExport(notebookShown!, "export sample", page);
            slow.Source += "\n# changed during export";
        });
        test.WaitFor(() => htmlExecution is null, 10);
        test.Check("notebook html: edits during execution cannot export stale results", () =>
            htmlExport is null && File.ReadAllText(page) == previous);
        test.Run(() => slow!.Source = originalSource);

        // Put back as it was, nothing saved
        test.Run(() =>
        {
            notebookShown!.Model.Cells.RemoveAt(notebookShown.Model.Cells.Count - 1);
            notebookShown.Remake = true;
            NotebookChanged(notebookShown, now: true);
        });
        test.WaitFor(() => !notebookShown!.Changed, 5);
    }

    private static int Occurrences(string text, string what)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += what.Length;
        }

        return count;
    }
}
