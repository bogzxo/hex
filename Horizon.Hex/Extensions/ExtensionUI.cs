using System.Numerics;

using Key = Silk.NET.Input.Key;

using Horizon.HIDL.Library;
using Horizon.HIDL.Preprocessing;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

using ValueType = Horizon.HIDL.Runtime.ValueType;

namespace Horizon.Hex;

/// <summary>A kind of view an extension adds, offered on the files it is for (see hex.view).</summary>
/// <remarks>Left and Right say whether Hex's sides stay out while the view is on screen, the hitbox editor wants no right one.</remarks>
internal sealed record ExtensionViewKind(HexExtension Owner, string Id, string Title, string Icon, string Layout, string? When, IRuntimeValue? Open,
    bool Left = true, bool Right = true);

/// <summary>
/// What an extension made out of a layout, a view of a file, a page of the panel or a window, as an extension sees it.
/// <c>ui.get(name)</c> is a part of it (its properties, as a layout's own code sees them), <c>ui.file</c> the file a view
/// is of, <c>ui.on_tick</c> and <c>ui.on_key</c> what to call as time goes by and for the keys while it is on screen,
/// <c>ui.close()</c> takes a window or a page away, <c>ui.size</c> is how much room it has.
/// </summary>
internal sealed class ExtensionUI(HexExtension owner, string kind, UILayout made, UIComponent root, string? file, Action? close, Func<Vector2>? room = null) : IHostValue
{
    public ValueType Type { get; init; } = ValueType.Host;

    public string TypeName => $"{kind} of {Owner.Name}";

    public HexExtension Owner { get; } = owner;

    public UILayout Made { get; } = made;

    /// <summary>What everything it made is inside of, shown and hidden as one.</summary>
    public UIComponent Root { get; } = root;

    public string? File { get; } = file;

    public IRuntimeValue? Tick { get; private set; }

    public IRuntimeValue? Key { get; private set; }

    /// <summary>Whether it was taken away, a window closed or its file.</summary>
    public bool Closed { get; set; }

    public IRuntimeValue Get(string member) => member switch
    {
        "get" => Natives.Function("get", args => Made.TryGet(args.Text(0), out UIComponent? part)
            ? part.Object
            : throw args.Wrong($"the layout has nothing called '{args.Text(0)}'.")),
        "file" => File is { } path ? new StringValue(path) : Values.Null,
        "on_tick" => Tick ?? Values.Null,
        "on_key" => Key ?? Values.Null,
        "visible" => Values.Bool(Root.Visible),
        "size" => new Vector2Value(room?.Invoke() ?? Root.Bounds.Size),
        "close" => Natives.Action("close", _ => close?.Invoke()),
        _ => Values.Null
    };

    public void Set(string member, IRuntimeValue value)
    {
        switch (member)
        {
            case "on_tick": Tick = value is NullValue ? null : value; break;
            case "on_key": Key = value is NullValue ? null : value; break;
            default: throw new HidlRuntimeException($"{member} of a {kind} can't be set, on_tick and on_key can.");
        }
    }
}
