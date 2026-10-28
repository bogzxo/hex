using System.Numerics;

using Hex.Core.Notebooks;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Jupyter notebooks, natively. A .ipynb opens as a document of text like any other (its JSON is what's saved, backed up,
// reloaded from disk and shown by the Code view) and comes up on its Notebook view, the cells one under the other, code
// typed into an editor of its own with the language's colours, markdown written out, and under each code cell what
// running it came to. The cells are read out of the JSON as the notebook comes up and whenever the JSON changes from
// under them, and written back into it a moment after they change, so saving, undo, the dirty dot and the disk all work
// as they do for every file. Running goes through a kernel Hex talks to itself (Hex.Core.Notebooks), see
// DawnScene.NotebookKernel. The cells are DawnScene.NotebookCells, their outputs DawnScene.NotebookOutputs, the keys and
// commands DawnScene.NotebookCommands.
internal sealed partial class DawnScene
{
    // How long after the last change to the cells their JSON is written, and how tall the bar over them is
    private const float NOTEBOOK_WRITE_AFTER = 0.3f;
    private const float NOTEBOOK_BAR = 34.0f;

    private Panel notebookView = null!;
    private StackPanel notebookBar = null!;
    private ScrollPanel notebookScroll = null!;
    private StackPanel notebookCells = null!;
    private Button kernelButton = null!;
    private Label notebookProblem = null!;

    private readonly Dictionary<HexDocument, HexNotebook> notebooks = [];

    // The notebook whose cells are on screen, null while none is
    private HexNotebook? notebookShown;

    /// <summary>Whether a file is a Jupyter notebook, by its name.</summary>
    internal static bool IsNotebookFile(string? path) => path is not null && path.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a document is a notebook that can be shown as its cells (not a diff of one, not a picture).</summary>
    private static bool IsNotebook(HexDocument of) => of.IsText && of.Picture is null && of.DiffOf is null && IsNotebookFile(of.Path);

    /// <summary>Whether the notebook view is what's on screen.</summary>
    private bool NotebookShown => notebookShown is not null && notebookView.Visible;

    private void BuildNotebookView()
    {
        notebookView = layout.Get<Panel>("notebook_view");
        notebookView.Visible = false;
        Engine.WindowManager.Exclusive += _ => MakeOutputPictures();
        notebookView.Color = compositor.Skin is { } skin ? skin.PanelColor with { W = 1.0f } : new Vector4(0.08f, 0.09f, 0.12f, 1.0f);

        notebookBar = notebookView.Add(new StackPanel
        {
            Direction = UIDirection.Horizontal,
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft,
            Position = new Vector2(8.0f, -4.0f),
            Spacing = 2.0f
        });
        BuildNotebookBar();

        notebookScroll = notebookView.Add(new NotebookScrollPanel
        {
            Zoom = delta => ZoomCode(delta > 0 ? CODE_ZOOM_STEP : 1 / CODE_ZOOM_STEP),
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft,
            Position = new Vector2(0.0f, -NOTEBOOK_BAR),
            Clip = true
        });
        notebookCells = notebookScroll.Add(new StackPanel
        {
            Direction = UIDirection.Vertical,
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft,
            Padding = new UIEdges(10.0f, 10.0f, 8.0f, 60.0f),
            Spacing = 6.0f
        });

        // What's wrong with JSON that can't be read as a notebook, in place of its cells
        notebookProblem = notebookView.Add(new Label
        {
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft,
            Position = new Vector2(16.0f, -NOTEBOOK_BAR - 12.0f),
            Wrap = true,
            Align = Origin.TopLeft,
            TextScale = 0.15f,
            Visible = false
        });

        layout.Get<Button>("notebook_button").OnPressed = () => SwitchView(HexView.Notebook);
        documentClosed += CloseNotebook;
    }

    /// <summary>The notebook of a document, read from its JSON the first time it's asked for.</summary>
    private HexNotebook NotebookOf(HexDocument of)
    {
        if (!notebooks.TryGetValue(of, out HexNotebook? notebook))
        {
            notebooks[of] = notebook = new HexNotebook { Document = of };
            ReadNotebook(notebook);
        }

        return notebook;
    }

    // Helper method to read the cells out of the document's JSON, what's wrong with it said when it can't be
    private void ReadNotebook(HexNotebook notebook)
    {
        string json = notebook.Document.Editor.Text;
        try
        {
            notebook.Model = NotebookFile.Read(json);
            notebook.Unreadable = null;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            notebook.Unreadable = e.Message;
        }

        notebook.Json = json;
        notebook.JsonVersion = notebook.Document.Editor.Document.Version;
        notebook.Changed = false;
        notebook.Selected = Math.Clamp(notebook.Selected, 0, Math.Max(0, notebook.Model.Cells.Count - 1));
        notebook.Remake = true;
    }

    /// <summary>The cells changed, their JSON written a moment from now (now, for a change that isn't typing).</summary>
    private void NotebookChanged(HexNotebook notebook, bool now = false)
    {
        notebook.Changed = true;
        notebook.WriteIn = now ? 0.0f : NOTEBOOK_WRITE_AFTER;
    }

    // Helper method to write the cells into the document as its JSON, one step to undo, only when it's something else
    private void WriteNotebook(HexNotebook notebook)
    {
        notebook.Changed = false;
        if (notebook.Unreadable is not null)
            return;

        // Against what was last read or written, the document's own text is the whole of it made again
        string json = NotebookFile.Write(notebook.Model);
        if (string.Equals(json, notebook.Json, StringComparison.Ordinal) && notebook.Document.Editor.Document.Version == notebook.JsonVersion)
            return;

        Rewrite(notebook.Document, json);
        notebook.Json = json;
        notebook.JsonVersion = notebook.Document.Editor.Document.Version;
        notebook.Document.Modified = !notebook.Document.Editor.Document.TextEquals(notebook.Document.SavedCode) || notebook.Document.CrLf != notebook.Document.SavedCrLf;
        tabsDirty = true;
    }

    /// <summary>Whatever the cells have that the JSON hasn't yet written into it, before the file's saved.</summary>
    private void FlushNotebook(HexDocument of)
    {
        if (notebooks.TryGetValue(of, out HexNotebook? notebook) && notebook.Changed)
            WriteNotebook(notebook);
    }

    /// <summary>A notebook's view put up or taken down as ApplyView decides what's in the middle.</summary>
    private void ApplyNotebookView(bool shown)
    {
        notebookView.Visible = shown;
        HexNotebook? showing = shown ? NotebookOf(document) : null;
        if (showing != notebookShown)
        {
            notebookShown = showing;
            if (showing is not null)
                showing.Remake = true;
            else
                ClearCellViews();
        }

        layout.Get<Button>("notebook_button").Visible = IsNotebook(document);
        layout.Get<Button>("notebook_button").Selected = shown;
    }

    /// <summary>A notebook closed, its kernel stopped with it.</summary>
    private void CloseNotebook(HexDocument closed)
    {
        if (!notebooks.Remove(closed, out HexNotebook? notebook))
            return;

        if (notebook == notebookShown)
        {
            notebookShown = null;
            ClearCellViews();
        }

        StopKernel(notebook);
    }

    /// <summary>
    /// Every tick, what every kernel said taken in, and for the notebook on screen its JSON read again when it changed
    /// from under the cells, written when the cells changed, the cells made again when they have to be, and everything
    /// fitted to the width there is.
    /// </summary>
    private void UpdateNotebooks(float dt)
    {
        foreach (HexNotebook notebook in notebooks.Values)
        {
            TakeKernelEvents(notebook);
            if (notebook.Changed && (notebook.WriteIn -= dt) <= 0.0f)
                WriteNotebook(notebook);
        }

        if (notebookShown is not { } shown || !notebookView.Visible)
            return;

        // The JSON changed from under the cells, typed in the Code view, an undo, or read again from the disk
        if (!shown.Changed && shown.Document.Editor.Document.Version != shown.JsonVersion)
            ReadNotebook(shown);

        notebookProblem.Visible = shown.Unreadable is not null;
        notebookScroll.Visible = shown.Unreadable is null;
        if (shown.Unreadable is { } why)
            notebookProblem.Text = Said("notebook.not_read", ("name", shown.Document.Name), ("why", why));

        if (shown.Remake)
            MakeCellViews(shown);

        UpdateCellViews(shown);
        UpdateNotebookBar(shown);
        FitNotebook();
    }

    // Helper method for the sizes of what's in the view, out of the size FitChrome gave it
    private void FitNotebook()
    {
        Vector2 size = notebookView.Size;
        notebookScroll.Size = new Vector2(size.X, MathF.Max(40.0f, size.Y - NOTEBOOK_BAR));
        notebookProblem.Size = notebookProblem.Size with { X = MathF.Max(100.0f, size.X - 32.0f) };
        FitCellViews(MathF.Max(200.0f, size.X - notebookCells.Padding.Total.X - 14.0f));
    }
}
