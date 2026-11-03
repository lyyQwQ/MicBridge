using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MicBridge.App;
using MicBridge.Core.Control;
using MicBridge.Core.Protocol;
using static MicBridge.Core.Tests.TrustTestSupport;

namespace MicBridge.Core.Tests;

public sealed class LocalControlServerTests
{
    [Fact]
    public async Task PairingEventAndStatusExposeSameRequestAndDecisionIsCorrelated()
    {
        await using var fixture = await Fixture.StartAsync();
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(true, null));
        var notification = await fixture.Output.EventAsync("pairing_request");
        var request = notification.GetProperty("request");
        Assert.Equal(AndroidId, request.GetProperty("deviceId").GetString());
        Assert.Equal("phone", request.GetProperty("deviceName").GetString());
        Assert.True(DateTimeOffset.TryParse(request.GetProperty("expiresAt").GetString(), out _));
        fixture.Input.Send(new { id = "status", command = "status" });
        var status = (await fixture.Output.ResponseAsync("status")).GetProperty("snapshot");
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(request.Deserialize<PendingPairing>(jsonOptions), status.GetProperty("pendingPairing").Deserialize<PendingPairing>(jsonOptions));
        Assert.Equal(JsonValueKind.Null, status.GetProperty("trustedDevice").ValueKind);
        Assert.False(status.TryGetProperty("pairingCode", out _));
        fixture.Input.Send(new { id = "stale", command = "pairing_decide", requestId = "stale", decision = "allow" });
        Assert.False((await fixture.Output.ResponseAsync("stale")).GetProperty("ok").GetBoolean());
        fixture.Input.Send(new { id = "deny", command = "pairing_decide", requestId = request.GetProperty("requestId").GetString(), decision = "deny" });
        Assert.True((await fixture.Output.ResponseAsync("deny")).GetProperty("ok").GetBoolean());
        Assert.Equal("pairing_denied", FrameCodec.ReadJson<ErrorPayload>(await ReadAsync(phone.GetStream())).Code);
        fixture.Input.Send(new { id = "after", command = "status" });
        Assert.Equal(JsonValueKind.Null, (await fixture.Output.ResponseAsync("after")).GetProperty("snapshot").GetProperty("pendingPairing").ValueKind);
    }

    [Fact]
    public async Task StatusRecoversPendingRequestWhenIpcAttachesAfterEvent()
    {
        await using var fixture = await Fixture.StartAsync(startIpc: false);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Core.PairingRequested += (_, _) => pending.TrySetResult();
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(true, null));
        await pending.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Server.Start();
        fixture.Input.Send(new { id = "recover", command = "status" });
        var status = (await fixture.Output.ResponseAsync("recover")).GetProperty("snapshot");
        Assert.Equal(fixture.Core.PendingPairing!.RequestId, status.GetProperty("pendingPairing").GetProperty("requestId").GetString());
        fixture.Core.DecidePairing(fixture.Core.PendingPairing.RequestId, "deny");
        Assert.Equal("pairing_denied", FrameCodec.ReadJson<ErrorPayload>(await ReadAsync(phone.GetStream())).Code);
    }

    [Fact]
    public async Task StdinEofRejectsPendingApprovalBeforeShutdownCallback()
    {
        await using var fixture = await Fixture.StartAsync();
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(true, null));
        var notification = await fixture.Output.EventAsync("pairing_request");
        fixture.Input.End();
        await fixture.Shutdown.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(fixture.Core.PendingPairing);
        Assert.False(fixture.Core.DecidePairing(notification.GetProperty("request").GetProperty("requestId").GetString()!, "allow"));
        Assert.Equal("pairing_denied", FrameCodec.ReadJson<ErrorPayload>(await ReadAsync(phone.GetStream())).Code);
        Assert.Null(fixture.Store.State.Device);
    }

    [Fact]
    public async Task AllowPublishesPublicTrustedDeviceAndForgetRemovesIt()
    {
        await using var fixture = await Fixture.StartAsync();
        using var phone = await ConnectAsync(fixture.Port);
        var stream = phone.GetStream();
        var windows = await SendHelloAsync(stream, Hello(true, null));
        var notification = await fixture.Output.EventAsync("pairing_request");
        fixture.Input.Send(new { id = "allow", command = "pairing_decide", requestId = notification.GetProperty("request").GetProperty("requestId").GetString(), decision = "allow" });
        Assert.True((await fixture.Output.ResponseAsync("allow")).GetProperty("ok").GetBoolean());
        var grant = FrameCodec.ReadJson<PairGrantPayload>(await ReadAsync(stream));
        await WriteAsync(stream, FrameCodec.Json(MessageType.PairAck, 2, new PairAckPayload(grant.PairingId)));
        await AuthenticateAsync(stream, windows, grant.Secret, grant.PairingId);
        fixture.Input.Send(new { id = "trusted", command = "status" });
        var status = (await fixture.Output.ResponseAsync("trusted")).GetProperty("snapshot");
        var trusted = status.GetProperty("trustedDevice");
        Assert.Equal(AndroidId, trusted.GetProperty("deviceId").GetString());
        Assert.Equal("phone", trusted.GetProperty("deviceName").GetString());
        Assert.Equal(grant.PairingId, trusted.GetProperty("pairingId").GetString());
        Assert.False(trusted.TryGetProperty("secret", out _));
        Assert.DoesNotContain(grant.Secret, status.GetRawText());
        fixture.Input.Send(new { id = "forget", command = "forget_device" });
        var forgotten = await fixture.Output.ResponseAsync("forget");
        Assert.True(forgotten.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, forgotten.GetProperty("snapshot").GetProperty("trustedDevice").ValueKind);
        Assert.Null(fixture.Store.State.Device);
        Assert.False(fixture.Core.Snapshot.AndroidConnected);
    }

    [Fact]
    public async Task RemovedPeerDisappearsFromEventsAndDiscoveryResponse()
    {
        await using var fixture = await Fixture.StartAsync(startCore: false);
        var peer = new MicBridge.Core.Discovery.DiscoveredPeer(
            "phone", "removed-phone", "127.0.0.1", 17891, [], [], ["tcp"], "android");
        void Publish(string eventName)
        {
            var field = typeof(CoreController).GetField(eventName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var handler = Assert.IsType<EventHandler<MicBridge.Core.Discovery.DiscoveredPeer>>(field.GetValue(fixture.Core));
            handler(fixture.Core, peer);
        }
        Publish(nameof(CoreController.DiscoveredPeersChanged));
        Assert.Contains((await fixture.Output.EventAsync("peers")).GetProperty("peers").EnumerateArray(),
            item => item.GetProperty("deviceId").GetString() == peer.DeviceId);
        Publish(nameof(CoreController.DiscoveredPeerRemoved));
        Assert.DoesNotContain((await fixture.Output.EventAsync("peers")).GetProperty("peers").EnumerateArray(),
            item => item.GetProperty("deviceId").GetString() == peer.DeviceId);
        fixture.Input.Send(new { id = "discover", command = "discover" });
        Assert.DoesNotContain((await fixture.Output.ResponseAsync("discover")).GetProperty("peers").EnumerateArray(),
            item => item.GetProperty("deviceId").GetString() == peer.DeviceId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WindowlessHostDrainsResponsesAndDisposesCore(bool occupiedPort, bool shutdownCommand)
    {
        using var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        blocker.Start();
        var port = occupiedPort ? ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port : FreePort();
        var sink = new Sink();
        var core = new CoreController(new CoreOptions(TcpPort: port, DiscoveryPort: 0), sink,
            sessionMonitor: new ManualSessionMonitor(), trustStore: new MemoryTrustStore());
        using var input = new InputReader();
        using var output = new OutputWriter();
        var host = Program.RunAsync(core, input, output);
        input.Send(new { id = "status", command = "status" });
        Assert.True((await output.ResponseAsync("status")).GetProperty("ok").GetBoolean());
        if (shutdownCommand) input.Send(new { id = "shutdown", command = "shutdown" });
        else input.End();
        Assert.Equal(0, await host.WaitAsync(TimeSpan.FromSeconds(10)));
        if (shutdownCommand)
            Assert.True((await output.ResponseAsync("shutdown")).GetProperty("ok").GetBoolean());
        Assert.True(sink.Disposed);
        Assert.True(sink.Closed);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public int Port { get; } = FreePort();
        public MemoryTrustStore Store { get; } = new();
        public CoreController Core { get; }
        public InputReader Input { get; } = new();
        public OutputWriter Output { get; } = new();
        public LocalControlServer Server { get; }
        public TaskCompletionSource Shutdown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Fixture()
        {
            Core = new CoreController(new CoreOptions(TcpPort: Port, DiscoveryPort: 0), new Sink(), sessionMonitor: new ManualSessionMonitor(), trustStore: Store);
            Server = new LocalControlServer(Core, () => Shutdown.TrySetResult(), Input, Output);
        }
        public static async Task<Fixture> StartAsync(bool startIpc = true, bool startCore = true)
        {
            var fixture = new Fixture();
            if (startCore) await fixture.Core.StartAsync();
            if (startIpc) fixture.Server.Start();
            return fixture;
        }
        public async ValueTask DisposeAsync() { await Server.DisposeAsync(); await Core.DisposeAsync(); }
    }
    private sealed class InputReader : TextReader
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
        public void Send(object command) => lines.Writer.TryWrite(JsonSerializer.Serialize(command));
        public void End() => lines.Writer.TryComplete();
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try { return await lines.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return null; }
        }
    }
    private sealed class OutputWriter : TextWriter
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(string? value) { lines.Writer.TryWrite(value!); return Task.CompletedTask; }
        public Task<JsonElement> EventAsync(string name) => FindAsync("event", name);
        public Task<JsonElement> ResponseAsync(string id) => FindAsync("id", id);
        private async Task<JsonElement> FindAsync(string property, string value)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (true)
            {
                var json = JsonSerializer.Deserialize<JsonElement>(await lines.Reader.ReadAsync(timeout.Token));
                if (json.TryGetProperty(property, out var actual) && actual.GetString() == value) return json;
            }
        }
    }
    private sealed class Sink : IAudioSink
    {
        public string Name => "test";
        public bool IsOpen => false;
        public bool Closed { get; private set; }
        public bool Disposed { get; private set; }
        public void Open() { }
        public void Write(ReadOnlySpan<byte> data) { }
        public void Close() => Closed = true;
        public void Dispose() => Disposed = true;
    }
}
