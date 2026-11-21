using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace Hex.Core;

/// <summary>
/// Pairing (docs/hex-link-plan.md, 3.3), the one moment two unknown keys are introduced, so the one moment a person has
/// to look. After the handshake and the <c>pair</c> lines, over the same TLS stream:
/// <code>
/// joiner: commit &lt;SHA-256 of its 32-byte nonce, hex&gt;
/// host:   nonce &lt;its own 32-byte nonce, hex&gt;
/// joiner: reveal &lt;its nonce&gt;
/// host:   ready                       or NO mismatch when the nonce isn't the one committed to
///         both show the code, six digits of SHA-256 over both pins and both nonces (HexLinkSas)
/// each:   confirm or deny             as its person answered, read concurrently with asking them
///         both confirmed: each stores the other with what it granted, and the connection carries on as a link
/// host:   link                        or NO busy
/// joiner: OK                          or NO busy
/// </code>
/// The joiner committed to its nonce before seeing the host's and the host sent its own before seeing the joiner's, so
/// neither can steer the code, and somebody in the middle, who must use keys of their own on each side, makes the two
/// sides hash different pins. A miss is one in a million and closes the window: one try a window, whatever its end.
/// </summary>
internal sealed partial class HexLinkTransport
{
    // How long a pairing may take from the handshake to the last confirm
    private const int PAIRING_MS = 120000;

    private PairingWindow? pairing;

    private sealed class PairingWindow(DateTime until, Func<HexLinkPairing, Task<HexLinkRights?>> ask)
    {
        public DateTime Until { get; } = until;
        public Func<HexLinkPairing, Task<HexLinkRights?>> Ask { get; } = ask;
        public Timer? Closer { get; set; }
    }

    /// <summary>
    /// How a pairing this machine hosted ended, paired or not, on the connection's thread. A pairing joined from here is
    /// answered by what <see cref="Pair"/> returns instead.
    /// </summary>
    public Action<HexLinkPairResult>? Paired { get; set; }

    /// <summary>Whether an unknown device may pair now.</summary>
    public bool PairingOpen => PairingUntil is not null;

    /// <summary>When the pairing window closes, null when it's closed.</summary>
    public DateTime? PairingUntil
    {
        get
        {
            lock (gate)
                return pairing is not null && DateTime.UtcNow < pairing.Until ? pairing.Until : ActiveInvite?.Until.UtcDateTime;
        }
    }

    /// <summary>
    /// Lets one unknown device pair during <paramref name="window"/> (five minutes from Link's window). The first that
    /// tries takes the window whatever comes of it. <paramref name="ask"/> is shown the other device and the code and
    /// answers what this machine grants it, or null to decline.
    /// </summary>
    public void OpenPairing(TimeSpan window, Func<HexLinkPairing, Task<HexLinkRights?>> ask)
    {
        CloseInvite();
        var opened = new PairingWindow(DateTime.UtcNow + window, ask);
        lock (gate)
        {
            pairing?.Closer?.Dispose();
            pairing = opened;
            opened.Closer = new Timer(_ => CloseIf(opened), null, window, Timeout.InfiniteTimeSpan);
        }

        Audit?.Write("pairing open", "", $"{(int)window.TotalSeconds} s");
        Changed?.Invoke();
    }

    /// <summary>Closes the pairing window. A pairing already under way goes on to its end.</summary>
    public void ClosePairing()
    {
        CloseInvite();
        PairingWindow? was;
        lock (gate)
        {
            was = pairing;
            pairing = null;
        }

        if (was is null)
            return;
        was.Closer?.Dispose();
        Changed?.Invoke();
    }

    private void CloseIf(PairingWindow window)
    {
        lock (gate)
        {
            if (pairing != window)
                return;
            pairing = null;
        }

        window.Closer?.Dispose();
        Audit?.Write("pairing closed", "", "its time ran out");
        Changed?.Invoke();
    }

    // The window's asking taken by the device that came to pair, the window closed behind it, null when there's none open
    private Func<HexLinkPairing, Task<HexLinkRights?>>? TakePairing()
    {
        PairingWindow? taken;
        lock (gate)
        {
            taken = pairing is not null && DateTime.UtcNow < pairing.Until ? pairing : null;
            pairing = null;
        }

        if (taken is null)
            return null;
        taken.Closer?.Dispose();
        Changed?.Invoke();
        return taken.Ask;
    }

