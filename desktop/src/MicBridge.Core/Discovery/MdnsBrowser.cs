using System.Net.Sockets;
using System.Net;
using System.Net.NetworkInformation;
using Makaretu.Dns;

namespace MicBridge.Core.Discovery;

public sealed record DiscoveredPeer(
    string Name,
    string DeviceId,
    string Host,
    int Port,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Sinks,
    IReadOnlyList<string> Transports,
    string Role = "");

public sealed class MdnsBrowser : IDisposable
{
    private const string ServiceType = "_micbridge._tcp";
    private readonly Action<DiscoveredPeer> onPeer;
    private readonly Action<DiscoveredPeer>? onPeerRemoved;
    private readonly Action<string> log;
    private readonly Dictionary<string, DiscoveredPeer> peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> serviceKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object peerGate = new();
    private MulticastService? multicast;
    private ServiceDiscovery? discovery;
    private Timer? expiryTimer;

    public MdnsBrowser(
        Action<DiscoveredPeer> onPeer,
        Action<string>? log = null,
        Action<DiscoveredPeer>? onPeerRemoved = null)
    {
        this.onPeer = onPeer;
        this.onPeerRemoved = onPeerRemoved;
        this.log = log ?? (_ => { });
    }

    public void Start()
    {
        if (multicast is not null) return;
        try
        {
            var nextMulticast = CreateMulticastService();
            var nextDiscovery = new ServiceDiscovery(nextMulticast);
            multicast = nextMulticast;
            discovery = nextDiscovery;
            nextDiscovery.ServiceInstanceDiscovered += (_, args) =>
            {
                nextMulticast.SendQuery(args.ServiceInstanceName.ToString());
            };
            nextDiscovery.ServiceInstanceShutdown += (_, args) => RemoveService(args.ServiceInstanceName.ToString());
            nextMulticast.Start();
            nextDiscovery.QueryServiceInstances(new DomainName(ServiceType));
            expiryTimer = new Timer(_ => RefreshPeers(nextDiscovery), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            multicast = nextMulticast;
            discovery = nextDiscovery;
        }
        catch (Exception error)
        {
            log($"mDNS browser unavailable: {error.Message}");
            Dispose();
        }
    }

    private MulticastService CreateMulticastService()
    {
        // 0.27 默认过滤一分钟内相同响应；本地十五秒过期要求每次续查响应都可续命。
        var service = new MulticastService(MdnsNetworkInterfaces.Filter)
        {
            UseIpv4 = true,
            UseIpv6 = false,
            IgnoreDuplicateMessages = false,
        };
        service.AnswerReceived += OnAnswerReceived;
        return service;
    }

    private void OnAnswerReceived(object? sender, MessageEventArgs args)
    {
        var records = args.Message.Answers.Concat(args.Message.AdditionalRecords).ToArray();
        foreach (var srv in records.OfType<SRVRecord>())
        {
            if (!srv.Name.ToString().Contains(ServiceType, StringComparison.OrdinalIgnoreCase)) continue;
            var txt = records.OfType<TXTRecord>().FirstOrDefault(record => record.Name == srv.Name);
            var values = txt?.Strings
                .Select(value => value.Split(['='], 2, StringSplitOptions.None))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (values.GetValueOrDefault("protocol") != "1") continue;
            var id = values.GetValueOrDefault("id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var address = records.OfType<ARecord>().FirstOrDefault(record => record.Name == srv.Target)?.Address;
            if (address is null) continue;
            if (!int.TryParse(srv.Port.ToString(), out var port) || port is < 1 or > 65535) continue;
            var peer = new DiscoveredPeer(
                values.GetValueOrDefault("name") ?? srv.Name.ToString(),
                id,
                address.ToString(),
                port,
                Split(values.GetValueOrDefault("sources")),
                Split(values.GetValueOrDefault("sinks")),
                Split(values.GetValueOrDefault("transports")),
                values.GetValueOrDefault("role") ?? "");
            var service = srv.Name.ToString();
            var key = $"{peer.Role}:{peer.DeviceId}";
            var changed = false;
            DiscoveredPeer? replaced = null;
            lock (peerGate)
            {
                if (serviceKeys.TryGetValue(service, out var previousKey) && previousKey != key)
                {
                    peers.Remove(previousKey, out replaced);
                    lastSeen.Remove(previousKey);
                }
                serviceKeys[service] = key;
                if (!peers.TryGetValue(key, out var previous) || previous != peer) changed = true;
                peers[key] = peer;
                lastSeen[key] = DateTimeOffset.UtcNow;
            }
            if (replaced is not null) onPeerRemoved?.Invoke(replaced);
            if (changed) onPeer(peer);
        }
    }
    private void RefreshPeers(ServiceDiscovery activeDiscovery)
    {
        try { activeDiscovery.QueryServiceInstances(new DomainName(ServiceType)); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or SocketException)
        { log($"mDNS refresh unavailable: {error.Message}"); }
        finally { ExpirePeers(); }
    }


    private void RemoveService(string service)
    {
        DiscoveredPeer? removed = null;
        lock (peerGate)
        {
            if (serviceKeys.Remove(service, out var key))
            {
                peers.Remove(key, out removed);
                lastSeen.Remove(key);
            }
        }
        if (removed is not null) onPeerRemoved?.Invoke(removed);
    }

    private void ExpirePeers()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(15);
        List<DiscoveredPeer>? expired = null;
        lock (peerGate)
        {
            foreach (var key in lastSeen.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray())
            {
                if (peers.Remove(key, out var peer))
                {
                    expired ??= [];
                    expired.Add(peer);
                }
                lastSeen.Remove(key);
                foreach (var service in serviceKeys.Where(pair => pair.Value == key).Select(pair => pair.Key).ToArray())
                {
                    serviceKeys.Remove(service);
                }
            }
        }
        if (expired is null || onPeerRemoved is null) return;
        foreach (var peer in expired) onPeerRemoved(peer);
    }

    private static IReadOnlyList<string> Split(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void Dispose()
    {
        expiryTimer?.Dispose();
        expiryTimer = null;
        if (discovery is not null) discovery.Dispose();
        if (multicast is not null) multicast.Stop();
        discovery = null;
        multicast = null;
        lock (peerGate)
        {
            peers.Clear();
            lastSeen.Clear();
            serviceKeys.Clear();
        }
    }
}

internal static class MdnsNetworkInterfaces
{
    public static IEnumerable<NetworkInterface> Filter(IEnumerable<NetworkInterface> interfaces) => interfaces.Where(IsUsable);
    public static IEnumerable<IPAddress> GetIpv4Addresses()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces().Where(IsUsable))
        {
            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) yield return address.Address;
            }
        }
    }
    public static IEnumerable<IPAddress> GetIpv4Broadcasts()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces().Where(IsUsable))
        {
            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || address.IPv4Mask is null) continue;
                var addressBytes = address.Address.GetAddressBytes();
                var maskBytes = address.IPv4Mask.GetAddressBytes();
                var broadcastBytes = addressBytes.Select((value, index) => (byte)(value | ~maskBytes[index])).ToArray();
                yield return new IPAddress(broadcastBytes);
            }
        }
    }


    private static bool IsUsable(NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up) return false;
        if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;
        var text = $"{networkInterface.Name} {networkInterface.Description}".ToLowerInvariant();
        if (text.Contains("loopback") || text.Contains("tailscale") || text.Contains("clash") || text.Contains("vmware") || text.Contains("virtual") || text.Contains("vpn") || text.Contains("wireguard") || text.Contains("wintun") || text.Contains("tap") || text.Contains("hyper-v") || text.Contains("docker") || text.Contains("zerotier")) return false;
        return networkInterface.GetIPProperties().UnicastAddresses.Any(address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
    }
}
