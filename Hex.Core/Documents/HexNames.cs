using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>
/// The names the components of a layout go by in its code, both ways round. A name was looked up by going through every
/// component there was, for every name a new component was offered and every one the history put back.
/// </summary>
internal sealed class HexNames
{
    private readonly Dictionary<UIComponent, string> byComponent = [];
    private readonly Dictionary<string, UIComponent> byName = new(StringComparer.Ordinal);

    public int Count => byComponent.Count;

    public bool TryGetName(UIComponent component, out string name) => byComponent.TryGetValue(component, out name!);

    public string? NameOf(UIComponent component) => byComponent.GetValueOrDefault(component);

    /// <summary>The component going by a name, null for none.</summary>
    public UIComponent? Find(string name) => byName.GetValueOrDefault(name);

    /// <summary>Whether something goes by a name already.</summary>
    public bool Has(string name) => byName.ContainsKey(name);

    public void Set(UIComponent component, string name)
    {
        if (byComponent.TryGetValue(component, out string? old) && byName.TryGetValue(old, out UIComponent? holder) && holder == component)
            byName.Remove(old);

        byComponent[component] = name;
        byName[name] = component;
    }

    public void Remove(UIComponent component)
    {
        // The name only goes with it if it was still its, two things are never meant to share one but a script can
        if (byComponent.Remove(component, out string? name) && byName.TryGetValue(name, out UIComponent? holder) && holder == component)
            byName.Remove(name);
    }

    public void Clear()
    {
        byComponent.Clear();
        byName.Clear();
    }
}
