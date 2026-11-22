using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Hex.Core;

/// <summary>A host and port deliberately specified for an invitation or its join route.</summary>
internal sealed record HexLinkInviteRoute
{
    internal const int MaxHostLength = 253;

    private HexLinkInviteRoute(string host, int port)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; }
    public int Port { get; }

    public static bool TryCreate(string host, int port, [NotNullWhen(true)] out HexLinkInviteRoute? route)
    {
        route = null;
        if (port is <= 0 or > 65535 || host.Length is 0 or > MaxHostLength)
            return false;

        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            if (host.Contains('%') || host.Contains('['))
                return false;
            host = address.ToString();
        }
        else
        {
            string[] labels = host.TrimEnd('.').Split('.');
            if (host.EndsWith("..", StringComparison.Ordinal) || labels.Any(label =>
                label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-') ||
                label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
                return false;
        }

        route = new HexLinkInviteRoute(host, port);
        return true;
    }

    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}
