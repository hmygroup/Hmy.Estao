using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Tests;

public sealed class PacingCalculatorTests
{
    [Fact]
    public void target_line_uses_the_full_window_when_history_is_empty()
    {
        var now = DateTimeOffset.Parse("2026-08-11T12:00:00Z");
        var rangeStart = now.AddDays(-7);

        var result = PacingCalculator.Compute([], 15D, rangeStart, now);

        Assert.NotNull(result);
        Assert.Equal(rangeStart, result.WindowStart);
        Assert.Equal(rangeStart, result.TargetLine[0].Timestamp);
        Assert.Equal(now, result.TargetLine[^1].Timestamp);
    }

    [Fact]
    public void a_single_recent_sample_does_not_collapse_the_target_line_at_now()
    {
        var now = DateTimeOffset.Parse("2026-08-11T12:00:00Z");
        var rangeStart = now.AddDays(-7);
        PacingPoint[] points = [new(now.AddMinutes(-2), .35D)];

        var result = PacingCalculator.Compute(points, 15D, rangeStart, now);

        Assert.NotNull(result);
        Assert.Equal(rangeStart, result.WindowStart);
        Assert.Equal(rangeStart, result.TargetLine[0].Timestamp);
    }

    [Fact]
    public void a_detected_reset_still_starts_a_new_target_line()
    {
        var now = DateTimeOffset.Parse("2026-08-11T12:00:00Z");
        var rangeStart = now.AddDays(-7);
        var resetAt = now.AddHours(-3);
        PacingPoint[] points =
        [
            new(resetAt.AddMinutes(-1), .82D),
            new(resetAt, .04D),
            new(now, .12D)
        ];

        var result = PacingCalculator.Compute(points, 15D, rangeStart, now);

        Assert.NotNull(result);
        Assert.Equal(resetAt, result.WindowStart);
        Assert.Equal(resetAt, result.TargetLine[0].Timestamp);
    }

    [Fact]
    public void display_target_always_reaches_the_end_of_the_cycle()
    {
        var start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var end = start.AddDays(30);

        var line = PacingCalculator.BuildTargetLine(start, end, 15D);

        Assert.Equal(start, line[0].Timestamp);
        Assert.Equal(end, line[^1].Timestamp);
        Assert.Equal(1D, line[^1].Value);
    }

    [Fact]
    public void low_target_reaches_cycle_end_without_forcing_one_hundred_percent()
    {
        var start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var end = start.AddHours(5);

        var line = PacingCalculator.BuildTargetLine(start, end, 15D);

        Assert.Equal(end, line[^1].Timestamp);
        Assert.Equal(.03125D, line[^1].Value, 6);
    }
}
