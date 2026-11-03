using MicBridge.Core.Discovery;

namespace MicBridge.Core.Tests;

public sealed class DiscoveryPeerMergerTests
{
    [Fact]
    public void RemovingOneSourceKeepsPeerUntilLastSourceDisappears()
    {
        var merger = new DiscoveryPeerMerger();
        var published = new List<DiscoveredPeer>();
        var removed = new List<DiscoveredPeer>();
        var mdns = Peer("mdns-host", "android");
        var udp = Peer("udp-host", "android");

        merger.Observe("mdns", mdns, published.Add);
        merger.Observe("udp", udp, published.Add);
        Assert.Equal("udp-host", published[^1].Host);

        published.Clear();
        merger.Remove("mdns", mdns, published.Add, removed.Add);
        Assert.Empty(removed);
        Assert.Equal("udp-host", Assert.Single(published).Host);
        merger.Remove("udp", udp, published.Add, removed.Add);
        Assert.Equal("android:id-1", $"{Assert.Single(removed).Role}:{removed[0].DeviceId}");
    }

    [Fact]
    public void RoleAndIdAreTheMergedIdentity()
    {
        var merger = new DiscoveryPeerMerger();
        var published = new List<DiscoveredPeer>();
        var removed = new List<DiscoveredPeer>();
        var android = Peer("android-host", "android");
        var windows = Peer("windows-host", "windows");

        merger.Observe("mdns", android, published.Add);
        merger.Observe("udp", windows, published.Add);
        merger.Remove("mdns", android, published.Add, removed.Add);

        Assert.Equal(android, Assert.Single(removed));
        Assert.Equal("windows-host", published[^1].Host);
    }

    [Fact]
    public void LateRemovalCannotDeleteNewObservationFromSameSource()
    {
        var merger = new DiscoveryPeerMerger();
        var visible = new Dictionary<string, DiscoveredPeer>();
        void Publish(DiscoveredPeer peer) => visible[$"{peer.Role}:{peer.DeviceId}"] = peer;
        void Remove(DiscoveredPeer peer) => visible.Remove($"{peer.Role}:{peer.DeviceId}");
        var old = Peer("old-host", "android");
        var current = old with { Host = "new-host" };
        merger.Observe("mdns", old, Publish);
        merger.Observe("udp", old, Publish);
        merger.Observe("mdns", current, Publish);
        merger.Remove("mdns", old, Publish, Remove);
        Assert.Equal(current, visible["android:id-1"]);
        merger.Remove("udp", old, Publish, Remove);
        Assert.Equal(current, visible["android:id-1"]);
        merger.Remove("mdns", current, Publish, Remove);
        Assert.Empty(visible);
    }

    private static DiscoveredPeer Peer(string host, string role) =>
        new("phone", "id-1", host, 17891, [], [], ["tcp"], role);
}
