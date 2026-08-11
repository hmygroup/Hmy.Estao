namespace Hmy.Estao.Core.Models;

public sealed record UsageExhaustionForecast(
    DateTimeOffset EstimatedAt,
    DateTimeOffset? ResetAt,
    bool ResetOccursFirst,
    double DailyConsumptionRate);

/// <summary>
/// Projects when a rate limit reaches 100% from its observed consumption
/// velocity. A detected reset starts a new trend; with sparse history, the
/// provider's reset boundary supplies the beginning of the current window.
/// </summary>
public static class UsageExhaustionCalculator
{
    private const double ResetDropThreshold = 0.05D;

    public static UsageExhaustionForecast? Compute(
        IReadOnlyList<PacingPoint> points,
        double? currentPercentUsed,
        TimeSpan windowRange,
        DateTimeOffset now,
        DateTimeOffset? resetAt)
    {
        if (windowRange <= TimeSpan.Zero) return null;

        var nominalStart = resetAt is { } reset && reset > now
            ? reset - windowRange
            : now - windowRange;
        var ordered = points
            .Where(point => point.Timestamp >= nominalStart && point.Timestamp <= now &&
                !double.IsNaN(point.Value) && !double.IsInfinity(point.Value))
            .OrderBy(point => point.Timestamp)
            .ToArray();

        var windowStart = nominalStart;
        for (var index = ordered.Length - 1; index > 0; index--)
        {
            if (ordered[index].Value + ResetDropThreshold < ordered[index - 1].Value)
            {
                windowStart = ordered[index].Timestamp;
                break;
            }
        }

        var fallbackCurrent = ordered.Length == 0 ? 0D : ordered[^1].Value;
        var current = Math.Clamp(currentPercentUsed ?? fallbackCurrent, 0D, 1D);
        if (current <= 0D) return null;
        if (current >= 1D)
            return new UsageExhaustionForecast(now, resetAt, ResetOccursFirst: false, double.PositiveInfinity);

        var trend = ordered.Where(point => point.Timestamp >= windowStart).ToArray();
        var baselineAt = windowStart;
        var baselineValue = 0D;
        if (trend.Length >= 2)
        {
            baselineAt = trend[0].Timestamp;
            baselineValue = Math.Clamp(trend[0].Value, 0D, 1D);
        }

        var elapsedDays = (now - baselineAt).TotalDays;
        var consumed = current - baselineValue;
        if (elapsedDays <= 0D || consumed <= 0D)
        {
            // Providers such as Copilot may return several unchanged samples
            // before the first observable increment. Fall back to the average
            // consumption since reset instead of suppressing the forecast.
            baselineAt = windowStart;
            baselineValue = 0D;
            elapsedDays = (now - baselineAt).TotalDays;
            consumed = current;
        }
        if (elapsedDays <= 0D || consumed <= 0D) return null;

        var dailyRate = consumed / elapsedDays;
        if (dailyRate <= 0D || double.IsInfinity(dailyRate) || double.IsNaN(dailyRate)) return null;

        var estimatedAt = now + TimeSpan.FromDays((1D - current) / dailyRate);
        var resetOccursFirst = resetAt is { } nextReset && nextReset > now && nextReset <= estimatedAt;
        return new UsageExhaustionForecast(estimatedAt, resetAt, resetOccursFirst, dailyRate);
    }
}
