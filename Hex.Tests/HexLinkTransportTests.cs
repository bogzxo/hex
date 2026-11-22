using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit.Abstractions;

using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// Link's door over loopback: two known devices link and talk, and everything that isn't let in isn't, an unknown key,
/// no certificate, the wrong device at an address, a forgotten one, a second link, an address that isn't private.
/// </summary>
public sealed class HexLinkTransportTests(ITestOutputHelper output) : IDisposable
{
    private readonly HexLinkTestMachine pc = new("PC");
    private readonly HexLinkTestMachine mac = new("Mac");

    public void Dispose()
    {
        pc.Dispose();
        mac.Dispose();
    }

    private async Task<(HexLinkConnection Dialled, HexLinkConnection Accepted)> Link()
    {
        HexLinkTestMachine.Know(pc, HexLinkRights.View, mac, HexLinkRights.View | HexLinkRights.Steer);
        int port = mac.Listen();
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkConnection dialled = await pc.Transport.Dial("127.0.0.1", port, mac.Device.Id, cancel.Token);
        HexLinkConnection accepted = await mac.Accepted.Task.WaitAsync(HexLinkTestMachine.WAIT);
        return (dialled, accepted);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hello_uses_the_common_capability_and_preserves_json_with_an_old_peer(bool raw)
    {
        HexLinkTestMachine.Know(pc, HexLinkRights.View, mac, HexLinkRights.View);
        using var older = new HexLinkTransport(pc.Identity, pc.Trust)
        {
            BulkSupport = raw ? HexLinkBulkSupport.Raw : HexLinkBulkSupport.None
        };
        int port = mac.Listen();
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        using var dialled = await older.Dial("127.0.0.1", port, mac.Device.Id, cancel.Token);
        using var accepted = await mac.Accepted.Task.WaitAsync(cancel.Token);
        Assert.Equal(older.BulkSupport, dialled.Bulk);
        Assert.Equal(older.BulkSupport, accepted.Bulk);

        using var a = new HexLinkSession(dialled).Start();
        using var b = new HexLinkSession(accepted);
        b.Requested = (_, parameters, _) => Task.FromResult(parameters["data"]?.DeepClone());
        b.Start();

        byte[] payload = [0, 10, 127, 255];
        JsonNode? echoed = await a.Ask("test/json", new JsonObject { ["data"] = Convert.ToBase64String(payload) }, cancel.Token);
        Assert.Equal(payload, Convert.FromBase64String(echoed!.GetValue<string>()));
        Assert.Equal(raw, a.SupportsBulk);
        if (!raw)
        {
            HexLinkException unsupported = await Assert.ThrowsAsync<HexLinkException>(() => a.OpenBulkAsync("test/raw", 1, cancel: cancel.Token));
            Assert.Equal(HexLinkErrors.METHOD_NOT_FOUND, unsupported.Code);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SAS_pairing_negotiates_bulk_on_the_first_link_if_both_peers_support_it(bool modern)
    {
        using var joining = new HexLinkTransport(pc.Identity, pc.Trust)
        {
            BulkSupport = modern ? HexLinkBulkSupport.Zstandard : HexLinkBulkSupport.None
        };
        int port = mac.Listen();
        mac.Transport.OpenPairing(TimeSpan.FromMinutes(1), _ => Task.FromResult<HexLinkRights?>(HexLinkRights.View));
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkPairResult paired = await joining.Pair("127.0.0.1", port, _ => Task.FromResult<HexLinkRights?>(HexLinkRights.View), cancel.Token);
        Assert.True(paired.Paired, paired.Reason);
        Assert.NotNull(paired.Connection);
        using HexLinkConnection accepted = await mac.Accepted.Task.WaitAsync(cancel.Token);
        Assert.Equal(joining.BulkSupport, accepted.Bulk);
        Assert.Equal(accepted.Bulk, paired.Connection.Bulk);
    }

    // Opt in with HEX_LINK_WIRE_BENCH=1 and a detailed console logger. Repeats real source bytes mixed with random
    // bytes to isolate transfer over pinned loopback TLS from filesystem operations and RPC admission delays.
    [Fact]
    public async Task Benchmark_loopback_TLS_wire_throughput()
    {
        if (Environment.GetEnvironmentVariable("HEX_LINK_WIRE_BENCH") != "1")
            return;

        string root = Environment.GetEnvironmentVariable("HEX_LINK_WIRE_BENCH_ROOT") ?? SourceRoot();
        byte[] source = ReadSources(root);
        byte[] random = new byte[source.Length];
        new Random(42).NextBytes(random);

        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var (client, server) = await Link();
        using var dialled = client;
        using var accepted = server;
        Assert.Equal(HexLinkBulkSupport.Zstandard, dialled.Bulk);
        Assert.Equal(HexLinkBulkSupport.Zstandard, accepted.Bulk);

        (int Count, int Size, bool Mixed)[] workloads =
        [
            (4096, HexLinkFrameHeader.MaximumPayload, true),
            (30_000, 2048, true),
            (512, HexLinkFrameHeader.MaximumPayload, false)
        ];
        var baseline = new (TimeSpan Duration, long WireBytes)[workloads.Length];
        for (int i = 0; i < workloads.Length; i++)
        {
            var (count, size, mixed) = workloads[i];
            baseline[i] = await LegacyRound(dialled.Stream, accepted.Stream, count, size, source, mixed ? random : source, cancel.Token);
        }

        long transmitted = 0;
        var counted = new HexLinkProtocolTests.ObservedStream(dialled.Stream, (bytes, _) =>
        {
            transmitted += bytes.Length;
            return ValueTask.CompletedTask;
        });
        using var sender = new HexLinkSession(new HexLinkConnection(counted, dialled.Device, dialled.Address,
            dialled.Dialled, () => dialled.Rights, bulk: dialled.Bulk)).Start();
        using var receiver = new HexLinkSession(accepted);
        receiver.RegisterBulkReceiver("test/benchmark", HexLinkRights.View, (length, _, _) => ValueTask.FromResult<IHexLinkBulkSink>(new CountingSink(length)));
        receiver.Start();

        for (int i = 0; i < workloads.Length; i++)
        {
            var (count, size, mixed) = workloads[i];
            long length = (long)count * size;
            long before = transmitted;
            Stopwatch elapsed = Stopwatch.StartNew();
            await using (HexLinkSession.BulkUpload upload = await sender.OpenBulkAsync("test/benchmark", length, cancel: cancel.Token))
            {
                byte[] batch = new byte[HexLinkFrameHeader.MaximumPayload];
                int used = 0;
                for (int part = 0; part < count; part++)
                {
                    Payload(source, mixed ? random : source, part, size).CopyTo(batch.AsMemory(used));
                    used += size;
                    if (used == batch.Length)
                    {
                        await upload.WriteAsync(batch, cancel.Token);
                        used = 0;
                    }
                }
                if (used > 0)
                    await upload.WriteAsync(batch.AsMemory(0, used), cancel.Token);
                await upload.CompleteAsync(cancel.Token);
            }
            elapsed.Stop();

            double megabytes = length / 1_000_000d;
            output.WriteLine($"{(mixed ? "Mixed" : "Source only")}, {count:N0} × {size:N0} bytes ({megabytes:N2} MB), " +
                $"JSON/base64 {megabytes / baseline[i].Duration.TotalSeconds:N1} MB/s, {baseline[i].Duration.TotalSeconds:N2} s, {baseline[i].WireBytes / (double)length:P1} wire/input; " +
                $"bulk {megabytes / elapsed.Elapsed.TotalSeconds:N1} MB/s, {elapsed.Elapsed.TotalSeconds:N2} s, {(transmitted - before) / (double)length:P1} wire/input");
        }
    }

    private static async Task<(TimeSpan Duration, long WireBytes)> LegacyRound(Stream from, Stream to,
        int count, int size, byte[] source, byte[] random, CancellationToken cancel)
    {
        using var reader = new StreamReader(to, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
        Stopwatch elapsed = Stopwatch.StartNew();
        Task<long> sending = Task.Run(() =>
        {
            long wireBytes = 0;
            var buffer = new ArrayBufferWriter<byte>();
            using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            for (int i = 0; i < count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                buffer.ResetWrittenCount();
                writer.Reset(buffer);
                var message = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "git/chunk",
                    ["params"] = new JsonObject { ["data"] = Convert.ToBase64String(Payload(source, random, i, size).Span) }
                };
                message.WriteTo(writer);
                writer.Flush();
                buffer.GetSpan(1)[0] = (byte)'\n';
                buffer.Advance(1);
                from.Write(buffer.WrittenSpan);
                from.Flush();
                wireBytes += buffer.WrittenCount;
            }
            return wireBytes;
        }, cancel);

        for (int i = 0; i < count; i++)
        {
            JsonNode? message = JsonNode.Parse((await reader.ReadLineAsync(cancel))!);
            byte[] decoded = Convert.FromBase64String(message!["params"]!["data"]!.GetValue<string>());
            Assert.True(decoded.AsSpan().SequenceEqual(Payload(source, random, i, size).Span));
        }

        return (elapsed.Elapsed, await sending.WaitAsync(cancel));
    }

    private static ReadOnlyMemory<byte> Payload(byte[] source, byte[] random, int index, int bytes)
    {
        int offset = (index / 2 % (source.Length / bytes)) * bytes;
        return ((index & 1) == 0 ? source : random).AsMemory(offset, bytes);
    }

    private static string SourceRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (Directory.Exists(Path.Combine(folder.FullName, "Hex.Core", "Link")))
                return folder.FullName;
        }
        throw new DirectoryNotFoundException("Set HEX_LINK_WIRE_BENCH_ROOT to a Hex source checkout.");
    }

    private static byte[] ReadSources(string root)
    {
        byte[] source = new byte[8 * 1024 * 1024];
        int written = 0;
        foreach (string folder in new[] { "Hex.Core/Link", "Hex.Tests", "Hex.Dawn/Testing", "docs" })
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(root, folder)).Order(StringComparer.Ordinal))
            {
                if (Path.GetExtension(path) is not (".cs" or ".md"))
                    continue;
                using FileStream file = File.OpenRead(path);
                written += file.ReadAtLeast(source.AsSpan(written), source.Length - written, throwOnEndOfStream: false);
                if (written == source.Length)
                    return source;
            }
        }
        if (written < HexLinkFrameHeader.MaximumPayload)
            throw new IOException("The Link benchmark needs at least 256 KiB of Hex source files.");
        return source[..written];
    }

    private sealed class CountingSink(long length) : IHexLinkBulkSink
    {
        private long received;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancel)
        {
            received += bytes.Length;
            return ValueTask.CompletedTask;
        }

        public ValueTask CommitAsync(CancellationToken cancel)
        {
            Assert.Equal(length, received);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Two_known_devices_link_and_talk_both_ways()
    {
        (HexLinkConnection dialled, HexLinkConnection accepted) = await Link();

        Assert.Equal(IPAddress.Loopback, mac.Transport.ListeningAddress);
        Assert.Equal(mac.Device.Id, dialled.Device.Id);
        Assert.Equal("Mac", dialled.Device.Name);
        Assert.True(dialled.Dialled);
        Assert.Equal(pc.Device.Id, accepted.Device.Id);
        Assert.Equal("PC", accepted.Device.Name);
        Assert.False(accepted.Dialled);

        // Each side's rights are what it granted the other
        Assert.Equal(HexLinkRights.View, dialled.Rights);
        Assert.Equal(HexLinkRights.View | HexLinkRights.Steer, accepted.Rights);

        await HexLinkTestMachine.WriteLine(dialled.Stream, "hello mac");
        Assert.Equal("hello mac", await HexLinkTestMachine.ReadLine(accepted.Stream));
        await HexLinkTestMachine.WriteLine(accepted.Stream, "hello pc");
        Assert.Equal("hello pc", await HexLinkTestMachine.ReadLine(dialled.Stream));

        Assert.Single(pc.Transport.Links);
        Assert.Single(mac.Transport.Links);
        Assert.Equal("127.0.0.1", mac.Trust.Find(pc.Device.Id)!.Address);
    }

    [Fact]
    public async Task A_known_incoming_link_uses_the_selected_address()
    {
        HexLinkTestMachine.Know(pc, HexLinkRights.View, mac, HexLinkRights.View);
        int port = mac.Listen();
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);

        Assert.True(mac.Trust.SetRoute(pc.Device.Id, new HexLinkEndpoint("::1", port)));
        HexLinkRefused refused = await Assert.ThrowsAsync<HexLinkRefused>(() => pc.Transport.Dial("127.0.0.1", port, mac.Device.Id, cancel.Token));
        Assert.Equal("route", refused.Reason);
        Assert.False(mac.Accepted.Task.IsCompleted);

        Assert.True(mac.Trust.SetRoute(pc.Device.Id, new HexLinkEndpoint("127.0.0.1", port)));
        using HexLinkConnection accepted = await pc.Transport.Dial("127.0.0.1", port, mac.Device.Id, cancel.Token);
        Assert.Equal(mac.Device.Id, accepted.Device.Id);
        Assert.Equal(HexLinkRights.View, accepted.Rights);
    }

    [Fact]
    public async Task An_unknown_device_is_refused_having_been_shown_nothing()
    {
        // The PC knows the Mac, the Mac doesn't know the PC
        pc.Trust.Add(mac.Device, HexLinkRights.View);
        int port = mac.Listen();

        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkRefused refused = await Assert.ThrowsAsync<HexLinkRefused>(() => pc.Transport.Dial("127.0.0.1", port, mac.Device.Id, cancel.Token));
        Assert.Equal("unknown", refused.Reason);

        // By hand: everything the Mac says to an unknown device is the refusal
        using var ssl = await HexLinkTestMachine.RawClient(port, pc.Identity.Certificate);
        Assert.NotNull(ssl);
        await HexLinkTestMachine.WriteLine(ssl, "HEX-LINK 1 link \"PC\"");
        Assert.Equal("NO unknown\n", await HexLinkTestMachine.ReadToEnd(ssl));
        Assert.False(mac.Accepted.Task.IsCompleted);
        Assert.Empty(mac.Transport.Links);
    }

    [Fact]
    public async Task A_client_without_a_certificate_is_refused()
    {
        int port = mac.Listen();
        using var ssl = await HexLinkTestMachine.RawClient(port, null);
        if (ssl is not null)
        {
            // TLS 1.3, the client finds out on its first read
            try
            {
                await HexLinkTestMachine.WriteLine(ssl, "HEX-LINK 1 link \"nobody\"");
            }
            catch (IOException)
            {
            }

            Assert.Equal("", await HexLinkTestMachine.ReadToEnd(ssl));
        }

        Assert.False(mac.Accepted.Task.IsCompleted);
    }

    [Fact]
    public async Task Dialling_one_device_and_finding_another_is_refused()
    {
        using var other = new HexLinkTestMachine("Other");
        HexLinkTestMachine.Know(pc, HexLinkRights.View, mac, HexLinkRights.View);
        pc.Trust.Add(other.Device, HexLinkRights.View);
        int port = mac.Listen();

        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkRefused refused = await Assert.ThrowsAsync<HexLinkRefused>(() => pc.Transport.Dial("127.0.0.1", port, other.Device.Id, cancel.Token));
        Assert.Equal("pin", refused.Reason);
        Assert.False(mac.Accepted.Task.IsCompleted);
        Assert.Empty(pc.Transport.Links);
    }

    [Fact]
    public async Task A_device_that_answers_unknown_to_the_dialler_is_refused()
    {
        // The Mac knows the PC but the PC never paired with the Mac
        mac.Trust.Add(pc.Device, HexLinkRights.View);
        int port = mac.Listen();
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkRefused refused = await Assert.ThrowsAsync<HexLinkRefused>(() => pc.Transport.Dial("127.0.0.1", port, cancel.Token));
        Assert.Equal("unknown", refused.Reason);
        Assert.False(mac.Accepted.Task.IsCompleted);
    }

    [Fact]
    public async Task A_forgotten_device_is_cut_at_once_and_its_redial_refused()
    {
        (HexLinkConnection dialled, HexLinkConnection accepted) = await Link();

        Assert.True(mac.Trust.Forget(pc.Device.Id));

        // The PC's end sees its link close, and the Mac's end has no rights left
        Assert.Equal("", await HexLinkTestMachine.ReadToEnd(dialled.Stream));
        Assert.Equal(HexLinkRights.None, accepted.Rights);
        Assert.Empty(mac.Transport.Links);

        dialled.Dispose();
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkRefused refused = await Assert.ThrowsAsync<HexLinkRefused>(() => pc.Transport.Dial("127.0.0.1", mac.Transport.Port, mac.Device.Id, cancel.Token));
        Assert.Equal("unknown", refused.Reason);
    }

    [Fact]
    public async Task Rights_are_read_live()
    {
        (_, HexLinkConnection accepted) = await Link();
        Assert.Equal(HexLinkRights.View | HexLinkRights.Steer, accepted.Rights);

        mac.Trust.SetRights(pc.Device.Id, HexLinkRights.Edit);
        Assert.Equal(HexLinkRights.Edit, accepted.Rights);
    }

    [Fact]
    public async Task A_second_link_to_a_linked_device_is_refused_busy()
    {
        (HexLinkConnection dialled, _) = await Link();

        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkRefused busy = await Assert.ThrowsAsync<HexLinkRefused>(() => pc.Transport.Dial("127.0.0.1", mac.Transport.Port, mac.Device.Id, cancel.Token));
        Assert.Equal("busy", busy.Reason);

        // Another transport of the same device's, which doesn't know it has a link already, is told so by the Mac
        using var again = new HexLinkTransport(pc.Identity, pc.Trust);
        busy = await Assert.ThrowsAsync<HexLinkRefused>(() => again.Dial("127.0.0.1", mac.Transport.Port, mac.Device.Id, cancel.Token));
        Assert.Equal("busy", busy.Reason);

        // Once the first is gone a new one is let in
        dialled.Dispose();
        HexLinkConnection replaced = await RetryDial(again, mac.Transport.Port, mac.Device.Id);
        Assert.Equal(mac.Device.Id, replaced.Device.Id);
        replaced.Dispose();
    }

    // The Mac notices a closed link when it next looks, so a redial straight after the close may still be busy for a moment
    private static async Task<HexLinkConnection> RetryDial(HexLinkTransport transport, int port, string id)
    {
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        while (true)
        {
            try
            {
                return await transport.Dial("127.0.0.1", port, id, cancel.Token);
            }
            catch (HexLinkRefused e) when (e.Reason == "busy" && !cancel.IsCancellationRequested)
            {
                await Task.Delay(50);
            }
        }
    }

    [Fact]
    public async Task Two_devices_dialling_each_other_at_once_end_with_one_link()
    {
        HexLinkTestMachine.Know(pc, HexLinkRights.View, mac, HexLinkRights.View);
        int pcPort = pc.Listen(), macPort = mac.Listen();
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);

        Task<HexLinkConnection> one = pc.Transport.Dial("127.0.0.1", macPort, mac.Device.Id, cancel.Token);
        Task<HexLinkConnection> two = mac.Transport.Dial("127.0.0.1", pcPort, pc.Device.Id, cancel.Token);
        try
        {
            await Task.WhenAll(one, two);
        }
        catch (HexLinkRefused)
        {
        }

        // At least one got through, and neither side holds two
        Assert.True(one.IsCompletedSuccessfully || two.IsCompletedSuccessfully);
        Assert.True(pc.Transport.Links.Count <= 1);
        Assert.True(mac.Transport.Links.Count <= 1);
    }

    [Fact]
    public async Task An_address_that_isnt_private_is_dropped_before_tls()
    {
        HexLinkTestMachine.Know(pc, HexLinkRights.View, mac, HexLinkRights.View);
        var asked = new List<IPAddress>();
        mac.Transport.Admits = address =>
        {
            lock (asked)
                asked.Add(address);
            return false;
        };
        int port = mac.Listen();

        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        await Assert.ThrowsAnyAsync<IOException>(() => pc.Transport.Dial("127.0.0.1", port, mac.Device.Id, cancel.Token));
        Assert.False(mac.Accepted.Task.IsCompleted);
        lock (asked)
            Assert.True(IPAddress.IsLoopback(Assert.Single(asked).IsIPv4MappedToIPv6 ? asked[0].MapToIPv4() : asked[0]));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("192.168.0.30", true)]
    [InlineData("192.169.0.1", false)]
    [InlineData("169.254.10.10", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.81.32.118", true)]
    [InlineData("100.80.122.37", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("100.63.255.255", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("febf::1", true)]
    [InlineData("fec0::1", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("::ffff:100.80.122.37", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Private_addresses_are_the_ones_said(string address, bool isPrivate)
    {
        Assert.Equal(isPrivate, HexLinkAddresses.IsPrivate(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Nothing_listens_before_start_and_stop_closes_it()
    {
        Assert.False(mac.Transport.Listening);
        Assert.Equal(0, mac.Transport.Port);
        int port = mac.Listen();
        Assert.True(mac.Transport.Listening);
        Assert.Equal(IPAddress.Loopback, mac.Transport.ListeningAddress);
        Assert.NotEqual(0, port);
        mac.Transport.Stop();
        Assert.False(mac.Transport.Listening);

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var client = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetwork);
        // A closed loopback port can refuse the attempt or leave the bounded probe unanswered.
        Exception? error = await Record.ExceptionAsync(() =>
            client.ConnectAsync(IPAddress.Loopback, port, cancel.Token).AsTask());
        Assert.True(error is System.Net.Sockets.SocketException ||
            (error is OperationCanceledException && cancel.IsCancellationRequested),
            error?.ToString() ?? "A fresh loopback client connected after Stop.");
    }

    [Fact]
    public async Task Stop_closes_every_link()
    {
        (HexLinkConnection dialled, _) = await Link();
        mac.Transport.Stop();
        Assert.Equal("", await HexLinkTestMachine.ReadToEnd(dialled.Stream));
        Assert.Empty(mac.Transport.Links);
    }

    [Fact]
    public async Task The_first_lines_take_nothing_of_what_follows()
    {
        // Bytes sent by the dialler straight after its OK reach the protocol whole
        HexLinkTestMachine.Know(pc, HexLinkRights.View, mac, HexLinkRights.View);
        int port = mac.Listen();
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkConnection dialled = await pc.Transport.Dial("127.0.0.1", port, mac.Device.Id, cancel.Token);
        await dialled.Stream.WriteAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}\n"));
        await dialled.Stream.FlushAsync();
        HexLinkConnection accepted = await mac.Accepted.Task.WaitAsync(HexLinkTestMachine.WAIT);
        Assert.Equal("{\"jsonrpc\":\"2.0\"}", await HexLinkTestMachine.ReadLine(accepted.Stream));
        dialled.Dispose();
    }
}
