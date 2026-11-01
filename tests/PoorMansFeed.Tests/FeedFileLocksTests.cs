using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class FeedFileLocksTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    // Only proves a caller is still blocked; nothing waits on it to succeed
    private static readonly TimeSpan StillBlocked = TimeSpan.FromMilliseconds(150);

    // Another flow: a task started inside the holder's flow would inherit its hold and be refused
    private static Task<IDisposable> EnterOnPool(FeedFileLocks locks, string feedKey)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(() => locks.Enter(feedKey));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime giveUp = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < giveUp, "The condition wasn't met in time.");
            await Task.Delay(5);
        }
    }

    private static async Task AssertBlockedAsync(Task task)
    {
        await Task.WhenAny(task, Task.Delay(StillBlocked));
        Assert.False(task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_same_feed_waits_for_the_first_holder(bool otherCase)
    {
        var locks = new FeedFileLocks();
        IDisposable first = locks.Enter("AS15169");

        Task<IDisposable> second = EnterOnPool(locks, otherCase ? "as15169" : "AS15169");

        await UntilAsync(() => locks.InFlight == 2);
        await AssertBlockedAsync(second);
        first.Dispose();
        IDisposable held = await second.WaitAsync(Patience);
        Assert.Equal(1, locks.InFlight);
        held.Dispose();
        Assert.Equal(0, locks.InFlight);
    }

    [Fact]
    public async Task Another_feed_doesnt_wait()
    {
        var locks = new FeedFileLocks();
        using IDisposable first = locks.Enter("AS15169");

        using IDisposable second = await EnterOnPool(locks, "AS15170").WaitAsync(Patience);

        Assert.Equal(2, locks.InFlight);
    }

    [Fact]
    public async Task WhenNone_completes_at_zero_and_when_the_last_holder_leaves()
    {
        var locks = new FeedFileLocks();
        Assert.True(locks.WhenNoneAsync(CancellationToken.None).IsCompletedSuccessfully);

        IDisposable first = locks.Enter("AS15169");
        Task<IDisposable> second = EnterOnPool(locks, "AS15169");
        await UntilAsync(() => locks.InFlight == 2);
        Task none = locks.WhenNoneAsync(CancellationToken.None);

        first.Dispose();
        IDisposable held = await second.WaitAsync(Patience);
        await AssertBlockedAsync(none);
        held.Dispose();
        await none.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_wait_after_an_earlier_one_completed_waits_for_the_new_holder()
    {
        var locks = new FeedFileLocks();
        IDisposable first = locks.Enter("AS15169");
        Task earlier = locks.WhenNoneAsync(CancellationToken.None);
        first.Dispose();
        await earlier.WaitAsync(Patience);

        IDisposable again = locks.Enter("AS15169");
        Task later = locks.WhenNoneAsync(CancellationToken.None);
        await AssertBlockedAsync(later);
        again.Dispose();
        await later.WaitAsync(Patience);
    }

    [Fact]
    public async Task WhenNone_throws_when_its_token_is_cancelled()
    {
        var locks = new FeedFileLocks();
        using IDisposable first = locks.Enter("AS15169");
        using var cts = new CancellationTokenSource();

        Task none = locks.WhenNoneAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => none.WaitAsync(Patience));
        Assert.Equal(1, locks.InFlight);
    }

    [Fact]
    public async Task A_second_dispose_of_a_handle_releases_nothing_more()
    {
        var locks = new FeedFileLocks();
        IDisposable first = locks.Enter("AS15169");
        Task<IDisposable> second = EnterOnPool(locks, "AS15169");
        await UntilAsync(() => locks.InFlight == 2);
        Task<IDisposable> third = EnterOnPool(locks, "AS15169");
        await UntilAsync(() => locks.InFlight == 3);

        first.Dispose();
        first.Dispose();

        Task<IDisposable> winner = await Task.WhenAny(second, third).WaitAsync(Patience);
        Task<IDisposable> waiter = winner == second ? third : second;
        await AssertBlockedAsync(waiter);
        Assert.Equal(2, locks.InFlight);
        (await winner).Dispose();
        (await waiter.WaitAsync(Patience)).Dispose();
        Assert.Equal(0, locks.InFlight);
    }

    [Fact]
    public async Task A_feed_can_be_held_again_after_its_last_holder_left()
    {
        var locks = new FeedFileLocks();
        locks.Enter("AS15169").Dispose();

        using IDisposable again = await EnterOnPool(locks, "AS15169").WaitAsync(Patience);

        Assert.Equal(1, locks.InFlight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_flow_that_holds_a_lock_is_refused_a_second_without_waiting(bool otherKey)
    {
        var locks = new FeedFileLocks();
        IDisposable first = locks.Enter("AS15169");

        Assert.Throws<InvalidOperationException>(() => locks.Enter(otherKey ? "AS15170" : "AS15169"));
        Assert.Equal(1, locks.InFlight);

        first.Dispose();
        using IDisposable again = locks.Enter(otherKey ? "AS15170" : "AS15169");
        Assert.Equal(1, locks.InFlight);
    }

    [Fact]
    public async Task A_task_started_inside_the_holders_flow_is_refused()
    {
        var locks = new FeedFileLocks();
        using IDisposable first = locks.Enter("AS15169");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => locks.Enter("AS15170")));
        Assert.Equal(1, locks.InFlight);
    }

    [Fact]
    public async Task After_admitting_stops_a_new_caller_is_refused_and_the_holder_and_waiter_finish()
    {
        var locks = new FeedFileLocks();
        IDisposable first = locks.Enter("AS15169");
        Task<IDisposable> waiter = EnterOnPool(locks, "AS15169");
        await UntilAsync(() => locks.InFlight == 2);

        locks.StopAdmitting();

        await Assert.ThrowsAsync<InvalidOperationException>(() => EnterOnPool(locks, "AS15170"));
        Assert.Equal(2, locks.InFlight);
        Task none = locks.WhenNoneAsync(CancellationToken.None);
        first.Dispose();
        (await waiter.WaitAsync(Patience)).Dispose();
        await none.WaitAsync(Patience);
        Assert.Throws<InvalidOperationException>(() => locks.Enter("AS15169"));
        Assert.Equal(0, locks.InFlight);
    }

    [Fact]
    public void A_missing_key_is_refused()
    {
        var locks = new FeedFileLocks();

        Assert.ThrowsAny<ArgumentException>(() => locks.Enter(""));
        Assert.ThrowsAny<ArgumentException>(() => locks.Enter(null!));
        Assert.Equal(0, locks.InFlight);
    }

    [Fact]
    public async Task HeldByThisFlow_is_true_only_for_the_key_this_flow_holds_ignoring_case()
    {
        var locks = new FeedFileLocks();
        Assert.False(locks.HeldByThisFlow("AS15169"));

        IDisposable hold = locks.Enter("AS15169");
        Assert.True(locks.HeldByThisFlow("AS15169"));
        Assert.True(locks.HeldByThisFlow("as15169"));
        Assert.False(locks.HeldByThisFlow("AS13335"));
        Task<bool> otherFlow;
        using (ExecutionContext.SuppressFlow())
            otherFlow = Task.Run(() => locks.HeldByThisFlow("AS15169"));
        Assert.False(await otherFlow);

        hold.Dispose();
        Assert.False(locks.HeldByThisFlow("AS15169"));
        IDisposable other = locks.Enter("AS13335");
        Assert.False(locks.HeldByThisFlow("AS15169"));
        Assert.Throws<InvalidOperationException>(() => locks.Enter("AS15169"));
        other.Dispose();
    }
}
