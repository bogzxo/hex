using Hex.Core;

namespace Hex.Tests;

/// <summary>The pure parts of the Local AI window: pages, the first run's steps and the suggestion for a machine.</summary>
public sealed class LocalAiSetupFlowTests
{
    private static LlamaCppPlatform Machine(long vramMiB, long ramMiB = 32 * 1024) =>
        new("windows", "x64", ramMiB, vramMiB > 0 ? new LlamaCppGpu(LlamaCppGpu.NVIDIA, "Test card", vramMiB) : null);

    [Theory]
    [InlineData(null, "Overview")]
    [InlineData("", "Overview")]
    [InlineData("models", "Models")]
    [InlineData("Presets", "Presets")]
    [InlineData(" engine ", "Engine")]
    [InlineData("predictions", "Predictions")]
[InlineData("advanced", "Overview")]
    [InlineData("nonsense", "Overview")]
    [InlineData("99", "Overview")]
    public void Page_names_parse(string? name, string expected) => Assert.Equal(expected, LocalAiSetupFlow.ParsePage(name).ToString());

    [Theory]
    [InlineData(@"D:\llama.cpp\b12651\llama-server.exe", "b12651", "cuda-12", true)]
    [InlineData(@"D:\llama.cpp\b12651-cuda-12\llama-server.exe", "b12651", "cuda-12", true)]
    [InlineData(@"D:\llama.cpp\b12651-vulkan\llama-server.exe", "b12651", "cuda-12", false)]
    [InlineData(@"D:\llama.cpp\b12600\llama-server.exe", "b12651", "cuda-12", false)]
    [InlineData(@"D:/llama.cpp/b12651/llama-server.exe", "b12651", null, true)]
    [InlineData(null, "b12651", "cuda-12", false)]
    public void A_build_is_installed_by_a_tag_folder_with_or_without_the_backend(string? path, string tag, string? backend, bool expected) =>
        Assert.Equal(expected, LocalAiSetupFlow.BuildInstalled(path, tag, backend));

    [Fact]
    public void First_run_opens_only_when_no_model_is_assigned()
    {
        HexSettings settings = new(null);
        Assert.True(LocalAiSetupFlow.NeedsFirstRun(settings));
        settings.LocalAiChatModel = "file:d:\\models\\a.gguf";
        Assert.False(LocalAiSetupFlow.NeedsFirstRun(settings));
        settings.LocalAiChatModel = "  ";
        settings.LocalAiPredictModel = "catalog:zeta-2.1";
        Assert.False(LocalAiSetupFlow.NeedsFirstRun(settings));
    }

    [Fact]
    public void Steps_move_and_stop_at_the_ends()
    {
        Assert.Equal(FirstRunStep.Engine, LocalAiSetupFlow.Next(FirstRunStep.Hardware));
        Assert.Equal(FirstRunStep.Test, LocalAiSetupFlow.Next(FirstRunStep.Test));
        Assert.Equal(FirstRunStep.Hardware, LocalAiSetupFlow.Previous(FirstRunStep.Hardware));
        Assert.Equal(FirstRunStep.Models, LocalAiSetupFlow.Previous(FirstRunStep.Test));
    }

    [Fact]
    public void Each_step_waits_for_what_it_settles()
    {
        var empty = new FirstRunState(false, false, null, null, false, false, false);
        Assert.False(LocalAiSetupFlow.CanAdvance(FirstRunStep.Hardware, empty));
        Assert.Equal(FirstRunStep.Hardware, LocalAiSetupFlow.Reached(empty));

        var hardware = empty with { HardwareKnown = true };
        Assert.True(LocalAiSetupFlow.CanAdvance(FirstRunStep.Hardware, hardware));
        Assert.False(LocalAiSetupFlow.CanAdvance(FirstRunStep.Engine, hardware));
        Assert.Equal(FirstRunStep.Engine, LocalAiSetupFlow.Reached(hardware));

        var engine = hardware with { EngineReady = true };
        Assert.Equal(FirstRunStep.Models, LocalAiSetupFlow.Reached(engine));

        // A model picked but not downloaded doesn't let it on
        var picked = engine with { PredictModel = "catalog:qwen3.5-9b" };
        Assert.False(LocalAiSetupFlow.CanAdvance(FirstRunStep.Models, picked));
        var ready = picked with { PredictReady = true };
        Assert.True(LocalAiSetupFlow.CanAdvance(FirstRunStep.Models, ready));
        Assert.Equal(FirstRunStep.Test, LocalAiSetupFlow.Reached(ready));

        // A second model for chat has to be on the disk too
        var two = ready with { ChatModel = "catalog:mellum2.1", ChatReady = false };
        Assert.False(LocalAiSetupFlow.CanAdvance(FirstRunStep.Models, two));
        Assert.True(LocalAiSetupFlow.CanAdvance(FirstRunStep.Models, two with { ChatReady = true }));
    }

