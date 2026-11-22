using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

// Writing files through Hex, so an agent kept on the harness (its own Read, Edit and Write turned back by the guard) can
// still change code. hex_edit replaces a piece of a file by what it is, the way an agent's own Edit does, without having
// to read the file whole first. hex_write writes a whole file. A file open in Hex with changes nobody saved yet is
// changed in the editor, as one step to undo, rather than written over on the disk under the person's feet; any other
// is written to the disk, its line endings and byte order mark kept, and a tab that shows it takes the change in.
internal sealed partial class DawnScene
{
    private IEnumerable<HexMcpTool> FileTools() =>
    [
        new(new McpTool("hex_edit", "Edit a file",
            "Replace a piece of a file with another, found by its exact text (old_string, which has to be there once unless replace_all). No reading the file first, hex_read shows the lines to take it from. A file open in Hex with unsaved changes is changed in the editor. Use it in place of your own Edit.",
            McpSchema.Object([
                ("path", McpSchema.String("The file, inside the workspace or whole.")),
                ("old_string", McpSchema.String("The exact text to replace, enough of it to be found once.")),
                ("new_string", McpSchema.String("What goes in its place.")),
                ("replace_all", McpSchema.Flag("Every place it's found rather than the one.")),
                ("check", McpSchema.Flag("Wait a moment for the errors its language server says after, when one's running for it, true by default."))], "path", "old_string", "new_string")),
            McpEditFile),
        new(new McpTool("hex_write", "Write a file",
            "Write a whole file, made with its folders when it isn't there. For a new file or one changed throughout, hex_edit for a change to part of one. Use it in place of your own Write.",
            McpSchema.Object([
                ("path", McpSchema.String("The file, inside the workspace or whole.")),
                ("content", McpSchema.String("All of what it's to say.")),
                ("check", McpSchema.Flag("Wait a moment for the errors its language server says after, when one's running for it, true by default."))], "path", "content")),
            McpWriteFile)
    ];

