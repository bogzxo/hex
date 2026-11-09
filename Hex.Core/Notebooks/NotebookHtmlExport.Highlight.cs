using System.Text;
using System.Text.Json.Nodes;
using Horizon.UI.Scripting;

namespace Hex.Core.Notebooks;

internal static partial class NotebookHtmlExport
{
    private static string? LanguageText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static string ExportLanguage(JsonObject metadata, string fallback) =>
        LanguageText(metadata["language"]) ?? LanguageText((metadata["language_info"] as JsonObject)?["name"])
        ?? LanguageText((metadata["polyglot_notebook"] as JsonObject)?["kernelName"])
        ?? LanguageText((metadata["dotnet_interactive"] as JsonObject)?["language"])
        ?? LanguageText((metadata["kernelspec"] as JsonObject)?["language"]) ?? fallback;

    private static string HighlightCode(string source, string language)
    {
        string id = language.Trim().ToLowerInvariant() switch
        {
            "c#" or "cs" => "csharp", "f#" or "fs" => "fsharp",
            "py" or "python3" => "python", "js" => "javascript", "ts" => "typescript",
            "sh" or "bash" => "shellscript", var other => other
        };
        try
        {
            if (CodeLanguages.ForLanguageId(id) is not { } highlighter)
                return Encode(source);
            // Normalize only the tokenizer input; copy every original character, including line endings.
            string normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            var spans = highlighter.Read(normalized, lines.Length);
            var html = new StringBuilder();
            int offset = 0;
            for (int line = 0; line < lines.Length; line++)
            {
                int length = lines[line].Length, cursor = 0;
                foreach (SyntaxSpan span in spans[line])
                {
                    if (span.Start < cursor || span.Length < 0 || span.Start > length || span.Length > length - span.Start)
                        return Encode(source);
                    html.Append(Encode(source.Substring(offset + cursor, span.Start - cursor)));
                    string? kind = span.Kind switch
                    {
                        SyntaxKind.Keyword or SyntaxKind.Control or SyntaxKind.Directive => "keyword",
                        SyntaxKind.Text or SyntaxKind.Escape => "string",
                        SyntaxKind.Comment => "comment",
                        SyntaxKind.Number or SyntaxKind.Constant => "number",
                        SyntaxKind.Type or SyntaxKind.Builtin => "type",
                        SyntaxKind.Call or SyntaxKind.Declaration => "function",
                        _ => null
                    };
                    if (kind is not null) html.Append("<span class=\"syntax-").Append(kind).Append("\">");
                    html.Append(Encode(source.Substring(offset + span.Start, span.Length)));
                    if (kind is not null) html.Append("</span>");
                    cursor = span.Start + span.Length;
                }
                html.Append(Encode(source.Substring(offset + cursor, length - cursor)));
                offset += length;
                if (offset < source.Length && source[offset] == '\r') html.Append(source[offset++]);
                if (offset < source.Length && source[offset] == '\n') html.Append(source[offset++]);
            }
            return html.ToString();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A missing or failing grammar must never prevent saving the notebook.
            return Encode(source);
        }
    }
}