    [Fact]
    public void The_flow_is_finished_once_the_model_was_timed()
    {
        var ready = new FirstRunState(true, true, "a", null, true, false, false);
        Assert.False(LocalAiSetupFlow.Finished(ready));
        Assert.True(LocalAiSetupFlow.Finished(ready with { Measured = true }));
        Assert.False(LocalAiSetupFlow.Finished(ready with { Measured = true, EngineReady = false }));
    }

    [Theory]
    [InlineData(24576)]
    [InlineData(12288)]
    [InlineData(8192)]
    public void Enough_memory_suggests_one_model_for_both(long vram)
    {
        LocalAiRecommendation recommendation = LocalAiSetupFlow.Recommend(Machine(vram));
        Assert.True(recommendation.OneModel);
        Assert.Equal("qwen3.5-9b", recommendation.Predict.Id);
    }

    [Fact]
    public void A_small_card_suggests_the_small_chat_model()
    {
        LocalAiRecommendation recommendation = LocalAiSetupFlow.Recommend(Machine(1024));
        Assert.True(recommendation.Chat.Chats);
        Assert.True(recommendation.Predict.Chats);
        Assert.Equal("qwen2.5-coder-1.5b", recommendation.Chat.Id);
    }

    [Fact]
    public void No_card_suggests_the_smallest_chat_model_for_both()
    {
        LocalAiRecommendation recommendation = LocalAiSetupFlow.Recommend(Machine(0, 16 * 1024));
        Assert.True(recommendation.OneModel);
        Assert.Equal("qwen2.5-coder-1.5b", recommendation.Predict.Id);
    }

    [Fact]
    public void Recommendations_always_chat_for_chat_and_never_exceed_the_machine()
    {
        foreach (long vram in new long[] { 0, 2048, 4096, 6144, 8192, 10240, 12288, 16384, 24576 })
        {
            LlamaCppPlatform machine = Machine(vram);
            LocalAiRecommendation recommendation = LocalAiSetupFlow.Recommend(machine);
            Assert.True(recommendation.Chat.Chats);
            if (vram > 0)
                Assert.NotEqual(LlamaCppFit.TooBig, LlamaCppModels.Fit(recommendation.Predict, machine));
        }
    }

    private static readonly LocalModelPreset Defaults = new(16384, 0.7, 0.9, 40, 0.0, 1.0, "model", "auto", "");

    [Fact]
    public void Preset_fields_parse_clamp_and_fall_back_to_the_default()
    {
        LocalModelPreset typed = Defaults with { Temperature = 0.2, Context = 8192 };
        Assert.Equal(0.3, LocalAiSetupFlow.WithField(typed, Defaults, "temperature", "0.3").Temperature);
        Assert.Equal(2.0, LocalAiSetupFlow.WithField(typed, Defaults, "temperature", "9").Temperature);
        Assert.Equal(0.7, LocalAiSetupFlow.WithField(typed, Defaults, "temperature", "").Temperature);
        Assert.Equal(0.7, LocalAiSetupFlow.WithField(typed, Defaults, "temperature", "abc").Temperature);
        Assert.Equal(32768, LocalAiSetupFlow.WithField(typed, Defaults, "context", "32768").Context);
        Assert.Equal(16384, LocalAiSetupFlow.WithField(typed, Defaults, "context", " ").Context);
        Assert.Equal("--threads 8", LocalAiSetupFlow.WithField(typed, Defaults, "extra", " --threads 8 ").Extra);
        Assert.Equal(typed, LocalAiSetupFlow.WithField(typed, Defaults, "unknown_field", "1"));
    }

    [Fact]
    public void Only_server_side_preset_fields_restart_the_server()
    {
        Assert.False(LocalAiSetupFlow.NeedsRestart(Defaults, Defaults with { Temperature = 0.1, TopK = 10, Thinking = "off" }));
        Assert.True(LocalAiSetupFlow.NeedsRestart(Defaults, Defaults with { Context = 32768 }));
        Assert.True(LocalAiSetupFlow.NeedsRestart(Defaults, Defaults with { Offload = "experts" }));
        Assert.True(LocalAiSetupFlow.NeedsRestart(Defaults, Defaults with { Extra = "--threads 8" }));
        Assert.False(LocalAiSetupFlow.NeedsRestart(Defaults with { Extra = "x" }, Defaults with { Extra = " x " }));
    }

