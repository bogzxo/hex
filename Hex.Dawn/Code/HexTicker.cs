using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>
/// A component that draws nothing and takes no room (it's kept hidden), there for a tick of its own. The search view
/// waits out a quarter of a second of typing with one before it looks.
/// </summary>
internal sealed class HexTicker : UIComponent
{
    /// <summary>Every tick, with how long it was.</summary>
    public Action<float>? Ticked { get; set; }

    protected override void Update(float dt) => Ticked?.Invoke(dt);
}
