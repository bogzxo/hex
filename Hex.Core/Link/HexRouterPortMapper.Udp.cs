using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Hex.Core;

public sealed partial class HexRouterPortMapper
{
    private sealed class PcpMapping(RouterRoute route, int localPort, TimeSpan timeout) : IMapping
    {
        private readonly byte[] nonce = RandomNumberGenerator.GetBytes(12);
        private IPEndPoint? assigned;
        private bool mayExist;

        public async Task<Grant?> MapAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sent = DateTimeOffset.UtcNow;
            byte[]? response = await Exchange(Lifetime).ConfigureAwait(false);
            if (response is { Length: >= 2 } && (response[0] != 2 || response[3] != 0))
                return null;

            mayExist = true;
            if (response is not null)
            {
                var address = new IPAddress(response.AsSpan(44, 16));
                if (address.IsIPv4MappedToIPv6)
                    address = address.MapToIPv4();
                assigned = new(address, ReadPort(response, 42));
                if (PublicGrant(address, assigned.Port, ReadSeconds(response, 4), sent) is { } grant)
                    return grant;
            }

            await DeleteAsync().ConfigureAwait(false);
            return null;
        }

        public async Task DeleteAsync()
        {
            if (!mayExist)
                return;
            mayExist = false;
            await Exchange(0).ConfigureAwait(false);
        }

        private Task<byte[]?> Exchange(uint lifetime)
        {
            byte[] request = new byte[60];
            request[0] = 2;
            request[1] = 1; // MAP
            WriteSeconds(request, 4, lifetime);
            route.Local.MapToIPv6().GetAddressBytes().CopyTo(request, 8);
            nonce.CopyTo(request, 24);
            request[36] = 6; // TCP
            WritePort(request, 40, localPort);
            WritePort(request, 42, assigned?.Port ?? localPort);
            assigned?.Address.MapToIPv6().GetAddressBytes().CopyTo(request, 44);

            return UdpExchange(route, request, timeout, response =>
                // An older gateway may explicitly reject PCP as an unknown NAT-PMP version.
                (response.Length == 8 && response[0] == 0 && response[1] == 129 && ReadPort(response, 2) == 1)
                || (response.Length >= 24 && response[0] == 2 && response[1] == 129
                    && (response[3] != 0 || (response.Length >= 60 && response[36] == 6
                        && ReadPort(response, 40) == localPort && response.AsSpan(24, 12).SequenceEqual(nonce)
                        && (ReadSeconds(response, 4) == 0) == (lifetime == 0)))));
        }
    }

    private sealed class NatPmpMapping(RouterRoute route, int localPort, TimeSpan timeout) : IMapping
    {
        private int assigned;
        private bool mayExist;

        public async Task<Grant?> MapAsync(CancellationToken cancellationToken)
        {
            byte[]? addressResponse = await UdpExchange(route, [0, 0], timeout,
                response => response.Length == 12 && response[0] == 0 && response[1] == 128).ConfigureAwait(false);
            if (addressResponse is null || ReadPort(addressResponse, 2) != 0)
                return null;
            var address = new IPAddress(addressResponse.AsSpan(8, 4));
            if (!IsPublicIpv4(address))
                return null;

            cancellationToken.ThrowIfCancellationRequested();
            var sent = DateTimeOffset.UtcNow;
            byte[]? response = await Exchange(Lifetime).ConfigureAwait(false);
            if (response is not null && ReadPort(response, 2) != 0)
                return null;

            mayExist = true;
            if (response is not null)
            {
                assigned = ReadPort(response, 10);
                if (PublicGrant(address, assigned, ReadSeconds(response, 12), sent) is { } grant)
                    return grant;
            }

            await DeleteAsync().ConfigureAwait(false);
            return null;
        }

        public async Task DeleteAsync()
        {
            if (!mayExist)
                return;
            mayExist = false;
            await Exchange(0).ConfigureAwait(false);
        }

        private Task<byte[]?> Exchange(uint lifetime)
        {
            byte[] request = new byte[12];
            request[1] = 2; // Map TCP
            WritePort(request, 4, localPort);
            WritePort(request, 6, lifetime == 0 ? 0 : assigned == 0 ? localPort : assigned);
            WriteSeconds(request, 8, lifetime);
            return UdpExchange(route, request, timeout, response =>
                response.Length == 16 && response[0] == 0 && response[1] == 130 && ReadPort(response, 8) == localPort
                && (ReadPort(response, 2) != 0 || (ReadSeconds(response, 12) == 0) == (lifetime == 0)));
        }
    }

    private static async Task<byte[]?> UdpExchange(RouterRoute route, byte[] request, TimeSpan timeout, Func<byte[], bool> accepts)
    {
        try
        {
            using var socket = new UdpClient(new IPEndPoint(route.Local, 0));
            // A connected UDP socket only receives packets from the chosen gateway and its protocol port.
            socket.Connect(route.Control);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var wait = new CancellationTokenSource(timeout / 2);
                try
                {
                    await socket.SendAsync(request, wait.Token).ConfigureAwait(false);
                    while (!wait.IsCancellationRequested)
                    {
                        var packet = await socket.ReceiveAsync(wait.Token).ConfigureAwait(false);
                        if (accepts(packet.Buffer))
                            return packet.Buffer;
                    }
                }
                catch (OperationCanceledException) when (wait.IsCancellationRequested)
                {
                }
            }
        }
        catch (SocketException)
        {
        }
        return null;
    }

    private static int ReadPort(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));
    private static uint ReadSeconds(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
    private static void WritePort(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), (ushort)value);
    private static void WriteSeconds(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
}
