namespace Hex.Core;

/// <summary>How a far (jump) prediction is asked for (docs/next-edit-plan.md, 4.1).</summary>
internal enum PredictionFar
{
    /// <summary>Not at all, the rename tracker's suggestions only.</summary>
    None,

    /// <summary>A Zeta region at the candidate line.</summary>
    ZetaRegion,

    /// <summary>A chat request for the region at the candidate line.</summary>
    Chat,
}

/// <summary>
/// What is done for a model of a kind: the format near the caret, how far predictions are asked for, and whether it can
/// follow an instruction (Ctrl+I). An instruct model is asked near the caret with <see cref="ChatEditFormat"/>; a model
/// that does both keeps fill in the middle there, and gets the chat format for far predictions when those come.
/// </summary>
internal sealed record PredictionStrategy(PredictionModelKind Kind, IPredictionFormat Near, PredictionFar Far, bool Instructions)
{
    /// <summary>
    /// The strategy for a kind. A format the options name is the user's choice and stays the near format; otherwise the
    /// model's name picks one of the kind's family: an edit model gets a Zeta format (2.1 when the name says nothing), every
    /// other kind a fill in the middle one.
    /// </summary>
    public static PredictionStrategy For(PredictionOptions options, PredictionModelKind kind)
    {
        // An instruct model is asked as a chat, which Hex Link can't carry (its request has no turns), so a linked device
        // keeps the fill in the middle prompt
        bool chat = kind == PredictionModelKind.Instruct && !options.Provider.Trim().Equals(PredictionProviders.LINK, StringComparison.OrdinalIgnoreCase);
        IPredictionFormat near = PredictionFormats.Named(options.Format) ?? (chat ? ChatEditFormat.Near : Default(options.Model, kind));
        return kind switch
        {
            PredictionModelKind.EditModel => new(kind, near, PredictionFar.ZetaRegion, false),
            PredictionModelKind.Fim => new(kind, near, PredictionFar.None, false),
            PredictionModelKind.FimAndInstruct => new(kind, near, PredictionFar.Chat, true),
            _ => new(kind, near, PredictionFar.Chat, true),
        };
    }

    /// <summary>The strategy for the model the options describe, its kind decided as <see cref="PredictionModels.KindOf"/> does.</summary>
    public static PredictionStrategy For(PredictionOptions options) => For(options, PredictionModels.KindOf(options));

    private static IPredictionFormat Default(string model, PredictionModelKind kind)
    {
        IPredictionFormat inferred = PredictionFormats.Infer(model);
        bool zeta = inferred.Name is PredictionFormats.ZETA2 or PredictionFormats.ZETA21;
        if (kind == PredictionModelKind.EditModel)
            return zeta ? inferred : PredictionFormats.Named(PredictionFormats.ZETA21)!;
        return zeta ? FimFormat.Qwen : inferred;
    }
}
