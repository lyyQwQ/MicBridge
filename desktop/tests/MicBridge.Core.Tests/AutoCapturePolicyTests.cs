using MicBridge.Core.Control;

namespace MicBridge.Core.Tests;

public sealed class AutoCapturePolicyTests
{
    [Theory]
    [InlineData(false, 123, false)]
    [InlineData(true, 42, false)]
    [InlineData(true, 123, true)]
    [InlineData(true, 0, true)]
    public void OnlyExternalActiveSessionsCount(bool active, uint pid, bool expected)
    {
        Assert.Equal(expected, AutoCapturePolicy.IsExternalActiveSession(active, pid, 42));
    }

    [Fact]
    public void RisingEdgeStartsOnceAndInactiveStateNeverStarts()
    {
        var policy = new AutoCapturePolicy();
        policy.Observe(false, TimeSpan.Zero);
        Assert.False(policy.TryStart(true, true));
        policy.Observe(true, TimeSpan.FromMilliseconds(100));
        Assert.True(policy.TryStart(true, true));
        policy.Observe(true, TimeSpan.FromSeconds(10));
        Assert.False(policy.TryStart(true, true));
        Assert.True(policy.OwnsCapture);
    }

    [Fact]
    public void ConnectionArrivingDuringActiveSessionCanStart()
    {
        var policy = new AutoCapturePolicy();
        policy.Observe(true, TimeSpan.Zero);
        Assert.False(policy.TryStart(false, true));
        Assert.False(policy.TryStart(true, false));
        Assert.False(policy.OwnsCapture);
        policy.ConnectionChanged();
        Assert.True(policy.TryStart(true, true));
        policy.ConnectionChanged();
        Assert.True(policy.TryStart(true, true));
    }

    [Fact]
    public void ShortGapKeepsListenerActiveAndCancelsStop()
    {
        var policy = Started();
        var inactiveAt = TimeSpan.FromSeconds(1);
        var beforeDebounce = inactiveAt + AutoCapturePolicy.ListenerDebounce - TimeSpan.FromMilliseconds(1);
        policy.Observe(false, inactiveAt);
        policy.Observe(false, beforeDebounce);
        Assert.True(policy.ListenerActive);
        Assert.False(policy.TryStop(beforeDebounce));
        policy.Observe(true, beforeDebounce);
        Assert.False(policy.TryStop(inactiveAt + AutoCapturePolicy.StopDelay));
        Assert.False(policy.TryStart(true, true));
    }

    [Fact]
    public void AutomaticCaptureStopsOnceAtThreeSecondsOfInactivity()
    {
        var policy = Started();
        var inactiveAt = TimeSpan.FromSeconds(1);
        policy.Observe(false, inactiveAt);
        policy.Observe(false, inactiveAt + AutoCapturePolicy.ListenerDebounce);
        Assert.False(policy.ListenerActive);
        Assert.False(policy.TryStop(inactiveAt + AutoCapturePolicy.ListenerDebounce));
        var deadline = inactiveAt + AutoCapturePolicy.StopDelay;
        policy.Observe(false, deadline - TimeSpan.FromMilliseconds(1));
        Assert.False(policy.TryStop(deadline - TimeSpan.FromMilliseconds(1)));
        Assert.True(policy.OwnsCapture);
        policy.Observe(false, deadline);
        Assert.True(policy.TryStop(deadline));
        Assert.False(policy.OwnsCapture);
        Assert.False(policy.TryStop(deadline + AutoCapturePolicy.StopDelay));
    }

    [Fact]
    public void ActiveBeforeStopDeadlineCancelsStopAndNextGapGetsFullDelay()
    {
        var policy = Started();
        var inactiveAt = TimeSpan.FromSeconds(1);
        var oldDeadline = inactiveAt + AutoCapturePolicy.StopDelay;
        policy.Observe(false, inactiveAt);
        policy.Observe(false, inactiveAt + AutoCapturePolicy.ListenerDebounce);
        policy.Observe(true, oldDeadline - TimeSpan.FromMilliseconds(1));
        Assert.True(policy.ListenerActive);
        Assert.False(policy.TryStop(oldDeadline));
        Assert.False(policy.TryStart(true, true));
        var nextInactiveAt = oldDeadline + TimeSpan.FromSeconds(1);
        policy.Observe(false, nextInactiveAt);
        var nextDeadline = nextInactiveAt + AutoCapturePolicy.StopDelay;
        policy.Observe(false, nextDeadline - TimeSpan.FromMilliseconds(1));
        Assert.False(policy.TryStop(nextDeadline - TimeSpan.FromMilliseconds(1)));
        policy.Observe(false, nextDeadline);
        Assert.True(policy.TryStop(nextDeadline));
    }

