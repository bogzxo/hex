using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

/// <summary>Which model AI edits use: the code model when it can chat, else an installed instruct model.</summary>
public sealed class AiEditModelsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-ai-edit-" + Guid.NewGuid().ToString("N"));

    public AiEditModelsTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    private string Touch(string name)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private HexSettings LlamaCpp(string codeFile) => new(null)
    {
        EditPredictions = true, EditPredictionsProvider = HexSettings.LLAMACPP, LlamaCppServer = Touch("llama-server.exe"),
        LlamaCppModel = codeFile, LlamaCppModels = folder, LlamaCppPort = 8091,
    };

    [Fact]
    public void A_code_model_that_chats_does_the_edits_itself()
    {
        string qwen = Touch(LlamaCppModels.Find("qwen3.5-9b")!.File);
        AiEditModel? model = AiEditModels.Resolve(LlamaCpp(qwen), out string? problem);
        Assert.NotNull(model);
        Assert.Null(problem);
        Assert.False(model.Swaps);
        Assert.Equal("openai", model.Provider);
        Assert.Equal("local", model.Model);
        Assert.Null(AiEditModels.LlamaCppEditFile(LlamaCpp(qwen)));
    }

    [Fact]
    public void Zeta_gets_the_installed_edit_model_swapped_in()
    {
        string zeta = Touch(LlamaCppModels.Find("zeta-2.1")!.File);
        string mellum = Touch(LlamaCppModels.Find("mellum2.1")!.File);
        HexSettings settings = LlamaCpp(zeta);
        Assert.Equal(mellum, AiEditModels.LlamaCppEditFile(settings));
        AiEditModel? model = AiEditModels.Resolve(settings, out string? problem);
        Assert.NotNull(model);
        Assert.Null(problem);
        Assert.True(model.Swaps);
        Assert.Equal("mellum2.1", model.Model);
        Assert.Equal(LlamaCppModels.Find("mellum2.1")!.Title, model.Title);
        Assert.True(model.Thinks);
        Assert.True(settings.LlamaCppConfig!.Router);
    }

    [Fact]
    public void Zeta_alone_has_no_model_for_edits()
    {
        HexSettings settings = LlamaCpp(Touch(LlamaCppModels.Find("zeta-2.1")!.File));
        Assert.Null(AiEditModels.Resolve(settings, out string? problem));
        Assert.Equal("no_model", problem);
    }

    [Fact]
    public void A_named_edit_model_that_isnt_there_is_not_set_up()
    {
        HexSettings settings = LlamaCpp(Touch(LlamaCppModels.Find("zeta-2.1")!.File));
        settings.AiEditModel = Path.Combine(folder, "missing.gguf");
        Assert.Null(AiEditModels.Resolve(settings, out string? problem));
        Assert.Equal("not_set_up", problem);
    }

    [Fact]
    public void Llama_cpp_without_a_server_or_model_is_not_set_up()
    {
        var settings = new HexSettings(null) { EditPredictions = true, EditPredictionsProvider = HexSettings.LLAMACPP };
        Assert.Null(AiEditModels.Resolve(settings, out string? problem));
        Assert.Equal("not_set_up", problem);
    }

    [Fact]
    public void Ollama_with_zeta_has_no_model_and_with_a_chat_model_uses_it()
    {
        var settings = new HexSettings(null)
        {
            EditPredictions = true, EditPredictionsProvider = "ollama", EditPredictionsApiUrl = HexSettings.OLLAMA_URL, EditPredictionsModel = "hf.co/zed-industries/zeta-2.1",
        };
        Assert.Null(AiEditModels.Resolve(settings, out string? problem));
        Assert.Equal("no_model", problem);

        settings.EditPredictionsModel = "qwen3:14b";
        AiEditModel? model = AiEditModels.Resolve(settings, out problem);
        Assert.NotNull(model);
        Assert.Equal("qwen3:14b", model.Model);
        Assert.False(model.Swaps);
        Assert.Equal("ollama", model.Provider);

        settings.AiEditModel = "mellum:12b";
        model = AiEditModels.Resolve(settings, out problem);
        Assert.Equal("mellum:12b", model!.Model);
        Assert.True(model.Swaps);
    }

    [Fact]
    public void Zeta_as_code_model_lists_mellum_and_qwen_as_agents_and_the_router_has_three_sections()
    {
        string zeta = Touch(LlamaCppModels.Find("zeta-2.1")!.File);
        Touch(LlamaCppModels.Find("mellum2.1")!.File);
        Touch(LlamaCppModels.Find("qwen3.5-9b")!.File);
        HexSettings settings = LlamaCpp(zeta);

        Assert.Equal(["mellum2.1", "qwen3.5-9b"], AiEditModels.AgentModels(settings).Select(model => model.Model));
        Assert.All(AiEditModels.AgentModels(settings), model => Assert.True(model.Swaps));

        string preset = LlamaCppServerArgs.Preset(settings.LlamaCppConfig!);
        Assert.Contains("[local]", preset);
        Assert.Contains("[mellum2.1]", preset);
        Assert.Contains("[qwen3.5-9b]", preset);
        Assert.Equal(1, preset.Split("load-on-startup = true").Length - 1);
        Assert.Equal(2, preset.Split("load-on-startup = false").Length - 1);
    }

    [Fact]
    public void A_code_model_that_chats_is_the_first_agent_and_not_listed_twice()
    {
        string qwen = Touch(LlamaCppModels.Find("qwen3.5-9b")!.File);
        Touch(LlamaCppModels.Find("mellum2.1")!.File);
        HexSettings settings = LlamaCpp(qwen);

        IReadOnlyList<AiEditModel> models = AiEditModels.AgentModels(settings);
        Assert.Equal(["local", "mellum2.1"], models.Select(model => model.Model));
        Assert.False(models[0].Swaps);
        Assert.True(models[1].Swaps);
        Assert.Equal("openai", models[0].Provider);

        LlamaCppServerConfig config = settings.LlamaCppConfig!;
        Assert.Equal("mellum2.1", Assert.Single(config.Others!).Alias);
        Assert.DoesNotContain(config.Extras, extra => extra.Path == qwen);
    }

    private string ChatGguf(string file, string name)
    {
        using var ms = new MemoryStream();
        using var w = new System.IO.BinaryWriter(ms);
        void Str(string s) { byte[] b = System.Text.Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        w.Write(System.Text.Encoding.ASCII.GetBytes("GGUF"));
        w.Write(3u); w.Write(0UL); w.Write(3UL);
        Str("general.architecture"); w.Write(8u); Str("llama");
        Str("general.name"); w.Write(8u); Str(name);
        Str("tokenizer.chat_template"); w.Write(8u); Str("{{ messages }}");
        w.Flush();
        string path = Path.Combine(folder, file);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [Fact]
    public void Every_downloaded_chat_model_of_the_library_is_an_agent_model_by_its_name_and_in_the_router_preset()
    {
        string zeta = Touch(LlamaCppModels.Find("zeta-2.1")!.File);
        ChatGguf("my-own-chat-model.gguf", "My Own Chat");
        HexSettings settings = LlamaCpp(zeta);
        LocalModelLibrary library = LocalModelLibrary.Shared;
        HexSettings? before = library.Settings;
        library.Settings = settings;
        try
        {
            library.RefreshNow();
            IReadOnlyList<AiEditModel> models = AiEditModels.AgentModels(settings);
            AiEditModel own = Assert.Single(models, model => model.Model == "my-own-chat-model");
            Assert.Equal("My Own Chat", own.Title);
            Assert.DoesNotContain(models, model => model.Model == "qwen2.5-coder-1.5b");   // not downloaded: not offered
            Assert.Contains("[my-own-chat-model]", LlamaCppServerArgs.Preset(settings.LlamaCppConfig!));
        }
        finally
        {
            library.Settings = before;
        }
    }

    [Fact]
    public void ForAgent_finds_by_model_or_title_else_the_first_else_null()
    {
        string zeta = Touch(LlamaCppModels.Find("zeta-2.1")!.File);
        Touch(LlamaCppModels.Find("mellum2.1")!.File);
        Touch(LlamaCppModels.Find("qwen3.5-9b")!.File);
        HexSettings settings = LlamaCpp(zeta);

        Assert.Equal("qwen3.5-9b", AiEditModels.ForAgent(settings, "qwen3.5-9b")!.Model);
        Assert.Equal("qwen3.5-9b", AiEditModels.ForAgent(settings, "QWEN3.5 9B")!.Model);
        Assert.Equal("mellum2.1", AiEditModels.ForAgent(settings, "nothing")!.Model);
        Assert.Equal("mellum2.1", AiEditModels.ForAgent(settings, null)!.Model);
        Assert.Null(AiEditModels.ForAgent(new HexSettings(null) { EditPredictionsProvider = "link" }, "x"));
        Assert.Empty(AiEditModels.AgentModels(new HexSettings(null) { EditPredictionsProvider = HexSettings.LLAMACPP }));
    }

    [Fact]
    public void Ollama_lists_the_code_model_when_it_chats()
    {
        var settings = new HexSettings(null)
        {
            EditPredictions = true, EditPredictionsProvider = "ollama", EditPredictionsApiUrl = HexSettings.OLLAMA_URL, EditPredictionsModel = "qwen3:14b",
        };
        Assert.Equal(["qwen3:14b"], AiEditModels.AgentModels(settings).Select(model => model.Model));
        settings.EditPredictionsModel = "hf.co/zed-industries/zeta-2.1";
        Assert.Empty(AiEditModels.AgentModels(settings));
    }

    [Fact]
    public void Link_has_no_model()
    {
        var settings = new HexSettings(null) { EditPredictionsProvider = "link" };
        Assert.Null(AiEditModels.Resolve(settings, out string? problem));
        Assert.Equal("no_model", problem);
    }
}
