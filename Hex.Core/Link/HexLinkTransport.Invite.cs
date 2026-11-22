using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Hex.Core;

internal sealed partial class HexLinkTransport
{
    private InviteWindow? invitation;

    private sealed class InviteWindow(HexLinkInvite token, Func<HexLinkPairing, Task<HexLinkRights?>> ask, Func<HexLinkPairingTerms>? terms)
    {
        public HexLinkInvite Token { get; } = token;
        public Func<HexLinkPairing, Task<HexLinkRights?>> Ask { get; } = ask;
        public Func<HexLinkPairingTerms>? Terms { get; } = terms;
        public Timer? Timer { get; set; }
        public int WrongProofs { get; set; }
    }

    public HexLinkInvite? ActiveInvite
    {
        get
        {
            lock (gate)
                return invitation is { } open && DateTimeOffset.UtcNow < open.Token.Until ? open.Token : null;
        }
    }

    public bool OpenInvite(IEnumerable<IPAddress> addresses, TimeSpan lifetime, Func<HexLinkPairing, Task<HexLinkRights?>> ask,
        Func<HexLinkPairingTerms>? terms = null, HexLinkInviteRoute? publicRoute = null)
    {
        if (publicRoute is not null && !AllowPublic)
            return false;
        HexLinkInvite? token = HexLinkInvite.Create(identity.Id, Port, DateTimeOffset.UtcNow + lifetime, addresses, publicRoute);
        if (token is null)
            return false;
        ClosePairing();
        var opened = new InviteWindow(token, ask, terms);
        lock (gate)
        {
            if (listener is null)
                return false;
            invitation?.Timer?.Dispose();
            invitation = opened;
            opened.Timer = new Timer(_ => CloseInvite(opened), null, lifetime, Timeout.InfiniteTimeSpan);
        }

        Audit?.Write("invite opened");
        Changed?.Invoke();
        return true;
    }

    private void CloseInvite(InviteWindow? expected = null)
    {
        InviteWindow? closed;
        lock (gate)
        {
            closed = invitation;
            if (closed is null || expected is not null && closed != expected)
                return;
            invitation = null;
        }

        closed.Timer?.Dispose();
        Changed?.Invoke();
    }

    // Keep the connection counted against the handshake limit until it proves possession of the invitation.
    private async Task<bool> HostInvite(SslStream ssl, Socket socket, HexLinkDevice device, string address,
        CancellationToken stop, CancellationToken handshake, HexLinkBulkSupport bulk, Action proved)
    {
        InviteWindow? open;
        lock (gate)
            open = invitation;
        if (open is null || DateTimeOffset.UtcNow >= open.Token.Until)
        {
            await Say(ssl, "NO invite_expired", handshake);
            return false;
        }

        await Say(ssl, HelloLine("invite"), handshake);
        byte[]? joinerNonce = HexLinkSas.BytesAfter(await ReadLine(ssl, handshake), "nonce");
        byte[] hostNonce = RandomNumberGenerator.GetBytes(HexLinkSas.NONCE);
        await Say(ssl, "nonce " + HexLinkSas.Hex(hostNonce), handshake);
        byte[]? received = HexLinkSas.BytesAfter(await ReadLine(ssl, handshake), "proof");
        bool valid = joinerNonce is not null && received is not null &&
            CryptographicOperations.FixedTimeEquals(received, open.Token.Proof(false, device.Id, hostNonce, joinerNonce));
        bool removed = false;
        string? refusal;
        lock (gate)
        {
            if (invitation != open || DateTimeOffset.UtcNow >= open.Token.Until)
                refusal = "invite_expired";
            else
            {
                refusal = valid ? null : "invite_invalid";
                if (valid || ++open.WrongProofs >= 5)
                {
                    invitation = null;
                    removed = true;
                }
            }
        }

        if (removed)
        {
            open.Timer?.Dispose();
            Changed?.Invoke();
        }

        if (refusal is not null)
        {
            Audit?.Write("invite refused", device.Id, $"{address}, {refusal}");
            await Say(ssl, "NO " + refusal, handshake);
            return false;
        }

        await Say(ssl, "proof " + HexLinkSas.Hex(open.Token.Proof(true, device.Id, hostNonce, joinerNonce!)), handshake);
        HexLinkPairingTerms? terms = open.Terms?.Invoke();
        await Say(ssl, HexLinkInvite.TermsLine(terms), handshake);
        proved();
        return await HostPairing(ssl, socket, device, address, stop, bulk, invited: open.Ask, terms: terms);
    }

