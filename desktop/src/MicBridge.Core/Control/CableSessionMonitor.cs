using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MicBridge.Core.Control;

public interface ICaptureSessionMonitor : IAsyncDisposable
{
    void Start(Action<bool> observe);
}

/// <summary>All WASAPI objects are created, read and released on one MTA thread.</summary>
public sealed class CableSessionMonitor(Action<string> log) : ICaptureSessionMonitor
{
    private readonly object gate = new();
    private readonly ManualResetEventSlim stop = new();
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? thread;
    private bool signalDisposed;
    private bool disposed;

    public void Start(Action<bool> observe)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (thread is not null) return;
            thread = new Thread(() => Run(observe)) { IsBackground = true, Name = "micbridge-cable-sessions" };
            try
            {
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
            }
            catch { finished.TrySetResult(); throw; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (!disposed)
            {
                disposed = true;
                stop.Set();
                if (thread is null) finished.TrySetResult();
            }
        }
        await finished.Task.ConfigureAwait(false);
        lock (gate)
        {
            if (!signalDisposed) { stop.Dispose(); signalDisposed = true; }
        }
    }

    private void Run(Action<bool> observe)
    {
        var faulted = false;
        var previousActive = false;
        var previousProcesses = "";
        try
        {
            while (!stop.IsSet)
            {
                var active = false;
                var activeProcesses = new SortedDictionary<uint, string>();
                try
                {
                    // Re-enumerate on every poll so new endpoints and sessions are visible.
                    using var enumerator = new MMDeviceEnumerator();
                    var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                    var found = false;
                    foreach (var device in devices)
                    {
                        using (device)
                        {
                            if (!device.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)) continue;
                            found = true;
                            // MMDevice.Dispose owns AudioSessionManager.Dispose (NAudio 2.2.1).
                            var manager = device.AudioSessionManager;
                            var sessions = manager.Sessions;
                            for (var i = 0; i < sessions.Count; i++)
                            {
                                using var session = sessions[i];
                                var processId = session.GetProcessID;
                                if (AutoCapturePolicy.IsExternalActiveSession(
                                    session.State == AudioSessionState.AudioSessionStateActive,
                                    processId, (uint)Environment.ProcessId))
                                {
                                    active = true;
                                    activeProcesses[processId] = ProcessName(processId);
                                }
                            }
                        }
                    }
                    if (!found) throw new InvalidOperationException("Active CABLE Output capture endpoint is unavailable");
                    if (faulted) log("CABLE Output session monitoring recovered");
                    faulted = false;
                }
                catch (Exception error)
                {
                    active = false;
                    activeProcesses.Clear();
                    if (!faulted) log($"CABLE Output session monitoring unavailable: {error.Message}");
                    faulted = true;
                }
                var processes = string.Join(", ", activeProcesses.Select(pair => $"{pair.Value}(PID={pair.Key})"));
                if (active != previousActive || processes != previousProcesses)
                {
                    log($"CABLE Output listenerActive={active.ToString().ToLowerInvariant()} sessions=[{processes}]" +
                        (!active && previousProcesses.Length > 0 ? $" previous=[{previousProcesses}]" : ""));
                    previousActive = active;
                    previousProcesses = processes;
                }
                if (!stop.IsSet) observe(active);
                stop.Wait(TimeSpan.FromMilliseconds(100));
            }
        }
        finally { finished.TrySetResult(); }
    }

    private static string ProcessName(uint processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(checked((int)processId));
            return process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException)
        {
            return "unknown";
        }
    }
}
