using TaskbarTiles.Contracts;

namespace TaskbarTiles.Tests;

public class BackoffScheduleTests
{
    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 10)]
    [InlineData(2, 60)]
    [InlineData(3, 300)]
    [InlineData(4, 300)]
    [InlineData(int.MaxValue, 300)]
    public void Default_is_2s_10s_1min_then_every_5min(int previousFailures, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), BackoffSchedule.Default.Delay(previousFailures));

    [Fact]
    public void Last_step_repeats()
    {
        var schedule = new BackoffSchedule([TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(7)]);

        Assert.Equal(TimeSpan.FromMilliseconds(5), schedule.Delay(0));
        Assert.Equal(TimeSpan.FromMilliseconds(7), schedule.Delay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(7), schedule.Delay(50));
    }

    [Fact]
    public void Empty_schedule_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new BackoffSchedule([]));

    [Fact]
    public void Negative_step_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackoffSchedule([TimeSpan.FromSeconds(-1)]));
}
