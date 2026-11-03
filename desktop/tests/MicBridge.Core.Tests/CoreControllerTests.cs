using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MicBridge.Core.Control;
using MicBridge.Core.Protocol;

using MicBridge.Core.Discovery;
namespace MicBridge.Core.Tests;

public sealed class CoreControllerTests
{
    [Fact]
    public async Task InstallationIdentitySurvivesControllerRestart()
    {
        var store = new MemoryTrustStore();
        await using var first = new CoreController(new CoreOptions(), new FakeAudioSink(), sessionMonitor: new ManualSessionMonitor(), trustStore: store);
        await using var second = new CoreController(new CoreOptions(), new FakeAudioSink(), sessionMonitor: new ManualSessionMonitor(), trustStore: store);
        Assert.Equal(store.State.InstallationId, first.DeviceId);
        Assert.Equal(first.DeviceId, second.DeviceId);
    }

    [Fact]
    public async Task TcpHandshakeWithoutHelloIsClosedAfterDeadline()
    {
        var tcpPort = GetFreePort();
        await using var core = new CoreController(
            new CoreOptions(TcpPort: tcpPort, DeviceId: TrustTestSupport.WindowsId),
            new FakeAudioSink(), sessionMonitor: new ManualSessionMonitor(), trustStore: new MemoryTrustStore());
        await core.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, tcpPort);
        await using var stream = client.GetStream();

