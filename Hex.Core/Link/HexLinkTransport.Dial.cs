using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// Dialling a known device, and the pieces both ends share: the TLS options, the first line and reading a line a byte at
/// a time, so nothing past the line the transport wants is taken from the stream the protocol reads next.
/// </summary>
internal sealed partial class HexLinkTransport
{
    // What a device said in its first line, its id the pin the handshake proved
    private sealed record Hello(int Version, string Mode, HexLinkDevice Device);

    /// <summary>Dials whichever known device answers at an address. One this machine doesn't know is refused.</summary>
    public Task<HexLinkConnection> Dial(string host, int port, CancellationToken cancel) => Dial(host, port, null, cancel);

    /// <summary>
    /// Dials a known device at an address and links to it. When <paramref name="expectId"/> is said, the device that
    /// answers has to be that one, another (a different machine at the address now, or somebody in the middle) is
    /// closed having been told nothing. Throws <see cref="HexLinkException"/> with why when it isn't let in or gives up,
    /// <c>busy</c> when there's a link to the device already.
    /// </summary>
    public async Task<HexLinkConnection> Dial(string host, int port, string? expectId, CancellationToken cancel)
    {
        if (expectId is not null && LinkTo(expectId) is not null)
            throw new HexLinkException("busy", "there's a link to that device already");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(HANDSHAKE_MS);
        (SslStream ssl, Socket socket, string pin, string address) = await Connect(host, port, timeout.Token);
        bool kept = false;
        try
        {
            if (expectId is not null && !string.Equals(pin, expectId, StringComparison.OrdinalIgnoreCase))
            {
                Audit?.Write("refused", pin, $"{address} answered as another device than {expectId}");
                throw new HexLinkException("pin", $"{address} answered as another device than the one dialled");
            }

            if (trust.Find(pin) is null)
            {
                Audit?.Write("refused", pin, $"{address} answered as a device this machine doesn't know");
                throw new HexLinkException("unknown", $"{address} answered as a device this machine doesn't know");
            }

            await Say(ssl, HelloLine("link"), timeout.Token);
            string? line = await Answer(ssl, timeout.Token);
            Hello? hello = ParseHello(line, pin);
            if (hello is null || hello.Mode != "link")
                throw new HexLinkException("refused", $"{address} said something that isn't Link's answer");

            HexLinkConnection connection = await DiallerSettles(ssl, socket, hello.Device, address, port, timeout.Token);
            kept = true;
            return connection;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new HexLinkException("timeout", $"{address} didn't finish the handshake in time");
        }
        finally
        {
            if (!kept)
                await ssl.DisposeAsync();
        }
    }

    /// <summary>
    /// The dialler's end of the last line: a place for the device taken and raised, then OK, or <c>NO busy</c> when
    /// there's a link already or a winning one coming in.
    /// </summary>
    private async Task<HexLinkConnection> DiallerSettles(SslStream ssl, Socket socket, HexLinkDevice device, string address, int port, CancellationToken cancel)
    {
        Slot? slot = Claim(device.Id, socket, dialledByMe: true, out string refusal);
        if (slot is null)
        {
            await Say(ssl, "NO " + refusal, cancel);
            throw new HexLinkException(refusal, refusal == "busy" ? "there's a link to that device already" : "there are as many links as there may be");
        }

        try
        {
            await Say(ssl, "OK", cancel);
        }
        catch
        {
            Release(slot);
            throw;
        }

        return Up(slot, ssl, device, address, port, dialled: true);
    }

