using System.Net;
using System.Net.Sockets;
using System.Text;
using MicBridge.App;
using MicBridge.Core.Control;
using MicBridge.Core.Protocol;

namespace MicBridge.Core.Tests;

public sealed class IpcShutdownTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisposeCancelsBlockedWriteOrFlushAndHasDeadline(bool blockFlush, bool ignoreCancellation)
    {
        await using var core = NewCore(new DisposalSink(), 0);
        using var input = new StringReader("{\"id\":\"status\",\"command\":\"status\"}\n");
        using var output = new BlockedWriter(blockFlush, ignoreCancellation);
        var server = new LocalControlServer(core, () => { }, input, output);
        server.Start();
        await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(output.CancellationObserved.Task.IsCompletedSuccessfully);
            Assert.False(output.Released.Task.IsCompleted);
        }
        finally { output.Released.TrySetResult(); }
    }

    [Fact]
    public async Task DisposeHasDeadlineWhenInputIgnoresCancellation()
    {
        await using var core = NewCore(new DisposalSink(), 0);
        using var input = new BlockedReader();
        var server = new LocalControlServer(core, () => { }, input, TextWriter.Null);
        server.Start();
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try { await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { input.Released.TrySetResult(null); }
    }

    [Fact]
    public async Task HostDisposesCoreBeforeBlockedOutputIsReleased()
    {
        // 占用端口让宿主走启动失败后的退出路径，避免真实 LAN 发现影响本用例。
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var sink = new DisposalSink();
        var core = NewCore(sink, ((IPEndPoint)occupied.LocalEndpoint).Port);
        using var input = new StringReader("{\"id\":\"status\",\"command\":\"status\"}\n");
        using var output = new BlockedWriter(false, true);
        var host = Program.RunAsync(core, input, output);
        await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await sink.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(output.Released.Task.IsCompleted);
            Assert.Equal(0, await host.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            output.Released.TrySetResult();
            await host.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static CoreController NewCore(DisposalSink sink, int port) => new(
        new CoreOptions(TcpPort: port, DiscoveryPort: 0), sink,
        sessionMonitor: new ManualSessionMonitor(), trustStore: new MemoryTrustStore());

    private sealed class BlockedWriter(bool blockFlush, bool ignoreCancellation) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task WriteLineAsync(string? value) => blockFlush ? Task.CompletedTask : BlockAsync(CancellationToken.None);
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            blockFlush ? Task.CompletedTask : BlockAsync(cancellationToken);
        public override Task FlushAsync() => blockFlush ? BlockAsync(CancellationToken.None) : Task.CompletedTask;
        public override Task FlushAsync(CancellationToken cancellationToken) => blockFlush ? BlockAsync(cancellationToken) : Task.CompletedTask;
        private async Task BlockAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            Entered.TrySetResult();
            try
            {
                if (ignoreCancellation) await Released.Task;
                else await Released.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class BlockedReader : TextReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return new(Released.Task);
        }
    }

    private sealed class DisposalSink : IAudioSink
    {
        public string Name => "ipc-shutdown-test";
        public bool IsOpen => false;
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Open() { }
        public void Write(ReadOnlySpan<byte> data) { }
        public void Close() { }
        public void Dispose() => Disposed.TrySetResult();
    }
}