    [Fact]
    public void The_cards_memory_is_read_from_nvidia_smis_lines()
    {
        Assert.Equal((4200L, 12288L), LocalAiGpu.Parse("4200, 12288\n"));
        Assert.Equal((100L, 24576L), LocalAiGpu.Parse("1, 8192\n100, 24576\n"));
        Assert.Null(LocalAiGpu.Parse("not a number"));
        Assert.Null(LocalAiGpu.Parse(""));
    }

    [Fact]
    public void The_trade_off_sentence_follows_the_suggestion()
    {
        LlamaCppModel qwen = LlamaCppModels.Find("qwen3.5-9b")!, zeta = LlamaCppModels.Find("zeta-2.1")!;
        Assert.Equal("local_ai.tradeoff_one", LocalAiSetupFlow.TradeOffKey(new LocalAiRecommendation(qwen, qwen)));
        Assert.Equal("local_ai.tradeoff_two", LocalAiSetupFlow.TradeOffKey(new LocalAiRecommendation(zeta, qwen)));
    }

    [Theory]
    [InlineData(false, false, "llamacpp.build_download")]
    [InlineData(true, true, "local_ai.build_reinstall")]
    [InlineData(true, false, "llamacpp.build_update")]
    public void The_build_button_says_download_reinstall_or_update(bool found, bool same, string key) =>
        Assert.Equal(key, LocalAiSetupFlow.BuildButtonKey(found, same));

    [Fact]
    public void Setup_is_offered_as_set_up_until_something_was_set_up()
    {
        Assert.Equal("local_ai.set_up_first", LocalAiSetupFlow.SetUpKey(false));
        Assert.Equal("local_ai.set_up_again", LocalAiSetupFlow.SetUpKey(true));
    }

    [Theory]
    [InlineData(0L, "1 MB")]
    [InlineData(45_000_000L, "45 MB")]
    [InlineData(499_600_000L, "500 MB")]
    [InlineData(5_600_000_000L, "5.6 GB")]
    [InlineData(1_000_000_000L, "1.0 GB")]
    public void Sizes_under_a_gigabyte_are_in_megabytes(long bytes, string text) => Assert.Equal(text, LocalAiSetupFlow.SizeText(bytes));

    [Fact]
    public void Applied_says_whether_the_server_restarted()
    {
        Assert.Equal("local_ai.applied_restart", LocalAiSetupFlow.AppliedKey(true, true));
        Assert.Equal("local_ai.applied_saved", LocalAiSetupFlow.AppliedKey(false, true));
        Assert.Equal("local_ai.applied_now", LocalAiSetupFlow.AppliedKey(false, false));
    }

    [Fact]
    public void Badges_that_dont_fit_are_cut_for_a_count()
    {
        float[] widths = [60, 60, 60, 60];
        Assert.Equal(4, LocalAiSetupFlow.FitCount(widths, 400, 8, 34));
        Assert.Equal(2, LocalAiSetupFlow.FitCount(widths, 200, 8, 34));
        Assert.Equal(1, LocalAiSetupFlow.FitCount(widths, 20, 8, 34));
        Assert.Equal(3, LocalAiSetupFlow.FitCount(widths, 240, 8, 0));
    }

    [Fact]
    public void A_row_of_buttons_breaks_into_lines_that_fit()
    {
        Assert.Equal([3], LocalAiSetupFlow.RowBreaks([100, 100, 100], 320, 8));
        Assert.Equal([2, 1], LocalAiSetupFlow.RowBreaks([100, 100, 100], 250, 8));
        Assert.Equal([1, 1], LocalAiSetupFlow.RowBreaks([300, 300], 200, 8));
        Assert.Empty(LocalAiSetupFlow.RowBreaks([], 200, 8));
    }

    [Fact]
    public void The_window_redraws_when_a_role_is_assigned_from_anywhere()
    {
        // The window's signature takes the ids as parts, so a role assigned moves it
        Assert.NotEqual(LocalAiSetupFlow.Signature("overview", "", ""), LocalAiSetupFlow.Signature("overview", "file:mellum", ""));
    }
}
