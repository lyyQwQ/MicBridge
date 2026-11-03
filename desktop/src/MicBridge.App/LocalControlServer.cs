using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MicBridge.Core.Control;
using MicBridge.Core.Discovery;

namespace MicBridge.App;

internal sealed class LocalControlServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CoreController core;
    private readonly Action shutdown;
    private readonly ConcurrentDictionary<string, DiscoveredPeer> peers = new();
    private readonly Channel<string> output = Channel.CreateBounded<string>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource lifetime = new();
    private Task? writerTask;
    private Task? readerTask;
    private readonly TextReader input;
    private readonly TextWriter outputWriter;
    private readonly bool supportsCancelableLineWrite;
    public LocalControlServer(CoreController core, Action shutdown, TextReader? input = null, TextWriter? outputWriter = null)
    {
        this.core = core;
        this.shutdown = shutdown;
        this.input = input ?? Console.In;
        this.outputWriter = outputWriter ?? Console.Out;
        // TextWriter 基类的 memory 重载不会调用旧的 string 重载，保留现有输出适配器的行为。
        supportsCancelableLineWrite = this.outputWriter.GetType().GetMethod(nameof(TextWriter.WriteLineAsync),
            [typeof(ReadOnlyMemory<char>), typeof(CancellationToken)])!.DeclaringType != typeof(TextWriter);
    }
    public void Start()
    {
        core.SnapshotChanged += OnSnapshot;
        core.DiscoveredPeersChanged += OnPeer;
        core.DiscoveredPeerRemoved += OnRemoved;
        core.PairingRequested += OnPairingRequested;
        writerTask = WriteLoopAsync();
        readerTask = Task.Run(ReadLoopAsync);
    }
    private object Snapshot()
    {
        var s = core.Snapshot;
        return new { state = s.State == CoreState.ConnectedIdle ? "connected_idle" : s.State.ToString().ToLowerInvariant(), device = s.AndroidPeer, detail = s.Detail, connected = s.AndroidConnected, pendingPairing = core.PendingPairing, trustedDevice = core.TrustedDevice, trustState = core.TrustState, audioDevice = s.AudioDevice, audioTransport = s.AudioTransport, droppedAudioFrames = s.DroppedAudioFrames, audioPort = s.AudioPort, listenerActive = s.ListenerActive };
    }
    private void OnSnapshot(object? sender, CoreSnapshot value) => output.Writer.TryWrite(JsonSerializer.Serialize(new { @event = "state", snapshot = Snapshot() }, JsonOptions));
    private async void OnPairingRequested(object? sender, PendingPairing request)
    {
        try { await output.Writer.WriteAsync(JsonSerializer.Serialize(new { @event = "pairing_request", request }, JsonOptions), lifetime.Token); }
        catch (Exception error) when (error is OperationCanceledException or ChannelClosedException) { core.DisablePairingApproval(); }
    }
    private void OnPeer(object? sender, DiscoveredPeer peer) { peers[$"{peer.Role}:{peer.DeviceId}"] = peer; PublishPeers(); }
    private void OnRemoved(object? sender, DiscoveredPeer peer) { peers.TryRemove($"{peer.Role}:{peer.DeviceId}", out _); PublishPeers(); }
    private void PublishPeers() => output.Writer.TryWrite(JsonSerializer.Serialize(new { @event = "peers", peers = peers.Values.ToArray() }, JsonOptions));
    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var line in output.Reader.ReadAllAsync(lifetime.Token))
            {
                var write = supportsCancelableLineWrite
                    ? outputWriter.WriteLineAsync(line.AsMemory(), lifetime.Token)
                    : outputWriter.WriteLineAsync(line);
                await write.WaitAsync(lifetime.Token);
                await outputWriter.FlushAsync(lifetime.Token).WaitAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { core.DisablePairingApproval(); shutdown(); }
    }
    private async Task ReadLoopAsync()
    {
        try
        {
            while (await input.ReadLineAsync(lifetime.Token) is { } line)
            {
                string? id = null;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    id = root.GetProperty("id").GetString();
                    var command = root.GetProperty("command").GetString();
                    object response;
                    switch (command)
                    {
                        case "status": response = new { ok = true, snapshot = Snapshot() }; break;
                        case "discover": response = new { ok = true, peers = peers.Values.ToArray() }; break;
                        case "connect":
                            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                            {
                                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                                await core.ConnectAsync(root.GetProperty("host").GetString()!, root.GetProperty("port").GetInt32(),
                                    root.TryGetProperty("expectedDeviceId", out var expected) ? expected.GetString() : null, timeout.Token);
                            }
                            response = new { ok = true }; break;
                        case "start": case "stop":
                            response = await core.DispatchAsync(command.ToUpperInvariant(), lifetime.Token); break;
                        case "pairing_decide":
                            var accepted = core.DecidePairing(root.GetProperty("requestId").GetString()!, root.GetProperty("decision").GetString()!);
                            response = accepted ? (object)new { ok = true, accepted = true }
                                : new { ok = false, accepted = false, code = "pairing_request_stale", message = "配对请求已失效或决定无效，请在手机上重新连接" };
                            break;
                        case "forget_device":
                            await core.ForgetDeviceAsync(lifetime.Token);
                            response = new { ok = true, snapshot = Snapshot() }; break;
                        case "shutdown":
                            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                            {
                                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                                try { await core.DispatchAsync("STOP", deadline.Token); }
                                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
                            }
                            await RespondAsync(id, new { ok = true });
                            shutdown(); return;
                        default: response = new { ok = false, message = "Unsupported command" }; break;
                    }
                    await RespondAsync(id, response);
                }
                catch (Exception e) when (e is not OperationCanceledException || !lifetime.IsCancellationRequested)
                { await RespondAsync(id, new { ok = false, message = e.Message }); }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ChannelClosedException) { }
        finally { core.DisablePairingApproval(); shutdown(); }
    }
    private async Task RespondAsync(string? id, object response)
    {
        var envelope = JsonSerializer.SerializeToNode(response, JsonOptions)!.AsObject();
        envelope["id"] = id;
        await output.Writer.WriteAsync(envelope.ToJsonString(JsonOptions), lifetime.Token);
    }
    public async ValueTask DisposeAsync()
    {
        core.DisablePairingApproval();
        core.PairingRequested -= OnPairingRequested;
        core.SnapshotChanged -= OnSnapshot; core.DiscoveredPeersChanged -= OnPeer; core.DiscoveredPeerRemoved -= OnRemoved;
        output.Writer.TryComplete();
        // 先给正常响应短暂排空机会，再取消；不合作的输入输出也不能阻塞宿主退出。
        if (writerTask is not null) await Task.WhenAny(writerTask, Task.Delay(TimeSpan.FromMilliseconds(250)));
        lifetime.Cancel();
        var stopped = Task.WhenAll(writerTask ?? Task.CompletedTask, readerTask ?? Task.CompletedTask);
        try { await stopped.WaitAsync(TimeSpan.FromMilliseconds(750)); }
        catch (TimeoutException) { }
        finally
        {
            // 未完成的读取可能仍使用令牌，延迟释放并观察最终异常。
            _ = stopped.ContinueWith(task => { _ = task.Exception; lifetime.Dispose(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
