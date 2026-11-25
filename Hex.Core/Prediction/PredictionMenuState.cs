namespace Hex.Core;

/// <summary>What the Predictions menu says of the chat model.</summary>
internal enum ChatModelState
{
    /// <summary>A chat model of its own is set.</summary>
    Chosen,
    /// <summary>None is set and the predictions' model can chat, so it is used.</summary>
    SameAsPredictions,
    /// <summary>None is set and the predictions' model can't chat (Zeta), so there is no chat until one is picked.</summary>
    NoneCantChat,
    /// <summary>None is set; AI Edit's own model (the router's edit model) is the effective one.</summary>
    Automatic,
    /// <summary>Nothing is set at all.</summary>
    NoneSet,
}

/// <summary>The words-free parts of the Predictions status menu, kept apart so they can be tested.</summary>
internal static class PredictionMenuState
{
    /// <summary>Which of the chat model's states the roles are in.</summary>
    public static ChatModelState ChatState(HexSettings settings, LocalAiRoleModels roles)
    {
        if (!string.IsNullOrWhiteSpace(settings.LocalAiChatModel) && roles.Chat is not null)
            return ChatModelState.Chosen;
        if (roles.ChatAutomatic && roles.Chat is not null)
            return ChatModelState.Automatic;
        if (roles.Chat is not null)
            return ChatModelState.SameAsPredictions;
        return roles.Predict is not null ? ChatModelState.NoneCantChat : ChatModelState.NoneSet;
    }

    /// <summary>The other of the display modes, eager or subtle.</summary>
    public static string OtherMode(string mode) => string.Equals(mode, "subtle", StringComparison.Ordinal) ? "eager" : "subtle";
}
