using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Shell;

/// <summary>
/// Section panel shown under <see cref="EditorSettingsCatalog.MODULES"/> in Settings. When no module changes are pending,
/// displays a calm note that module toggles take effect on the next launch. When one or more module toggles differ from
/// the active session, renders an accent-tinted banner card with a themed Restart Hex button that adapts to every skin.
/// </summary>
public sealed class ModulesRestartPanel : StackPanel
{
    private const float CARD_PAD_X = 14.0f, CARD_PAD_Y = 10.0f, OUTER_LEFT = 14.0f, OUTER_RIGHT = 10.0f, OUTER_Y = 6.0f, GAP = 12.0f;
    private const float BUTTON_RESERVE = 152.0f;

    private readonly float maxWidth;
    private readonly Label message;
    private readonly Button restart;

    /// <summary>How many optional modules currently have saved changes awaiting a restart.</summary>
    public int PendingCount { get; private set; }

    /// <summary>Whether at least one module change is awaiting a restart.</summary>
    public bool HasPending => PendingCount > 0;

    /// <summary>The themed Restart Hex button shown when <see cref="HasPending"/> is true.</summary>
    public Button RestartButton => restart;

    public ModulesRestartPanel(float width, Action onRestart)
    {
        ArgumentNullException.ThrowIfNull(onRestart);
        maxWidth = MathF.Max(280.0f, width);
        Direction = UIDirection.Horizontal;
        Spacing = GAP;
        Anchor = Origin.TopLeft;
        Pivot = Origin.TopLeft;
        Size = new Vector2(maxWidth, 0.0f);

        message = Add(new Label
        {
            Text = HexText.Get("settings_page.modules_restart_idle"),
            Dim = true,
            Wrap = true,
            Align = Origin.Left,
            Anchor = Origin.Left,
            Pivot = Origin.Left
        });

        restart = Add(new Button
        {
            Label = HexText.Get("settings_page.modules_restart_button"),
            Icon = "debug-restart",
            Primary = true,
            Animated = false,
            Tooltip = HexText.Get("settings_page.modules_restart_button_tip"),
            OnPressed = onRestart,
            Visible = false,
            Anchor = Origin.Center,
            Pivot = Origin.Center
        });

        ApplyLayoutState();
    }

    /// <summary>Updates the banner state for the current count of pending module changes.</summary>
    public void SetPending(int count)
    {
        count = Math.Max(0, count);
        PendingCount = count;
        restart.Visible = count > 0;
        message.Dim = count == 0;
        message.Text = count switch
        {
            0 => HexText.Get("settings_page.modules_restart_idle"),
            1 => HexText.Get("settings_page.modules_restart_pending_one"),
            _ => HexText.Get("settings_page.modules_restart_pending_many", ("count", count.ToString()))
        };
        ApplyLayoutState();
    }

    private void ApplyLayoutState()
    {
        float innerWidth = MathF.Max(160.0f, maxWidth - OUTER_LEFT - OUTER_RIGHT);
        if (HasPending)
        {
            Padding = new UIEdges(OUTER_LEFT + CARD_PAD_X, OUTER_Y + CARD_PAD_Y, OUTER_RIGHT + CARD_PAD_X, OUTER_Y + CARD_PAD_Y);
            message.Size = new Vector2(MathF.Max(120.0f, innerWidth - CARD_PAD_X * 2.0f - GAP - BUTTON_RESERVE), 0.0f);
        }
        else
        {
            Padding = new UIEdges(OUTER_LEFT, OUTER_Y, OUTER_RIGHT, OUTER_Y);
            message.Size = new Vector2(innerWidth, 0.0f);
        }
    }

    protected override void Paint(UIDrawList list)
    {
        if (HasPending)
        {
            UISkin skin = list.Skin;
            UIRect card = Bounds.Shrink(new UIEdges(OUTER_LEFT, OUTER_Y, OUTER_RIGHT, OUTER_Y));
            float radius = MathF.Max(6.0f, skin.CornerRadius);
            list.RoundRect(card, radius, skin.AccentColor with { W = 0.12f });
            list.RoundOutline(card, radius, 1.0f, skin.AccentColor with { W = 0.45f });
        }

        base.Paint(list);
    }
}