    private async Task<HexLinkPairingTerms?> AuthenticateInvite(SslStream ssl, HexLinkInvite invite, CancellationToken ended)
    {
        byte[] joinerNonce = RandomNumberGenerator.GetBytes(HexLinkSas.NONCE);
        await Say(ssl, "nonce " + HexLinkSas.Hex(joinerNonce), ended);
        byte[]? hostNonce = HexLinkSas.BytesAfter(await Answer(ssl, ended), "nonce");
        if (hostNonce is null)
            throw new HexLinkRefused("invite_invalid", "the host did not send an invite challenge");

        await Say(ssl, "proof " + HexLinkSas.Hex(invite.Proof(false, identity.Id, hostNonce, joinerNonce)), ended);
        byte[]? proof = HexLinkSas.BytesAfter(await Answer(ssl, ended), "proof");
        if (proof is null || !CryptographicOperations.FixedTimeEquals(proof, invite.Proof(true, identity.Id, hostNonce, joinerNonce)))
            throw new HexLinkRefused("invite_invalid", "the host did not authenticate the invitation");
        if (!HexLinkInvite.TryTerms(await Answer(ssl, ended), out HexLinkPairingTerms? terms))
            throw new HexLinkRefused("invite_invalid", "the host's invitation terms could not be read");
        return terms;
    }

    private async Task<(SslStream Ssl, Socket Socket, string Pin, string Address, int Port, string? SavedHost)> ConnectInvite(
        HexLinkInvite invite, HexLinkInviteRoute? overrideRoute, CancellationToken stop)
    {
        TimeSpan remaining = invite.Until - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new HexLinkRefused("invite_expired", "the invitation expired");

        using var race = CancellationTokenSource.CreateLinkedTokenSource(stop);
        race.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(HANDSHAKE_MS, remaining.TotalMilliseconds)));
        var pending = new Dictionary<Task<(SslStream Ssl, Socket Socket, string Pin, string Address)>,
            (int Port, string? SavedHost, bool IsLan)>();
        if (overrideRoute is not null)
            AddRoute(overrideRoute.Host, overrideRoute.Port, isLan: false);
        else
        {
            foreach (IPAddress address in invite.Addresses)
                AddRoute(address.ToString(), invite.Port, isLan: !HexLinkInvite.IsVpn(address));
            if (invite.PublicRoute is { } publicRoute)
            {
                if (AllowPublic)
                    AddRoute(publicRoute.Host, publicRoute.Port, isLan: false);
                else if (pending.Count == 0)
                    throw new HexLinkRefused("public_disabled", "Public connections can be enabled in Devices.");
            }
        }

        void AddRoute(string host, int port, bool isLan)
        {
            var dial = Connect(host, port, race.Token, pin => string.Equals(pin, invite.Pin, StringComparison.OrdinalIgnoreCase));
            pending.Add(dial, (port, IPAddress.TryParse(host, out _) ? null : host, isLan));
        }

        HexLinkRefused? failed = null;
        try
        {
            while (pending.Count > 0)
            {
                var ready = await Task.WhenAny(pending.Keys);
                if (ready.IsCompletedSuccessfully)
                {
                    if (!pending[ready].IsLan)
                        ready = await FindInviteLan(pending.Where(route => route.Value.IsLan).Select(route => route.Key),
                            Task.Delay(TimeSpan.FromMilliseconds(300), race.Token)) ?? ready;
                    stop.ThrowIfCancellationRequested();
                    if (DateTimeOffset.UtcNow >= invite.Until)
                        throw new HexLinkRefused("invite_expired", "the invitation expired");
                    var selected = pending[ready];
                    pending.Remove(ready);
                    var (ssl, socket, pin, address) = await ready;
                    return (ssl, socket, pin, address, selected.Port, selected.SavedHost);
                }

                pending.Remove(ready);
                try
                {
                    await ready;
                }
                catch (HexLinkRefused e)
                {
                    if (failed is null || failed.Reason == "unreachable")
                        failed = e;
                }
                catch (OperationCanceledException)
                {
                    // A dial timed out or the whole race was cancelled.
                }
            }

            stop.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= invite.Until)
                throw new HexLinkRefused("invite_expired", "the invitation expired");
            throw failed ?? new HexLinkRefused("timeout", "no invitation address answered");
        }
        finally
        {
            race.Cancel();
            foreach (var other in pending.Keys)
                _ = DisposeInviteLoser(other);
        }
    }

    // Dials authenticate TLS only. Until the winner is chosen, no endpoint sees an invitation proof.
    internal static async Task<Task<T>?> FindInviteLan<T>(IEnumerable<Task<T>> routes, Task grace)
    {
        List<Task> pending = [.. routes];
        pending.Add(grace);
        while (pending.Count > 1)
        {
            Task ready = await Task.WhenAny(pending);
            if (ready == grace)
                break;
            if (ready is Task<T> lan && lan.IsCompletedSuccessfully)
                return lan;
            pending.Remove(ready);
        }

        return null;
    }

    private static async Task DisposeInviteLoser(Task<(SslStream Ssl, Socket Socket, string Pin, string Address)> other)
    {
        try
        {
            var connection = await other;
            await connection.Ssl.DisposeAsync();
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The losing endpoint never receives a Link application message.
        }
    }

    private SslClientAuthenticationOptions ClientOptions(Action<string> seen, Func<string, bool>? accepts)
    {
        SslClientAuthenticationOptions options = ClientOptions(seen);
        if (accepts is not null)
            options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null)
                    return false;
                string pin = HexLinkIdentity.PinOf(certificate);
                seen(pin);
                return accepts(pin);
            };
        return options;
    }
}
