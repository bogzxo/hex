using System.Numerics;

using Horizon.HIDL.Library;
using Horizon.HIDL.Preprocessing;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

using ValueType = Horizon.HIDL.Runtime.ValueType;

namespace Hex.Dawn;

/// <summary>A kind of view an extension adds, offered on the files it is for (see hex.view).</summary>
/// <remarks>Left and Right say whether Hex's sides stay out while the view is on screen, the hitbox editor wants no right one.</remarks>
internal sealed record ExtensionViewKind(HexExtension Owner, string Id, string Title, string Icon, string Layout, string? When, IRuntimeValue? Open,
    bool Left = true, bool Right = true);
