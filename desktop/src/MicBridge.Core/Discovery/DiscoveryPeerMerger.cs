namespace MicBridge.Core.Discovery;

/// <summary>合并 mDNS 与 UDP 发现，避免一个来源过期误删另一个来源。</summary>
public sealed class DiscoveryPeerMerger
{
    private readonly object gate = new();
    private readonly Dictionary<string, Dictionary<string, DiscoveredPeer>> byKey = new(StringComparer.OrdinalIgnoreCase);

    public void Observe(string source, DiscoveredPeer peer, Action<DiscoveredPeer> onPeer)
    {
        lock (gate)
        {
            var key = PeerKey(peer);
            if (!byKey.TryGetValue(key, out var sources))
                byKey[key] = sources = new(StringComparer.OrdinalIgnoreCase);
            sources[source] = peer;
            onPeer(peer);
        }
    }

    public void Remove(string source, DiscoveredPeer peer, Action<DiscoveredPeer> onPeer, Action<DiscoveredPeer> onRemoved)
    {
        lock (gate)
        {
            var key = PeerKey(peer);
            // 底层在锁外通知过期；旧记录的迟到删除不能撤销同来源的新观察。
            if (!byKey.TryGetValue(key, out var sources) || !sources.TryGetValue(source, out var current) || current != peer) return;
            sources.Remove(source);
            if (sources.Count != 0)
                onPeer(sources.Values.Last());
            else
            {
                byKey.Remove(key);
                onRemoved(peer);
            }
        }
    }

    private static string PeerKey(DiscoveredPeer peer) => $"{peer.Role}:{peer.DeviceId}";
}
