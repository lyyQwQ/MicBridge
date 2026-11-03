using System.Net;
using System.Reflection;
using MicBridge.Core.Discovery;

namespace MicBridge.Core.Tests;

public sealed class UdpDiscoveryCacheTests
{
    [Fact]
    public void BroadcastAddressesAreReusedUntilNetworkChanges()
    {
        using var discovery = new UdpDiscovery(_ => { });
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(UdpDiscovery);
        var getAddresses = type.GetMethod("GetBroadcastAddresses", flags)!;
        var changed = type.GetMethod("OnNetworkAddressChanged", flags)!;
        var dirty = type.GetField("broadcastAddressesDirty", flags)!;
        var cached = type.GetField("broadcastAddresses", flags)!;
        var sentinel = new[] { IPAddress.Parse("192.0.2.255") };
        cached.SetValue(discovery, sentinel);
        dirty.SetValue(discovery, 0);

        for (var i = 0; i < 10_000; i++)
            Assert.Same(sentinel, getAddresses.Invoke(discovery, null));

        changed.Invoke(discovery, [null, EventArgs.Empty]);
        Assert.Equal(1, dirty.GetValue(discovery));
        var refreshed = getAddresses.Invoke(discovery, null);
        Assert.NotSame(sentinel, refreshed);
        Assert.Equal(0, dirty.GetValue(discovery));
        Assert.Same(refreshed, getAddresses.Invoke(discovery, null));
    }
}
