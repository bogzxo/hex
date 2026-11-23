using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>A message said by a person on Link, with the id all its copies carry.</summary>
internal sealed record HexLinkTalk(Guid Id, HexLinkDevice Sender, string Text, DateTimeOffset SentAt)
{
    /// <summary>The most characters a message may have.</summary>
    public const int MOST_TEXT = 2000;

    /// <summary>The most messages kept in one Link session, their ids kept as long as the transcript.</summary>
    public const int MOST_MESSAGES = 4096;

    /// <summary>The ordered notification that carries the message.</summary>
    public const string METHOD = "talk/say";

    /// <summary>A message as the wire says it, with the sender when another device passes it on.</summary>
    public JsonObject ToJson(bool relayed = false)
    {
        var said = new JsonObject { ["id"] = Id.ToString("N"), ["text"] = Text, ["sentAt"] = SentAt.ToUnixTimeMilliseconds() };
        if (relayed)
            said["device"] = new JsonObject { ["id"] = Sender.Id, ["name"] = Sender.Name };
        return said;
    }

    /// <summary>A message read from the wire, null if it is missing anything the chat needs.</summary>
    public static HexLinkTalk? Of(JsonObject said, HexLinkDevice direct)
    {
        if (HexLinkData.String(said, "id", 33) is not { Length: 32 } id || !Guid.TryParseExact(id, "N", out Guid parsed)
            || parsed == Guid.Empty || HexLinkData.String(said, "text", MOST_TEXT + 1) is not { } text || !Valid(text)
            || HexLinkData.Long(said, "sentAt") is not { } at || at is < 0 or > 253_402_300_799_999)
            return null;

        if (!said.ContainsKey("device"))
            return new(parsed, direct, text, DateTimeOffset.FromUnixTimeMilliseconds(at));

        JsonObject? device = HexLinkData.Object(said, "device");
        if (HexLinkData.String(device, "id", 65) is not { Length: 64 } who || !who.All(char.IsAsciiHexDigit)
            || HexLinkData.String(device, "name", HexLinkNames.MOST + 1) is not { Length: <= HexLinkNames.MOST } name
            || HexLinkNames.Clean(name) is not { } clean)
            return null;

        return new(parsed, new HexLinkDevice(who.ToUpperInvariant(), clean), text, DateTimeOffset.FromUnixTimeMilliseconds(at));
    }

    /// <summary>Whether there is text to show in a message and it fits the limit.</summary>
    public static bool Valid(string text) => text.Length <= MOST_TEXT && !string.IsNullOrWhiteSpace(text)
        && !text.Any(c => char.IsControl(c) && c is not ('\n' or '\t'));
}

internal enum HexLinkTalkResult { Sent, Invalid, NoView, TooFast, Full }

internal sealed partial class HexLink
{
    private readonly Lock talkGate = new();
    private readonly HashSet<Guid> talkSeen = [];
    private readonly TalkRate talkLocalRate = new();

    /// <summary>A message was said here or received over Link, on the thread it was heard on.</summary>
    public event Action<HexLinkTalk>? TalkHeard;

    // Each connection pays for both direct and relayed messages so a sender cannot get more room by changing the name.
    private sealed class TalkRate
    {
        private const int BURST = 12;
        private const long GAP_MS = 750;
        private long next;

        public bool Take()
        {
            long now = Environment.TickCount64;
            if (next >= now + BURST * GAP_MS)
                return false;
            next = Math.Max(now, next) + GAP_MS;
            return true;
        }
    }

    /// <summary>A message queued for the devices that let this one speak, or the reason it could not be queued.</summary>
    public HexLinkTalkResult SayTalk(string text)
    {
        if (!HexLinkTalk.Valid(text))
            return HexLinkTalkResult.Invalid;

        Joined[] to = [.. All().Where(link => link.Hello && !link.Peer.IsClosed && HexLinkAllowed.Has(link.Peer.Granted, HexLinkRights.View))];
        if (to.Length == 0)
            return HexLinkTalkResult.NoView;

        var message = new HexLinkTalk(Guid.NewGuid(), services.Device, text, DateTimeOffset.UtcNow);
        lock (talkGate)
        {
            if (talkSeen.Count >= HexLinkTalk.MOST_MESSAGES)
                return HexLinkTalkResult.Full;
            if (!talkLocalRate.Take())
                return HexLinkTalkResult.TooFast;
            talkSeen.Add(message.Id);
        }

        _ = SendTalk(to, message, relayed: false);
        TalkHeard?.Invoke(message);
        return HexLinkTalkResult.Sent;
    }

    private ValueTask HearTalk(Joined from, JsonObject said)
    {
        if (!HexLinkAllowed.Has(from.Host.Rights, HexLinkRights.View))
            return ValueTask.CompletedTask;

        lock (talkGate)
        {
            if (talkSeen.Count >= HexLinkTalk.MOST_MESSAGES || !from.TalkRate.Take())
                return ValueTask.CompletedTask;
        }

        bool relayed = said.ContainsKey("device");
        if (HexLinkTalk.Of(said, from.Peer.Device) is not { } message || message.Sender.Id == services.Device.Id
            || (relayed && (message.Sender.Id == from.Host.Device.Id || PeerOf(message.Sender.Id) is not null)))
            return ValueTask.CompletedTask;

        lock (talkGate)
        {
            if (talkSeen.Count >= HexLinkTalk.MOST_MESSAGES || !talkSeen.Add(message.Id))
                return ValueTask.CompletedTask;
        }

        TalkHeard?.Invoke(message);
        if (relayed)
            return ValueTask.CompletedTask;

        // The person who said it is left out by device id, even if their link has been replaced since it arrived.
        return new ValueTask(SendTalk(All().Where(link => link.Hello && link.Host.Device.Id != message.Sender.Id
            && HexLinkAllowed.Has(link.Host.Rights, HexLinkRights.View)), message, relayed: true));
    }

    private static Task SendTalk(IEnumerable<Joined> links, HexLinkTalk message, bool relayed) =>
        Task.WhenAll(links.Select(link => SendTalk(link.Session, message, relayed)));

    private static async Task SendTalk(HexLinkSession session, HexLinkTalk message, bool relayed)
    {
        // A link stalled on its own traffic can hold up this notification for a little while, then the next one moves on.
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await session.TellAsync(HexLinkTalk.METHOD, message.ToJson(relayed), limit.Token); }
        catch (OperationCanceledException) when (limit.IsCancellationRequested) { }
    }
}
