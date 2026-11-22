using System.Net;
using System.Net.Sockets;

using Hex.Core;

namespace Hex.Tests;

/// <summary>mDNS packets to bytes and back, the responder's answers, and what a browser makes of what it hears.</summary>
public sealed class HexLinkDiscoveryTests
{
    private static readonly HexLinkAnnouncement MAC = HexLinkAnnouncement.Of("Bogdan's Mac.local", "ABCD1234" + new string('0', 56), 47833, [IPAddress.Parse("192.168.0.30"), IPAddress.Parse("10.0.0.5")]);

    [Fact]
    public void Starting_discovery_reports_a_socket_failure_without_opening_a_socket()
    {
        using var discovery = new HexLinkDiscovery
        {
            SocketFactory = () => throw new SocketException((int)SocketError.AddressAlreadyInUse)
        };
        Assert.False(discovery.Browse());
        Assert.Contains("UDP port 5353", discovery.Off, StringComparison.Ordinal);
        Assert.False(discovery.Announce("Mac", MAC.Id8, MAC.Port));
    }

    [Fact]
    public void A_message_round_trips()
    {
        var message = new HexLinkDnsMessage(7, true, [new HexLinkDnsQuestion("x.local", HexLinkDns.A, Unicast: true)],
            [new HexLinkDnsRecord(HexLinkDiscovery.SERVICE, HexLinkDns.PTR, 4500) { Target = "Mac (ABCD1234)." + HexLinkDiscovery.SERVICE }],
            [
                new HexLinkDnsRecord("Mac (ABCD1234)." + HexLinkDiscovery.SERVICE, HexLinkDns.SRV, 120, Flush: true) { Port = 47833, Target = "hex-abcd1234.local" },
                new HexLinkDnsRecord("Mac (ABCD1234)." + HexLinkDiscovery.SERVICE, HexLinkDns.TXT, 4500) { Texts = ["id=ABCD1234", "v=1"] },
                new HexLinkDnsRecord("hex-abcd1234.local", HexLinkDns.A, 120) { Address = IPAddress.Parse("192.168.0.30") },
                new HexLinkDnsRecord("hex-abcd1234.local", HexLinkDns.AAAA, 120) { Address = IPAddress.Parse("fe80::1") }
            ]);

        HexLinkDnsMessage read = HexLinkDns.Decode(HexLinkDns.Encode(message))!;
        Assert.Equal(7, read.Id);
        Assert.True(read.Response);
        HexLinkDnsQuestion question = Assert.Single(read.Questions);
        Assert.Equal(("x.local", HexLinkDns.A, true), (question.Name, question.Type, question.Unicast));
        Assert.Equal("Mac (ABCD1234)." + HexLinkDiscovery.SERVICE, Assert.Single(read.Answers).Target);
        Assert.Equal(4, read.Additionals.Count);
        Assert.Equal((47833, "hex-abcd1234.local", true), (read.Additionals[0].Port, read.Additionals[0].Target, read.Additionals[0].Flush));
        Assert.Equal(["id=ABCD1234", "v=1"], read.Additionals[1].Texts);
        Assert.Equal(IPAddress.Parse("192.168.0.30"), read.Additionals[2].Address);
        Assert.Equal(IPAddress.Parse("fe80::1"), read.Additionals[3].Address);
        Assert.Equal(120u, read.Additionals[2].Ttl);
    }

    [Fact]
    public void Compressed_names_are_followed()
    {
        // A response as a system responder writes it: the PTR's target ends in a pointer back to the question's name
        byte[] packet =
        [
            0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0,
            // at 12: _hex-link._tcp.local
            9, .. "_hex-link"u8, 4, .. "_tcp"u8, 5, .. "local"u8, 0,
            0, 12, 0, 1, 0, 0, 0x11, 0x94,
            0, 7,
            4, .. "Mac1"u8, 0xC0, 12
        ];

        HexLinkDnsMessage read = HexLinkDns.Decode(packet)!;
        HexLinkDnsRecord ptr = Assert.Single(read.Answers);
        Assert.Equal(HexLinkDiscovery.SERVICE, ptr.Name);
        Assert.Equal("Mac1." + HexLinkDiscovery.SERVICE, ptr.Target);
        Assert.Equal(4500u, ptr.Ttl);
    }

    [Fact]
    public void Broken_packets_read_as_nothing()
    {
        Assert.Null(HexLinkDns.Decode([1, 2, 3]));

        // A name pointing at itself
        byte[] loop = [0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 1, 0, 1];
        Assert.Null(HexLinkDns.Decode(loop));

        // A record longer than the packet
        byte[] cut = [0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 1, 0, 200, 1, 2];
        Assert.Null(HexLinkDns.Decode(cut));
    }

