using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

public sealed class CountedTimeTests
{
    private const long Cap = 30;

    [Theory]
    [InlineData(100, 110, 10)]
    [InlineData(100, 130, 30)]
    [InlineData(100, 500, 30)]
    [InlineData(100, 100, 0)]
    [InlineData(100, 50, 0)]
    public void A_step_counts_the_time_before_its_later_stamp_up_to_the_cap(long from, long to, long counted)
    {
        var time = new CountedTime();

        time.AddStep(from, to, Cap);

        Assert.Equal(counted, time.Total);
    }

    [Fact]
    public void Time_already_counted_is_not_counted_again()
    {
        var time = new CountedTime();
        time.AddStep(0, 100, Cap);
        time.AddStep(0, 100, Cap);
        Assert.Equal(30, time.Total);

        time.AddStep(80, 90, Cap);
        Assert.Equal(30, time.Total);

        time.AddStep(90, 110, Cap);
        Assert.Equal(40, time.Total);

        time.AddStep(110, 115, Cap);
        Assert.Equal(45, time.Total);
    }

    [Fact]
    public void Spans_counted_out_of_order_add_up()
    {
        var time = new CountedTime();
        time.AddStep(0, 100, Cap);
        time.AddStep(0, 10, Cap);
        Assert.Equal(40, time.Total);

        time.AddStep(40, 50, Cap);
        Assert.Equal(50, time.Total);

        time.AddStep(0, 75, Cap);
        Assert.Equal(70, time.Total);
    }

    [Fact]
    public void A_step_over_several_counted_spans_counts_only_the_gaps_between_them()
    {
        var time = new CountedTime();
        time.AddStep(0, 10, Cap);
        time.AddStep(15, 20, Cap);
        time.AddStep(24, 26, Cap);
        Assert.Equal(17, time.Total);

        time.AddStep(-100, 28, Cap);
        Assert.Equal(30, time.Total);

        time.AddStep(0, 27, Cap);
        Assert.Equal(30, time.Total);
    }

    [Fact]
    public void Steps_at_the_ends_of_the_range_count_without_overflow()
    {
        var time = new CountedTime();
        time.AddStep(long.MinValue, long.MaxValue, Cap);
        Assert.Equal(Cap, time.Total);

        time.AddStep(long.MaxValue, long.MinValue, Cap);
        Assert.Equal(Cap, time.Total);

        time.AddStep(long.MinValue, long.MinValue + 5, Cap);
        Assert.Equal(Cap + 5, time.Total);
    }
}
