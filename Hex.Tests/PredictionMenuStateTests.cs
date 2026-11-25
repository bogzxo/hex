using Hex.Core;

using Xunit;

namespace Hex.Tests;

public sealed class PredictionMenuStateTests
{
    private static LocalModel Model(string id, LocalModelCaps caps) =>
        new(id, id, LocalModelSource.Ollama, null, 0, caps, null, null, null, null, LocalModelFit.Unknown, null);

    private static HexSettings Fresh() => new(Path.Combine(Path.GetTempPath(), "hex-menu-state-" + Guid.NewGuid().ToString("N"), "settings.hor"));

    private static HexSettings Settings(string chat)
    {
        HexSettings settings = Fresh();
        settings.LocalAiChatModel = chat;
        return settings;
    }

    [Fact]
    public void A_chat_model_of_its_own_is_chosen()
    {
        var chat = Model("ollama:chat", LocalModelCaps.Chat);
        var roles = new LocalAiRoleModels(Model("ollama:p", LocalModelCaps.Predict), chat, true);
        Assert.Equal(ChatModelState.Chosen, PredictionMenuState.ChatState(Settings("ollama:chat"), roles));
    }

    [Fact]
    public void No_chat_model_uses_the_predictions_when_it_can_chat()
    {
        var both = Model("ollama:p", LocalModelCaps.Predict | LocalModelCaps.Chat);
        var roles = new LocalAiRoleModels(both, both, false);
        Assert.Equal(ChatModelState.SameAsPredictions, PredictionMenuState.ChatState(Settings(string.Empty), roles));
    }

    [Fact]
    public void A_predictions_model_that_cannot_chat_leaves_no_chat_model()
    {
        var roles = new LocalAiRoleModels(Model("ollama:zeta", LocalModelCaps.Predict), null, false);
        Assert.Equal(ChatModelState.NoneCantChat, PredictionMenuState.ChatState(Settings(string.Empty), roles));
        Assert.Equal(ChatModelState.NoneSet, PredictionMenuState.ChatState(Settings(string.Empty), new LocalAiRoleModels(null, null, false)));
    }

    [Fact]
    public void An_automatic_chat_model_is_said_to_be_automatic_unless_one_is_chosen()
    {
        var zeta = Model("ollama:zeta", LocalModelCaps.Predict);
        var mellum = Model("file:mellum", LocalModelCaps.Chat);
        var roles = new LocalAiRoleModels(zeta, mellum, true, ChatAutomatic: true);
        Assert.Equal(ChatModelState.Automatic, PredictionMenuState.ChatState(Settings(string.Empty), roles));
        Assert.Equal(ChatModelState.Chosen, PredictionMenuState.ChatState(Settings("file:mellum"), roles));
    }

    [Fact]
    public void The_display_mode_toggles_between_eager_and_subtle()
    {
        Assert.Equal("subtle", PredictionMenuState.OtherMode("eager"));
        Assert.Equal("eager", PredictionMenuState.OtherMode("subtle"));
        Assert.Equal("subtle", PredictionMenuState.OtherMode("anything"));
        HexSettings settings = Fresh();
        Assert.Equal("eager", settings.EditPredictionsMode);
        Assert.False(settings.PredictionSubtle);
    }
}