    [Fact]
    public void A_query_for_the_service_is_answered_with_everything_needed_to_dial()
    {
        HexLinkDnsMessage query = HexLinkDns.Decode(HexLinkDns.Encode(new HexLinkDnsMessage(0, false, [new HexLinkDnsQuestion(HexLinkDiscovery.SERVICE, HexLinkDns.PTR)], [], [])))!;
        HexLinkDnsMessage answer = HexLinkDns.Decode(HexLinkDns.Encode(HexLinkDiscovery.AnswerTo(query, MAC)!))!;

        string instance = "Bogdan's Maclocal (ABCD1234)." + HexLinkDiscovery.SERVICE;
        HexLinkDnsRecord ptr = Assert.Single(answer.Answers);
        Assert.Equal(HexLinkDns.PTR, ptr.Type);
        Assert.Equal(instance, ptr.Target);

        HexLinkDnsRecord srv = Assert.Single(answer.Additionals, record => record.Type == HexLinkDns.SRV);
        Assert.Equal((instance, (ushort)47833, "hex-abcd1234.local"), (srv.Name, srv.Port, srv.Target));
        HexLinkDnsRecord txt = Assert.Single(answer.Additionals, record => record.Type == HexLinkDns.TXT);
        Assert.Equal(["id=ABCD1234", "v=1"], txt.Texts);
        Assert.Equal([IPAddress.Parse("192.168.0.30"), IPAddress.Parse("10.0.0.5")], answer.Additionals.Where(record => record.Type == HexLinkDns.A).Select(record => record.Address));
    }

    [Fact]
    public void Questions_of_its_other_names_are_answered_and_others_not()
    {
        string instance = MAC.Instance + "." + HexLinkDiscovery.SERVICE;
        HexLinkDnsMessage Ask(string name, ushort type) => new(0, false, [new HexLinkDnsQuestion(name, type)], [], []);

        Assert.Equal(HexLinkDns.SRV, Assert.Single(HexLinkDiscovery.AnswerTo(Ask(instance, HexLinkDns.SRV), MAC)!.Answers).Type);
        Assert.Equal(HexLinkDns.TXT, Assert.Single(HexLinkDiscovery.AnswerTo(Ask(instance.ToUpperInvariant(), HexLinkDns.TXT), MAC)!.Answers).Type);
        Assert.Equal(2, HexLinkDiscovery.AnswerTo(Ask("hex-abcd1234.local", HexLinkDns.A), MAC)!.Answers.Count);
        Assert.Equal(2, HexLinkDiscovery.AnswerTo(Ask(instance, HexLinkDns.ANY), MAC)!.Answers.Count);
        Assert.Equal(HexLinkDiscovery.SERVICE, Assert.Single(HexLinkDiscovery.AnswerTo(Ask("_services._dns-sd._udp.local", HexLinkDns.PTR), MAC)!.Answers).Target);
        Assert.Null(HexLinkDiscovery.AnswerTo(Ask("_http._tcp.local", HexLinkDns.PTR), MAC));
        Assert.Null(HexLinkDiscovery.AnswerTo(Ask("other.local", HexLinkDns.A), MAC));
    }

    [Fact]
    public void A_browser_finds_what_it_hears_and_lets_it_go_on_goodbye()
    {
        using var discovery = new HexLinkDiscovery();
        var changes = 0;
        discovery.Changed = () => changes++;
        HexLinkDnsMessage query = new(0, false, [new HexLinkDnsQuestion(HexLinkDiscovery.SERVICE, HexLinkDns.PTR)], [], []);
        HexLinkDnsMessage answer = HexLinkDiscovery.AnswerTo(query, MAC)!;

        discovery.Hear(HexLinkDns.Decode(HexLinkDns.Encode(answer))!, IPAddress.Parse("192.168.0.30"));
        HexLinkNearby found = Assert.Single(discovery.Found);
        Assert.Equal("Bogdan's Maclocal", found.Name);
        Assert.Equal("ABCD1234", found.Id8);
        Assert.Equal(47833, found.Port);
        Assert.Equal([IPAddress.Parse("192.168.0.30"), IPAddress.Parse("10.0.0.5")], found.Addresses);
        Assert.True(changes > 0);

        // The same records with no time to live, as said when a Hex stops announcing
        HexLinkDnsRecord[] goodbye = [.. answer.Answers.Concat(answer.Additionals).Select(record => record with { Ttl = 0 })];
        discovery.Hear(new HexLinkDnsMessage(0, true, [], goodbye, []), IPAddress.Parse("192.168.0.30"));
        Assert.Empty(discovery.Found);
    }
}
