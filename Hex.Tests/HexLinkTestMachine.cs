using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// A Hex's Link for a test: an identity (its key kept by the file alone, never the real Keychain), its known devices and
/// a transport, in a temp folder of its own that goes when it does. What its transport took in waits in <see cref="Accepted"/>.
/// </summary>
internal sealed class HexLinkTestMachine : IDisposable
{
    public static readonly TimeSpan WAIT = TimeSpan.FromSeconds(20);

    public string Folder { get; }
    public HexLinkIdentity Identity { get; }
    public HexLinkTrust Trust { get; }
    public HexLinkTransport Transport { get; }
    public TaskCompletionSource<HexLinkConnection> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public HexLinkDevice Device => Identity.Device;

    public HexLinkTestMachine(string name)
    {
        Folder = Path.Combine(Path.GetTempPath(), "hex-link-test-" + Guid.NewGuid().ToString("N"));
        Identity = HexLinkIdentity.LoadOrCreate(Folder, name, fileOnly: true);
        Trust = new HexLinkTrust(Folder);
        Transport = new HexLinkTransport(Identity, Trust);
        Transport.Accepted = connection =>
        {
            if (!Accepted.TrySetResult(connection))
                connection.Dispose();
        };
    }

    /// <summary>Listens on a free loopback-reachable port and says which.</summary>
    public int Listen()
    {
        Transport.Start(0);
        return Transport.Port;
    }

    /// <summary>Each knows the other, with what each grants.</summary>
    public static void Know(HexLinkTestMachine one, HexLinkRights oneGrants, HexLinkTestMachine other, HexLinkRights otherGrants)
    {
        one.Trust.Add(other.Device, oneGrants);
        other.Trust.Add(one.Device, otherGrants);
    }

    /// <summary>
    /// A TLS client by hand, with this machine's certificate or none, for saying what the transport wouldn't. The
    /// handshake may fail on TLS 1.2 when the server refuses the certificate, which is returned as null.
    /// </summary>
    public static async Task<SslStream?> RawClient(int port, X509Certificate2? certificate)
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var ssl = new SslStream(client.GetStream(), false);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "hex-link",
                ClientCertificates = certificate is null ? null : [certificate],
                LocalCertificateSelectionCallback = certificate is null ? null : (_, _, _, _, _) => certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            });
            return ssl;
        }
        catch (Exception e) when (e is AuthenticationException or IOException)
        {
            ssl.Dispose();
            return null;
        }
    }

    /// <summary>Everything the other end says until it closes, or what it said before reading failed.</summary>
    public static async Task<string> ReadToEnd(Stream stream)
    {
        var said = new MemoryStream();
        var buffer = new byte[1024];
        using var timeout = new CancellationTokenSource(WAIT);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                said.Write(buffer, 0, read);
        }
        catch (Exception e) when (e is IOException or AuthenticationException or ObjectDisposedException)
        {
        }

        return Encoding.UTF8.GetString(said.ToArray());
    }

    public static async Task WriteLine(Stream stream, string line)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        await stream.FlushAsync();
    }

    public static async Task<string?> ReadLine(Stream stream)
    {
        var line = new List<byte>();
        var one = new byte[1];
        using var timeout = new CancellationTokenSource(WAIT);
        while (await stream.ReadAsync(one, timeout.Token) == 1)
        {
            if (one[0] == '\n')
                return Encoding.UTF8.GetString([.. line]);
            line.Add(one[0]);
        }

        return line.Count == 0 ? null : Encoding.UTF8.GetString([.. line]);
    }

    public void Dispose()
    {
        Transport.Dispose();
        if (Accepted.Task.IsCompletedSuccessfully)
            Accepted.Task.Result.Dispose();
        Identity.Dispose();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
