using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core.Notebooks;

/// <summary>
/// A notebook read from and written to the JSON of its .ipynb file. Written the way Jupyter writes it (keys in order, one
/// space of indent, text as a list of its lines, a new line at the end), so a notebook saved from Hex and from Jupyter
/// come out the same and a diff of one only shows what changed. Read in whatever shape nbformat allows (a string or a
/// list of lines for any text).
/// </summary>
internal static class NotebookFile
{
    private static readonly JsonWriterOptions WRITING = new()
    {
        Indented = true,
        IndentSize = 1,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>A notebook out of its file's text, an empty one for an empty file. Throws a JsonException for text that isn't one.</summary>
    public static Notebook Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Notebook.Empty();

        if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) is not JsonObject top)
            throw new JsonException("a notebook is a JSON object");

        var notebook = new Notebook
        {
            Format = top["nbformat"]?.GetValue<int>() ?? 4,
            FormatMinor = top["nbformat_minor"]?.GetValue<int>() ?? 5,
            Metadata = top["metadata"]?.DeepClone() as JsonObject ?? []
        };
        if (notebook.Format < 4)
            throw new JsonException($"nbformat {notebook.Format} is older than Hex reads, open it in Jupyter once to bring it up to 4");

        foreach (var (name, value) in top)
        {
            if (name is not ("cells" or "metadata" or "nbformat" or "nbformat_minor"))
                notebook.Rest[name] = value?.DeepClone();
        }

        if (top["cells"] is JsonArray cells)
        {
            foreach (JsonNode? node in cells)
            {
                if (node is JsonObject cell)
                    notebook.Cells.Add(ReadCell(cell));
            }
        }

        return notebook;
    }

    private static NotebookCell ReadCell(JsonObject cell)
    {
        var read = new NotebookCell
        {
            Kind = cell["cell_type"]?.GetValue<string>() switch { "markdown" => CellKind.Markdown, "raw" => CellKind.Raw, _ => CellKind.Code },
            Source = Text(cell["source"]),
            Metadata = cell["metadata"]?.DeepClone() as JsonObject ?? []
        };
        if (cell["id"]?.GetValue<string>() is { Length: > 0 } id)
            read.Id = id;
        if (cell["execution_count"] is JsonValue count && count.TryGetValue(out int run))
            read.ExecutionCount = run;

        if (cell["outputs"] is JsonArray outputs)
        {
            foreach (JsonNode? output in outputs)
            {
                if (output is JsonObject one)
                    read.Outputs.Add(Joined(one));
            }
        }

        foreach (var (name, value) in cell)
        {
            if (name is not ("cell_type" or "source" or "metadata" or "id" or "execution_count" or "outputs"))
                read.Rest[name] = value?.DeepClone();
        }

        return read;
    }

    /// <summary>Text as nbformat has it, a string or a list of lines, as one string.</summary>
    public static string Text(JsonNode? node) => node switch
    {
        JsonArray lines => string.Concat(lines.Select(line => line?.GetValue<string>() ?? string.Empty)),
        JsonValue value when value.TryGetValue(out string? text) => text,
        _ => string.Empty
    };

    // An output with its text as one string each, the stream's text, a traceback's lines kept a list
    private static JsonObject Joined(JsonObject output)
    {
        var joined = (JsonObject)output.DeepClone();
        if (joined["text"] is JsonArray)
            joined["text"] = Text(joined["text"]);
        if (joined["data"] is JsonObject data)
        {
            foreach (string mime in data.Select(pair => pair.Key).ToList())
            {
                if (data[mime] is JsonArray && !IsJson(mime))
                    data[mime] = Text(data[mime]);
            }
        }

        return joined;
    }

    // Whether what's under a type of data is JSON of its own rather than text (application/json, a vnd...+json)
    private static bool IsJson(string mime) => mime == "application/json" || mime.EndsWith("+json", StringComparison.Ordinal);

    // Whether text of a type is written as a list of its lines, the text types are, the base64 of a picture isn't
    private static bool IsLined(string mime) => mime.StartsWith("text/", StringComparison.Ordinal) || mime is "image/svg+xml" or "application/javascript";

    /// <summary>The notebook's file, the way Jupyter writes it.</summary>
    public static string Write(Notebook notebook)
    {
        var top = new JsonObject();
        var cells = new JsonArray();
        foreach (NotebookCell cell in notebook.Cells)
            cells.Add((JsonNode)WriteCell(cell, notebook.FormatMinor));

        top["cells"] = cells;
        top["metadata"] = notebook.Metadata.DeepClone();
        top["nbformat"] = notebook.Format;
        top["nbformat_minor"] = notebook.FormatMinor;
        foreach (var (name, value) in notebook.Rest)
            top[name] = value?.DeepClone();

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WRITING))
            WriteSorted(writer, top);
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static JsonObject WriteCell(NotebookCell cell, int minor)
    {
        var written = new JsonObject
        {
            ["cell_type"] = cell.Kind switch { CellKind.Markdown => "markdown", CellKind.Raw => "raw", _ => "code" },
            ["metadata"] = cell.Metadata.DeepClone(),
            ["source"] = Lines(cell.Source)
        };

        // Ids came in with 4.5, a notebook older than that has none and gets none
        if (minor >= 5)
            written["id"] = cell.Id;

        if (cell.Kind == CellKind.Code)
        {
            written["execution_count"] = cell.ExecutionCount is { } count ? JsonValue.Create(count) : null;
            var outputs = new JsonArray();
            foreach (JsonObject output in cell.Outputs)
                outputs.Add((JsonNode)Lined(output));
            written["outputs"] = outputs;
        }

        foreach (var (name, value) in cell.Rest)
            written[name] = value?.DeepClone();
        return written;
    }

    // An output as it's written, its text as lists of lines
    private static JsonObject Lined(JsonObject output)
    {
        var lined = (JsonObject)output.DeepClone();
        if (lined["text"] is JsonValue text && text.TryGetValue(out string? stream))
            lined["text"] = Lines(stream);
        if (lined["data"] is JsonObject data)
        {
            foreach (string mime in data.Select(pair => pair.Key).ToList())
            {
                if (IsLined(mime) && data[mime] is JsonValue value && value.TryGetValue(out string? said))
                    data[mime] = Lines(said);
            }
        }

        return lined;
    }

    /// <summary>Text as a list of its lines, each with its new line on the end but the last, the way nbformat writes it.</summary>
    public static JsonArray Lines(string text)
    {
        var lines = new JsonArray();
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0)
            {
                lines.Add((JsonNode)text[start..]);
                break;
            }

            lines.Add((JsonNode)text[start..(end + 1)]);
            start = end + 1;
        }

        return lines;
    }

    // Writes a node with every object's keys in order, as Python's json.dumps(sort_keys=True) does
    private static void WriteSorted(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (name, value) in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(name);
                    WriteSorted(writer, value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (JsonNode? item in array)
                    WriteSorted(writer, item);
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}
