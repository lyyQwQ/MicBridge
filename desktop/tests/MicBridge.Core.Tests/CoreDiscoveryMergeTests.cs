using System.Reflection;
using MicBridge.Core.Control;
using MicBridge.Core.Discovery;

namespace MicBridge.Core.Tests;

public sealed class CoreDiscoveryMergeTests
{
    [Fact]
    public async Task RemovingMdnsLeavesUdpPeerAndRoleSeparatesSameId()
    {
        await using var core = new CoreController(new CoreOptions(), new Sink(),
            sessionMonitor: new ManualSessionMonitor(), trustStore: new MemoryTrustStore());
        var visible = new Dictionary<string, DiscoveredPeer>();
        core.DiscoveredPeersChanged += (_, peer) => visible[$"{peer.Role}:{peer.DeviceId}"] = peer;
        core.DiscoveredPeerRemoved += (_, peer) => visible.Remove($"{peer.Role}:{peer.DeviceId}");
        var mdns = new DiscoveredPeer("phone", "same-id", "mdns-host", 17891, [], [], ["tcp"], "android");
        var udp = mdns with { Host = "udp-host" };
        Publish("browser", "onPeer", mdns);
        Publish("udpDiscovery", "onPeer", udp);
        Publish("udpDiscovery", "onPeer", udp with { Role = "windows" });
        Publish("browser", "onPeerRemoved", mdns);
        Assert.Equal("udp-host", visible["android:same-id"].Host);
        Assert.Contains("windows:same-id", visible.Keys);
        Publish("udpDiscovery", "onPeerRemoved", udp);
        Assert.DoesNotContain("android:same-id", visible.Keys);
        Assert.Contains("windows:same-id", visible.Keys);

        void Publish(string source, string callback, DiscoveredPeer peer)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var discovery = typeof(CoreController).GetField(source, flags)!.GetValue(core)!;
            ((Action<DiscoveredPeer>)discovery.GetType().GetField(callback, flags)!.GetValue(discovery)!)(peer);
        }
    }

    private sealed class Sink : IAudioSink
    {
        public string Name => "test";
        public bool IsOpen => false;
        public void Open() { }
        public void Write(ReadOnlySpan<byte> bytes) { }
        public void Close() { }
        public void Dispose() { }
    }
}
