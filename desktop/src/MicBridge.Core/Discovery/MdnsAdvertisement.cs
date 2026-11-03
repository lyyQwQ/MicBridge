using System.Net.NetworkInformation;
using System.Text;
using Makaretu.Dns;

namespace MicBridge.Core.Discovery;

public sealed class MdnsAdvertisement : IDisposable
{
    private MulticastService? multicast;
    private ServiceDiscovery? discovery;
    private ServiceProfile? profile;


    public void Start(
        string name,
        int port,
        string deviceId = "windows",
        IReadOnlyList<string>? sources = null,
        IReadOnlyList<string>? sinks = null,
        IReadOnlyList<string>? transports = null)
    {
        if (multicast is not null) return;
        MulticastService? nextMulticast = null;
        ServiceDiscovery? nextDiscovery = null;
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            var addresses = MdnsNetworkInterfaces.Filter(interfaces)
                .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Distinct()
                .ToArray();
            nextMulticast = new MulticastService(MdnsNetworkInterfaces.Filter)
            {
                UseIpv4 = true,
                UseIpv6 = false,
            };
            nextDiscovery = new ServiceDiscovery(nextMulticast!);
            var nextProfile = new ServiceProfile(CreateInstanceName(name), new DomainName("_micbridge._tcp"), checked((ushort)port), addresses);
            nextProfile.AddProperty("role", "windows");
            nextProfile.AddProperty("protocol", "1");
            nextProfile.AddProperty("id", deviceId);
            nextProfile.AddProperty("name", LimitUtf8(name, 250));
            nextProfile.AddProperty("sources", string.Join(',', sources ?? ["micbridge_mic"]));
            nextProfile.AddProperty("sinks", string.Join(',', sinks ?? ["windows_audio"]));
            nextProfile.AddProperty("transports", string.Join(',', transports ?? ["tcp", "udp"]));
            nextDiscovery!.Advertise(nextProfile);
            nextMulticast!.Start();
            multicast = nextMulticast;
            discovery = nextDiscovery;
            profile = nextProfile;
        }
        catch
        {
            nextDiscovery?.Dispose();
            nextMulticast?.Stop();
            throw;
        }
    }

    public static DomainName CreateInstanceName(string name)
    {
        // Makaretu 会把字符串中的点解析成多个标签；数组构造保留单个实例标签。
        var label = LimitUtf8(name, 63);
        return new DomainName(new[] { string.IsNullOrWhiteSpace(label) ? "MicBridge" : label });
    }

    private static string LimitUtf8(string name, int limit)
    {
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            if (bytes + rune.Utf8SequenceLength > limit) break;
            result.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }

    public void Dispose()
    {
        if (discovery is not null && profile is not null)
        {
            discovery.Unadvertise(profile);
        }
        discovery?.Dispose();
        multicast?.Stop();
        multicast = null;
        discovery = null;
        profile = null;
    }
}
