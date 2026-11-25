namespace Hex.Core;

[Flags]
internal enum LocalModelCaps { None = 0, Predict = 1, Fim = 2, EditModel = 4, Chat = 8, Tools = 16, Thinking = 32, Vision = 64 }

internal static class LocalModelCapsOf
{
    /// <summary>Inferred from the GGUF: a chat template gives Chat (plus Tools and Thinking by what it mentions), FIM tokens give Fim and Predict, Zeta gives EditModel and Predict and not Chat.</summary>
    public static LocalModelCaps From(GgufInfo info)
    {
        var caps = LocalModelCaps.None;
        if (info.ChatTemplate is { Length: > 0 })
        {
            caps |= LocalModelCaps.Chat;
            if (info.TemplateTools)
                caps |= LocalModelCaps.Tools;
            if (info.TemplateThinking)
                caps |= LocalModelCaps.Thinking;
        }
        if (info.HasFimTokens) caps |= LocalModelCaps.Fim | LocalModelCaps.Predict;
        if (info.IsZeta)
        {
            // Its tokenizer has FIM tokens, but Hex drives it through the Zeta format, so Fim (what Hex uses FIM with) is dropped
            caps |= LocalModelCaps.EditModel | LocalModelCaps.Predict;
            caps &= ~(LocalModelCaps.Chat | LocalModelCaps.Tools | LocalModelCaps.Thinking | LocalModelCaps.Fim);
        }
        return caps;
    }

    /// <summary>The same for Ollama, from /api/show "capabilities" (completion, tools, thinking, insert, vision).</summary>
    public static LocalModelCaps FromOllama(IReadOnlyList<string> capabilities, string name)
    {
        var caps = LocalModelCaps.None;
        foreach (string capability in capabilities ?? [])
        {
            switch (capability.Trim().ToLowerInvariant())
            {
                case "completion": caps |= LocalModelCaps.Chat; break;
                case "tools": caps |= LocalModelCaps.Tools; break;
                case "thinking": caps |= LocalModelCaps.Thinking; break;
                case "insert": caps |= LocalModelCaps.Fim | LocalModelCaps.Predict; break;
                case "vision": caps |= LocalModelCaps.Vision; break;
            }
        }
        if ((name ?? string.Empty).Contains("zeta", StringComparison.OrdinalIgnoreCase))
        {
            caps |= LocalModelCaps.EditModel | LocalModelCaps.Predict;
            caps &= ~(LocalModelCaps.Chat | LocalModelCaps.Tools | LocalModelCaps.Thinking | LocalModelCaps.Fim);
        }
        return caps;
    }
}

internal enum LocalAiRole { Predict, Chat }   // Chat covers AI Edit, Link Chat and the Local agent
