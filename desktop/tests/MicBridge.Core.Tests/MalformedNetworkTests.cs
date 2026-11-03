using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MicBridge.Core.Discovery;
using MicBridge.Core.Protocol;

namespace MicBridge.Core.Tests;

public sealed class MalformedNetworkTests
{
    [Theory]
    [InlineData(0x80000000u)]
    [InlineData(uint.MaxValue)]
    public async Task UnsignedOversizedLengthIsInvalidDataForDecodeAndStream(uint length)
    {
        var bytes = FrameCodec.Encode(new Frame(MessageType.Audio, 1, []));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12, 4), length);
        Assert.Throws<InvalidDataException>(() => FrameCodec.Decode(bytes));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await FrameCodec.ReadAsync(new MemoryStream(bytes), CancellationToken.None));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"protocol\":1,\"id\":\"x\",\"name\":\"x\",\"tcp_port\":17891}")]
    [InlineData("{\"protocol\":1,\"id\":\"x\",\"name\":\"x\",\"role\":null,\"tcp_port\":17891}")]
    [InlineData("{\"protocol\":1,\"id\":\"x\",\"name\":\"x\",\"role\":\"android\",\"tcp_port\":17891,\"sources\":null}")]
    public async Task MissingDiscoveryFieldsDoNotStopReceiver(string malformed)
    {
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Dispose();
        var found = new TaskCompletionSource<DiscoveredPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var discovery = new UdpDiscovery(peer => found.TrySetResult(peer), port: port);
        discovery.Start("local", "local", 17891);
        using var sender = new UdpClient();
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        await sender.SendAsync(Encoding.UTF8.GetBytes(malformed), endpoint);
        await sender.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new DiscoveryAnnouncement(1, "peer", "phone", "android", 17891, [], [], ["tcp"])), endpoint);
        Assert.Equal("peer", (await found.Task.WaitAsync(TimeSpan.FromSeconds(3))).DeviceId);
    }
}
