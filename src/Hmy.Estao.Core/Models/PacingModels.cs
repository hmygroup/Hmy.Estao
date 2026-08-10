namespace Hmy.Estao.Core.Models;

/// <summary>
/// A single point on a pacing/target reference line. Shares the same shape as
/// the chart's own history points so the UI layer can map it 1:1.
/// </summary>
public readonly record struct PacingPoint(DateTimeOffset Timestamp, double Value);

/// <summary>
/// Result of comparing a window's real usage against a daily pacing target.
/// </summary>
public sealed record PacingResult(
    IReadOnlyList<PacingPoint> TargetLine,
    DateTimeOffset WindowStart,
    double ExpectedPercentNow,
    double ActualPercentNow,
    bool IsOverPace);

/// <summary>
/// Computes a "budget pacing" reference line for a rate-limit window: how much
/// of the window's quota the user should have consumed by now if they spend it
/// at a steady <c>dailyTargetPercent</c> per day, starting either from the
/// last detected reset (a drop in the history) or from the earliest point
/// Estao has on record for that window.
/// </summary>
public static class PacingCalculator
{
    /// <summary>
    /// A usage drop larger than this (in absolute percent-used terms) between
    /// two consecutive samples is treated as the provider's window resetting,
    /// rather than normal noise/measurement jitter.
    /// </summary>
    private const double ResetDropThreshold = 0.05D;

    public static PacingResult? Compute(
        IReadOnlyList<PacingPoint> orderedPoints,
        double dailyTargetPercent,
        DateTimeOffset rangeStart,
        DateTimeOffset now)
    {
        if (dailyTargetPercent <= 0D || now <= rangeStart)
        {
            return null;
        }

        var windowStart = DetectWindowStart(orderedPoints, rangeStart);
        var dailyRate = Math.Clamp(dailyTargetPercent, 0D, 1000D) / 100D;
        var totalSpan = now - windowStart;
        if (totalSpan <= TimeSpan.Zero)
        {
            return null;
        }

        var expectedNow = Math.Clamp(dailyRate * totalSpan.TotalDays, 0D, 1D);
        var line = BuildTargetLine(windowStart, now, dailyRate);
        var actualNow = orderedPoints.Count > 0 ? orderedPoints[^1].Value : 0D;

        return new PacingResult(line, windowStart, expectedNow, actualNow, actualNow > expectedNow);
    }

    private static DateTimeOffset DetectWindowStart(IReadOnlyList<PacingPoint> orderedPoints, DateTimeOffset rangeStart)
    {
        for (var index = orderedPoints.Count - 1; index > 0; index--)
        {
            var current = orderedPoints[index];
            var previous = orderedPoints[index - 1];
            if (current.Value + ResetDropThreshold < previous.Value)
            {
                return current.Timestamp < rangeStart ? rangeStart : current.Timestamp;
            }
        }

        if (orderedPoints.Count > 0 && orderedPoints[0].Timestamp > rangeStart)
        {
            return orderedPoints[0].Timestamp;
        }

        return rangeStart;
    }

    private static IReadOnlyList<PacingPoint> BuildTargetLine(DateTimeOffset windowStart, DateTimeOffset now, double dailyRate)
    {
        if (dailyRate <= 0D)
        {
            return [new PacingPoint(windowStart, 0D), new PacingPoint(now, 0D)];
        }

        // Days needed to reach 100% at the configured daily rate.
        var saturationDays = 1D / dailyRate;
        var saturationAt = windowStart + TimeSpan.FromDays(saturationDays);

        if (saturationAt >= now)
        {
            var expectedNow = Math.Clamp(dailyRate * (now - windowStart).TotalDays, 0D, 1D);
            return [new PacingPoint(windowStart, 0D), new PacingPoint(now, expectedNow)];
        }

        return
        [
            new PacingPoint(windowStart, 0D),
            new PacingPoint(saturationAt, 1D),
            new PacingPoint(now, 1D)
        ];
    }
}
