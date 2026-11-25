using System.Text.Json;

using Hex.Core;

namespace Hex.Tests;

/// <summary>The pure parts behind the Local AI window's fixes: installed builds, the dialog's start, the redraw key and what's loaded.</summary>
public sealed class LocalAiWindowLogicTests
{
    [Theory]
    [InlineData(@"D:\llama\b12651-cuda\llama-server.exe", "b12651", "cuda", true)]
    [InlineData(@"D:\llama\b12651-cuda\llama-server.exe", "b12651", "vulkan", false)]
    [InlineData(@"D:\llama\b12651-cuda\llama-server.exe", "b12651", null, true)]
    [InlineData(@"D:\llama\b12000-cuda\llama-server.exe", "b12651", "cuda", false)]
    [InlineData(null, "b12651", "cuda", false)]
    [InlineData("/opt/llama/b12651-cpu/llama-server", "b12651", "cpu", true)]
    public void A_build_is_installed_when_its_folder_is_the_servers(string? server, string tag, string? backend, bool expected) =>
        Assert.Equal(expected, LocalAiSetupFlow.BuildInstalled(server, tag, backend));

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]    // put away by Escape, OnClosed never ran
    [InlineData(true, true, false, false)]    // taken out of the UI
    [InlineData(false, true, true, false)]
    public void A_window_that_is_hidden_or_has_no_parent_is_closed(bool has, bool visible, bool parent, bool expected) =>
        Assert.Equal(expected, LocalAiWindowState.IsOpen(has, visible, parent));

    private static readonly LocalModelPreset Base = LocalModelPresets.Default(new LocalModel("catalog:x", "X", LocalModelSource.Catalog, null, 1, LocalModelCaps.Chat, null, null, null, null, LocalModelFit.Unknown, null));

    [Fact]
    public void Only_a_server_side_change_of_a_role_model_restarts_the_server()
    {
        LocalModelPreset sampling = Base with { Temperature = 0.1, TopK = 3 }, context = Base with { Context = Base.Context + 1024 };
        Assert.False(LocalAiSetupFlow.RestartsServer("a", "a", "b", Base, sampling));      // sampling is per request
        Assert.True(LocalAiSetupFlow.RestartsServer("a", "a", "b", Base, context));
        Assert.True(LocalAiSetupFlow.RestartsServer("b", "a", "b", Base, context));
        Assert.False(LocalAiSetupFlow.RestartsServer("c", "a", "b", Base, context));       // a model in no role
        Assert.False(LocalAiSetupFlow.RestartsServer("a", null, null, Base, context));
    }

    [Fact]
    public void A_config_that_differs_only_in_sampling_or_a_non_role_models_context_is_the_same_server()
    {
        LocalModelPreset Other(Func<LocalModelPreset, LocalModelPreset> change) => change(Base);
        LlamaCppServerConfig Make(LocalModelPreset role, LocalModelPreset other) => new(@"C:\s.exe", @"C:\a.gguf", "a", 8080, 32768, "auto", "", null, null, [("b", @"C:\b.gguf")],
            new Dictionary<string, LocalModelPreset> { ["a"] = role, ["b"] = other });
        LlamaCppServerConfig start = Make(Base, Base);
        Assert.True(start.SameServer(Make(Other(p => p with { Temperature = 0.3 }), Base)));
        Assert.True(start.SameServer(Make(Base, Other(p => p with { Context = 4096, Offload = "cpu" }))));
        Assert.False(start.SameServer(Make(Other(p => p with { Context = 4096 }), Base)));
        Assert.False(start.SameServer(Make(Base, Base) with { Extra = "--threads 2" }));
    }

    [Fact]
    public void Time_It_times_the_roles_models_only()
    {
        var presets = new Dictionary<string, LocalModelPreset>();
        var qwenOnly = new LlamaCppServerConfig(@"C:\s.exe", @"C:\q.gguf", "qwen", 8080, 32768, "auto", "", null, null, [("mellum", @"C:\m.gguf")], presets);
        Assert.Equal(["qwen"], qwenOnly.RoleAliases);
        var two = qwenOnly with { EditModel = @"C:\m.gguf", EditAlias = "mellum" };
        Assert.Equal(["qwen", "mellum"], two.RoleAliases);
    }

    [Theory]
    [InlineData("0.35", "0.2", true)]
    [InlineData("0.20", "0.2", false)]
    [InlineData("", "0.2", false)]
    [InlineData("--a", "", true)]
    public void The_changed_marker_follows_what_is_typed(string typed, string fromProfile, bool expected) =>
        Assert.Equal(expected, LocalAiSetupFlow.ChangedFromProfile(typed, fromProfile));

    [Fact]
    public void A_draft_is_stale_when_the_saved_presets_moved()
    {
        Assert.False(LocalAiSetupFlow.DraftStale(null, "x"));
        Assert.False(LocalAiSetupFlow.DraftStale("x", "x"));
        Assert.True(LocalAiSetupFlow.DraftStale("{\"a\":1}", string.Empty));
    }

    [Fact]
    public void The_folder_dialog_starts_in_the_models_folder_else_home()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Equal(folder, LocalAiSetupFlow.ModelsDialogStart(folder, "home"));
            Assert.Equal("home", LocalAiSetupFlow.ModelsDialogStart(Path.Combine(folder, "missing"), "home"));
            Assert.Equal("home", LocalAiSetupFlow.ModelsDialogStart("", "home"));
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void The_signature_is_equal_for_equal_state_and_differs_when_anything_moves()
    {
        string a = LocalAiSetupFlow.Signature("x", 1, true, new[] { "m1", "m2" }, null);
        Assert.Equal(a, LocalAiSetupFlow.Signature("x", 1, true, new[] { "m1", "m2" }, null));
        Assert.NotEqual(a, LocalAiSetupFlow.Signature("x", 1, true, new[] { "m1", "m3" }, null));
        Assert.NotEqual(a, LocalAiSetupFlow.Signature("x", 2, true, new[] { "m1", "m2" }, null));
        Assert.NotEqual(LocalAiSetupFlow.Signature("a", "bc"), LocalAiSetupFlow.Signature("ab", "c"));
    }

    [Fact]
    public void A_ready_summary_says_how_predictions_run_and_whether_chat_and_link_chat_are_on()
    {
        Assert.Equal(["local_ai.ready_predictions_typing", "local_ai.ready_chat_on", "local_ai.ready_link_on"], LocalAiSetupFlow.ReadyKeys(true, "after_typing", true, true));
        Assert.Equal(["local_ai.ready_predictions_request", "local_ai.ready_chat_off", "local_ai.ready_link_off"], LocalAiSetupFlow.ReadyKeys(true, "shortcut", false, false));
        Assert.Equal("local_ai.ready_predictions_off", LocalAiSetupFlow.ReadyKeys(false, "shortcut", true, false)[0]);
    }

    [Fact]
    public void A_suggested_model_found_on_the_disk_is_not_offered_again_as_a_download()
    {
        LlamaCppModel qwen = LlamaCppModels.All.First(model => model.Id == "qwen3.5-9b");
        var one = new LocalAiRecommendation(qwen, qwen);
        Assert.Empty(LocalAiSetupFlow.RecommendedToDownload(one, _ => true));
        Assert.Single(LocalAiSetupFlow.RecommendedToDownload(one, _ => false));
        Assert.True(LocalAiSetupFlow.IsRecommended(one, "qwen3.5-9b"));
        Assert.False(LocalAiSetupFlow.IsRecommended(one, "other"));
        Assert.False(LocalAiSetupFlow.IsRecommended(one, null));
    }

    [Fact]
    public void Only_loaded_models_are_listed_from_a_router_and_all_from_a_plain_server()
    {
        using JsonDocument router = JsonDocument.Parse("""
            {"data":[{"id":"a","status":{"value":"loaded"}},{"id":"b","status":{"value":"unloaded"}},{"id":"c","status":{"value":"loading"}}]}
            """);
        Assert.Equal(["a"], LlamaCppServer.Loaded(router.RootElement));
        using JsonDocument plain = JsonDocument.Parse("""{"data":[{"id":"x"},{"id":"y"}]}""");
        Assert.Equal(["x", "y"], LlamaCppServer.Loaded(plain.RootElement));
        using JsonDocument none = JsonDocument.Parse("{}");
        Assert.Empty(LlamaCppServer.Loaded(none.RootElement));
    }
}
