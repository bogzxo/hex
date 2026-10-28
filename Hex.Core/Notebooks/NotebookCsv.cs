using System.Text;

namespace Hex.Core.Notebooks;

/// <summary>CSV with column headers, CRLF records, and RFC 4180 escaping. Cell text is preserved verbatim.</summary>
internal static class NotebookCsv
{
    public static string Write(IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows)
    {
        var result = new StringBuilder();
        void Row(IEnumerable<string> values)
        {
            bool first = true;
            foreach (string value in values)
            {
                if (!first) result.Append(',');
                first = false;
                if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
                    result.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                else result.Append(value);
            }
            result.Append("\r\n");
        }
        Row(headers);
        foreach (var row in rows) Row(row);
        return result.ToString();
    }
}
