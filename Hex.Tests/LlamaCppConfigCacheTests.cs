using Hex.Core;

namespace Hex.Tests;

/// <summary>HexSettings.LlamaCppConfig is read every frame: it's cached on its inputs and the disk is looked at only now and then.</summary>
public sealed class LlamaCppConfigCacheTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-llama-cache-" + Guid.NewGuid().ToString("N"));

    public LlamaCppConfigCacheTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    private string Touch(string name)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Repeated_reads_are_equal_and_dont_touch_the_disk_again()
    {
        long now = 1000;
        HexSettings settings = new(null)
        {
            EditPredictions = true, EditPredictionsProvider = HexSettings.LLAMACPP, LlamaCppServer = Touch("llama-server.exe"),
            LlamaCppModel = Touch(LlamaCppModels.Find("zeta-2.1")!.File), LlamaCppModels = folder, LlamaCppClock = () => now,
        };
        LlamaCppServerConfig? first = settings.LlamaCppConfig;
        int checks = settings.LlamaCppDiskChecks;
        Assert.NotNull(first);
        Assert.True(checks > 0);
        for (int i = 0; i < 50; i++)
            Assert.Same(first, settings.LlamaCppConfig);
        Assert.Equal(checks, settings.LlamaCppDiskChecks);
    }

    [Fact]
    public void A_changed_setting_rebuilds_it()
    {
        long now = 1000;
        HexSettings settings = new(null)
        {
            LlamaCppServer = Touch("llama-server.exe"), LlamaCppModel = Touch("a.gguf"), LlamaCppClock = () => now,
        };
        Assert.Equal(settings.LlamaCppPort, settings.LlamaCppConfig!.Port);
        settings.LlamaCppPort = 9123;
        Assert.Equal(9123, settings.LlamaCppConfig!.Port);
        settings.LlamaCppModel = string.Empty;
        Assert.Null(settings.LlamaCppConfig);
    }

    [Fact]
    public void A_model_downloaded_later_is_noticed_after_the_wait()
    {
        long now = 1000;
        HexSettings settings = new(null)
        {
            EditPredictions = true, EditPredictionsProvider = HexSettings.LLAMACPP, LlamaCppServer = Touch("llama-server.exe"),
            LlamaCppModel = Touch(LlamaCppModels.Find("zeta-2.1")!.File), LlamaCppModels = folder, LlamaCppClock = () => now,
        };
        Assert.Null(settings.LlamaCppConfig!.EditModel);
        string mellum = Touch(LlamaCppModels.Find("mellum2.1")!.File);
        Assert.Null(settings.LlamaCppConfig!.EditModel);
        now += HexSettings.LLAMACPP_DISK_RECHECK_MS;
        Assert.Equal(mellum, settings.LlamaCppConfig!.EditModel);
    }
}
