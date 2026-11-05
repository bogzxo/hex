using System.Text;
using System.Text.Json.Nodes;

using Hex.Core.Notebooks;
using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The notebook on screen saved as one HTML page (Notebook, Export as HTML...), the cells and outputs as they are now,
// unsaved ones too, without running anything or touching the notebook's own file. Asked where with the same dialog as
// every save, made and written off the editor's thread, said when it's done with a way to open it. An output's picture
// can be saved as the file it came as from its right click (an SVG as its SVG)
internal sealed partial class DawnScene
{
    // The export being written, where to, and null once it's done and said
    private Task? htmlExport;
    private string htmlExportTarget = string.Empty;

    /// <summary>The notebook as an HTML page, saved where it's asked to be.</summary>
    private void ExportNotebookHtml(HexNotebook notebook)
    {
        if (htmlExport is { IsCompleted: false })
        {
            Say(Said("notebook.html_busy"));
            return;
        }

        HexDocument document = notebook.Document;
        string title = Path.GetFileNameWithoutExtension(document.Path ?? document.Name);
        string directory = document.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path))!
            : explorer.workspace.IsEmpty ? browseDirectory : explorer.workspace.Folders[0];
        Ask(HexDialogs.SaveFile(directory, title + ".html", [(Said("dialogs.files_of_kind", ("kind", ".html")), "*.html")], DialogHost), target =>
        {
            if (!target.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !target.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                target += ".html";

            // What it is now, copied on this thread, so a kernel's output coming in or typing meanwhile can't reach it
            Notebook now = NotebookSnapshot(notebook.Model);
            var options = new NotebookHtmlOptions { Title = title, Math = NotebookRender.MathSvg };
            htmlExportTarget = target;
            Say(Said("notebook.html_exporting", ("name", Path.GetFileName(target))));
            htmlExport = Task.Run(() => File.WriteAllText(target, NotebookHtmlExport.Write(now, options), new UTF8Encoding(false)));
            htmlExport.ContinueWith(_ => Engine.WindowManager.Wake(), TaskScheduler.Default);
        });
    }

    /// <summary>A copy of a notebook that shares nothing it could change with the one being edited.</summary>
    internal static Notebook NotebookSnapshot(Notebook model)
    {
        var copy = new Notebook
        {
            Format = model.Format,
            FormatMinor = model.FormatMinor,
            Metadata = (JsonObject)model.Metadata.DeepClone(),
            Rest = (JsonObject)model.Rest.DeepClone()
        };
        foreach (NotebookCell cell in model.Cells)
        {
            var kept = new NotebookCell
            {
                Kind = cell.Kind,
                Source = cell.Source,
                Id = cell.Id,
                ExecutionCount = cell.ExecutionCount,
                Metadata = (JsonObject)cell.Metadata.DeepClone(),
                Rest = (JsonObject)cell.Rest.DeepClone()
            };
            foreach (JsonObject output in cell.Outputs)
                kept.Outputs.Add((JsonObject)output.DeepClone());
            copy.Cells.Add(kept);
        }

        return copy;
    }

    // Every tick, an export that's finished said, with a way to open what it wrote
    private void UpdateHtmlExport()
    {
        if (htmlExport is not { IsCompleted: true } done)
            return;

        htmlExport = null;
        string target = htmlExportTarget;
        if (done.Exception is { } failed)
        {
            Notify(Said("notebook.html_failed", ("why", (failed.InnerException ?? failed).Message)), error: true);
            return;
        }

        // A tablet has no browser Hex can hand a file to yet, it's said where the page is and shown in the explorer
        if (OperatingSystem.IsAndroid())
        {
            bool inWorkspace = explorer.workspace.Folders.Any(folder => Path.GetFullPath(target).StartsWith(Path.GetFullPath(folder), StringComparison.Ordinal));
            string said = Said("notebook.html_exported_here", ("name", Path.GetFileName(target)), ("folder", Path.GetDirectoryName(target) ?? target));
            if (inWorkspace)
                Notify(said, actions: (Said("notebook.html_show"), () => ShowExported(target)));
            else
                Notify(said);
            return;
        }

        Notify(Said("notebook.html_exported", ("name", Path.GetFileName(target))), actions: (Said("notebook.html_open"), () => OpenInBrowser(target)));
    }

    // Helper method for an exported file shown in the explorer, its folders opened up to it
    private void ShowExported(string target)
    {
        explorer.OpenUpTo(target);
        left.Show(true);
        Side = HexSide.Explorer;
    }

    /* An output's picture saved as it came */

    // Helper method for a right click on an output's picture that saves it as the file it came as, nothing redrawn
    private void OfferSaveImage(NotebookPicture picture, string mime, JsonNode data)
    {
        picture.OnContextMenu = _ =>
        {
            var menu = new Menu("picture");
            menu.Add(Said("notebook.save_image"), () => SaveOutputImage(mime, data), icon: "save");
            contextMenu = ContextMenu.ShowAt(chrome, compositor.Pointer.Position, menu);
        };
    }

    private void SaveOutputImage(string mime, JsonNode data)
    {
        string extension = mime switch { "image/svg+xml" => ".svg", "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".png" };
        HexDocument document = this.document;
        string name = Path.GetFileNameWithoutExtension(document.Path ?? document.Name) + "-output" + extension;
        string directory = document.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path))! : browseDirectory;
        string text = NotebookFile.Text(data);
        Ask(HexDialogs.SaveFile(directory, name, [(Said("dialogs.files_of_kind", ("kind", extension)), "*" + extension)], DialogHost), target =>
        {
            if (!Path.HasExtension(target))
                target += extension;
            try
            {
                // An SVG as its own text, a raster picture as the bytes its base64 was, so nothing is lost
                if (mime == "image/svg+xml")
                    File.WriteAllText(target, text, new UTF8Encoding(false));
                else
                    File.WriteAllBytes(target, Convert.FromBase64String(string.Concat(text.Where(c => !char.IsWhiteSpace(c)))));
                Notify(Said("notebook.image_saved", ("name", Path.GetFileName(target))));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException)
            {
                Notify(Said("notebook.image_failed", ("why", error.Message)), error: true);
            }
        });
    }
}
