using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using MicBridge.Core.Control;
using MicBridge.Core.Protocol;

namespace MicBridge.Core.Tests;

public sealed class CoreControllerAudioTests
{
    [Theory]
    [InlineData("tcp")]
    [InlineData("udp")]
    public async Task CurrentAudioWritesToOpenSinkBeforeStreamingAcknowledgement(string transport)
    {
        await using var session = await AudioSession.CreateAsync(transport);
        session.StartConsumer();
        var starting = session.Core.DispatchAsync("START");
        Assert.Equal(MessageType.Start, (await TrustTestSupport.ReadAsync(session.Stream)).Type);
        Assert.Equal(CoreState.Starting, session.Core.Snapshot.State);
        Assert.True(session.Sink.IsOpen);
        byte[] pcm = [1, 2, 3, 4];
        await session.SendAudioAsync(pcm);
        Assert.Equal(pcm, await session.Sink.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(starting.IsCompleted);
        Assert.Equal(CoreState.Starting, session.Core.Snapshot.State);
        await session.AcknowledgeAsync();
        Assert.True((await starting.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
    }

    [Theory]
    [InlineData("tcp")]
    [InlineData("udp")]
    public async Task PreviousCaptureQueuedAudioCannotWriteIntoReopenedSink(string transport)
    {
        await using var session = await AudioSession.CreateAsync(transport);
        var first = session.Core.DispatchAsync("START");
        Assert.Equal(MessageType.Start, (await TrustTestSupport.ReadAsync(session.Stream)).Type);
        await session.AcknowledgeAsync();
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
        await session.SendAudioAsync([11, 12]);
        await session.WaitForQueuedAudioAsync();
        Assert.False(session.Sink.Writes.Reader.TryRead(out _));
        Assert.True((await session.Core.DispatchAsync("STOP")).Ok);
        Assert.Equal(MessageType.Stop, (await TrustTestSupport.ReadAsync(session.Stream)).Type);
        Assert.False(session.Sink.IsOpen);

        var second = session.Core.DispatchAsync("START");
        Assert.Equal(MessageType.Start, (await TrustTestSupport.ReadAsync(session.Stream)).Type);
        Assert.Equal(2, session.Sink.Opens);
        // Schedule the real consumer only after STOP/Open. The old frame arrived through
        // the real TCP/UDP receiver and remains queued with its original capture identity.
        session.StartConsumer();
        byte[] current = [21, 22];
        await session.SendAudioAsync(current);
        Assert.Equal(current, await session.Sink.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(session.Sink.Writes.Reader.TryRead(out _));
        await session.AcknowledgeAsync();
        Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
    }

    [Theory]
    [InlineData("tcp")]
    [InlineData("udp")]
    public async Task StopAndReopenWaitForInFlightSinkWrite(string transport)
    {
        await using var session = await AudioSession.CreateAsync(transport);
        session.StartConsumer();
        var first = session.Core.DispatchAsync("START");
        Assert.Equal(MessageType.Start, (await TrustTestSupport.ReadAsync(session.Stream)).Type);
        await session.AcknowledgeAsync();
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
        using var release = new ManualResetEventSlim();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Sink.BeforeWrite = () =>
        {
            // A sink may query controller state on another thread. connectionGate must be free.
            Assert.True(Task.Run(() => session.Core.Snapshot).Wait(TimeSpan.FromSeconds(2)));
            writing.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        try
        {
            await session.SendAudioAsync([31, 32]);
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var stop = Task.Run(() => session.Core.DispatchAsync("STOP"));
            Assert.Equal(MessageType.Stop, (await TrustTestSupport.ReadAsync(session.Stream)).Type);
            var restart = session.Core.DispatchAsync("START");
            Assert.False(stop.IsCompleted);
            Assert.False(restart.IsCompleted);
            Assert.True(session.Sink.IsOpen);
            Assert.Equal(1, session.Sink.Opens);
            release.Set();
            Assert.True((await stop.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
            Assert.Equal(MessageType.Start, (await TrustTestSupport.ReadAsync(session.Stream)).Type);
            Assert.Equal(2, session.Sink.Opens);
            Assert.Equal(new byte[] { 31, 32 }, await session.Sink.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            await session.AcknowledgeAsync();
            Assert.True((await restart.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task ConfirmationCannotRepublishStreamingAfterStop()
    {
        await using var session = await AudioSession.CreateAsync("tcp");
        session.Sink.Open();
        session.SetField("snapshot", session.Core.Snapshot with { State = CoreState.Starting });
        var confirmation = new TaskCompletionSource<bool>();
        session.SetField("captureStartConfirmation", confirmation);
        var stopped = confirmation.Task.ContinueWith(_ => session.Core.DispatchAsync("STOP").GetAwaiter().GetResult(),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await session.AcknowledgeAsync();
        await stopped.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MessageType.Stop, (await TrustTestSupport.ReadAsync(session.Stream)).Type);
        await TrustTestSupport.WriteAsync(session.Stream, FrameCodec.Json(MessageType.Ping, 99, new CommandPayload("PING")));
        Assert.Equal(MessageType.Status, (await TrustTestSupport.ReadAsync(session.Stream)).Type);
        Assert.Equal(CoreState.ConnectedIdle, session.Core.Snapshot.State);
        Assert.False(session.Sink.IsOpen);
    }

    [Fact]
    public async Task OversizedUdpLengthDoesNotStopSubsequentAudio()
    {
        await using var session = await AudioSession.CreateAsync("udp");
        session.StartConsumer();
        var starting = session.Core.DispatchAsync("START");
        Assert.Equal(MessageType.Start, (await TrustTestSupport.ReadAsync(session.Stream)).Type);
        await session.AcknowledgeAsync();
        Assert.True((await starting.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
        var malformed = FrameCodec.Encode(new Frame(MessageType.Audio, 7, []));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(malformed.AsSpan(12, 4), 0x80000000);
        await session.SendDatagramAsync(malformed);
        await session.SendAudioAsync([41, 42]);
        Assert.Equal(new byte[] { 41, 42 }, await session.Sink.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private sealed class AudioSession : IAsyncDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly TcpClient phone;
        private readonly UdpClient sender = new();
        private readonly CancellationToken token;
        private readonly string transport;
        private readonly int udpPort;
        public CoreController Core { get; }
        public RecordingSink Sink { get; }
        public NetworkStream Stream => phone.GetStream();

        private AudioSession(CoreController core, RecordingSink sink, TcpClient phone, string transport, int udpPort)
        {
            Core = core;
            Sink = sink;
            this.phone = phone;
            this.transport = transport;
            this.udpPort = udpPort;
            token = ((CancellationTokenSource)GetField("lifetime")!).Token;
        }

        public static async Task<AudioSession> CreateAsync(string transport)
        {
            var sink = new RecordingSink();
            var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
            var core = new CoreController(new CoreOptions(AudioTransport: transport, UdpPort: port, DiscoveryPort: 0),
                sink, sessionMonitor: new ManualSessionMonitor(), trustStore: MemoryTrustStore.Trusted());
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var phone = new TcpClient();
            await phone.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await listener.AcceptTcpClientAsync();
            var session = new AudioSession(core, sink, phone, transport, port);
            // Start production network handlers independently of the consumer to make
            // queue retention deterministic, without adding production-only test hooks.
            session.SetField("udp", receiver);
            session.SetField("udpTask", session.Invoke("ReceiveUdpAsync", session.token));
            session.Invoke("TrackClient", server, session.token, false, null);
            var hello = await TrustTestSupport.SendHelloAsync(session.Stream,
                TrustTestSupport.Hello() with { AudioTransports = [transport] });
            await TrustTestSupport.AuthenticateAsync(session.Stream, hello);
            Assert.Equal(transport, core.Snapshot.AudioTransport);
            return session;
        }

        public void StartConsumer() => SetField("audioTask", Invoke("ConsumeAudioAsync", token));
        public Task AcknowledgeAsync() => TrustTestSupport.WriteAsync(Stream,
            FrameCodec.Json(MessageType.Status, 5, new StatusPayload("streaming")));
        public async Task SendAudioAsync(byte[] pcm)
        {
            var frame = new Frame(MessageType.Audio, 6, pcm);
            if (transport == "tcp") await TrustTestSupport.WriteAsync(Stream, frame);
            else await sender.SendAsync(FrameCodec.Encode(frame), new IPEndPoint(IPAddress.Loopback, udpPort));
        }
        public async Task SendDatagramAsync(byte[] bytes) =>
            await sender.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, udpPort));
        public async Task WaitForQueuedAudioAsync()
        {
            var channel = GetField("audio")!;
            var reader = channel.GetType().GetProperty("Reader")!.GetValue(channel)!;
            var count = reader.GetType().GetProperty("Count")!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while ((int)count.GetValue(reader)! == 0) await Task.Delay(10, deadline.Token);
        }
        private object? GetField(string name) => typeof(CoreController).GetField(name, PrivateInstance)!.GetValue(Core);
        public void SetField(string name, object? value) => typeof(CoreController).GetField(name, PrivateInstance)!.SetValue(Core, value);
        private object? Invoke(string name, params object?[] args) => typeof(CoreController).GetMethod(name, PrivateInstance)!.Invoke(Core, args);
        public async ValueTask DisposeAsync()
        {
            phone.Dispose();
            sender.Dispose();
            await Core.DisposeAsync();
        }
    }

    private sealed class RecordingSink : IAudioSink
    {
        public string Name => "recording sink";
        public bool IsOpen { get; private set; }
        public int Opens { get; private set; }
        public Channel<byte[]> Writes { get; } = Channel.CreateUnbounded<byte[]>();
        public Action? BeforeWrite { get; set; }
        public void Open() { IsOpen = true; Opens++; }
        public void Write(ReadOnlySpan<byte> data)
        {
            BeforeWrite?.Invoke();
            Assert.True(IsOpen);
            Writes.Writer.TryWrite(data.ToArray());
        }
        public void Close() => IsOpen = false;
        public void Dispose() => Close();
    }
}