    /// <summary>The host's end of a pairing, from the joiner's <c>pair</c> line on. Whether the connection was kept as a link.</summary>
    private async Task<bool> HostPairing(SslStream ssl, Socket socket, HexLinkDevice device, string address, CancellationToken stop,
        HexLinkBulkSupport bulk = HexLinkBulkSupport.None, Func<HexLinkPairing, Task<HexLinkRights?>>? invited = null)
    {
        Func<HexLinkPairing, Task<HexLinkRights?>>? ask = invited ?? TakePairing();
        if (ask is null)
        {
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(stop);
            quick.CancelAfter(HANDSHAKE_MS);
            await Say(ssl, "NO closed", quick.Token);
            Audit?.Write("refused", device.Id, $"{address}, came to pair with the pairing window closed");
            return false;
        }

        using var ended = CancellationTokenSource.CreateLinkedTokenSource(stop);
        ended.CancelAfter(PAIRING_MS);
        HexLinkPairResult result = HexLinkPairResult.Failed("failed", device);
        bool kept = false;
        string code = "";
        try
        {
            if (invited is null)
            {
                await Say(ssl, HelloLine("pair"), ended.Token);
                byte[]? commitment = HexLinkSas.BytesAfter(await ReadLine(ssl, ended.Token), "commit");
                if (commitment is null)
                {
                    await Say(ssl, "NO mismatch", ended.Token);
                    result = HexLinkPairResult.Failed("mismatch", device);
                    return false;
                }

                byte[] hostNonce = RandomNumberGenerator.GetBytes(HexLinkSas.NONCE);
                await Say(ssl, "nonce " + HexLinkSas.Hex(hostNonce), ended.Token);
                byte[]? joinerNonce = HexLinkSas.BytesAfter(await ReadLine(ssl, ended.Token), "reveal");
                if (joinerNonce is null || !HexLinkSas.Keeps(commitment, joinerNonce))
                {
                    await Say(ssl, "NO mismatch", ended.Token);
                    result = HexLinkPairResult.Failed("mismatch", device);
                    return false;
                }

                await Say(ssl, "ready", ended.Token);
                code = HexLinkSas.CodeOf(identity.Id, device.Id, hostNonce, joinerNonce);
            }

            (string verdict, HexLinkRights granted) = await Decide(ssl, ask, new HexLinkPairing(device, code, address, ended.Token, Invited: invited is not null), ended);
            if (verdict != "paired")
            {
                result = HexLinkPairResult.Failed(verdict, device, code);
                return false;
            }

            trust.Add(device, granted, address);
            result = new HexLinkPairResult(true, "paired", device, code, granted, null);
            try
            {
                kept = await ListenerSettles(ssl, socket, device, address, 0, ended.Token, yes: "link", bulk: bulk);
                if (kept)
                    result = result with { Connection = LinkTo(device.Id) };
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Paired, the link can be dialled again
            }

            return kept;
        }
        catch (OperationCanceledException)
        {
            result = HexLinkPairResult.Failed("timeout", device, code);
            return false;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            result = HexLinkPairResult.Failed(e is HexLinkRefused link ? link.Reason : "failed", device, code);
            return false;
        }
        finally
        {
            if (!result.Paired)
                ended.Cancel();
            Audit?.Write(result.Paired ? "paired" : "pairing failed", device.Id, $"{address}, \"{device.Name}\", {result.Reason}{(result.Paired ? ", granted " + string.Join(' ', HexLinkTrust.RightsNames(result.Granted)) : "")}");
            try
            {
                Paired?.Invoke(result);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Hex] saying how a pairing ended failed, {e.Message}");
            }
        }
    }

    /// <summary>
    /// Pairs with the device at an address, which has to have its pairing window open. <paramref name="ask"/> is shown
    /// the other device and the code and answers what this machine grants it, or null to decline. What's returned says
    /// how it ended; when paired, each side has stored the other and the connection carries on as a link (Connection),
    /// unless there was one up already. Only <paramref name="cancel"/> throws.
    /// </summary>
    public async Task<HexLinkPairResult> Pair(string host, int port, Func<HexLinkPairing, Task<HexLinkRights?>> ask, CancellationToken cancel, HexLinkInvite? invite = null)
    {
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        ended.CancelAfter(PAIRING_MS);
        SslStream? ssl = null;
        HexLinkDevice? device = null;
        string code = "", address = host;
        HexLinkPairResult result = HexLinkPairResult.Failed("failed");
        bool kept = false;
        try
        {
            (ssl, Socket socket, string pin, address) = invite is null
                ? await Connect(host, port, ended.Token)
                : await ConnectInvite(invite, ended.Token);
            string mode = invite is null ? "pair" : "invite";
            await Say(ssl, HelloLine(mode), ended.Token);
            Hello? hello = ParseHello(await Answer(ssl, ended.Token), pin);
            if (hello is null || hello.Mode != mode || hello.Version != 1)
                return result = HexLinkPairResult.Failed(invite is null ? "failed" : "version");
            device = hello.Device;

            if (invite is not null)
                await AuthenticateInvite(ssl, invite, ended.Token);
            else
            {
                byte[] joinerNonce = RandomNumberGenerator.GetBytes(HexLinkSas.NONCE);
                await Say(ssl, "commit " + HexLinkSas.Hex(HexLinkSas.CommitmentOf(joinerNonce)), ended.Token);
                byte[]? hostNonce = HexLinkSas.BytesAfter(await Answer(ssl, ended.Token), "nonce");
                if (hostNonce is null)
                    return result = HexLinkPairResult.Failed("mismatch", device);
                await Say(ssl, "reveal " + HexLinkSas.Hex(joinerNonce), ended.Token);
                if (await Answer(ssl, ended.Token) != "ready")
                    return result = HexLinkPairResult.Failed("mismatch", device);

                code = HexLinkSas.CodeOf(pin, identity.Id, hostNonce, joinerNonce);
            }

            (string verdict, HexLinkRights granted) = await Decide(ssl, ask, new HexLinkPairing(device, code, address, ended.Token, Invited: invite is not null), ended);
            if (verdict != "paired")
                return result = HexLinkPairResult.Failed(verdict, device, code);

            port = invite?.Port ?? port;
            trust.Add(device, granted, address, port);
            result = new HexLinkPairResult(true, "paired", device, code, granted, null);
            try
            {
                if (await Answer(ssl, ended.Token) == "link")
                {
                    HexLinkConnection connection = await DiallerSettles(ssl, socket, device, address, port, ended.Token, NegotiateBulk(hello.Bulk));
                    kept = true;
                    result = result with { Connection = connection };
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Paired, the link can be dialled again
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return result = HexLinkPairResult.Failed("timeout", device, code);
        }
        catch (HexLinkRefused e)
        {
            return result = HexLinkPairResult.Failed(e.Reason, device, code);
        }
        catch (Exception e) when (e is IOException or AuthenticationException or ObjectDisposedException or SocketException)
        {
            return result = HexLinkPairResult.Failed("failed", device, code);
        }
        finally
        {
            if (!result.Paired)
                ended.Cancel();
            if (!kept && ssl is not null)
                await ssl.DisposeAsync();
            Audit?.Write(result.Paired ? "paired" : "pairing failed", device?.Id ?? "", $"{address}, joined, {result.Reason}");
        }
    }

    /// <summary>
    /// Asks this side's person and hears the other side's answer at once, so a decline over there closes the dialog here.
    /// Says this side's answer as soon as there is one. "paired" with what this side granted when both confirmed, else
    /// declined-here or declined-there.
    /// </summary>
    private static async Task<(string Verdict, HexLinkRights Granted)> Decide(SslStream ssl, Func<HexLinkPairing, Task<HexLinkRights?>> ask, HexLinkPairing shown, CancellationTokenSource ended)
    {
        Task<string?> theirs = ReadLine(ssl, ended.Token);
        Task<HexLinkRights?> mine = AskSafely(ask, shown);
        if (await Task.WhenAny(mine, theirs) == theirs && !(theirs.IsCompletedSuccessfully && theirs.Result == "confirm"))
        {
            ended.Cancel();
            if (theirs.IsCompletedSuccessfully)
                return ("declined-there", HexLinkRights.None);
            await theirs;
        }

        HexLinkRights? granted = await mine.WaitAsync(ended.Token);
        await Say(ssl, granted is null ? "deny" : "confirm", ended.Token);
        if (granted is null)
            return ("declined-here", HexLinkRights.None);

        return await theirs == "confirm" ? ("paired", granted.Value) : ("declined-there", HexLinkRights.None);
    }

    // The person's answer, an asking that threw taken as a decline
    private static async Task<HexLinkRights?> AskSafely(Func<HexLinkPairing, Task<HexLinkRights?>> ask, HexLinkPairing shown)
    {
        try
        {
            return await ask(shown);
        }
        catch (Exception e)
        {
            if (e is not OperationCanceledException)
                Console.WriteLine($"[Hex] asking about a pairing failed, {e.Message}");
            return null;
        }
    }
}
