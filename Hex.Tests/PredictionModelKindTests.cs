using System.Net;
using System.Text;
using Hex.Core;

namespace Hex.Tests;

/// <summary>Which kind of model a model is: by its name, by what the servers say of it, and by the setting.</summary>
public sealed class PredictionModelKindTests
{
    [Fact]
    public void The_name_says_the_kind()
    {
        (string Model, PredictionModelKind Kind)[] table =
        [
            ("zeta-2.1", PredictionModelKind.EditModel),
            ("hf.co/bartowski/zed-industries_zeta-2-GGUF:Q4_K_M", PredictionModelKind.EditModel),
            ("mradermacher/zeta-2.1-i1-GGUF:Q4_K_M", PredictionModelKind.EditModel),
            ("qwen3.5-9b", PredictionModelKind.FimAndInstruct),
            ("qwen3.5:9b", PredictionModelKind.FimAndInstruct),
            ("mellum2.1", PredictionModelKind.FimAndInstruct),
            ("qwen2.5-coder-1.5b", PredictionModelKind.Fim),
            ("qwen2.5-coder:7b-base", PredictionModelKind.Fim),
            ("starcoder2:3b", PredictionModelKind.Fim),
            ("codellama:7b-code", PredictionModelKind.Fim),
            ("deepseek-coder:6.7b", PredictionModelKind.Fim),
            ("codestral:22b", PredictionModelKind.Fim),
            ("codegemma:2b", PredictionModelKind.Fim),
            ("qwen2.5-coder:7b-instruct", PredictionModelKind.FimAndInstruct),
            ("codegemma-7b-it", PredictionModelKind.FimAndInstruct),
            ("llama3.1:8b-instruct", PredictionModelKind.Instruct),
            ("gemma-3-it", PredictionModelKind.Instruct),
            ("some-chat-model", PredictionModelKind.Instruct),
            ("fitting-model", PredictionModelKind.Fim),
            ("unknown", PredictionModelKind.Fim),
        ];
        foreach (var (model, kind) in table)
            Assert.True(kind == PredictionModels.FromName(model), model);
    }

    private const string SHOW_FIM = """{"template":"{{ .Prompt }}{{ if .Suffix }}<|fim_suffix|>{{ .Suffix }}{{ end }}","capabilities":["completion","insert"]}""";
    private const string SHOW_BOTH = """{"template":"{{- if .Suffix }}<|fim_prefix|>{{ .Prompt }}<|fim_suffix|>{{ .Suffix }}{{ else }}{{ range .Messages }}<|im_start|>{{ .Role }}{{ end }}{{ end }}","capabilities":["completion","insert","tools"]}""";
    private const string SHOW_CHAT = """{"template":"{{ range .Messages }}<|im_start|>{{ .Role }}\n{{ .Content }}<|im_end|>{{ end }}","capabilities":["completion","tools"]}""";
    private const string SHOW_RAW = """{"template":"{{ .Prompt }}","capabilities":["completion"]}""";

    [Fact]
    public void Ollamas_show_is_read_for_its_template_and_capabilities()
    {
        Assert.Equal(PredictionModelKind.Fim, PredictionModels.ParseOllamaShow(SHOW_FIM));
        Assert.Equal(PredictionModelKind.FimAndInstruct, PredictionModels.ParseOllamaShow(SHOW_BOTH));
        Assert.Equal(PredictionModelKind.Instruct, PredictionModels.ParseOllamaShow(SHOW_CHAT));
        Assert.Null(PredictionModels.ParseOllamaShow(SHOW_RAW));
        Assert.Equal(PredictionModelKind.Fim, PredictionModels.ParseOllamaShow("""{"capabilities":["completion","insert"]}"""));
        Assert.Null(PredictionModels.ParseOllamaShow("not json"));
        Assert.Null(PredictionModels.ParseOllamaShow("[]"));
    }

    [Fact]
    public void Llama_servers_props_and_tokenize_are_read()
    {
        Assert.True(PredictionModels.ParseLlamaProps("""{"chat_template":"{% for m in messages %}","default_generation_settings":{}}"""));
        Assert.False(PredictionModels.ParseLlamaProps("""{"chat_template":""}"""));
        Assert.Null(PredictionModels.ParseLlamaProps("""{"total_slots":1}"""));
        Assert.Null(PredictionModels.ParseLlamaProps("{"));

        Assert.True(PredictionModels.ParseTokenize("""{"tokens":[151659]}"""));
        Assert.False(PredictionModels.ParseTokenize("""{"tokens":[27,91,69,10]}"""));
        Assert.False(PredictionModels.ParseTokenize("nope"));

        Assert.Equal(PredictionModelKind.FimAndInstruct, PredictionModels.Combine(true, true));
        Assert.Equal(PredictionModelKind.Fim, PredictionModels.Combine(true, false));
        Assert.Equal(PredictionModelKind.Instruct, PredictionModels.Combine(false, true));
        Assert.Null(PredictionModels.Combine(false, false));
    }

    [Fact]
    public void The_server_overrides_the_name_except_for_edit_models_and_base_code_models_with_a_chat_template()
    {
        Assert.Equal(PredictionModelKind.Instruct, PredictionModels.Decide("auto", "unknown", PredictionModelKind.Instruct));
        Assert.Equal(PredictionModelKind.EditModel, PredictionModels.Decide("auto", "zeta-2.1", PredictionModelKind.Instruct));
        Assert.Equal(PredictionModelKind.Fim, PredictionModels.Decide("auto", "qwen2.5-coder-1.5b", PredictionModelKind.FimAndInstruct));
        Assert.Equal(PredictionModelKind.FimAndInstruct, PredictionModels.Decide("auto", "qwen3.5-9b", PredictionModelKind.FimAndInstruct));
    }

