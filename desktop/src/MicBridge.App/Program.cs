using System.Text;
using MicBridge.Core.Audio;
using MicBridge.Core.Control;

namespace MicBridge.App;

internal static class Program
{
    public static async Task<int> Main()
    {
        // WinExe never allocates a console; Flutter supplies these inherited pipe handles.
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        // stdout 由进程退出关闭；同步 Dispose 的最终 flush 也可能被父进程堵住。
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var error = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MicBridge", "logs", "core.log");
        Console.SetError(new RotatingErrorWriter(logPath, error));
        var core = new CoreController(
            new CoreOptions(ServiceName: string.IsNullOrWhiteSpace(Environment.MachineName) ? "MicBridge" : Environment.MachineName,
                AudioTransport: Environment.GetEnvironmentVariable("MICBRIDGE_AUDIO_TRANSPORT") ?? "tcp"),
            new NAudioSink(),
            message => Console.Error.WriteLine(message));
        return await RunAsync(core, input, output);
    }

    internal static async Task<int> RunAsync(CoreController core, TextReader input, TextWriter output)
    {
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var localControl = new LocalControlServer(core, () => shutdown.TrySetResult(), input, output);
        try
        {
            localControl.Start();
            try { await core.StartAsync(); }
            catch (Exception error) { Console.Error.WriteLine($"核心启动失败：{error.Message}"); }
            await shutdown.Task;
        }
        finally
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await core.DispatchAsync("STOP", deadline.Token); }
                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
                catch (Exception error) { Console.Error.WriteLine($"核心停止失败：{error.Message}"); }
            }
            finally
            {
                var ipcDisposal = localControl.DisposeAsync().AsTask();
                try { await core.DisposeAsync(); }
                catch (Exception error) { Console.Error.WriteLine($"核心释放失败：{error.Message}"); }
                finally
                {
                    try { await ipcDisposal; }
                    catch (Exception error) { Console.Error.WriteLine($"本地控制释放失败：{error.Message}"); }
                }
            }
        }
        return 0;
    }
}