    /// <summary>A TCP connection and the TLS handshake as the client, the server's pin noted, nothing yet said.</summary>
    private async Task<(SslStream Ssl, Socket Socket, string Pin, string Address)> Connect(string host, int port, CancellationToken cancel)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        SslStream? ssl = null;
        try
        {
            try
            {
                await socket.ConnectAsync(host, port, cancel);
            }
            catch (SocketException e)
            {
                throw new HexLinkException("unreachable", $"{host}:{port} couldn't be reached, {e.Message}", e);
            }

            string address = socket.RemoteEndPoint is IPEndPoint remote ? HexLinkAddresses.Show(remote.Address) : host;
            ssl = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
            string? pin = null;
            try
            {
                await ssl.AuthenticateAsClientAsync(ClientOptions(seen => pin = seen), cancel);
            }
            catch (AuthenticationException e)
            {
                throw new HexLinkException("refused", $"{address} refused the handshake, {e.Message}", e);
            }

            if (pin is null)
                throw new HexLinkException("refused", $"{address} presented no certificate");
            return (ssl, socket, pin, address);
        }
        catch
        {
            if (ssl is not null)
                await ssl.DisposeAsync();
            else
                socket.Dispose();
            throw;
        }
    }

    // The other end's answer to a first line, a refusal it said thrown as one, and the end of the stream (a refusal on
    // TLS 1.3 shows as that or as an error on the first read) said as refused
    private static async Task<string> Answer(SslStream ssl, CancellationToken cancel)
    {
        string? line;
        try
        {
            line = await ReadLine(ssl, cancel);
        }
        catch (IOException e) when (e is not HexLinkException)
        {
            throw new HexLinkException("refused", $"the connection was closed, {e.Message}", e);
        }

        if (line is null)
            throw new HexLinkException("refused", "the connection was closed without an answer");
        if (line.StartsWith("NO", StringComparison.Ordinal))
        {
            string reason = line.Length > 3 ? line[3..] : "refused";
            throw new HexLinkException(reason, reason switch
            {
                "unknown" => "the other device doesn't know this one, pair first",
                "busy" => "there's a link between the two already",
                "full" => "the other device has as many links as it may",
                "closed" => "the other device isn't pairing now",
                "version" => "the other device speaks another version of Link",
                "mismatch" => "the pairing's numbers didn't agree",
                _ => $"refused, {reason}"
            });
        }

        return line;
    }

    private SslServerAuthenticationOptions ServerOptions(Action<string> seen) => new()
    {
        ServerCertificate = identity.Certificate,
        ClientCertificateRequired = true,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        AllowRenegotiation = false,
        AllowTlsResume = false,
        RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            // No CA and no host name, the pin is the whole question and it's asked after the handshake
            if (certificate is null)
                return false;
            seen(HexLinkIdentity.PinOf(certificate));
            return true;
        }
    };

    private SslClientAuthenticationOptions ClientOptions(Action<string> seen) => new()
    {
        TargetHost = "hex-link",
        ClientCertificates = [identity.Certificate],
        LocalCertificateSelectionCallback = (_, _, _, _, _) => identity.Certificate,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        AllowRenegotiation = false,
        AllowTlsResume = false,
        RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null)
                return false;
            seen(HexLinkIdentity.PinOf(certificate));
            return true;
        }
    };

    // This device's first line, its name and what it runs as a JSON object, escaped so it's one line whatever the name
    private string HelloLine(string mode)
    {
        HexLinkDevice me = identity.Device;
        var about = new JsonObject { ["name"] = me.Name, ["os"] = me.Os, ["arch"] = me.Arch, ["version"] = me.Version };
        return $"HEX-LINK 1 {mode} {about.ToJsonString()}";
    }

    /// <summary>
    /// A first line read, null when it isn't one. The device is the pin with what it says of itself, a bare JSON string
    /// taken as just its name. A name is cut to 64 characters and has its control characters taken out.
    /// </summary>
    private static Hello? ParseHello(string? line, string pin)
    {
        if (line is null)
            return null;
        string[] parts = line.Split(' ', 4);
        if (parts.Length != 4 || parts[0] != "HEX-LINK" || !int.TryParse(parts[1], out int version) || parts[2] is not ("link" or "pair"))
            return null;

        string name = "", os = "", arch = "", said = "";
        try
        {
            switch (JsonNode.Parse(parts[3]))
            {
                case JsonValue value when value.TryGetValue(out string? text):
                    name = text;
                    break;
                case JsonObject about:
                    name = Text(about, "name");
                    os = Text(about, "os");
                    arch = Text(about, "arch");
                    said = Text(about, "version");
                    break;
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return new Hello(version, parts[2], new HexLinkDevice(pin, Clean(name, NAME_MOST), Clean(os, 32), Clean(arch, 32), Clean(said, 64)));
    }

    private static string Text(JsonObject json, string name) => json[name] is JsonValue value && value.TryGetValue(out string? text) ? text : "";

    private static string Clean(string text, int most)
    {
        var clean = new StringBuilder(Math.Min(text.Length, most));
        foreach (char c in text)
        {
            if (clean.Length == most)
                break;
            if (!char.IsControl(c))
                clean.Append(c);
        }

        return clean.ToString().Trim();
    }

    /// <summary>
    /// A line read a byte at a time, without its line feed, null at the end of the stream before any of it. The bytes
    /// after the line stay in the stream for whoever reads next. Longer than <see cref="LINE_MOST"/> throws.
    /// </summary>
    private static async Task<string?> ReadLine(Stream stream, CancellationToken cancel)
    {
        var line = new byte[LINE_MOST];
        var one = new byte[1];
        int count = 0;
        while (true)
        {
            int read = await stream.ReadAsync(one.AsMemory(), cancel);
            if (read == 0)
                return count == 0 ? null : throw new HexLinkException("refused", "the connection was closed in the middle of a line");
            if (one[0] == (byte)'\n')
                return Encoding.UTF8.GetString(line, 0, count).TrimEnd('\r');
            if (count == LINE_MOST)
                throw new HexLinkException("refused", "a first line too long");
            line[count++] = one[0];
        }
    }

    private static async Task Say(Stream stream, string line, CancellationToken cancel)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancel);
        await stream.FlushAsync(cancel);
    }

    private static string Describe(Exception e) => e switch
    {
        OperationCanceledException => "the handshake took too long",
        AuthenticationException => $"the handshake failed, {e.Message}",
        HexLinkException link => link.Message,
        _ => e.Message
    };
}
