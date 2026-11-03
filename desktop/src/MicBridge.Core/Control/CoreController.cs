using System.Threading.Channels;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using MicBridge.Core.Discovery;
using MicBridge.Core.Protocol;

namespace MicBridge.Core.Control;

public sealed record CoreOptions(
    int TcpPort = 17_891,
    int UdpPort = 17_892,
    string ServiceName = "MicBridge",
    string AudioTransport = "tcp",
    string? DeviceId = null,
    int DiscoveryPort = UdpDiscovery.DefaultPort,
    TimeSpan? PairingTimeout = null);

public sealed partial class CoreController : IAsyncDisposable
{
    private readonly CoreOptions options;
    private readonly IAudioSink sink;
    private readonly Action<string> log;
    private sealed record QueuedAudio(byte[] Data, long Generation, TcpClient Client, NetworkStream Stream);
    private readonly object sinkGate = new();
    private long captureGeneration;
    private bool acceptingAudio;
    private readonly Channel<QueuedAudio> audio = Channel.CreateBounded<QueuedAudio>(new BoundedChannelOptions(32)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly MdnsAdvertisement advertisement = new();
    private readonly UdpDiscovery udpDiscovery;
    private readonly MdnsBrowser browser;
    private readonly DiscoveryPeerMerger peerMerger = new();
    private readonly ICaptureSessionMonitor sessionMonitor;
    private readonly AutoCapturePolicy autoCapture = new();
    private readonly object autoCaptureGate = new();
    private CancellationTokenSource? automaticStartWait;
    private readonly long autoCaptureStarted;
    private TimeSpan AutoCaptureElapsed => clock.GetElapsedTime(autoCaptureStarted);
    private readonly Channel<bool> listenerUpdates = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private Task? autoCaptureTask;
    private bool listenerActive;
    private bool disconnectedListenerLogged;
    private bool autoConnectionReady;
    private readonly object disposeGate = new();
    private Task? disposeTask;
    private readonly object connectionGate = new();
    private readonly object clientTasksGate = new();
    private readonly HashSet<Task> clientTasks = [];
    private readonly SemaphoreSlim handshakeSlots = new(4, 4);
    private readonly string deviceId;
    private TcpListener? listener;
    private UdpClient? udp;
    private TcpClient? android;
    private NetworkStream? androidStream;
    private IPAddress? androidAddress;
    private string peerKind = "";
    private string peerDeviceId = "";
    private string peerConnectionId = "";
    private string localConnectionId = "";
    private Task? acceptTask;
    private Task? audioTask;
    private Task? udpTask;
    private CoreSnapshot snapshot = CoreSnapshot.Initial;
    private uint sequence;
    private string selectedTransport = FrameCodec.AudioTransportTcp;
    private long dropped;
    private readonly object pairingGate = new();
    private TaskCompletionSource<bool>? captureStartConfirmation;
    public CoreController(CoreOptions options, IAudioSink sink, Action<string>? log = null, ICaptureSessionMonitor? sessionMonitor = null,
        IDeviceTrustStore? trustStore = null, TimeProvider? timeProvider = null)
    {
        this.options = options;
        this.sink = sink;
        this.log = log ?? (_ => { });
        this.trustStore = trustStore ?? new DpapiDeviceTrustStore();
        clock = timeProvider ?? TimeProvider.System;
        autoCaptureStarted = clock.GetTimestamp();
        try { trustState = this.trustStore.Load(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            trustStoreFailed = true;
            this.log("Device trust could not be loaded; authentication is disabled");
            trustState = new DeviceTrustState(Guid.NewGuid().ToString("N"), null);
        }
        deviceId = string.IsNullOrWhiteSpace(options.DeviceId) ? trustState.InstallationId : options.DeviceId;
        if (!DpapiDeviceTrustStore.IsIdentity(deviceId)) throw new ArgumentException("DeviceId must be a 32 character lowercase hexadecimal UUID", nameof(options));
        browser = new MdnsBrowser(peer => { if (!peer.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)) peerMerger.Observe("mdns", peer, merged => DiscoveredPeersChanged?.Invoke(this, merged)); }, this.log,
            peer => { if (!peer.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)) peerMerger.Remove("mdns", peer, merged => DiscoveredPeersChanged?.Invoke(this, merged), removed => DiscoveredPeerRemoved?.Invoke(this, removed)); });
        udpDiscovery = new UdpDiscovery(peer => { if (!peer.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)) peerMerger.Observe("udp", peer, merged => DiscoveredPeersChanged?.Invoke(this, merged)); }, this.log, options.DiscoveryPort,
            peer => { if (!peer.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)) peerMerger.Remove("udp", peer, merged => DiscoveredPeersChanged?.Invoke(this, merged), removed => DiscoveredPeerRemoved?.Invoke(this, removed)); });
        this.sessionMonitor = sessionMonitor ?? new CableSessionMonitor(this.log);
    }

    public string DeviceId => deviceId;
    public TrustedDevice? TrustedDevice { get { lock (pairingGate) return trustStoreFailed ? null : trustState.Device?.PublicDevice; } }
    public PendingPairing? PendingPairing { get { lock (pairingGate) return pendingPairing; } }
    public string TrustState { get { lock (pairingGate) return trustStoreFailed ? "repair_required" : pendingPairing is not null ? "pairing" : trustState.Device is not null ? "trusted" : "unpaired"; } }
    public event EventHandler<PendingPairing>? PairingRequested;

    public event EventHandler<CoreSnapshot>? SnapshotChanged;
    public event EventHandler<DiscoveredPeer>? DiscoveredPeersChanged;
    public event EventHandler<DiscoveredPeer>? DiscoveredPeerRemoved;

    public CoreSnapshot Snapshot
    {
        get
        {
            lock (connectionGate) return snapshot;
        }
    }
    public Task ConnectAsync(DiscoveredPeer peer, CancellationToken cancellationToken = default) =>
        ConnectAsync(peer.Host, peer.Port, peer.DeviceId, cancellationToken);

    public async Task ConnectAsync(string host, int port, string? remoteDeviceId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(host));
        var client = new TcpClient();
        var handedOff = false;
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            TrackClient(client, lifetime.Token, outgoing: true, expectedDeviceId: remoteDeviceId);
            handedOff = true;
        }
        finally
        {
            if (!handedOff) client.Dispose();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        listener = new TcpListener(IPAddress.Any, options.TcpPort);
        listener.Start();
        acceptTask = AcceptLoopAsync(lifetime.Token);
        audioTask = ConsumeAudioAsync(lifetime.Token);
        if (string.Equals(options.AudioTransport, FrameCodec.AudioTransportUdp, StringComparison.OrdinalIgnoreCase))
        {
            udp = new UdpClient(options.UdpPort);
            udpTask = ReceiveUdpAsync(lifetime.Token);
        }

        try
        {
            advertisement.Start(options.ServiceName, options.TcpPort, deviceId);
        }
        catch (Exception error)
        {
            log($"mDNS advertisement unavailable: {error.Message}");
        }
        try
        {
            browser.Start();
        }
        catch (Exception error)
        {
            log($"mDNS browser unavailable: {error.Message}");
        }
        try
        {
            udpDiscovery.Start(options.ServiceName, deviceId, options.TcpPort, [], ["windows_audio"], [FrameCodec.AudioTransportTcp, FrameCodec.AudioTransportUdp]);
        }
        catch (Exception error)
        {
            log($"UDP discovery unavailable: {error.Message}");
        }
        autoCaptureTask = Task.Run(() => AutoCaptureLoopAsync(lifetime.Token));
        sessionMonitor.Start(ObserveListener);
        Publish(snapshot with { AudioDevice = sink.Name, AudioTransport = CurrentTransport(), AudioPort = CurrentAudioPort() });
    }

    public async Task<ResponsePayload> DispatchAsync(string command, CancellationToken cancellationToken = default)
    {
        return command.ToUpperInvariant() switch
        {
            "START" => await StartCaptureAsync(cancellationToken),
            "STOP" => await StopCaptureAsync(cancellationToken, notifyAndroid: true),
            "STATUS" or "PING" => new ResponsePayload(true, Snapshot.State.ToWire(), Status: BuildStatus()),
            _ => new ResponsePayload(false, Snapshot.State.ToWire(), $"unknown command: {command}", BuildStatus()),
        };
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate) return new ValueTask(disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        DisablePairingApproval();
        lifetime.Cancel();
        await sessionMonitor.DisposeAsync();
        listenerUpdates.Writer.TryComplete();
        if (autoCaptureTask is not null) await autoCaptureTask;
        browser.Dispose();
        udpDiscovery.Dispose();
        advertisement.Dispose();
        listener?.Stop();
        udp?.Dispose();
        CloseAndroid();
        await StopCaptureAsync(CancellationToken.None, notifyAndroid: false);
        try { if (acceptTask is not null) await acceptTask; } catch (OperationCanceledException) { }
        try { if (audioTask is not null) await audioTask; } catch (OperationCanceledException) { }
        try { if (udpTask is not null) await udpTask; } catch (OperationCanceledException) { }
        Task[] handlers;
        lock (clientTasksGate) handlers = clientTasks.ToArray();
        await Task.WhenAll(handlers);
        sink.Dispose();
        lifetime.Dispose();
        stateGate.Dispose();
        writeGate.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener!.AcceptTcpClientAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            TrackClient(client, cancellationToken);
        }
    }

    private void TrackClient(TcpClient client, CancellationToken cancellationToken, bool outgoing = false, string? expectedDeviceId = null)
    {
        lock (clientTasksGate)
        {
            if (cancellationToken.IsCancellationRequested) { client.Dispose(); return; }
            var task = HandleClientAsync(client, cancellationToken, outgoing, expectedDeviceId);
            clientTasks.Add(task);
            _ = task.ContinueWith(completed => { lock (clientTasksGate) clientTasks.Remove(completed); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken,
        bool outgoing = false,
        string? expectedDeviceId = null)
    {
        using var ownedClient = client;
        if (!handshakeSlots.Wait(0)) return;
        var ownsHandshakeSlot = true;
        var stream = client.GetStream();
        var localConnection = Guid.NewGuid().ToString("N");
        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        var handshakeCancellationToken = handshakeTimeout.Token;
        try
        {
            if (outgoing)
            {
                await WriteAsync(stream, FrameCodec.Json(MessageType.Hello, 1, new HelloPayload(
                    "windows", options.ServiceName, FrameCodec.AudioRate, FrameCodec.AudioBits, FrameCodec.AudioChannels,
                    [FrameCodec.AudioTransportTcp, FrameCodec.AudioTransportUdp], deviceId, localConnection,
                    [], ["windows_audio"], "device_trust_v1", false, TrustedDevice?.PairingId)), handshakeCancellationToken);
            }
            var helloFrame = await FrameCodec.ReadAsync(stream, handshakeCancellationToken);
            if (helloFrame.Type != MessageType.Hello)
            {
                await WriteAsync(stream, FrameCodec.Json(MessageType.Error, helloFrame.Sequence, new ErrorPayload("handshake_required", "first frame must be HELLO")), handshakeCancellationToken);
                return;
            }

            var hello = FrameCodec.ReadJson<HelloPayload>(helloFrame);
            try { ValidateHello(hello, expectedDeviceId); }
            catch (InvalidDataException error)
            {
                await WriteAsync(stream, FrameCodec.Json(MessageType.Error, helloFrame.Sequence, new ErrorPayload("hello_rejected", error.Message)), handshakeCancellationToken);
                return;
            }
            var hasExtendedHello = !string.IsNullOrWhiteSpace(hello.DeviceId) || !string.IsNullOrWhiteSpace(hello.ConnectionId);
            if (!outgoing && hasExtendedHello)
            {
                await WriteAsync(stream, FrameCodec.Json(MessageType.Hello, helloFrame.Sequence, new HelloPayload(
                    "windows", options.ServiceName, FrameCodec.AudioRate, FrameCodec.AudioBits, FrameCodec.AudioChannels,
                    [FrameCodec.AudioTransportTcp, FrameCodec.AudioTransportUdp], deviceId, localConnection,
                    [], ["windows_audio"], "device_trust_v1", false, TrustedDevice?.PairingId)), handshakeCancellationToken);
            }
            long authenticatedEpoch;
            try
            {
                authenticatedEpoch = await AuthenticateTrustAsync(client, stream, hello, localConnection, handshakeTimeout, handshakeCancellationToken, outgoing);
            }
            catch (AuthenticationException error)
            {
                if (!IsAndroidConnected()) Publish(Snapshot with { State = CoreState.Error, Detail = $"{error.Code}: {error.Message}" });
                await WriteAsync(stream, FrameCodec.Json(MessageType.Error, helloFrame.Sequence, new ErrorPayload(error.Code, error.Message)), handshakeCancellationToken);
                return;
            }

            handshakeTimeout.Cancel();
            handshakeTimeout.Dispose();
            handshakeSlots.Release();
            ownsHandshakeSlot = false;

            var peerAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            var candidateKey = ConnectionKey(deviceId, hello.DeviceId ?? hello.Peer, localConnection, hello.ConnectionId!);
            await stateGate.WaitAsync(cancellationToken);
            try
            {
                lock (pairingGate)
                {
                    if (authenticatedEpoch != trustEpoch || trustStoreFailed || forgetting) return;
                    lock (connectionGate)
                    {
                        if (android is not null)
                        {
                            var currentKey = ConnectionKey(deviceId, peerDeviceId, localConnectionId, peerConnectionId);
                            if (string.CompareOrdinal(candidateKey, currentKey) >= 0) return;
                            android.Close();
                        }
                        android = client;
                        autoConnectionReady = false;
                        androidStream = stream;
                        androidAddress = peerAddress;
                        peerKind = hello.Peer.ToLowerInvariant();
                        peerDeviceId = hello.DeviceId!;
                        peerConnectionId = hello.ConnectionId!;
                        localConnectionId = localConnection;
                        selectedTransport = SelectTransport(hello.AudioTransports);
                    }
                }
                CloseSink();
                SetConnectedUnderGate(true);
            }
            finally { stateGate.Release(); }
            if (!IsCurrentConnection(client, stream)) return;
            await WriteAsync(stream, FrameCodec.Json(MessageType.Status, helloFrame.Sequence, BuildStatus()), cancellationToken);
            lock (connectionGate)
            {
                if (ReferenceEquals(android, client)) autoConnectionReady = true;
            }
            listenerUpdates.Writer.TryWrite(true);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!IsCurrentConnection(client, stream)) break;
                var frame = await FrameCodec.ReadAsync(stream, cancellationToken);
                if (!IsCurrentConnection(client, stream)) break;
                await HandleFrameAsync(frame, client, stream, hello.Peer, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        catch (Exception error)
        {
            log($"peer connection ended: {error.Message}");
        }
        finally
        {
            if (ownsHandshakeSlot) handshakeSlots.Release();
            await DisconnectClientAsync(client, stream);
        }
    }

    private async Task DisconnectClientAsync(TcpClient client, NetworkStream stream)
    {
        if (!IsCurrentConnection(client, stream)) return;
        // A START waiter owns stateGate. Release only this socket's waiter before acquiring it.
        TrySetCaptureConfirmation(client, stream, false);
        await stateGate.WaitAsync();
        try
        {
            lock (connectionGate)
            {
                // Ownership may have changed while waiting: stale readers cannot close the new sink.
                if (!ReferenceEquals(android, client) || !ReferenceEquals(androidStream, stream)) return;
                captureStartConfirmation = null;
                android = null;
                androidStream = null;
                androidAddress = null;
                autoConnectionReady = false;
                peerKind = "";
                peerDeviceId = "";
                peerConnectionId = "";
                localConnectionId = "";
            }
            CloseSink();
            SetConnectedUnderGate(false);
        }
        finally { stateGate.Release(); }
        listenerUpdates.Writer.TryWrite(true);
    }


    private static void ValidateAuthField(string value, string field)
    {
        if (!DpapiDeviceTrustStore.IsIdentity(value))
            throw new AuthenticationException("auth_malformed", $"authentication {field} is invalid");
    }

    private sealed class AuthenticationException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private async Task HandleFrameAsync(Frame frame, TcpClient client, NetworkStream stream, string peer, CancellationToken cancellationToken)
    {
        if (!IsCurrentConnection(client, stream)) return;
        switch (frame.Type)
        {
            case MessageType.Audio when peer == "android" && CurrentTransport() == FrameCodec.AudioTransportTcp:
                EnqueueAudio(frame.Data, client, stream);
                break;
            case MessageType.Ping:
                if (!IsCurrentConnection(client, stream)) return;
                await WriteAsync(stream, FrameCodec.Json(MessageType.Status, frame.Sequence, BuildStatus()), cancellationToken);
                break;
            case MessageType.Status:
                var status = FrameCodec.ReadJson<StatusPayload>(frame);
                if (!IsCurrentConnection(client, stream)) return;
                if (status.State.Equals("error", StringComparison.OrdinalIgnoreCase))
                {
                    PublishIfCurrent(client, stream, Snapshot with { Detail = $"{status.ErrorCode ?? "android_audio_error"}: {status.Detail ?? "Android audio error"}" });
                    TrySetCaptureConfirmation(client, stream, false);
                    if (!IsCurrentConnection(client, stream)) return;
                    await FailAsync($"{status.ErrorCode ?? "android_audio_error"}: {status.Detail ?? "Android audio error"}", client, stream);
                }
                else
                {
                    if (status.State.Equals("streaming", StringComparison.OrdinalIgnoreCase))
                    {
                        TrySetCaptureConfirmation(client, stream, true);
                        break;
                    }
                    PublishIfCurrent(client, stream, Snapshot with
                    {
                        AndroidPeer = status.Device ?? Snapshot.AndroidPeer,
                        Detail = Snapshot.State == CoreState.Error ? Snapshot.Detail : status.Detail ?? "",
                    });
                }
                break;
            case MessageType.Error:
                if (!IsCurrentConnection(client, stream)) return;
                var error = FrameCodec.ReadJson<ErrorPayload>(frame);
                PublishIfCurrent(client, stream, Snapshot with { Detail = $"{error.Code}: {error.Message}" });
                TrySetCaptureConfirmation(client, stream, false);
                if (!IsCurrentConnection(client, stream)) return;
                await FailAsync($"{error.Code}: {error.Message}", client, stream);
                break;
            case MessageType.Start:
                _ = DispatchForConnectionAsync("START", client, stream, cancellationToken);
                break;
            case MessageType.Stop:
                _ = DispatchForConnectionAsync("STOP", client, stream, cancellationToken);
                break;
        }
    }

    private async Task DispatchForConnectionAsync(string command, TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
    {
        if (!IsCurrentConnection(client, stream)) return;
        try
        {
            if (command == "START")
                await StartCaptureAsync(cancellationToken, client, stream);
            else
                await StopCaptureAsync(cancellationToken, notifyAndroid: true, expectedClient: client, expectedStream: stream);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (IsCurrentConnection(client, stream))
        {
            await FailAsync(error.Message, client, stream);
        }
    }

    private async Task<ResponsePayload> StartCaptureAsync(
        CancellationToken cancellationToken,
        TcpClient? expectedClient = null,
        NetworkStream? expectedStream = null,
        bool automatic = false)
    {
        if (!automatic) lock (autoCaptureGate) autoCapture.ManualOverride();
        if (!IsExpectedConnection(expectedClient, expectedStream) || !IsAndroidConnected()) return new ResponsePayload(false, CoreState.Disconnected.ToWire(), "Android control connection is not available", BuildStatus());
        await stateGate.WaitAsync(cancellationToken);
        CancellationTokenSource? startWait = null;
        try
        {
            if (!IsExpectedConnection(expectedClient, expectedStream)) return new ResponsePayload(false, CoreState.Disconnected.ToWire(), "Android control connection is not available", BuildStatus());
            if (automatic)
            {
                var connected = IsAndroidConnected();
                var idle = Snapshot.State is CoreState.ConnectedIdle or CoreState.Error;
                bool start;
                lock (autoCaptureGate)
                {
                    start = autoCapture.TryStart(connected, idle);
                    if (start)
                    {
                        startWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        automaticStartWait = startWait;
                    }
                }
                if (!start) return new ResponsePayload(true, Snapshot.State.ToWire(), Status: BuildStatus());
            }
            if (Snapshot.State is CoreState.Streaming or CoreState.Starting)
            {
                return new ResponsePayload(true, Snapshot.State.ToWire(), "already streaming", BuildStatus());
            }
            Publish(Snapshot with { State = CoreState.Starting, Detail = "" });
            try
            {
                lock (sinkGate)
                {
                    lock (connectionGate) { acceptingAudio = false; captureGeneration++; }
                    sink.Open();
                }
            }
            catch (Exception error)
            {
                Publish(Snapshot with { State = CoreState.Error, Detail = error.Message, AudioDevice = sink.Name });
                return new ResponsePayload(false, CoreState.Error.ToWire(), error.Message, BuildStatus());
            }

            var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            captureStartConfirmation = confirmation;
            try
            {
                await SendAndroidAsync(MessageType.Start, new CommandPayload("START"), cancellationToken, expectedClient, expectedStream);
                bool confirmed;
                var waitStarted = clock.GetTimestamp();
                while (true)
                {
                    var remaining = TimeSpan.FromSeconds(5) - clock.GetElapsedTime(waitStarted);
                    if (remaining <= TimeSpan.Zero) throw new TimeoutException("Android did not confirm audio capture");
                    try
                    {
                        confirmed = await confirmation.Task.WaitAsync(remaining, clock, startWait?.Token ?? cancellationToken);
                        break;
                    }
                    catch (OperationCanceledException) when (startWait?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
                    {
                        // Already holding stateGate: stop before a queued manual START can run.
                        // Ownership/activity may have changed since the monitor cancelled the wait.
                        var stopped = await StopCaptureUnderGateAsync(cancellationToken, true, expectedClient, expectedStream, automatic: true);
                        if (Snapshot.State is not (CoreState.Starting or CoreState.Streaming)) return stopped;
                        lock (autoCaptureGate)
                        {
                            startWait!.Dispose();
                            startWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            automaticStartWait = startWait;
                        }
                    }
                }
                if (!confirmed)
                {
                    var detail = string.IsNullOrWhiteSpace(Snapshot.Detail) ? "Android did not confirm audio capture" : Snapshot.Detail;
                    CloseSink();
                    if (IsExpectedConnection(expectedClient, expectedStream)) Publish(Snapshot with { State = CoreState.Error, Detail = detail });
                    return new ResponsePayload(false, CoreState.Error.ToWire(), detail, BuildStatus());
                }
            }
            catch (Exception error)
            {
                captureStartConfirmation = null;
                CloseSink();
                if (IsExpectedConnection(expectedClient, expectedStream)) Publish(Snapshot with { State = CoreState.Error, Detail = error.Message });
                await StopAndroidAfterFailureAsync(expectedClient, expectedStream);
                return new ResponsePayload(false, CoreState.Error.ToWire(), error.Message, BuildStatus());
            }
            finally { captureStartConfirmation = null; }
            if (IsExpectedConnection(expectedClient, expectedStream))
                Publish(Snapshot with { State = CoreState.Streaming, AudioDevice = sink.Name });
            return new ResponsePayload(true, CoreState.Streaming.ToWire(), Status: BuildStatus());
        }
        finally
        {
            var captureFailed = Snapshot.State == CoreState.Error;
            lock (autoCaptureGate)
            {
                if (captureFailed) autoCapture.CaptureFailed();
                if (ReferenceEquals(automaticStartWait, startWait)) automaticStartWait = null;
                startWait?.Dispose();
            }
            stateGate.Release();
        }
    }

    private async Task<ResponsePayload> StopCaptureAsync(
        CancellationToken cancellationToken,
        bool notifyAndroid,
        TcpClient? expectedClient = null,
        NetworkStream? expectedStream = null,
        bool automatic = false)
    {
        if (!automatic) lock (autoCaptureGate) autoCapture.ManualOverride();
        await stateGate.WaitAsync(cancellationToken);
        try { return await StopCaptureUnderGateAsync(cancellationToken, notifyAndroid, expectedClient, expectedStream, automatic); }
        finally { stateGate.Release(); }
    }

    // Caller owns stateGate, including the interrupted automatic START path.
    private async Task<ResponsePayload> StopCaptureUnderGateAsync(
        CancellationToken cancellationToken,
        bool notifyAndroid,
        TcpClient? expectedClient,
        NetworkStream? expectedStream,
        bool automatic)
    {
        if (!IsExpectedConnection(expectedClient, expectedStream))
            return new ResponsePayload(false, CoreState.Disconnected.ToWire(), "Android control connection is no longer current", BuildStatus());
        if (automatic)
        {
            if (Snapshot.State is not (CoreState.Starting or CoreState.Streaming))
                return new ResponsePayload(true, Snapshot.State.ToWire(), Status: BuildStatus());
            bool stop;
            lock (autoCaptureGate) stop = autoCapture.TryStop(AutoCaptureElapsed);
            if (!stop) return new ResponsePayload(true, Snapshot.State.ToWire(), Status: BuildStatus());
        }
        if (Snapshot.State is CoreState.Disconnected or CoreState.ConnectedIdle)
        {
            CloseSink();
            return new ResponsePayload(true, Snapshot.State.ToWire(), "already idle", BuildStatus());
        }
        // Ignore a late streaming acknowledgement once this capture has been stopped.
        lock (connectionGate) { captureStartConfirmation = null; acceptingAudio = false; }
        var preservedDetail = Snapshot.State == CoreState.Error ? Snapshot.Detail : "";
        Publish(Snapshot with { State = CoreState.Stopping, Detail = preservedDetail });
        if (notifyAndroid && IsAndroidConnected())
        {
            try { await SendAndroidAsync(MessageType.Stop, new CommandPayload("STOP"), cancellationToken, expectedClient, expectedStream); }
            catch (Exception error) { log($"Android STOP failed: {error.Message}"); }
        }
        CloseSink();
        var nextState = IsAndroidConnected() ? CoreState.ConnectedIdle : CoreState.Disconnected;
        var detail = !string.IsNullOrWhiteSpace(preservedDetail)
            ? preservedDetail
            : Snapshot.State == CoreState.Error ? Snapshot.Detail : "";
        Publish(Snapshot with { State = nextState, Detail = detail });
        return new ResponsePayload(true, nextState.ToWire(), Status: BuildStatus());
    }

    private void ObserveListener(bool active)
    {
        lock (autoCaptureGate)
        {
            autoCapture.Observe(active, AutoCaptureElapsed);
            Volatile.Write(ref listenerActive, autoCapture.ListenerActive);
            if (autoCapture.CanStop(AutoCaptureElapsed)) automaticStartWait?.Cancel();
        }
        CoreSnapshot? changed = null;
        lock (connectionGate)
        {
            if (snapshot.ListenerActive != Volatile.Read(ref listenerActive))
                snapshot = changed = snapshot with { ListenerActive = Volatile.Read(ref listenerActive) };
        }
        // Publish only the listener field; do not overwrite a concurrent capture transition.
        if (changed is not null) SnapshotChanged?.Invoke(this, changed);
        listenerUpdates.Writer.TryWrite(true);
    }

    private async Task AutoCaptureLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in listenerUpdates.Reader.ReadAllAsync(cancellationToken))
            {
                TcpClient? client;
                NetworkStream? stream;
                bool ready;
                lock (connectionGate) { client = android; stream = androidStream; ready = autoConnectionReady; }
                if (!IsAndroidConnected())
                {
                    if (Volatile.Read(ref listenerActive) && !disconnectedListenerLogged)
                        log("CABLE Output listener active; Android is not connected, automatic START skipped");
                    disconnectedListenerLogged = Volatile.Read(ref listenerActive);
                    continue;
                }
                disconnectedListenerLogged = false;
                if (client is null || stream is null || !ready) continue;
                await StartCaptureAsync(cancellationToken, client, stream, automatic: true);
                await StopCaptureAsync(cancellationToken, true, client, stream, automatic: true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { log($"Automatic capture stopped: {error.Message}"); }
    }

    private async Task ConsumeAudioAsync(CancellationToken cancellationToken)
    {
        await foreach (var item in audio.Reader.ReadAllAsync(cancellationToken))
        {
            Exception? failure = null;
            lock (sinkGate)
            {
                lock (connectionGate)
                {
                    if (!acceptingAudio || item.Generation != captureGeneration ||
                        !ReferenceEquals(item.Client, android) || !ReferenceEquals(item.Stream, androidStream)) continue;
                }
                // The sink is external code: never call it while holding connectionGate.
                // Keep validation and Write serialized with Close/Open across capture sessions.
                try { sink.Write(item.Data); }
                catch (Exception error) { failure = error; }
            }
            if (failure is not null)
                await FailAsync(failure.Message, item.Client, item.Stream, notifyAndroid: true, expectedGeneration: item.Generation);
        }
    }

    private async Task ReceiveUdpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp!.ReceiveAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            if (result.Buffer.Length > FrameCodec.MaxUdpPacket || CurrentTransport() != FrameCodec.AudioTransportUdp) continue;
            TcpClient? client;
            NetworkStream? stream;
            lock (connectionGate)
            {
                if (androidAddress is null || !androidAddress.Equals(result.RemoteEndPoint.Address)) continue;
                client = android;
                stream = androidStream;
            }
            try
            {
                var frame = FrameCodec.Decode(result.Buffer);
                if (frame.Type == MessageType.Audio && client is not null && stream is not null) EnqueueAudio(frame.Data, client, stream);
            }
            catch (InvalidDataException) { }
        }
    }

    private void EnqueueAudio(byte[] data, TcpClient client, NetworkStream stream)
    {
        if (data.Length == 0 || data.Length % 2 != 0 || data.Length > FrameCodec.MaxPayload) return;
        lock (connectionGate)
        {
            if (!acceptingAudio || !ReferenceEquals(android, client) || !ReferenceEquals(androidStream, stream)) return;
            if (audio.Writer.TryWrite(new QueuedAudio(data, captureGeneration, client, stream))) return;
        }
        var droppedCount = (ulong)Interlocked.Increment(ref dropped);
        Publish(Snapshot with { DroppedAudioFrames = droppedCount });
    }

    private void CloseSink()
    {
        lock (sinkGate)
        {
            lock (connectionGate) acceptingAudio = false;
            sink.Close();
        }
    }

    private async Task SendAndroidAsync<T>(
        MessageType type,
        T payload,
        CancellationToken cancellationToken,
        TcpClient? expectedClient = null,
        NetworkStream? expectedStream = null)
    {
        NetworkStream stream;
        lock (connectionGate)
        {
            if (expectedClient is not null && (!ReferenceEquals(android, expectedClient) || !ReferenceEquals(androidStream, expectedStream)))
                throw new InvalidOperationException("Android control connection is no longer current");
            stream = androidStream ?? throw new InvalidOperationException("Android control connection is not available");
        }
        var frame = FrameCodec.Json(type, ++sequence, payload);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            if (type == MessageType.Start)
            {
                // Open completed and writeGate is held. Begin the actual START write before
                // admitting audio; connectionGate prevents a fast reply racing this boundary.
                // The protocol has no capture_id. In particular, an old UDP datagram arriving
                // after the new START cannot be distinguished from current capture audio.
                Task sending;
                lock (connectionGate)
                {
                    sending = WriteAsync(stream, frame, cancellationToken);
                    acceptingAudio = true;
                }
                await sending;
            }
            else await WriteAsync(stream, frame, cancellationToken);
        }
        finally { writeGate.Release(); }
    }

    private static async Task WriteAsync(Stream stream, Frame frame, CancellationToken cancellationToken)
    {
        var bytes = FrameCodec.Encode(frame);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private void CloseAndroid()
    {
        TcpClient? client;
        lock (connectionGate)
        {
            client = android;
            android = null;
            androidStream = null;
            androidAddress = null;
            peerKind = "";
            peerDeviceId = "";
            peerConnectionId = "";
            localConnectionId = "";
        }
        client?.Close();
    }

    private async Task SetConnectedAsync(bool connected, CancellationToken cancellationToken, TcpClient? expectedClient = null, NetworkStream? expectedStream = null)
    {
        await stateGate.WaitAsync(cancellationToken);
        try
        {
            if (expectedClient is not null && !IsExpectedConnection(expectedClient, expectedStream)) return;
            SetConnectedUnderGate(connected);
        }
        finally { stateGate.Release(); }
        listenerUpdates.Writer.TryWrite(true);
    }

    // Caller holds stateGate, shared by admission and owner-checked disconnect cleanup.
    private void SetConnectedUnderGate(bool connected)
    {
        lock (autoCaptureGate) autoCapture.ConnectionChanged();
        Publish(Snapshot with
        {
            State = connected ? CoreState.ConnectedIdle : CoreState.Disconnected,
            Detail = connected ? "" : Snapshot.Detail,
            AndroidConnected = connected && peerKind == "android",
            AndroidPeer = connected && peerKind == "android" ? peerDeviceId : "",
            PeerConnected = connected,
            PeerName = connected ? peerDeviceId : "",
            AudioTransport = CurrentTransport(),
            AudioPort = CurrentAudioPort(),
        });
    }

    private async Task FailAsync(string detail, TcpClient? expectedClient = null, NetworkStream? expectedStream = null, bool notifyAndroid = false, long? expectedGeneration = null)
    {
        await stateGate.WaitAsync();
        try
        {
            if (!IsExpectedConnection(expectedClient, expectedStream)) return;
            lock (connectionGate) if (expectedGeneration is not null && expectedGeneration != captureGeneration) return;
            CloseSink();
            lock (autoCaptureGate) autoCapture.CaptureFailed();
            Publish(Snapshot with { State = CoreState.Error, Detail = detail });
            if (notifyAndroid) await StopAndroidAfterFailureAsync(expectedClient, expectedStream);
        }
        finally { stateGate.Release(); }
    }

    // The caller holds stateGate, so cleanup cannot stop a replacement capture.
    private async Task StopAndroidAfterFailureAsync(TcpClient? expectedClient, NetworkStream? expectedStream)
    {
        if (!IsExpectedConnection(expectedClient, expectedStream) || !IsAndroidConnected()) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try { await SendAndroidAsync(MessageType.Stop, new CommandPayload("STOP"), deadline.Token, expectedClient, expectedStream); }
        catch (Exception error) { log($"Android failure cleanup STOP failed: {error.GetType().Name}"); }
    }

    private StatusPayload BuildStatus()
    {
        var current = Snapshot;
        return new StatusPayload(current.State.ToWire(), current.AndroidPeer, current.AudioDevice, current.Detail, null, current.DroppedAudioFrames, CurrentTransport(), CurrentAudioPort() == 0 ? null : CurrentAudioPort(), current.ListenerActive);
    }

    private void Publish(CoreSnapshot next)
    {
        lock (connectionGate) snapshot = next = next with { ListenerActive = Volatile.Read(ref listenerActive) };
        SnapshotChanged?.Invoke(this, next);
    }

    private bool IsAndroidConnected()
    {
        lock (connectionGate) return android is not null && peerKind == "android";
    }
    private bool IsCurrentConnection(TcpClient client, NetworkStream stream)
    {
        lock (connectionGate) return ReferenceEquals(android, client) && ReferenceEquals(androidStream, stream);
    }
    private bool IsExpectedConnection(TcpClient? client, NetworkStream? stream) =>
        client is null && stream is null || client is not null && stream is not null && IsCurrentConnection(client, stream);
    private bool TrySetCaptureConfirmation(TcpClient client, NetworkStream stream, bool confirmed)
    {
        lock (connectionGate)
        {
            if (!ReferenceEquals(android, client) || !ReferenceEquals(androidStream, stream)) return false;
            return captureStartConfirmation?.TrySetResult(confirmed) == true;
        }
    }
    private bool PublishIfCurrent(TcpClient client, NetworkStream stream, CoreSnapshot next)
    {
        lock (connectionGate)
        {
            if (!ReferenceEquals(android, client) || !ReferenceEquals(androidStream, stream)) return false;
            snapshot = next = next with { ListenerActive = Volatile.Read(ref listenerActive) };
        }
        SnapshotChanged?.Invoke(this, next);
        return true;
    }

    private string CurrentTransport()
    {
        lock (connectionGate) return selectedTransport;
    }

    private int CurrentAudioPort() => CurrentTransport() == FrameCodec.AudioTransportUdp && udp is not null ? options.UdpPort : 0;

    private string SelectTransport(IReadOnlyList<string>? transports)
    {
        if (!string.Equals(options.AudioTransport, FrameCodec.AudioTransportUdp, StringComparison.OrdinalIgnoreCase) || udp is null) return FrameCodec.AudioTransportTcp;
        return transports?.Any(value => value.Equals(FrameCodec.AudioTransportUdp, StringComparison.OrdinalIgnoreCase)) == true
            ? FrameCodec.AudioTransportUdp
            : FrameCodec.AudioTransportTcp;
    }

    private static string ConnectionKey(string localDeviceId, string remoteDeviceId, string localConnectionId, string remoteConnectionId)
    {
        var device = string.CompareOrdinal(localDeviceId, remoteDeviceId) <= 0
            ? $"{localDeviceId}:{remoteDeviceId}"
            : $"{remoteDeviceId}:{localDeviceId}";
        var connection = string.CompareOrdinal(localConnectionId, remoteConnectionId) <= 0
            ? $"{localConnectionId}:{remoteConnectionId}"
            : $"{remoteConnectionId}:{localConnectionId}";
        return $"{device}:{connection}";
    }



    private static void ValidateHello(HelloPayload hello, string? expectedDeviceId = null)
    {
        if (!hello.Peer.Equals("android", StringComparison.OrdinalIgnoreCase) &&
            !hello.Peer.Equals("windows", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("unsupported peer type");
        }
        if (expectedDeviceId is not null && hello.DeviceId is not null &&
            !expectedDeviceId.Equals(hello.DeviceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("HELLO device identity does not match discovered peer");
        }
        if (hello.AudioRate != FrameCodec.AudioRate || hello.AudioBits != FrameCodec.AudioBits || hello.AudioChannels != FrameCodec.AudioChannels)
        {
            throw new InvalidDataException("only 48 kHz 16-bit mono HELLO is supported");
        }
    }
}

internal static class CoreStateExtensions
{
    public static string ToWire(this CoreState state) => state switch
    {
        CoreState.ConnectedIdle => "connected_idle",
        _ => state.ToString().ToLowerInvariant(),
    };
}
