namespace Hex.Installation;

/// <summary>Installer texts loaded by the host with Horizon's localisation reader.</summary>
public static class InstallText
{
    private static IReadOnlyDictionary<string, string> strings = new Dictionary<string, string>();

    // Replace the complete snapshot before starting background installation work.
    public static void Use(IReadOnlyDictionary<string, string> texts) => Volatile.Write(ref strings, texts);

    public static string Get(string key, params (string Name, string Value)[] values)
    {
        if (!Volatile.Read(ref strings).TryGetValue("installer." + key, out string? text)) return "@installer." + key;
        foreach (var (name, value) in values) text = text.Replace("{" + name + "}", value);
        return text;
    }
}
