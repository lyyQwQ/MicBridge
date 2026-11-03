using System.Net;
using System.Net.Sockets;
using MicBridge.Core.Control;
using MicBridge.Core.Protocol;

namespace MicBridge.Core.Tests;

public sealed class AutoCaptureControllerTests
{
    [Fact]
    public async Task ActiveBeforeConnectionStartsAfterHandshakeAndKeepsCaptureUntilHoldExpires()
    {
        var monitor = new ManualSessionMonitor();
        var clock = new CaptureTestClock();
        var skipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = FreePort();
        var sink = new TestSink();
        await using var core = new CoreController(new CoreOptions(TcpPort: port, DiscoveryPort: 0, DeviceId: TrustTestSupport.WindowsId),
            sink, message => { if (message.Contains("automatic START skipped")) skipped.TrySetResult(); }, monitor, MemoryTrustStore.Trusted(), clock);
        await core.StartAsync();
        monitor.Emit(true);
        await skipped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CoreState.Disconnected, core.Snapshot.State);
        Assert.True(core.Snapshot.ListenerActive);
        Assert.Equal("", core.Snapshot.Detail);

        using var client = await ConnectAsync(port);
        var stream = client.GetStream();
        Assert.Equal(MessageType.Start, (await ReadAsync(stream)).Type);
        Assert.Equal(CoreState.Starting, core.Snapshot.State);
        Assert.Equal(1, sink.Opens);