    private IEnumerable<McpToolResult?> McpEditFile(JsonObject args)
    {
        string path = McpPath(args.String("path") ?? string.Empty);
        string old = (args.String("old_string") ?? string.Empty).Replace("\r\n", "\n");
        string with = (args.String("new_string") ?? string.Empty).Replace("\r\n", "\n");
        bool all = args.Flag("replace_all");
        if (old.Length == 0)
        {
            yield return McpToolResult.Error("old_string is empty, say the text to replace, or hex_write for a whole file.");
            yield break;
        }

        // Shared text and unsaved text are edited in their open editor.
        HexDocument? open = Context.Documents.Find(path);
        var live = LinkLiveOf(open);
        if (open is not null && (open.Modified || live is not null))
        {
            string text = open.Editor.Text.Replace("\r\n", "\n");
            if (Replaced(text, old, with, all, out string changed, out int count, out int line) is { } wrong)
            {
                yield return McpToolResult.Error($"{path}, {wrong}");
                yield break;
            }

            if (live is not null)
            {
                foreach (var step in LinkLiveAgentWrite(args, live, text, changed, $"Edited {path}:{line} ({Places(count)})."))
                    yield return step;
                yield break;
            }
            Context.Documents.Rewrite(open, changed);
            string edited = $"Edited {path}:{line} in the editor ({Places(count)}), it has changes nobody saved yet, so it isn't on the disk until it's saved.";
            foreach (McpToolResult? step in McpThenCheck(args, path, edited)) yield return step;
            yield break;
        }

        if (!File.Exists(path))
        {
            yield return McpToolResult.Error($"There's no file {path}, hex_write makes one.");
            yield break;
        }

        string? said = null;
        try
        {
            var (raw, bom) = ReadKept(path);
            bool crlf = raw.Contains("\r\n", StringComparison.Ordinal);
            if (Replaced(raw.Replace("\r\n", "\n"), old, with, all, out string changed, out int count, out int line) is { } wrong)
                said = $"!{path}, {wrong}";
            else
            {
                WriteKept(path, crlf ? changed.Replace("\n", "\r\n") : changed, bom);
                said = $"Edited {path}:{line} ({Places(count)}).";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            said = $"!{path} couldn't be changed, {e.Message}";
        }

        if (said.StartsWith('!'))
        {
            yield return McpToolResult.Error(said[1..]);
            yield break;
        }

        foreach (McpToolResult? step in McpThenCheck(args, path, said)) yield return step;
    }

    /// <summary>
    /// A change made answered with: what was done yielded at once as what's done so far (cut off by the call's time,
    /// that's the answer, so an agent never hears "timed out" of an edit that landed), then the check, then the whole.
    /// </summary>
    private IEnumerable<McpToolResult?> McpThenCheck(JsonObject args, string path, string done)
    {
        yield return McpToolResult.Done(done);
        foreach (string? step in McpCheckIfAsked(args, path))
        {
            if (step is null) yield return null;
            else done += step;
        }
        yield return McpToolResult.Text(done);
    }

    /// <summary>
    /// After a change, what the file's language server says is wrong with it, waited on a moment, unless check is false.
    /// A server that isn't up for the file yet is started for the next check, not waited on. Nulls while waiting, then what to add to the answer.
    /// </summary>
    private IEnumerable<string?> McpCheckIfAsked(JsonObject args, string path)
    {
        if (!args.Flag("check", true))
            yield break;
        long edited = System.Environment.TickCount64;
        languages.AgentFileWritten(path);
        foreach (string? step in McpCheckAfter(path, edited))
            yield return step;
    }

    private IEnumerable<McpToolResult?> McpWriteFile(JsonObject args)
    {
        string path = McpPath(args.String("path") ?? string.Empty);
        string content = args.String("content") ?? string.Empty;
        HexDocument? open = Context.Documents.Find(path);
        if (LinkLiveOf(open) is { } live)
        {
            foreach (var step in LinkLiveAgentWrite(args, live, open!.Editor.Text, content.ReplaceLineEndings("\n"), $"Wrote {path}."))
                yield return step;
            yield break;
        }
        if (open is { Modified: true })
        {
            Context.Documents.Rewrite(open, content.Replace("\r\n", "\n"));
            string wrote = $"Wrote {path} in the editor, it had changes nobody saved yet, so it isn't on the disk until it's saved.";
            foreach (McpToolResult? step in McpThenCheck(args, path, wrote)) yield return step;
            yield break;
        }

        string said;
        try
        {
            bool there = File.Exists(path);
            bool bom = false, crlf = OperatingSystem.IsWindows() && content.Contains("\r\n", StringComparison.Ordinal);
            if (there)
            {
                (string raw, bom) = ReadKept(path);
                crlf = raw.Contains("\r\n", StringComparison.Ordinal);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string text = content.Replace("\r\n", "\n");
            WriteKept(path, crlf ? text.Replace("\n", "\r\n") : text, bom);
            said = $"{(there ? "Wrote" : "Made")} {path}, {text.Count(c => c == '\n') + (text.Length > 0 && !text.EndsWith('\n') ? 1 : 0)} lines.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            said = $"!{path} couldn't be written, {e.Message}";
        }

        if (said.StartsWith('!'))
        {
            yield return McpToolResult.Error(said[1..]);
            yield break;
        }

        foreach (McpToolResult? step in McpThenCheck(args, path, said)) yield return step;
    }

    private static string Places(int count) => count == 1 ? "one place" : $"{count} places";

    /// <summary>
    /// A piece of a text replaced, or why it couldn't be, null when it was. Once unless every place is asked for, a piece
    /// found more than once without that is refused, which place was meant can't be told. The line of the first change,
    /// from 1, for the answer.
    /// </summary>
    internal static string? Replaced(string text, string old, string with, bool all, out string changed, out int count, out int line)
    {
        changed = text;
        count = 0;
        line = 0;
        int first = text.IndexOf(old, StringComparison.Ordinal);
        if (first < 0)
            return "old_string isn't in it, hex_read the lines again and copy them exactly";

        for (int at = first; at >= 0; at = text.IndexOf(old, at + old.Length, StringComparison.Ordinal))
            count++;
        if (count > 1 && !all)
            return $"old_string is in it {count} times, give more of the lines round it, or replace_all";

        line = text.AsSpan(0, first).Count('\n') + 1;
        changed = all ? text.Replace(old, with, StringComparison.Ordinal) : string.Concat(text.AsSpan(0, first), with, text.AsSpan(first + old.Length));
        return null;
    }

    // A file's text, and whether it began with UTF-8's byte order mark
    private static (string Text, bool Bom) ReadKept(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    // Written beside it and moved over it, so a Hex that stops halfway leaves the file as it was
    private static void WriteKept(string path, string text, bool bom)
    {
        string writing = path + ".hex-writing";
        File.WriteAllText(writing, text, new UTF8Encoding(bom));
        File.Move(writing, path, overwrite: true);
    }
}
