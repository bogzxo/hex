using System.Text.Json.Nodes;

namespace Hex.Core;

// A device's name changing while it's linked. link/hello says it as a link comes up; when the person renames this
// machine afterwards, link/name {name} tells each device linked to it at once, and the other end's peer takes the new
// name (cleaned, it's the other machine's data) and says it moved.
internal sealed partial class HexLinkPeer
{
    /// <summary>The other machine said it's called something else now, its device as it is now, on the link's thread.</summary>
    public Action<HexLinkDevice>? Renamed { get; set; }

    /// <summary>This machine's new name told to the other one (link/name), which shows it at once.</summary>
    public void SayName(string name)
    {
        if (HexLinkNames.Clean(name) is { } clean && !IsClosed)
            session.TellLatest("link/name", new JsonObject { ["name"] = clean });
    }

    // link/name heard: the other machine's new name taken, cleaned, and said
    private void TakeName(JsonObject said)
    {
        if (HexLinkNames.Clean(HexLinkData.String(said, "name", 400)) is not { } name || name == Device.Name)
            return;
        Device = Device with { Name = name };
        Renamed?.Invoke(Device);
        Changed?.Invoke();
    }
}
