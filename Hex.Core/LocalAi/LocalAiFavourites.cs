namespace Hex.Core;

/// <summary>
/// The models starred in the picker (local_ai_favourites: ids separated by semicolons). They sort first in the picker, and
/// a command goes through the ones that can chat. Plain data, so the order and the cycle are tested without a window.
/// </summary>
internal static class LocalAiFavourites
{
    /// <summary>The ids in a setting's value, in the order stored, each once.</summary>
    public static List<string> Parse(string value)
    {
        var ids = new List<string>();
        foreach (string part in value.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ids.Contains(part, StringComparer.OrdinalIgnoreCase))
                ids.Add(part);
        }

        return ids;
    }

    /// <summary>Whether a model is starred in a setting's value.</summary>
    public static bool Contains(string value, string id) => Parse(value).Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The setting's value with a model starred, or unstarred when it was.</summary>
    public static string Toggle(string value, string id)
    {
        List<string> ids = Parse(value);
        int at = ids.FindIndex(each => string.Equals(each, id, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
            ids.RemoveAt(at);
        else
            ids.Add(id);
        return string.Join(";", ids);
    }

    /// <summary>
    /// The favourite chat model after the current one, in the order they were starred, going round to the first. Only
    /// favourites that are still in the library and can chat count. The first when the current one isn't a favourite, null
    /// when there are none.
    /// </summary>
    public static LocalModel? Next(string value, IEnumerable<LocalModel> models, string? currentId)
    {
        var chat = new List<LocalModel>();
        foreach (string id in Parse(value))
        {
            LocalModel? model = models.FirstOrDefault(each => string.Equals(each.Id, id, StringComparison.OrdinalIgnoreCase));
            if (model is not null && model.IsDownloaded && model.Caps.HasFlag(LocalModelCaps.Chat))
                chat.Add(model);
        }

        if (chat.Count == 0)
            return null;

        int at = currentId is null ? -1 : chat.FindIndex(each => string.Equals(each.Id, currentId, StringComparison.OrdinalIgnoreCase));
        return chat[(at + 1) % chat.Count];
    }
}