    [Fact]
    public void The_setting_overrides_everything_and_auto_or_nonsense_overrides_nothing()
    {
        Assert.Equal(PredictionModelKind.Instruct, PredictionModels.Decide("instruct", "zeta-2.1", PredictionModelKind.Fim));
        Assert.Equal(PredictionModelKind.EditModel, PredictionModels.Decide(" EDIT_MODEL ", "qwen3.5-9b", null));
        Assert.Equal(PredictionModelKind.FimAndInstruct, PredictionModels.Decide("fim_instruct", "x", null));
        Assert.Equal(PredictionModelKind.Fim, PredictionModels.Decide("fim", "x", PredictionModelKind.Instruct));
        Assert.Equal(PredictionModelKind.EditModel, PredictionModels.Decide("auto", "zeta-2.1", null));
        Assert.Equal(PredictionModelKind.EditModel, PredictionModels.Decide("nonsense", "zeta-2.1", null));

        foreach (string name in PredictionModels.Names.Skip(1))
            Assert.Equal(name, PredictionModels.NameOf(PredictionModels.Named(name)!.Value));
        Assert.Null(PredictionModels.Named("auto"));
    }

    [Fact]
    public void The_options_kind_comes_from_the_setting_and_the_strategy_follows_it()
    {
        PredictionOptions options = PredictionTestKit.Options("infer") with { Model = "qwen3.5-9b" };
        Assert.Equal(PredictionModelKind.FimAndInstruct, PredictionModels.KindOf(options));
        PredictionStrategy strategy = PredictionStrategy.For(options);
        Assert.Equal("qwen", strategy.Near.Name);
        Assert.Equal(PredictionFar.Chat, strategy.Far);
        Assert.True(strategy.Instructions);

        PredictionOptions zeta = options with { Model = "zeta-2.1" };
        strategy = PredictionStrategy.For(zeta);
        Assert.Equal(PredictionModelKind.EditModel, strategy.Kind);
        Assert.Equal(PredictionFormats.ZETA21, strategy.Near.Name);
        Assert.Equal(PredictionFar.ZetaRegion, strategy.Far);
        Assert.False(strategy.Instructions);

        // A setting says an unrecognised name is an edit model: Zeta's format, not Qwen's
        strategy = PredictionStrategy.For(options with { Kind = "edit_model" });
        Assert.Equal(PredictionModelKind.EditModel, strategy.Kind);
        Assert.Equal(PredictionFormats.ZETA21, strategy.Near.Name);

        // A zeta name told to fill in the middle gets a fill in the middle format
        strategy = PredictionStrategy.For(zeta with { Kind = "fim" });
        Assert.Equal(PredictionModelKind.Fim, strategy.Kind);
        Assert.Equal("qwen", strategy.Near.Name);
        Assert.Equal(PredictionFar.None, strategy.Far);
        Assert.False(strategy.Instructions);

        // A format the user named is kept
        strategy = PredictionStrategy.For(options with { Format = "codestral" });
        Assert.Equal("codestral", strategy.Near.Name);

        // The default is what PredictionFormats.For said before
        Assert.Equal(PredictionFormats.For(zeta).Name, PredictionStrategy.For(zeta).Near.Name);
        Assert.Equal(PredictionFormats.For(PredictionTestKit.Options("infer")).Name, PredictionStrategy.For(PredictionTestKit.Options("infer")).Near.Name);
    }

    [Fact]
    public async Task A_probe_is_remembered_and_overrides_the_name()
    {
        var server = new Handler(request => request.RequestUri!.AbsolutePath == "/api/show" ? SHOW_CHAT : null);
        var options = PredictionTestKit.Options("infer") with { Model = "probe-" + Guid.NewGuid(), ApiUrl = "http://probe-test:11434" };
        Assert.Equal(PredictionModelKind.Fim, PredictionModels.KindOf(options));

        await PredictionModels.Probe(new HttpClient(server), options, default);

        Assert.Equal(PredictionModelKind.Instruct, PredictionModels.KindOf(options));
        Assert.Equal(PredictionModelKind.Fim, PredictionModels.KindOf(options with { Kind = "fim" }));
        Assert.Equal(1, server.Calls);

        // Asked once
        await PredictionModels.Probe(new HttpClient(server), options, default);
        Assert.Equal(1, server.Calls);
    }

    [Fact]
    public async Task A_llama_server_is_probed_by_its_props_and_one_fim_token()
    {
        var server = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/props" => """{"chat_template":"{% for m in messages %}x{% endfor %}"}""",
            "/tokenize" => """{"tokens":[151659]}""",
            _ => null,
        });
        var options = PredictionTestKit.Options("infer") with { Provider = "openai", Model = "probe-" + Guid.NewGuid(), ApiUrl = "http://llama-probe:8080/v1" };

        await PredictionModels.Probe(new HttpClient(server), options, default);

        Assert.Equal(PredictionModelKind.FimAndInstruct, PredictionModels.KindOf(options));
    }

    [Fact]
    public async Task A_server_that_fails_or_says_nothing_leaves_the_name_to_say()
    {
        var options = PredictionTestKit.Options("infer") with { Model = "zz-" + Guid.NewGuid(), ApiUrl = "http://probe-fails:11434" };
        await PredictionModels.Probe(new HttpClient(new Handler(_ => null)), options, default);
        await PredictionModels.Probe(new HttpClient(new Handler(_ => SHOW_RAW)), options, default);
        await PredictionModels.Probe(new HttpClient(new Handler(_ => throw new HttpRequestException("down"))), options, default);
        Assert.Equal(PredictionModelKind.Fim, PredictionModels.KindOf(options));
    }

    private sealed class Handler(Func<HttpRequestMessage, string?> answer) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Calls++;
            string? body = answer(request);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
