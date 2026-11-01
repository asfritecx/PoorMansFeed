namespace PoorMansFeed.Tests;

/// <summary>The gate every page's commands wait on.</summary>
public sealed class WorkGateTests
{
    [Fact]
    public void CanStartNeedsStartedNotBusyAndNoHold()
    {
        var gate = new WorkGate();
        Assert.False(gate.CanStart);

        gate.SetStarted(true);
        Assert.True(gate.CanStart);

        gate.SetBusy(true);
        Assert.False(gate.CanStart);
        gate.SetBusy(false);
        Assert.True(gate.CanStart);

        IDisposable hold = gate.Hold();
        Assert.False(gate.CanStart);
        hold.Dispose();
        Assert.True(gate.CanStart);

        gate.SetStarted(false);
        Assert.False(gate.CanStart);
    }

    [Fact]
    public void RefreshingBlocksCanStartOnItsOwnAndHoldsNothing()
    {
        var gate = new WorkGate();
        gate.SetStarted(true);
        int changes = 0;
        gate.Changed += (_, _) => changes++;

        gate.SetRefreshing(true);
        Assert.False(gate.CanStart);
        Assert.True(gate.IsStarted);
        Assert.False(gate.IsHeld);
        Assert.Equal(1, changes);

        gate.SetRefreshing(true);
        gate.SetBusy(true);
        gate.SetBusy(false);
        Assert.False(gate.CanStart);
        Assert.Equal(1, changes);

        gate.SetRefreshing(false);
        Assert.True(gate.CanStart);
        Assert.Equal(2, changes);

        // Busy already stops CanStart, but the refresh's own flip is still raised both ways
        gate.SetBusy(true);
        Assert.Equal(3, changes);
        gate.SetRefreshing(true);
        Assert.True(gate.IsRefreshing);
        gate.SetRefreshing(false);
        Assert.False(gate.IsRefreshing);
        Assert.False(gate.CanStart);
        Assert.Equal(5, changes);
    }

    [Fact]
    public void IsRefreshingFollowsSetRefreshingAndItsFlipIsRaisedWhileAHoldIsOut()
    {
        var gate = new WorkGate();
        gate.SetStarted(true);
        IDisposable hold = gate.Hold();
        int changes = 0;
        gate.Changed += (_, _) => changes++;
        Assert.False(gate.IsRefreshing);

        gate.SetRefreshing(true);
        Assert.True(gate.IsRefreshing);
        Assert.Equal(1, changes);
        gate.SetRefreshing(true);
        Assert.Equal(1, changes);

        gate.SetRefreshing(false);
        Assert.False(gate.IsRefreshing);
        Assert.False(gate.CanStart);
        Assert.Equal(2, changes);

        hold.Dispose();
        Assert.True(gate.CanStart);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void PageWorkNestsAndDisposingTwiceReleasesOnce()
    {
        var gate = new WorkGate();
        gate.SetStarted(true);
        Assert.False(gate.IsPageWorking);

        IDisposable outer = gate.EnterPageWork();
        IDisposable inner = gate.EnterPageWork();
        inner.Dispose();
        inner.Dispose();
        Assert.True(gate.IsPageWorking);
        outer.Dispose();
        Assert.False(gate.IsPageWorking);
        outer.Dispose();
        Assert.False(gate.IsPageWorking);

        // A released mark disposed again must not have freed a later one either
        IDisposable later = gate.EnterPageWork();
        inner.Dispose();
        outer.Dispose();
        Assert.True(gate.IsPageWorking);
        later.Dispose();
        Assert.False(gate.IsPageWorking);
    }

    [Fact]
    public void PageWorkRaisesNothingAndLeavesCanStart()
    {
        var gate = new WorkGate();
        gate.SetStarted(true);
        int changes = 0;
        gate.Changed += (_, _) => changes++;

        IDisposable work = gate.EnterPageWork();
        Assert.True(gate.IsPageWorking);
        Assert.True(gate.CanStart);
        Assert.False(gate.IsHeld);
        Assert.False(gate.IsRefreshing);
        work.Dispose();

        Assert.True(gate.CanStart);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void HoldsNestAndDisposingTwiceReleasesOnce()
    {
        var gate = new WorkGate();
        gate.SetStarted(true);

        IDisposable outer = gate.Hold();
        IDisposable inner = gate.Hold();

        inner.Dispose();
        Assert.False(gate.CanStart);
        inner.Dispose();
        Assert.False(gate.CanStart);

        outer.Dispose();
        Assert.True(gate.CanStart);
        outer.Dispose();
        Assert.True(gate.CanStart);

        // A released hold disposed again must not have freed a later hold either
        IDisposable later = gate.Hold();
        inner.Dispose();
        outer.Dispose();
        Assert.False(gate.CanStart);
        later.Dispose();
        Assert.True(gate.CanStart);
    }

    [Fact]
    public void IsHeldFollowsTheHoldsAndNotTheUpdate()
    {
        var gate = new WorkGate();
        Assert.False(gate.IsHeld);

        gate.SetStarted(true);
        IDisposable outer = gate.Hold();
        Assert.True(gate.IsHeld);
        IDisposable inner = gate.Hold();
        Assert.True(gate.IsHeld);
        inner.Dispose();
        Assert.True(gate.IsHeld);
        outer.Dispose();
        Assert.False(gate.IsHeld);

        // An update stops new work but holds nothing
        gate.SetBusy(true);
        Assert.False(gate.CanStart);
        Assert.False(gate.IsHeld);
        gate.SetBusy(false);
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public void ChangedFiresOnlyWhenCanStartFlips()
    {
        var gate = new WorkGate();
        int changes = 0;
        gate.Changed += (_, _) => changes++;

        // Not started: a hold or a busy update changes nothing that can be seen
        IDisposable early = gate.Hold();
        gate.SetBusy(true);
        gate.SetBusy(false);
        early.Dispose();
        Assert.Equal(0, changes);

        gate.SetStarted(true);
        Assert.Equal(1, changes);
        gate.SetStarted(true);
        Assert.Equal(1, changes);

        IDisposable first = gate.Hold();
        Assert.Equal(2, changes);
        IDisposable second = gate.Hold();
        gate.SetBusy(true);
        Assert.Equal(2, changes);

        first.Dispose();
        second.Dispose();
        Assert.Equal(2, changes);
        gate.SetBusy(false);
        Assert.Equal(3, changes);
        Assert.True(gate.CanStart);
    }

    // IsStarted is start-up alone: holds and updates don't change it, and its flip is raised even while CanStart stays false
    [Fact]
    public void IsStartedIgnoresHoldsAndBusyAndItsFlipIsRaised()
    {
        var gate = new WorkGate();
        int changes = 0;
        gate.Changed += (_, _) => changes++;
        IDisposable hold = gate.Hold();
        Assert.False(gate.IsStarted);

        gate.SetStarted(true);
        Assert.True(gate.IsStarted);
        Assert.False(gate.CanStart);
        Assert.Equal(1, changes);

        gate.SetBusy(true);
        Assert.True(gate.IsStarted);
        gate.SetBusy(false);
        hold.Dispose();
        Assert.True(gate.IsStarted);
        Assert.True(gate.CanStart);
    }
}
