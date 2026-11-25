using Hex.Core;

namespace Hex.Tests;

/// <summary>The window's search: what an empty query means once the window is closed and asked for a page.</summary>
public sealed class LocalAiWindowQueryTests
{
    [Fact]
    public void An_empty_query_keeps_every_row_and_a_page_is_never_empty_after_a_reset()
    {
        LocalAiRowDef[] rows = [new("context", LocalAiPage.Presets), new("engine_port", LocalAiPage.Engine)];
        (string, string, string) Texts(string key) => (key, key, key);
        // A "port" search left behind by a closed window would show only the Engine rows; reset to nothing it shows all of them
        Assert.Single(LocalAiWindowState.Filter("port", rows, Texts));
        Assert.Equal(2, LocalAiWindowState.Filter(string.Empty, rows, Texts).Count);
        Assert.Equal(LocalAiPage.Presets, LocalAiWindowState.JumpTo(LocalAiWindowState.Filter(string.Empty, rows, Texts), LocalAiPage.Presets));
    }
}
