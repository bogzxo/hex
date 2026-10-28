using Hex.Core.Notebooks;

namespace Hex.Tests;

public class NotebookCsvTests
{
    [Fact]
    public void Csv_preserves_headers_unicode_empty_cells_quotes_and_multiline_text()
    {
        string csv = NotebookCsv.Write(["Load, N", "α", "Notes"],
            [["1000", "", "a \"quoted\" beam"], ["2", "δ", "line 1\r\nline 2"]]);
        Assert.Equal("\"Load, N\",α,Notes\r\n1000,,\"a \"\"quoted\"\" beam\"\r\n2,δ,\"line 1\r\nline 2\"\r\n", csv);
    }

    [Fact]
    public void Export_includes_every_row_and_an_empty_table_keeps_its_headers()
    {
        Assert.Equal("x,y\r\n", NotebookCsv.Write(["x", "y"], []));
        string csv = NotebookCsv.Write(["index"], Enumerable.Range(0, 1000).Select(i => new[] { i.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        Assert.Equal(1001, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.EndsWith("999\r\n", csv);
    }
}
