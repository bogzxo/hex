using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core.Notebooks;

/// <summary>The code of a cell asking for something typed (input(), getpass()), and what it said asking.</summary>
internal sealed record NotebookInput(NotebookCell Cell, string Prompt, bool Password, JupyterMessage Request);

/// <summary>
/// What a kernel says as cells run, made into the cells' outputs the way Jupyter does. Each cell sent is remembered by the
/// id of its execute_request, so what comes back (on iopub and the reply on shell) goes to the cell it's about. Streams
/// run on into the last stream of the same name, a carriage return taking the line back to its start (a progress bar
/// drawn over itself), a display with an id can be updated in place later from any cell, and clear_output clears the
/// cell now or, told to wait, as the next output comes.
/// </summary>
internal sealed class NotebookRun
{
    private readonly Dictionary<string, NotebookCell> sent = [];
    private readonly Dictionary<string, List<JsonObject>> displays = [];
    private readonly HashSet<NotebookCell> clearing = [];

    /// <summary>The cells sent and not finished, in the order they were sent.</summary>
    public List<NotebookCell> Queued { get; } = [];

    /// <summary>The code asking for something typed, null while nothing is.</summary>
    public NotebookInput? Input { get; private set; }

    /// <summary>A cell sent to run, as the request it went as. Its outputs go, they're about to be made again.</summary>
    public void Sent(NotebookCell cell, string requestId)
    {
        sent[requestId] = cell;
        Queued.Remove(cell);
        Queued.Add(cell);
        cell.Outputs.Clear();
        cell.ExecutionCount = null;
    }

    /// <summary>Whether a cell's waiting to run or running.</summary>
    public bool IsQueued(NotebookCell cell) => Queued.Contains(cell);

    /// <summary>Everything forgotten (the kernel restarted or went), nothing's running any more.</summary>
    public void Forget()
    {
        sent.Clear();
        Queued.Clear();
        clearing.Clear();
        Input = null;
    }

    /// <summary>The input asked for answered, it's no longer asked.</summary>
    public void Answered() => Input = null;

    /// <summary>Takes in one thing the kernel said, and says which cell's outputs it changed, null for none.</summary>
    public NotebookCell? Take(KernelEvent said)
    {
        JupyterMessage message = said.Message;
        if (!sent.TryGetValue(message.ParentId, out NotebookCell? cell))
            return message.Type == "update_display_data" ? Update(message) : null;

        JsonObject content = message.Content;
        switch (message.Type)
        {
            case "execute_input":
                cell.ExecutionCount = Count(content);
                return cell;

            case "stream":
                Clear(cell);
                Stream(cell, content["name"]?.GetValue<string>() ?? "stdout", content["text"]?.GetValue<string>() ?? string.Empty);
                return cell;

            case "display_data":
            case "execute_result":
            {
                Clear(cell);
                var output = new JsonObject
                {
                    ["output_type"] = message.Type,
                    ["data"] = content["data"]?.DeepClone() ?? new JsonObject(),
                    ["metadata"] = content["metadata"]?.DeepClone() ?? new JsonObject()
                };
                if (message.Type == "execute_result")
                    output["execution_count"] = Count(content);
                cell.Outputs.Add(output);

                if (content["transient"]?["display_id"]?.GetValue<string>() is { Length: > 0 } id)
                {
                    if (!displays.TryGetValue(id, out var shown))
                        displays[id] = shown = [];
                    shown.Add(output);
                }
                return cell;
            }

            case "update_display_data":
                return Update(message) ?? cell;

            case "error":
                Clear(cell);
                cell.Outputs.Add(new JsonObject
                {
                    ["output_type"] = "error",
                    ["ename"] = content["ename"]?.GetValue<string>() ?? string.Empty,
                    ["evalue"] = content["evalue"]?.GetValue<string>() ?? string.Empty,
                    ["traceback"] = content["traceback"]?.DeepClone() ?? new JsonArray()
                });
                return cell;

            case "clear_output":
                if (content["wait"]?.GetValue<bool>() == true)
                    clearing.Add(cell);
                else
                    cell.Outputs.Clear();
                return cell;

            case "input_request":
                Input = new NotebookInput(cell, content["prompt"]?.GetValue<string>() ?? string.Empty, content["password"]?.GetValue<bool>() == true, message);
                return cell;

            case "execute_reply":
                if (Count(content) is { } count)
                    cell.ExecutionCount = count;
                Queued.Remove(cell);
                if (Input?.Cell == cell)
                    Input = null;
                sent.Remove(message.ParentId);

                // An error the reply says that iopub didn't (a kernel that only says it in its reply)
                if (content["status"]?.GetValue<string>() == "error" && !cell.Outputs.Any(output => output["output_type"]?.GetValue<string>() == "error"))
                {
                    cell.Outputs.Add(new JsonObject
                    {
                        ["output_type"] = "error",
                        ["ename"] = content["ename"]?.GetValue<string>() ?? "Error",
                        ["evalue"] = content["evalue"]?.GetValue<string>() ?? string.Empty,
                        ["traceback"] = content["traceback"]?.DeepClone() ?? new JsonArray()
                    });
                }
                return cell;
        }

        return null;
    }

