namespace MicBridge.Core.Control;

/// <summary>COM-free policy. Callers serialize access; time must be monotonic.</summary>
public sealed class AutoCapturePolicy
{
    public static readonly TimeSpan ListenerDebounce = TimeSpan.FromSeconds(1);
    // 省电与首字延迟的权衡：短暂停顿保留音频路由，持续空闲 3 秒后释放以减少耗电。
    public static readonly TimeSpan StopDelay = TimeSpan.FromSeconds(3);

    private bool attempted;
    private bool rawActive;
    private TimeSpan? inactiveSince;
    public bool ListenerActive { get; private set; }
    public bool OwnsCapture { get; private set; }

    public static bool IsExternalActiveSession(bool active, uint processId, uint ownProcessId) =>
        active && processId != ownProcessId;

    public void Observe(bool active, TimeSpan now)
    {
        rawActive = active;
        if (active)
        {
            if (!ListenerActive) attempted = false;
            inactiveSince = null;
            ListenerActive = true;
        }
        else if (inactiveSince is null)
        {
            inactiveSince = now;
        }
        if (!active && now - inactiveSince!.Value >= ListenerDebounce) ListenerActive = false;
    }

    public bool TryStart(bool connected, bool idle)
    {
        if (!rawActive || attempted || OwnsCapture || !connected || !idle) return false;
        attempted = true;
        OwnsCapture = true;
        return true;
    }

    public bool CanStop(TimeSpan now) =>
        OwnsCapture && !ListenerActive && inactiveSince is not null && now - inactiveSince.Value >= StopDelay;

    public bool TryStop(TimeSpan now)
    {
        if (!CanStop(now)) return false;
        OwnsCapture = false;
        return true;
    }

    public void CaptureFailed()
    {
        // Keep attempted latched until the next debounced rising edge.
        OwnsCapture = false;
    }

    public void ManualOverride()
    {
        OwnsCapture = false;
        attempted = true;
    }

    public void ConnectionChanged()
    {
        OwnsCapture = false;
        attempted = false;
    }
}
