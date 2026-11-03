using System.Net;
using System.Reflection;
using Makaretu.Dns;
using MicBridge.Core.Discovery;

namespace MicBridge.Core.Tests;

public sealed class MdnsIdentityTests
{
    [Fact]
    public void SameServiceChangingIdRemovesOldPeerBeforePublishingNewPeer()
    {
        var events = new List<string>();
        using var browser = new MdnsBrowser(peer => events.Add("add:" + peer.DeviceId),
            onPeerRemoved: peer => events.Add("remove:" + peer.DeviceId));
        var receive = typeof(MdnsBrowser).GetMethod("OnAnswerReceived", BindingFlags.Instance | BindingFlags.NonPublic)!;
        receive.Invoke(browser, [null, Answer("old-id")]);
        receive.Invoke(browser, [null, Answer("new-id")]);

        Assert.Equal(new[] { "add:old-id", "remove:old-id", "add:new-id" }, events);
        typeof(MdnsBrowser).GetMethod("RemoveService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(browser, ["phone._micbridge._tcp.local"]);
        Assert.Equal("remove:new-id", events[^1]);
        Assert.Equal(4, events.Count);
    }

    [Theory]
    [InlineData("office.pc", "office.pc")]
    [InlineData("office\\pc", "office\\pc")]
    [InlineData("", "MicBridge")]
    [InlineData("   ", "MicBridge")]
    [InlineData("pc\nname", "pcname")]
    public void InstanceNameIsOneLiteralLabel(string name, string expected)
    {
        var instance = MdnsAdvertisement.CreateInstanceName(name);
        Assert.Equal(expected, Assert.Single(instance.Labels));
        var profile = new ServiceProfile(instance, new DomainName("_micbridge._tcp"), 17891, Array.Empty<IPAddress>());
        Assert.Equal(4, profile.FullyQualifiedName.Labels.Count);
        Assert.Equal(expected, profile.FullyQualifiedName.Labels[0]);
        using var wire = new MemoryStream();
        new WireWriter(wire).WriteDomainName(profile.FullyQualifiedName);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(expected), wire.ToArray()[0]);
    }

    [Fact]
    public void InstanceNameLimitsUtf8WithoutSplittingUnicodeScalars()
    {
        Assert.Equal(new string('a', 63), Assert.Single(MdnsAdvertisement.CreateInstanceName(new string('a', 64)).Labels));
        Assert.Equal(new string('机', 21), Assert.Single(MdnsAdvertisement.CreateInstanceName(new string('机', 22)).Labels));
        var emoji = string.Concat(Enumerable.Repeat("😀", 16));
        var label = Assert.Single(MdnsAdvertisement.CreateInstanceName(emoji).Labels);
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 15)), label);
        Assert.Equal(60, System.Text.Encoding.UTF8.GetByteCount(label));
    }

    private static MessageEventArgs Answer(string id)
    {
        var service = new DomainName("phone._micbridge._tcp.local");
        var target = new DomainName("phone.local");
        var message = new Message();
        message.Answers.Add(new SRVRecord { Name = service, Target = target, Port = 17891 });
        message.AdditionalRecords.Add(new TXTRecord { Name = service, Strings = ["protocol=1", "role=android", "id=" + id] });
        message.AdditionalRecords.Add(new ARecord { Name = target, Address = IPAddress.Parse("192.0.2.10") });
        return new MessageEventArgs { Message = message, RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 5353) };
    }
}
