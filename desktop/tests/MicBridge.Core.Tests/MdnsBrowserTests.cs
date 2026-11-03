using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Makaretu.Dns;
using MicBridge.Core.Discovery;

namespace MicBridge.Core.Tests;

public sealed class MdnsBrowserTests
{
    [Fact]
    public void IdenticalRenewalAnswerPassesMakaretuFilterAndRefreshesExpiry()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var removed = new List<DiscoveredPeer>();
        using var browser = new MdnsBrowser(_ => { }, onPeerRemoved: removed.Add);
        using var multicast = (MulticastService)typeof(MdnsBrowser).GetMethod("CreateMulticastService", flags)!.Invoke(browser, null)!;
        Assert.False(multicast.IgnoreDuplicateMessages);
        var service = new DomainName("phone._micbridge._tcp.local");
        var host = new DomainName("phone.local");
        var message = new Message { QR = true };
        message.Answers.Add(new SRVRecord { Name = service, Target = host, Port = 17891 });
        message.Answers.Add(new TXTRecord { Name = service, Strings = ["protocol=1", "id=peer", "role=android"] });
        message.Answers.Add(new ARecord { Name = host, Address = IPAddress.Loopback });
        var packet = new UdpReceiveResult(message.ToByteArray(), new IPEndPoint(IPAddress.Loopback, 5353));
        multicast.OnDnsMessage(this, packet);
        var seen = (Dictionary<string, DateTimeOffset>)typeof(MdnsBrowser).GetField("lastSeen", flags)!.GetValue(browser)!;
        Assert.Contains("android:peer", seen.Keys);
        seen["android:peer"] = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(16);
        multicast.OnDnsMessage(this, packet);
        typeof(MdnsBrowser).GetMethod("ExpirePeers", flags)!.Invoke(browser, null);
        Assert.Empty(removed);
        Assert.True(seen["android:peer"] > DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2));
    }
}