    // Helper method for clear_output(wait=True), the clearing done as the next output comes
    private void Clear(NotebookCell cell)
    {
        if (clearing.Remove(cell))
            cell.Outputs.Clear();
    }

    private static int? Count(JsonObject content) =>
        content["execution_count"] is JsonValue value && value.TryGetValue(out int count) ? count : null;

    // Helper method for update_display_data, every output shown with that id given what it says now, in place
    private NotebookCell? Update(JupyterMessage message)
    {
        if (message.Content["transient"]?["display_id"]?.GetValue<string>() is not { Length: > 0 } id || !displays.TryGetValue(id, out var shown))
            return null;

        foreach (JsonObject output in shown)
        {
            output["data"] = message.Content["data"]?.DeepClone() ?? new JsonObject();
            output["metadata"] = message.Content["metadata"]?.DeepClone() ?? new JsonObject();
        }

        // Which cell doesn't matter much to whoever's drawing, they're all drawn again from their outputs
        return sent.Values.FirstOrDefault();
    }

    // Helper method to run a stream on, into the last output when that's the same stream
    private static void Stream(NotebookCell cell, string name, string text)
    {
        if (cell.Outputs.Count > 0 && cell.Outputs[^1] is { } last && last["output_type"]?.GetValue<string>() == "stream" && last["name"]?.GetValue<string>() == name)
        {
            last["text"] = Carried(last["text"]?.GetValue<string>() ?? string.Empty, text);
            return;
        }

        cell.Outputs.Add(new JsonObject { ["output_type"] = "stream", ["name"] = name, ["text"] = Carried(string.Empty, text) });
    }

    /// <summary>
    /// Text written after text the way Jupyter shows it. A carriage return on a line has the line be only what comes after
    /// it (tqdm draws its bar over itself so), one at the very end is kept for the next text to write over, and a backspace
    /// takes off the character before it.
    /// </summary>
    public static string Carried(string was, string more)
    {
        string text = (was + more).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.IndexOf('\r') < 0 && text.IndexOf('\b') < 0)
            return text;

        var made = new StringBuilder(text.Length);
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.IndexOf('\r') >= 0)
            {
                bool waiting = line.EndsWith('\r');
                string[] parts = line.TrimEnd('\r').Split('\r');
                line = parts[^1] + (waiting ? "\r" : string.Empty);
            }

            if (line.IndexOf('\b') >= 0)
            {
                var kept = new StringBuilder(line.Length);
                foreach (char c in line)
                {
                    if (c != '\b')
                        kept.Append(c);
                    else if (kept.Length > 0)
                        kept.Length--;
                }
                line = kept.ToString();
            }

            made.Append(line);
            if (i < lines.Length - 1)
                made.Append('\n');
        }

        return made.ToString();
    }
}
