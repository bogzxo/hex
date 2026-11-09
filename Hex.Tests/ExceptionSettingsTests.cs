using Hex.Core;

namespace Hex.Tests;

public class ExceptionSettingsTests
{
    [Fact]
    public void AdapterSpecificSelectionsPersistIncludingDisabledDefaults()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-exception-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.hor");
            var settings = new HexSettings(path);
            settings.ExceptionBreakpoints["coreclr/all"] = true;
            settings.ExceptionBreakpoints["python/uncaught"] = false;
            Assert.Null(settings.Save());
            var loaded = new HexSettings(path);
            Assert.True(loaded.ExceptionBreakpoints["coreclr/all"]);
            Assert.False(loaded.ExceptionBreakpoints["python/uncaught"]);
            Assert.False(loaded.ExceptionBreakpoints.ContainsKey("python/raised"));
        }
        finally { Directory.Delete(root, true); }
    }
}
