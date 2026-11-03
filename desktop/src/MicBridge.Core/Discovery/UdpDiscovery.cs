using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicBridge.Core.Discovery;

public sealed record DiscoveryAnnouncement(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("tcp_port")] int TcpPort,
    [property: JsonPropertyName("sources")] IReadOnlyList<string> Sources,
    [property: JsonPropertyName("sinks")] IReadOnlyList<string> Sinks,
    [property: JsonPropertyName("transports")] IReadOnlyList<string> Transports,
    [property: JsonPropertyName("announcement")] bool Announcement = true);

public sealed class UdpDiscovery : IDisposable
{
    public const int DefaultPort = 17_893;
    public const string MulticastAddress = "224.0.0.167";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Action<DiscoveredPeer> onPeer;
    private readonly Action<DiscoveredPeer>? onPeerRemoved;
    private readonly Action<string> log;
    private readonly Dictionary<string, DiscoveredPeer> peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object peerGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly int port;
    private UdpClient? socket;
    private Task? receiveTask;
    private Task? announceTask;
    private DiscoveryAnnouncement? localAnnouncement;
    private IPAddress[] broadcastAddresses = [];
    private int broadcastAddressesDirty = 1;

    public UdpDiscovery(
        Action<DiscoveredPeer> onPeer,
        Action<string>? log = null,
        int port = DefaultPort,
        Action<DiscoveredPeer>? onPeerRemoved = null)
    {
        this.onPeer = onPeer;
        this.onPeerRemoved = onPeerRemoved;
        this.log = log ?? (_ => { });
        this.port = port is >= 1 and <= 65535 ? port : DefaultPort;
    }

    public void Start(
        string name,
        string deviceId,
        int tcpPort,
        IReadOnlyList<string>? sources = null,
        IReadOnlyList<string>? sinks = null,
        IReadOnlyList<string>? transports = null)
    {
        if (socket is not null) return;
        if (string.IsNullOrWhiteSpace(deviceId) || tcpPort is < 1 or > 65535) return;

        localAnnouncement = new DiscoveryAnnouncement(
            1,
            deviceId,
            string.IsNullOrWhiteSpace(name) ? "MicBridge" : name,
            "windows",
            tcpPort,
            sources ?? [],
            sinks ?? ["windows_audio"],
            transports ?? ["tcp", "udp"]);

        try
        {
            var next = new UdpClient(AddressFamily.InterNetwork);
            next.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            next.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            next.EnableBroadcast = true;
            try
            {
                foreach (var address in MdnsNetworkInterfaces.GetIpv4Addresses())
                {
                    try { next.JoinMulticastGroup(IPAddress.Parse(MulticastAddress), address); }
                    catch (Exception error) { log($"UDP discovery multicast join on {address} failed: {error.Message}"); }
                }
            }
            catch (Exception error) { log($"UDP discovery multicast interface enumeration failed: {error.Message}"); }
            socket = next;
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            receiveTask = ReceiveLoopAsync(next, lifetime.Token);
            announceTask = AnnounceLoopAsync(lifetime.Token);
        }
        catch (Exception error)
        {
            log($"UDP discovery unavailable: {error.Message}");
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            socket?.Dispose();
            socket = null;
        }
    }

    private async Task ReceiveLoopAsync(UdpClient receiver, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await receiver.ReceiveAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException error)
            {
                if (!cancellationToken.IsCancellationRequested) log($"UDP discovery receive failed: {error.Message}");
                return;
            }

            if (result.Buffer.Length > 8 * 1024) continue;
            DiscoveryAnnouncement? announcement;
            try { announcement = JsonSerializer.Deserialize<DiscoveryAnnouncement>(result.Buffer, JsonOptions); }
            catch (JsonException) { continue; }
            if (!IsValid(announcement) || announcement!.Id.Equals(localAnnouncement?.Id, StringComparison.OrdinalIgnoreCase)) continue;

            var peer = new DiscoveredPeer(
                announcement.Name,
                announcement.Id,
                result.RemoteEndPoint.Address.ToString(),
                announcement.TcpPort,
                announcement.Sources,
                announcement.Sinks,
                announcement.Transports,
                announcement.Role);
            var key = $"{announcement.Role}:{announcement.Id}";
            var changed = false;
            lock (peerGate)
            {
                if (!peers.TryGetValue(key, out var previous) || previous != peer) changed = true;
                peers[key] = peer;
                lastSeen[key] = DateTimeOffset.UtcNow;
            }
            if (changed) onPeer(peer);

            if (announcement.Announcement && localAnnouncement is { } local)
            {
                try { await SendAsync(local with { Announcement = false }, result.RemoteEndPoint, cancellationToken); }
                catch (OperationCanceledException) { return; }
                catch (Exception error) { log($"UDP discovery response failed: {error.Message}"); }
            }
        }
    }

    private async Task AnnounceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SendAnnouncementAsync(cancellationToken);
                ExpirePeers();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { log($"UDP discovery announcement failed: {error.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SendAnnouncementAsync(CancellationToken cancellationToken)
    {
        if (localAnnouncement is null) return;
        var endpoints = new HashSet<IPEndPoint>
        {
            new(IPAddress.Parse(MulticastAddress), port),
            new(IPAddress.Broadcast, port),
        };
        try
        {
            foreach (var address in GetBroadcastAddresses())
            {
                endpoints.Add(new IPEndPoint(address, port));
            }
        }
        catch (Exception error) { log($"UDP discovery broadcast interface enumeration failed: {error.Message}"); }
        foreach (var endpoint in endpoints)
        {
            try { await SendAsync(localAnnouncement, endpoint, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { log($"UDP discovery announcement to {endpoint.Address} failed: {error.Message}"); }
        }
    }

    private IPAddress[] GetBroadcastAddresses()
    {
        if (Interlocked.Exchange(ref broadcastAddressesDirty, 0) == 0) return broadcastAddresses;
        try
        {
            // Repeated adapter enumeration retains native memory on some Windows systems.
            broadcastAddresses = MdnsNetworkInterfaces.GetIpv4Broadcasts().ToArray();
        }
        catch
        {
            Volatile.Write(ref broadcastAddressesDirty, 1);
            throw;
        }
        return broadcastAddresses;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs args) =>
        Volatile.Write(ref broadcastAddressesDirty, 1);

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
            }
        }
        if (expired is null || onPeerRemoved is null) return;
        foreach (var peer in expired) onPeerRemoved(peer);
    }
    private async Task SendAsync(DiscoveryAnnouncement announcement, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var current = socket ?? throw new ObjectDisposedException(nameof(UdpDiscovery));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(announcement, JsonOptions);
        await current.SendAsync(bytes, endpoint, cancellationToken);
    }

    private static bool IsValid(DiscoveryAnnouncement? announcement) =>
        announcement is not null &&
        announcement.Protocol == 1 &&
        !string.IsNullOrWhiteSpace(announcement.Id) &&
        !string.IsNullOrWhiteSpace(announcement.Name) &&
        (string.Equals(announcement.Role, "windows", StringComparison.OrdinalIgnoreCase) || string.Equals(announcement.Role, "android", StringComparison.OrdinalIgnoreCase)) &&
        announcement.TcpPort is >= 1 and <= 65535 &&
        announcement.Sources is not null && announcement.Sinks is not null && announcement.Transports is not null;

    public void Dispose()
    {
        lifetime.Cancel();
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        socket?.Dispose();
        socket = null;
        lock (peerGate)
        {
            peers.Clear();
            lastSeen.Clear();
        }
        lifetime.Dispose();
    }
}
