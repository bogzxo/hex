using System.Numerics;

using Horizon.Graphics;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

/// <summary>
/// The picture behind the code behind a notebook's cells, in the bottom right corner of the room they scroll in, placed
/// the way an editor of code places it (<see cref="CodeEditor.PlaceBackdrop(Horizon.UI.UIRect, Horizon.UI.UIRect, Texture, float, Vector2, float)"/>)
/// so it sits where it does over code. It stays put as the cells scroll by, they're drawn over it.
/// </summary>
internal sealed class NotebookBackdrop : UIComponent
{
    public Texture? Picture { get; set; }
    public float PictureScale { get; set; } = 0.5f;
    public float PictureOpacity { get; set; } = 0.3f;
    public Vector2 PictureOffset { get; set; }
    public float PictureStretch { get; set; } = 1.0f;

    protected override void Paint(UIDrawList list)
    {
        if (Picture is not { IsValid: true } picture || PictureOpacity <= 0.0f)
            return;

        list.PushClip(Bounds);
        list.Image(picture, CodeEditor.PlaceBackdrop(Bounds, Module?.Frame ?? Bounds, picture, PictureScale, PictureOffset, PictureStretch), new Vector4(1.0f, 1.0f, 1.0f, PictureOpacity));
        list.PopClip();
    }
}