    [Fact]
    public void ManualStartTakesOwnershipAndCannotBeAutomaticallyStopped()
    {
        var policy = Started();
        policy.ManualOverride();
        policy.Observe(false, TimeSpan.FromSeconds(1));
        policy.Observe(false, TimeSpan.FromSeconds(1) + AutoCapturePolicy.StopDelay);
        Assert.False(policy.OwnsCapture);
        Assert.False(policy.TryStop(TimeSpan.FromSeconds(1) + AutoCapturePolicy.StopDelay));
    }

    [Fact]
    public void ManualStopSuppressesRestartUntilARealNewRisingEdge()
    {
        var policy = Started();
        policy.ManualOverride();
        Assert.False(policy.TryStart(true, true));
        policy.Observe(false, TimeSpan.FromSeconds(1));
        policy.Observe(true, TimeSpan.FromMilliseconds(1500));
        Assert.False(policy.TryStart(true, true));
        policy.Observe(false, TimeSpan.FromSeconds(2));
        policy.Observe(false, TimeSpan.FromSeconds(3));
        policy.Observe(true, TimeSpan.FromSeconds(4));
        Assert.True(policy.TryStart(true, true));
    }

    [Fact]
    public void CaptureFailureOnlyRearmsAfterDebouncedNewRecording()
    {
        var policy = Started();
        policy.CaptureFailed();
        Assert.False(policy.OwnsCapture);
        policy.Observe(true, TimeSpan.FromSeconds(1));
        Assert.False(policy.TryStart(true, true));
        policy.Observe(false, TimeSpan.FromSeconds(2));
        policy.Observe(true, TimeSpan.FromMilliseconds(2500));
        Assert.False(policy.TryStart(true, true));
        policy.Observe(false, TimeSpan.FromSeconds(3));
        policy.Observe(false, TimeSpan.FromSeconds(4));
        Assert.False(policy.TryStop(TimeSpan.FromSeconds(4)));
        policy.Observe(true, TimeSpan.FromSeconds(5));
        Assert.False(policy.TryStart(false, true));
        Assert.True(policy.TryStart(true, true));
        Assert.False(policy.TryStart(true, true));
    }

    [Fact]
    public void InactivityDuringPendingStartIsRemembered()
    {
        var policy = Started();
        policy.Observe(false, TimeSpan.FromMilliseconds(100));
        policy.Observe(false, TimeSpan.FromMilliseconds(100) + AutoCapturePolicy.ListenerDebounce);
        // The START consumer may still be awaiting the Android acknowledgement.
        Assert.False(policy.ListenerActive);
        Assert.False(policy.TryStop(TimeSpan.FromMilliseconds(100) + AutoCapturePolicy.ListenerDebounce));
        Assert.True(policy.TryStop(TimeSpan.FromMilliseconds(100) + AutoCapturePolicy.StopDelay));
    }

    [Fact]
    public void DisconnectClearsOwnershipWithoutLosingListenerState()
    {
        var policy = Started();
        policy.ConnectionChanged();
        Assert.True(policy.ListenerActive);
        Assert.False(policy.OwnsCapture);
        Assert.False(policy.TryStart(false, true));
        policy.Observe(false, TimeSpan.FromSeconds(1));
        policy.Observe(false, TimeSpan.FromSeconds(1) + AutoCapturePolicy.StopDelay);
        Assert.False(policy.TryStop(TimeSpan.FromSeconds(1) + AutoCapturePolicy.StopDelay));
    }

    private static AutoCapturePolicy Started()
    {
        var policy = new AutoCapturePolicy();
        policy.Observe(true, TimeSpan.Zero);
        Assert.True(policy.TryStart(true, true));
        return policy;
    }
}
