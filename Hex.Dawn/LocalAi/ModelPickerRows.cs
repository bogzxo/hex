namespace Hex.Dawn;

/// <summary>Where a model sits in the picker's list, in the order the groups are shown.</summary>
internal enum ModelPickerGroup { Favourites, Assigned, Computer, Ollama, Linked, Recommended }

/// <summary>One row of the model picker before it is drawn: the model, its group, why it can't do the role (null when it can) and its text.</summary>
internal sealed record ModelPickerRow(LocalModel Model, ModelPickerGroup Group, string? Unsuitable, string Detail, string[] Badges, bool Favourite = false)
{
    /// <summary>What the chooser narrows by: the group, the name, the size and quant, the badges and where it's from; the id keeps two rows apart.</summary>
    public string SearchText => $"{Model.Name}  {Detail}  {string.Join(' ', Badges)}  {Model.Source}  {Model.Id}";
}

/// <summary>
/// The model picker's list as plain data (docs/local-ai-setup-plan.md, 3.1): which group each model is in, which can't do
/// the role and why, the order, the one line of size and quant, and the badges. The popup only draws it.
/// </summary>
internal static class ModelPicker
{
    /// <summary>The reason key (local_ai_picker.reason_*) a model can't be used for a role, null when it can.</summary>
    public static string? UnsuitableFor(LocalModel model, LocalAiRole role) => role switch
    {
        _ when model.Source == LocalModelSource.Catalog => "not_downloaded",
        LocalAiRole.Chat when !model.Caps.HasFlag(LocalModelCaps.Chat) => "cant_chat",
        LocalAiRole.Predict when (model.Caps & (LocalModelCaps.Predict | LocalModelCaps.Fim | LocalModelCaps.EditModel)) == 0 => "cant_predict",
        _ => null,
    };

    /// <summary>The group a model is shown in: those assigned to a role first, then by where it lives.</summary>
    public static ModelPickerGroup GroupOf(LocalModel model, IReadOnlySet<string> assigned, bool favourite = false)
    {
        // A starred model is in the first group, once: a row is never listed twice
        if (favourite)
            return ModelPickerGroup.Favourites;
        if (assigned.Contains(model.Id))
            return ModelPickerGroup.Assigned;
        return model.Source switch
        {
            LocalModelSource.Ollama => ModelPickerGroup.Ollama,
            LocalModelSource.Linked => ModelPickerGroup.Linked,
            LocalModelSource.Catalog => ModelPickerGroup.Recommended,
            _ => ModelPickerGroup.Computer,
        };
    }

    /// <summary>"9B · Q4_K_XL", or the size on disk when the file says neither.</summary>
    public static string DetailOf(LocalModel model)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(model.SizeLabel))
            parts.Add(model.SizeLabel!);
        if (!string.IsNullOrWhiteSpace(model.Quant))
            parts.Add(model.Quant!);
        if (parts.Count == 0 && model.Bytes > 0)
            parts.Add(LocalModelLibrary.Gb(model.Bytes) + " GB");
        // Not on this card: it runs on the device that serves it
        if (model.Source == LocalModelSource.Linked)
            parts.Add("runs on " + (model.Device ?? "a linked device"));
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The badges a model has: the Models page's, from the one function (<see cref="LocalModelBadges.Keys"/>), said by
    /// <paramref name="say"/> (the localised word of a key) or in English.
    /// </summary>
    public static string[] BadgesOf(LocalModel model, Func<string, string>? say = null) =>
        [.. LocalModelBadges.Keys(model.Caps).Select(key => say?.Invoke(key) ?? EnglishBadge(key))];

    private static string EnglishBadge(string key) => key switch
    {
        "fim" => "FIM",
        "edit" => "Edit model",
        _ => char.ToUpperInvariant(key[0]) + key[1..],
    };

    // Fits first, then tight, then unknown, too big last
    private static int FitRank(LocalModelFit fit) => fit switch
    {
        LocalModelFit.Fits => 0,
        LocalModelFit.Tight => 1,
        LocalModelFit.Unknown => 2,
        _ => 3,
    };

    /// <summary>
    /// The rows in the order shown: the groups in turn, in each the models that suit the role before those that don't, the
    /// ones that fit the card before the rest, then by name. A recommended download that is too big for the card isn't offered.
    /// </summary>
    public static List<ModelPickerRow> Rows(IEnumerable<LocalModel> models, LocalAiRole role, IReadOnlySet<string> assigned, IReadOnlySet<string>? favourites = null,
        Func<string, string>? say = null)
    {
        var rows = new List<ModelPickerRow>();
        foreach (LocalModel model in models)
        {
            bool favourite = favourites?.Contains(model.Id) == true;
            ModelPickerGroup group = GroupOf(model, assigned, favourite);
            if (model.Source == LocalModelSource.Catalog && model.Fit == LocalModelFit.TooBig)
                continue;
            rows.Add(new ModelPickerRow(model, group, UnsuitableFor(model, role), DetailOf(model), BadgesOf(model, say), favourite));
        }

        // Starred models first in their group, among those that suit the role
        return [.. rows
            .OrderBy(row => row.Group)
            .ThenBy(row => row.Unsuitable is null ? 0 : 1)
            .ThenBy(row => row.Favourite ? 0 : 1)
            .ThenBy(row => FitRank(row.Model.Fit))
            .ThenBy(row => row.Model.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The groups that have a row, in the order shown: each gets a heading over its rows.</summary>
    public static List<ModelPickerGroup> GroupsShown(IEnumerable<ModelPickerRow> rows) => [.. rows.Select(row => row.Group).Distinct()];

    /// <summary>The models the Local agent may choose between: the library's that can chat, not recommended downloads, files and servers alike.</summary>
    public static List<LocalModel> AgentChoices(IEnumerable<LocalModel> models) =>
        [.. models.Where(model => model.Source != LocalModelSource.Catalog && model.Caps.HasFlag(LocalModelCaps.Chat))
            .OrderBy(model => FitRank(model.Fit)).ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)];
}
