using System.Text.Json.Nodes;

using Hex.Core.Notebooks;

namespace Hex.Dawn;

// Notebooks by their path for an agent, so it doesn't have to open one before every call, and a new one made where it
// says. A notebook tool with a path opens that notebook first (if it isn't already the one on screen), one without
// works on the one on screen the way it always did.
internal sealed partial class DawnScene
{
    private IEnumerable<HexMcpTool> NotebookFileTools() =>
    [
        new(new McpTool("hex_notebook_new", "New notebook",
            "Make a Jupyter notebook at a path (an empty code cell, a kernel of the language said, python by default) and open it. An existing file is opened instead, never written over.",
            McpSchema.Object([
                ("path", McpSchema.String("Where, inside the workspace or whole. .ipynb is added when it hasn't got it.")),
                ("language", McpSchema.String("The kernel's language, python by default."))], "path")),
            NotebookNew)
    ];

    /// <summary>
    /// The notebook a call names opened first, when it names one. Yields nothing at all for no path, null for a tick
    /// waited, and an error to hand back when it couldn't be opened.
    /// </summary>
    private IEnumerable<McpToolResult?> McpNotebookAt(JsonObject arguments)
    {
        if (arguments.String("path") is not { Length: > 0 } asked)
            yield break;

        string path = McpPath(asked);
        if (document.Path is { } shown && PlatformPaths.Same(Path.GetFullPath(shown), path))
            yield break;

        if (!File.Exists(path))
        {
            yield return McpToolResult.Error($"There's no notebook at {path}, hex_notebook_new makes one.");
            yield break;
        }

        if (!Open(path))
        {
            yield return McpToolResult.Error($"Hex couldn't open {path}, the status bar says \"{Plain(messageItem.Text)}\".");
            yield break;
        }

        yield return null;
        yield return null;
    }

    private IEnumerable<McpToolResult?> NotebookNew(JsonObject arguments)
    {
        string path = McpPath(arguments.String("path") ?? string.Empty);
        if (!IsNotebookFile(path))
            path += ".ipynb";

        bool made = false;
        if (!File.Exists(path))
        {
            Notebook notebook = Notebook.Empty();
            string language = arguments.String("language") is { Length: > 0 } said ? said : "python";
            if (KernelSpecs.For(KernelSpecs.Find(), null, language) is { } spec)
                notebook.UseKernel(spec, []);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, NotebookFile.Write(notebook));
            made = true;
        }

        if (!Open(path))
        {
            yield return McpToolResult.Error($"Hex couldn't open {path}, the status bar says \"{Plain(messageItem.Text)}\".");
            yield break;
        }

        yield return null;
        yield return null;
        yield return McpToolResult.Text($"{(made ? "Made" : "There was already")} {path}, open now. hex_notebook_edit writes its cells, hex_notebook_run runs them.");
    }
}
