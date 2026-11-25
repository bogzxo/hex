namespace Hex.Core;

/// <summary>
/// What a linked device may do on this machine, said per device by the person here and checked on every request
/// (docs/hex-link-plan.md, 3.4). Nothing is the start, each is ticked at pairing or in Link's window after.
/// </summary>
[Flags]
internal enum HexLinkRights
{
    None = 0,

    /// <summary>See the fleet, chats, plans and the workspace's files.</summary>
    View = 1,

    /// <summary>Tell agents things, answer them, stop, restart and start them. As much as sitting at the machine.</summary>
    Steer = 2,

    /// <summary>Write the workspace's files.</summary>
    Edit = 4,

    /// <summary>
    /// Use this machine's model for autocomplete (edit predictions), when this machine shares its predictions. It takes
    /// nothing else, not even View, and nothing else takes it: a weaker machine may borrow the model without seeing a file.
    /// </summary>
    Predict = 8,

    /// <summary>
    /// Manage the agents here that this device did not start: stop, restart, rename, close and delete them. It takes
    /// Steer (and View) with it. A device that only has Steer still manages the chats it started itself.
    /// </summary>
    Manage = 16,

    /// <summary>
    /// Open the TCP ports this machine shares with the device (shared ports, docs/hex-link-plan.md). A port is reachable
    /// only when it is both shared to the device here and this right is ticked, off until the person ticks it. It takes View
    /// with it, not Steer: a tunnel to a local server runs nothing on this machine, but it does reach whatever listens there.
    /// </summary>
    Ports = 32,

    /// <summary>
    /// Use this machine's chat models (AI Edit, the agents, Link Chat's @ai) when this machine serves AI (docs/hex-link-plan.md,
    /// 5.7). Like Predict it takes nothing else, not even View: the device sees no files, it only sends the conversation it
    /// wants answered. Off until the person ticks it, per device.
    /// </summary>
    Ai = 64
}

/// <summary>
/// Another Hex as a link knows it. <paramref name="Id"/> is the SHA-256 of its certificate's public key in upper-case
/// hex, proved by the TLS handshake, the rest is what it says of itself and only ever shown.
/// </summary>
internal sealed record HexLinkDevice(string Id, string Name, string Os = "", string Arch = "", string Version = "")
{
    /// <summary>The start of the id in groups a person can read out, XXXX-XXXX-XXXX-XXXX.</summary>
    public string Fingerprint => Id.Length < 16 ? Id : string.Join('-', Enumerable.Range(0, 4).Select(group => Id.Substring(group * 4, 4)));
}

/// <summary>
/// A connection to a known device, past the TLS handshake, the pin check and the first line, which the transport
/// hands to whoever speaks the protocol over it (HexLinkSession). The stream is the encrypted one. What the device
/// may do here is asked each time rather than kept, so a right taken away in Link's window holds from its next request.
/// </summary>
internal sealed class HexLinkConnection(Stream stream, HexLinkDevice device, string address, bool dialled, Func<HexLinkRights> rights,
    Action? closed = null, HexLinkBulkSupport bulk = HexLinkBulkSupport.None, HexLinkAudit? audit = null) : IDisposable
{
    private int disposed;

    /// <summary>The authenticated stream carrying JSON messages and, when negotiated, binary frames.</summary>
    public Stream Stream { get; } = stream;

    /// <summary>Who's at the other end.</summary>
    public HexLinkDevice Device { get; } = device;

    /// <summary>The address it came from or was dialled at, for showing.</summary>
    public string Address { get; } = address;

    /// <summary>Whether this machine dialled it rather than took it in.</summary>
    public bool Dialled { get; } = dialled;

    /// <summary>What the device may do here now, None once it's forgotten.</summary>
    public HexLinkRights Rights => rights();

    /// <summary>The best bulk format supported by both ends of this connection.</summary>
    public HexLinkBulkSupport Bulk { get; } = bulk;

    public void ReportFault(string operation, Exception error)
    {
        string detail = $"{operation}: {error.GetType().Name}: {error.Message}";
        if (audit is not null)
            audit.Write("session fault", Device.Id, detail);
        else
            Console.WriteLine($"[Hex] Link {Device.Name}, {detail}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        try { Stream.Dispose(); } catch (Exception) { }
        closed?.Invoke();
    }
}