        var read = FrameCodec.ReadAsync(stream, CancellationToken.None).AsTask();
        await Assert.ThrowsAsync<EndOfStreamException>(() => read.WaitAsync(TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public async Task TcpCommands_StartAndStopFakeSink()
    {
        var tcpPort = GetFreePort();
        var sink = new FakeAudioSink();
        await using var core = new CoreController(new CoreOptions(TcpPort: tcpPort, DeviceId: TrustTestSupport.WindowsId), sink, sessionMonitor: new ManualSessionMonitor(), trustStore: MemoryTrustStore.Trusted());
        await core.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, tcpPort);
        await using var stream = client.GetStream();

        var windowsHello = await TrustTestSupport.SendHelloAsync(stream);
        await TrustTestSupport.AuthenticateAsync(stream, windowsHello);

        var streaming = new TaskCompletionSource<CoreSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new TaskCompletionSource<CoreSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.State == CoreState.Streaming) streaming.TrySetResult(snapshot);
            if (snapshot.State == CoreState.ConnectedIdle) idle.TrySetResult(snapshot);
        };
        await WriteAsync(stream, FrameCodec.Json(MessageType.Start, 2, new CommandPayload("START")));
        var start = await FrameCodec.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(MessageType.Start, start.Type);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 3, new StatusPayload("streaming", "蓝牙耳机")));

        var started = await streaming.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(sink.IsOpen);
        Assert.Equal(CoreState.Streaming, started.State);

        // Android AudioRecord sends 4096-byte TCP frames; UDP's 1180-byte limit must not drop them.
        var pcm = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        await WriteAsync(stream, new Frame(MessageType.Audio, 4, pcm));
        Assert.Equal(pcm, await sink.FirstAudio.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        await WriteAsync(stream, FrameCodec.Json(MessageType.Stop, 4, new CommandPayload("STOP")));
        var stop = await FrameCodec.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(MessageType.Stop, stop.Type);
        var stopped = await idle.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(CoreState.ConnectedIdle, stopped.State);
        Assert.False(sink.IsOpen);
    }
    [Fact]
    public async Task LateStreamingStatusAfterAndroidErrorDoesNotReopenSink()
    {
        var tcpPort = GetFreePort();
        var sink = new FakeAudioSink();
        await using var core = new CoreController(new CoreOptions(TcpPort: tcpPort, DeviceId: TrustTestSupport.WindowsId), sink, sessionMonitor: new ManualSessionMonitor(), trustStore: MemoryTrustStore.Trusted());
        await core.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, tcpPort);
        await using var stream = client.GetStream();
        var windowsHello = await TrustTestSupport.SendHelloAsync(stream);
        await TrustTestSupport.AuthenticateAsync(stream, windowsHello);

        await WriteAsync(stream, FrameCodec.Json(MessageType.Start, 3, new CommandPayload("START")));
        Assert.Equal(MessageType.Start, (await FrameCodec.ReadAsync(stream, CancellationToken.None)).Type);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 4, new StatusPayload("error", "蓝牙耳机") with { ErrorCode = "route_unavailable", Detail = "route unavailable" }));
        await Task.Delay(50);
        Assert.False(sink.IsOpen);
        Assert.Equal(CoreState.Error, core.Snapshot.State);

        await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 5, new StatusPayload("streaming", "蓝牙耳机")));
        await Task.Delay(50);
        Assert.False(sink.IsOpen);
        Assert.Equal(CoreState.Error, core.Snapshot.State);
    }

    [Fact]
    public async Task TcpCommands_RejectWrongTrustSecret()
    {
        var tcpPort = GetFreePort();
        await using var core = new CoreController(new CoreOptions(TcpPort: tcpPort, DeviceId: TrustTestSupport.WindowsId), new FakeAudioSink(), sessionMonitor: new ManualSessionMonitor(), trustStore: MemoryTrustStore.Trusted());
        await core.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, tcpPort);
        await using var stream = client.GetStream();
        var windowsHello = await TrustTestSupport.SendHelloAsync(stream);
        var challenge = FrameCodec.ReadJson<AuthChallengePayload>(await FrameCodec.ReadAsync(stream, CancellationToken.None));
        await WriteAsync(stream, FrameCodec.Json(MessageType.AuthProof, 2, new AuthProofPayload(
            FrameCodec.ComputeAuthProof("wrong", challenge.Challenge, TrustTestSupport.WindowsId, TrustTestSupport.AndroidId, windowsHello.ConnectionId!, TrustTestSupport.ConnectionId), TrustTestSupport.Nonce)));
        var error = FrameCodec.ReadJson<ErrorPayload>(await FrameCodec.ReadAsync(stream, CancellationToken.None));
        Assert.Equal("auth_proof_invalid", error.Code);
    }
    [Fact]
    public void ExtendedHelloFieldsRoundTripWithoutBreakingLegacyShape()
    {
        var payload = new HelloPayload(
            "android",
            "蓝牙耳机",
            FrameCodec.AudioRate,
            FrameCodec.AudioBits,
            FrameCodec.AudioChannels,
            ["tcp", "udp"],
            "android-instance",
            "attempt-1",
            ["micbridge_mic"],
            []);
        var decoded = FrameCodec.ReadJson<HelloPayload>(FrameCodec.Json(MessageType.Hello, 1, payload));
        Assert.Equal("android-instance", decoded.DeviceId);
        Assert.Equal("attempt-1", decoded.ConnectionId);
        Assert.Equal(["micbridge_mic"], decoded.Sources);
        Assert.Equal([], decoded.Sinks);
        var legacy = FrameCodec.ReadJson<HelloPayload>(FrameCodec.Json(MessageType.Hello, 2,
            new HelloPayload("android", null, FrameCodec.AudioRate, FrameCodec.AudioBits, FrameCodec.AudioChannels, ["tcp"])));
        Assert.Null(legacy.DeviceId);
        Assert.Null(legacy.ConnectionId);
    }
    [Fact]
    public void DiscoveryAnnouncementRoundTripsWireFields()
    {
        var payload = new DiscoveryAnnouncement(1, "windows-1", "蓝牙耳机", "windows", 17_891, ["micbridge_mic"], ["windows_audio"], ["tcp", "udp"]);
        var json = JsonSerializer.Serialize(payload);
        var decoded = JsonSerializer.Deserialize<DiscoveryAnnouncement>(json);
        Assert.NotNull(decoded);
        Assert.Equal(payload.Protocol, decoded.Protocol);
        Assert.Equal(payload.Id, decoded.Id);
        Assert.Equal(payload.Name, decoded.Name);
        Assert.Equal(payload.Role, decoded.Role);
        Assert.Equal(payload.TcpPort, decoded.TcpPort);
        Assert.Equal(payload.Sources, decoded.Sources);
        Assert.Equal(payload.Sinks, decoded.Sinks);
        Assert.Equal(payload.Transports, decoded.Transports);
        Assert.Equal(payload.Announcement, decoded.Announcement);
    }
    [Fact]
    public async Task UdpDiscoveryEmitsValidatedPeer()
    {
        var port = GetFreeUdpPort();
        var received = new TaskCompletionSource<DiscoveredPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var discovery = new UdpDiscovery(peer => received.TrySetResult(peer), port: port);
        discovery.Start("Windows", "windows-1", 17_891);
        using var sender = new UdpClient();
        var payload = new DiscoveryAnnouncement(1, "android-1", "Android", "android", 17_891, ["micbridge_mic"], [], ["tcp", "udp"]);
        await sender.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), new IPEndPoint(IPAddress.Loopback, port));
        var peer = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("android-1", peer.DeviceId);
        Assert.Equal("127.0.0.1", peer.Host);
        Assert.Equal("android", peer.Role);
        var response = await sender.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var reply = JsonSerializer.Deserialize<DiscoveryAnnouncement>(response.Buffer);
        Assert.NotNull(reply);
        Assert.Equal("windows-1", reply.Id);
        Assert.Equal("windows", reply.Role);
        Assert.Equal("Windows", reply.Name);
        Assert.Equal(["windows_audio"], reply.Sinks);
        Assert.False(reply.Announcement);
    }

    private static int GetFreeUdpPort()
    {
        using var probe = new UdpClient(0);
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static async Task WriteAsync(Stream stream, Frame frame)
    {
        await stream.WriteAsync(FrameCodec.Encode(frame));
        await stream.FlushAsync();
    }

    private static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private sealed class FakeAudioSink : IAudioSink
    {
        public string Name => "fake sink";
        public bool IsOpen { get; private set; }
        public TaskCompletionSource<byte[]> FirstAudio { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Open() => IsOpen = true;
        public void Write(ReadOnlySpan<byte> data) => FirstAudio.TrySetResult(data.ToArray());
        public void Close() => IsOpen = false;
        public void Dispose() => Close();
    }
}
