namespace Hex.Core;

/// <summary>
/// The badges a model has, in the order everywhere shows them (the Models page, the first run, the picker): one function so
/// the places can't drift. A key names the badge (<c>local_ai.cap_*</c> says it in words).
/// </summary>
internal static class LocalModelBadges
{
    /// <summary>The badge keys of a model's capabilities: predict, fim, edit, chat, tools, thinking, vision.</summary>
    public static string[] Keys(LocalModelCaps caps)
    {
        (LocalModelCaps Cap, string Key)[] all =
        [
            (LocalModelCaps.Predict, "predict"), (LocalModelCaps.Fim, "fim"), (LocalModelCaps.EditModel, "edit"),
            (LocalModelCaps.Chat, "chat"), (LocalModelCaps.Tools, "tools"), (LocalModelCaps.Thinking, "thinking"), (LocalModelCaps.Vision, "vision"),
        ];
        return [.. all.Where(each => (caps & each.Cap) != 0).Select(each => each.Key)];
    }
}
