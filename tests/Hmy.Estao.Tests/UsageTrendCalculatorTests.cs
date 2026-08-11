using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Tests;

public sealed class UsageTrendCalculatorTests
{
    [Fact]
    public void fast_trend_stops_at_first_pacing_breach()
    {
        var start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var now = start.AddDays(2);
        var end = start.AddDays(7);

        var projection = UsageTrendCalculator.Compute(.20D, .20D, 15D, start, now, end);

        Assert.NotNull(projection);
        Assert.True(projection.BreachesPacing);
        Assert.Equal(now.AddDays(2), projection.EndAt);
        Assert.Equal(.60D, projection.Line[^1].Value, 8);
    }

    [Fact]
    public void slower_trend_continues_to_reset_without_false_breach()
    {
        var start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var now = start.AddDays(2);
        var end = start.AddDays(7);

        var projection = UsageTrendCalculator.Compute(.10D, .02D, 15D, start, now, end);

        Assert.NotNull(projection);
        Assert.False(projection.BreachesPacing);
        Assert.Equal(end, projection.EndAt);
        Assert.Equal(.20D, projection.Line[^1].Value, 8);
    }

    [Fact]
    public void already_breached_trend_remains_visible_until_exhaustion()
    {
        var start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var now = start.AddDays(1);
        var end = start.AddDays(7);

        var projection = UsageTrendCalculator.Compute(.40D, .30D, 15D, start, now, end);

        Assert.NotNull(projection);
        Assert.True(projection.BreachesPacing);
        Assert.Equal(now.AddDays(2), projection.EndAt);
        Assert.Equal(1D, projection.Line[^1].Value);
    }
}
