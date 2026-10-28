using Horizon.Engine;
using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>Document zoom belongs to notebook content; the surrounding IDE keeps its own scale.</summary>
internal sealed class NotebookScrollPanel : ScrollPanel
{
    public Action<float>? Zoom { get; set; }
    protected override bool UsesScrollSpeed => GameEngine.Instance?.Input.Keyboard.Primary != true;

    protected override bool OnScroll(float delta)
    {
        if (GameEngine.Instance?.Input.Keyboard.Primary == true && Zoom is { } zoom)
        {
            zoom(delta);
            return true;
        }
        return base.OnScroll(delta);
    }
}