        // Do not acknowledge START yet. The monitor must still publish the falling edge.
        var inactive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.SnapshotChanged += (_, snapshot) =>
        {
            if (!snapshot.ListenerActive) inactive.TrySetResult();
            if (snapshot.State == CoreState.ConnectedIdle) idle.TrySetResult();
        };
        monitor.Emit(false);
        Assert.True(core.Snapshot.ListenerActive);
        clock.Advance(AutoCapturePolicy.ListenerDebounce);
        monitor.Emit(false);
        await inactive.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(sink.IsOpen);
        Assert.Equal(CoreState.Starting, core.Snapshot.State);
        var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.SnapshotChanged += (_, snapshot) => { if (snapshot.State == CoreState.Streaming) streaming.TrySetResult(); };
        await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 3, new StatusPayload("streaming")));
        await streaming.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(AutoCapturePolicy.StopDelay - AutoCapturePolicy.ListenerDebounce - TimeSpan.FromTicks(1));
        monitor.Emit(false);
        Assert.Equal(CoreState.Streaming, core.Snapshot.State);
        Assert.Equal(1, sink.Opens);
        clock.Advance(TimeSpan.FromTicks(1));
        monitor.Emit(false);
        Assert.Equal(MessageType.Stop, (await ReadAsync(stream)).Type);
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(sink.IsOpen);
        Assert.Equal(1, sink.Opens);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 3, new StatusPayload("streaming", "蓝牙耳机")));
        await WriteAsync(stream, FrameCodec.Json(MessageType.Ping, 4, new CommandPayload("PING")));
        var status = FrameCodec.ReadJson<StatusPayload>(await ReadAsync(stream));
        Assert.Equal("connected_idle", status.State);
        Assert.False(sink.IsOpen);
    }

    [Fact]
    public async Task ManualStartRemainsStreamingAfterListenerGoesInactive()
    {
        var monitor = new ManualSessionMonitor();
        var clock = new CaptureTestClock();
        var port = FreePort();
        var sink = new TestSink();
        await using var core = new CoreController(new CoreOptions(TcpPort: port, DiscoveryPort: 0, DeviceId: TrustTestSupport.WindowsId), sink, sessionMonitor: monitor, trustStore: MemoryTrustStore.Trusted(), timeProvider: clock);
        await core.StartAsync();
        using var client = await ConnectAsync(port);
        var stream = client.GetStream();
        var start = core.DispatchAsync("START");
        Assert.Equal(MessageType.Start, (await ReadAsync(stream)).Type);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 3, new StatusPayload("streaming")));
        Assert.True((await start.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
        monitor.Emit(true);
        monitor.Emit(false);
        clock.Advance(AutoCapturePolicy.StopDelay);
        monitor.Emit(false);
        Assert.False(core.Snapshot.ListenerActive);
        Assert.Equal(CoreState.Streaming, core.Snapshot.State);
        Assert.True(sink.IsOpen);
        // A manual STOP still follows the normal command path.
        var stop = core.DispatchAsync("STOP");
        Assert.Equal(MessageType.Stop, (await ReadAsync(stream)).Type);
        Assert.True((await stop.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
        Assert.False(sink.IsOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticStopPreservesAndroidError(bool acknowledgeStartup)
    {
        var monitor = new ManualSessionMonitor();
        var clock = new CaptureTestClock();
        var port = FreePort();
        var sink = new TestSink();
        await using var core = new CoreController(new CoreOptions(TcpPort: port, DiscoveryPort: 0, DeviceId: TrustTestSupport.WindowsId), sink, sessionMonitor: monitor, trustStore: MemoryTrustStore.Trusted(), timeProvider: clock);
        await core.StartAsync();
        using var client = await ConnectAsync(port);
        var stream = client.GetStream();
        monitor.Emit(true);
        Assert.Equal(MessageType.Start, (await ReadAsync(stream)).Type);
        if (acknowledgeStartup)
        {
            var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.SnapshotChanged += (_, snapshot) => { if (snapshot.State == CoreState.Streaming) streaming.TrySetResult(); };
            await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 3, new StatusPayload("streaming")));
            await streaming.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.SnapshotChanged += (_, snapshot) => { if (snapshot.State == CoreState.Error) error.TrySetResult(); };
        await WriteAsync(stream, FrameCodec.Json(MessageType.Error, 4, new ErrorPayload("route_lost", "selected microphone unavailable")));
        await error.Task.WaitAsync(TimeSpan.FromSeconds(2));
        monitor.Emit(false);
        clock.Advance(AutoCapturePolicy.ListenerDebounce);
        var unexpectedTransition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.SnapshotChanged += (_, snapshot) => { if (snapshot.State != CoreState.Error) unexpectedTransition.TrySetResult(); };
        monitor.Emit(false);
        var observation = Task.Delay(250);
        Assert.Same(observation, await Task.WhenAny(unexpectedTransition.Task, observation));
        Assert.Equal(CoreState.Error, core.Snapshot.State);
        Assert.Contains("selected microphone unavailable", core.Snapshot.Detail);
        Assert.False(sink.IsOpen);
        // There must be no automatic STOP queued ahead of the status response.
        await WriteAsync(stream, FrameCodec.Json(MessageType.Ping, 5, new CommandPayload("PING")));
        Assert.Equal(MessageType.Status, (await ReadAsync(stream)).Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureErrorKeepsAuthenticatedConnectionAndRecoversOnNextRecording(bool acknowledgeStartup)
    {
        var monitor = new ManualSessionMonitor();
        var clock = new CaptureTestClock();
        var port = FreePort();
        var sink = new TestSink();
        await using var core = new CoreController(new CoreOptions(TcpPort: port, DiscoveryPort: 0, DeviceId: TrustTestSupport.WindowsId),
            sink, sessionMonitor: monitor, trustStore: MemoryTrustStore.Trusted(), timeProvider: clock);
        await core.StartAsync();
        using var client = await ConnectAsync(port);
        var stream = client.GetStream();
        monitor.Emit(true);
        Assert.Equal(MessageType.Start, (await ReadAsync(stream)).Type);
        if (acknowledgeStartup)
        {
            var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.SnapshotChanged += (_, snapshot) => { if (snapshot.State == CoreState.Streaming) streaming.TrySetResult(); };
            await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 3, new StatusPayload("streaming")));
            await streaming.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        await WriteAsync(stream, FrameCodec.Json(MessageType.Error, 5, new ErrorPayload("input_not_selected", "select a microphone")));
        // PING is processed after error cleanup, providing a barrier for the same authenticated socket.
        await WriteAsync(stream, FrameCodec.Json(MessageType.Ping, 6, new CommandPayload("PING")));
        Assert.Equal(MessageType.Status, (await ReadAsync(stream)).Type);
        Assert.Equal(CoreState.Error, core.Snapshot.State);
        Assert.True(core.Snapshot.AndroidConnected);
        Assert.False(sink.IsOpen);

        for (var i = 0; i < 5; i++) monitor.Emit(true);
        await Task.Delay(150);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Ping, 7, new CommandPayload("PING")));
        Assert.Equal(MessageType.Status, (await ReadAsync(stream)).Type);
        Assert.Equal(1, sink.Opens);

        monitor.Emit(false);
        clock.Advance(AutoCapturePolicy.ListenerDebounce);
        monitor.Emit(false);
        Assert.False(core.Snapshot.ListenerActive);
        monitor.Emit(true);
        Assert.Equal(MessageType.Start, (await ReadAsync(stream)).Type);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.SnapshotChanged += (_, snapshot) => { if (snapshot.State == CoreState.Streaming) recovered.TrySetResult(); };
        await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 8, new StatusPayload("streaming")));
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CoreState.Streaming, core.Snapshot.State);
        Assert.True(core.Snapshot.AndroidConnected);
        Assert.Equal(2, sink.Opens);
        Assert.True(sink.IsOpen);
    }

    [Fact]
    public async Task ManualRetryAfterCaptureErrorWorksOnSameAuthenticatedConnection()
    {
        var monitor = new ManualSessionMonitor();
        var port = FreePort();
        var sink = new TestSink();
        await using var core = new CoreController(new CoreOptions(TcpPort: port, DiscoveryPort: 0, DeviceId: TrustTestSupport.WindowsId),
            sink, sessionMonitor: monitor, trustStore: MemoryTrustStore.Trusted());
        await core.StartAsync();
        using var client = await ConnectAsync(port);
        var stream = client.GetStream();
        var failedStart = core.DispatchAsync("START");
        Assert.Equal(MessageType.Start, (await ReadAsync(stream)).Type);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Error, 3, new ErrorPayload("route_lost", "microphone unavailable")));
        Assert.False((await failedStart.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Ping, 4, new CommandPayload("PING")));
        Assert.Equal(MessageType.Status, (await ReadAsync(stream)).Type);
        Assert.Equal(CoreState.Error, core.Snapshot.State);
        Assert.True(core.Snapshot.AndroidConnected);

        var retry = core.DispatchAsync("START");
        Assert.Equal(MessageType.Start, (await ReadAsync(stream)).Type);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 5, new StatusPayload("streaming")));
        Assert.True((await retry.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
        Assert.Equal(CoreState.Streaming, core.Snapshot.State);
        Assert.Equal(2, sink.Opens);
        Assert.True(sink.IsOpen);
        Assert.True(core.Snapshot.AndroidConnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsCaptureFailureStopsAndroidAndKeepsErrorWithoutRetrying(bool failSink)
    {
        var monitor = new ManualSessionMonitor();
        var port = FreePort();
        var sink = new TestSink { FailWrites = failSink };
        await using var core = new CoreController(new CoreOptions(TcpPort: port, DiscoveryPort: 0, DeviceId: TrustTestSupport.WindowsId),
            sink, sessionMonitor: monitor, trustStore: MemoryTrustStore.Trusted());
        await core.StartAsync();
        using var client = await ConnectAsync(port);
        var stream = client.GetStream();
        monitor.Emit(true);
        Assert.Equal(MessageType.Start, (await ReadAsync(stream)).Type);
        if (failSink)
        {
            var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.SnapshotChanged += (_, snapshot) => { if (snapshot.State == CoreState.Streaming) streaming.TrySetResult(); };
            await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 3, new StatusPayload("streaming")));
            await streaming.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WriteAsync(stream, new Frame(MessageType.Audio, 4, new byte[] { 0, 0 }));
        }
        // Without an acknowledgement, START times out after five seconds.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Assert.Equal(MessageType.Stop, (await FrameCodec.ReadAsync(stream, timeout.Token)).Type);
        var detail = core.Snapshot.Detail;
        Assert.False(string.IsNullOrWhiteSpace(detail));
        if (failSink) Assert.Contains("sink write failed", detail);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 5, new StatusPayload("idle")));
        await WriteAsync(stream, FrameCodec.Json(MessageType.Ping, 6, new CommandPayload("PING")));
        Assert.Equal(MessageType.Status, (await ReadAsync(stream)).Type);
        Assert.Equal(CoreState.Error, core.Snapshot.State);
        Assert.Equal(detail, core.Snapshot.Detail);
        Assert.True(core.Snapshot.AndroidConnected);
        Assert.False(sink.IsOpen);
        for (var i = 0; i < 5; i++) monitor.Emit(true);
        await Task.Delay(150);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Ping, 7, new CommandPayload("PING")));
        Assert.Equal(MessageType.Status, (await ReadAsync(stream)).Type);
        Assert.Equal(1, sink.Opens);
    }

    [Fact]
    public async Task ManualTakeoverDuringAutomaticStartupKeepsConfirmationWaitAlive()
    {
        var monitor = new ManualSessionMonitor();
        var clock = new CaptureTestClock();
        var port = FreePort();
        var sink = new TestSink();
        await using var core = new CoreController(new CoreOptions(TcpPort: port, DiscoveryPort: 0, DeviceId: TrustTestSupport.WindowsId), sink, sessionMonitor: monitor, trustStore: MemoryTrustStore.Trusted(), timeProvider: clock);
        await core.StartAsync();
        using var client = await ConnectAsync(port);
        var stream = client.GetStream();
        monitor.Emit(true);
        Assert.Equal(MessageType.Start, (await ReadAsync(stream)).Type);
        var takeover = core.DispatchAsync("START");
        monitor.Emit(false);
        clock.Advance(AutoCapturePolicy.ListenerDebounce);
        monitor.Emit(false);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 3, new StatusPayload("streaming")));
        Assert.True((await takeover.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
        clock.Advance(AutoCapturePolicy.StopDelay - AutoCapturePolicy.ListenerDebounce);
        monitor.Emit(false);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Ping, 4, new CommandPayload("PING")));
        Assert.Equal(MessageType.Status, (await ReadAsync(stream)).Type);
        Assert.Equal(CoreState.Streaming, core.Snapshot.State);
        Assert.True(sink.IsOpen);
        Assert.Equal(1, sink.Opens);
    }

    [Fact]
    public async Task InitialIdleStatusDoesNotRejectAutomaticStartConfirmation()
    {
        var monitor = new ManualSessionMonitor();
        var port = FreePort();
        var sink = new TestSink();
        await using var core = new CoreController(new CoreOptions(TcpPort: port, DiscoveryPort: 0), sink,
            sessionMonitor: monitor, trustStore: MemoryTrustStore.Trusted());
        await core.StartAsync();
        monitor.Emit(true);
        using var phone = await ConnectAsync(port);
        var stream = phone.GetStream();
        Assert.Equal(MessageType.Start, (await ReadAsync(stream)).Type);
        var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.SnapshotChanged += (_, value) => { if (value.State == CoreState.Streaming) streaming.TrySetResult(); };
        await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 3, new StatusPayload("idle")));
        await WriteAsync(stream, FrameCodec.Json(MessageType.Ping, 4, new CommandPayload("PING")));
        Assert.Equal(MessageType.Status, (await ReadAsync(stream)).Type);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 5, new StatusPayload("streaming")));
        await streaming.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(sink.IsOpen);
        Assert.Equal(1, sink.Opens);
        Assert.Equal(CoreState.Streaming, core.Snapshot.State);
    }

    [Fact]
    public async Task DisposalStopsMonitorAndIsIdempotent()
    {
        var monitor = new ManualSessionMonitor();
        var core = new CoreController(new CoreOptions(TcpPort: FreePort(), DiscoveryPort: 0), new TestSink(), sessionMonitor: monitor, trustStore: new MemoryTrustStore());
        await core.StartAsync();
        monitor.Emit(true);
        await Task.WhenAll(core.DisposeAsync().AsTask(), core.DisposeAsync().AsTask());
        Assert.Equal(1, monitor.Disposals);
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var hello = await TrustTestSupport.SendHelloAsync(stream);
        await TrustTestSupport.AuthenticateAsync(stream, hello);
        return client;
    }

    private static async Task<Frame> ReadAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        return await FrameCodec.ReadAsync(stream, timeout.Token);
    }

    private static async Task WriteAsync(Stream stream, Frame frame) => await stream.WriteAsync(FrameCodec.Encode(frame));

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class TestSink : IAudioSink
    {
        public string Name => "test";
        public bool IsOpen { get; private set; }
        public int Opens { get; private set; }
        public bool FailWrites { get; init; }
        public void Open() { Opens++; IsOpen = true; }
        public void Write(ReadOnlySpan<byte> data) { if (FailWrites) throw new IOException("sink write failed"); }
        public void Close() => IsOpen = false;
        public void Dispose() => Close();
    }
}

internal sealed class ManualSessionMonitor : ICaptureSessionMonitor
{
    private Action<bool>? observe;
    public int Disposals { get; private set; }
    public void Start(Action<bool> callback) => observe = callback;
    public void Emit(bool active) => observe?.Invoke(active);
    public ValueTask DisposeAsync() { Disposals++; observe = null; return ValueTask.CompletedTask; }
}

internal sealed class CaptureTestClock : TimeProvider
{
    private long timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref timestamp);
    public void Advance(TimeSpan elapsed) => Interlocked.Add(ref timestamp, elapsed.Ticks);
}
