using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Tests;

public sealed class UsageExhaustionCalculatorTests
{
    [Fact]
    public void projects_exhaustion_from_the_observed_rate()
    {
        var now = DateTimeOffset.Parse("2026-08-11T12:00:00Z");
        PacingPoint[] points = [new(now.AddDays(-1), .25D), new(now, .50D)];

        var forecast = UsageExhaustionCalculator.Compute(
            points, .50D, TimeSpan.FromDays(7), now, now.AddDays(3));

        Assert.NotNull(forecast);
        Assert.Equal(now.AddDays(2), forecast.EstimatedAt);
        Assert.False(forecast.ResetOccursFirst);
    }

    [Fact]
    public void reports_when_reset_happens_before_exhaustion()
    {
        var now = DateTimeOffset.Parse("2026-08-11T12:00:00Z");
        var resetAt = now.AddHours(6);
        PacingPoint[] points = [new(now.AddDays(-1), .10D), new(now, .20D)];

        var forecast = UsageExhaustionCalculator.Compute(
            points, .20D, TimeSpan.FromDays(7), now, resetAt);

        Assert.NotNull(forecast);
        Assert.True(forecast.ResetOccursFirst);
        Assert.Equal(resetAt, forecast.ResetAt);
    }

    [Fact]
    public void uses_window_start_when_only_current_usage_is_known()
    {
        var now = DateTimeOffset.Parse("2026-08-11T12:00:00Z");
        var resetAt = now.AddDays(5);

        var forecast = UsageExhaustionCalculator.Compute(
            [], .50D, TimeSpan.FromDays(7), now, resetAt);

        Assert.NotNull(forecast);
        Assert.Equal(now.AddDays(2), forecast.EstimatedAt);
    }

    [Fact]
    public void flat_recent_samples_fall_back_to_average_since_reset()
    {
        var now = DateTimeOffset.Parse("2026-08-11T12:00:00Z");
        var resetAt = now.AddDays(21);
        PacingPoint[] points =
        [
            new(now.AddHours(-2), .10D),
            new(now, .10D)
        ];

        var forecast = UsageExhaustionCalculator.Compute(
            points, .10D, TimeSpan.FromDays(30), now, resetAt);

        Assert.NotNull(forecast);
        Assert.True(forecast.ResetOccursFirst);
        Assert.Equal(1D / 90D, forecast.DailyConsumptionRate, 8);
    }
}
