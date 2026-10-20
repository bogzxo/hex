using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>
/// Helps with working out why a layout is laid out the way it is. While it is on, the layout that is being edited
/// draws its own layout over itself. The edges of every component, the padding inside of them, the gaps between
/// the children of a stack, and what the pointer is over along with its size.
/// The drawing is done by the UI the layouts are in (it is the one that knows where its components are on screen),
/// this decides whether it does and what of it, and puts the switches for that into a menu. It is part of the
/// editor rather than of the engine, and there in every build of it.
/// </summary>
internal sealed class HexLayoutDebugger(UICompositor stage)
{
    /// <summary>What is drawn while the debugger is on, each of which the menu switches by itself.</summary>
    public UILayoutOverlayOptions Options { get; } = new();

    /// <summary>Whether the layouts are drawing their layout over themselves right now.</summary>
    public bool IsOn
    {
        get => stage.LayoutOverlay is not null;
        set => stage.LayoutOverlay = value ? Options : null;
    }

    public void Toggle() => IsOn = !IsOn;

    /// <summary>
    /// Adds the switch of the debugger to a group of a menu, and under it one for everything it can draw. Those can only be
    /// picked while it is on.
    /// </summary>
    /// <param name="toggled">Called after the debugger was switched on or off from the menu, for whoever has something to say about it.</param>
    public void AddTo(HexCommands commands, string where, Action? toggled = null)
    {
        commands.Add(where, "Layout Debugger", () =>
        {
            Toggle();
            toggled?.Invoke();
        }).IsChecked = () => IsOn;

        Option(commands, where, "    Edges", () => Options.ShowBounds, on => Options.ShowBounds = on);
        Option(commands, where, "    Padding", () => Options.ShowPadding, on => Options.ShowPadding = on);
        Option(commands, where, "    Gaps", () => Options.ShowSpacing, on => Options.ShowSpacing = on);
        Option(commands, where, "    Sizes", () => Options.ShowLabels, on => Options.ShowLabels = on);
        Option(commands, where, "    Only under the pointer", () => Options.HoveredOnly, on => Options.HoveredOnly = on);
        Option(commands, where, "    Hidden components too", () => Options.ShowHidden, on => Options.ShowHidden = on);
    }

    private void Option(HexCommands commands, string where, string label, Func<bool> read, Action<bool> write)
    {
        MenuItem item = commands.Add(where, label, () => write(!read()));
        item.IsChecked = read;
        item.IsEnabled = () => IsOn;
    }
}
