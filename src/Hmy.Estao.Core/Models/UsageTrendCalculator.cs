namespace Hmy.Estao.Core.Models;

public sealed record UsageTrendProjection(
    IReadOnlyList<PacingPoint> Line,
    DateTimeOffset EndAt,
    bool BreachesPacing);

/// <summary>
/// Projects the observed usage slope through the current provider cycle and
/// stops at the first pacing breach, exhaustion, or reset boundary.
/// </summary>
public static class UsageTrendCalculator
{
    public static UsageTrendProjection? Compute(
        double currentPercentUsed,
        double dailyConsumptionRate,
        double dailyTargetPercent,
        DateTimeOffset windowStart,
        DateTimeOffset now,
        DateTimeOffset windowEnd)
    {
        if (windowEnd <= now || now < windowStart || dailyConsumptionRate <= 0D ||
            double.IsNaN(dailyConsumptionRate) || double.IsInfinity(dailyConsumptionRate))
            return null;

        var current = Math.Clamp(currentPercentUsed, 0D, 1D);
        var actualRate = dailyConsumptionRate;
        var targetRate = Math.Clamp(dailyTargetPercent, 0D, 1000D) / 100D;
        var elapsedDays = (now - windowStart).TotalDays;
        var horizonDays = (windowEnd - now).TotalDays;
        var paceNow = Math.Clamp(targetRate * elapsedDays, 0D, 1D);
        var alreadyBreached = current >= paceNow;

        double? breachDays = null;
        if (!alreadyBreached && targetRate > 0D && actualRate > targetRate && paceNow < 1D)
        {
            var candidate = (paceNow - current) / (actualRate - targetRate);
            var targetSaturatesIn = Math.Max(0D, 1D / targetRate - elapsedDays);
            if (candidate >= 0D && candidate <= horizonDays && candidate <= targetSaturatesIn)
                breachDays = candidate;
        }

        var exhaustionDays = current >= 1D ? 0D : (1D - current) / actualRate;
        if (!alreadyBreached && breachDays is null && exhaustionDays <= horizonDays)
            breachDays = exhaustionDays;

        var endDays = breachDays ?? Math.Min(exhaustionDays, horizonDays);
        if (alreadyBreached) endDays = Math.Min(exhaustionDays, horizonDays);
        endDays = Math.Clamp(endDays, 0D, horizonDays);
        if (endDays <= 0D) return null;

        var endMilliseconds = Math.Round(endDays * TimeSpan.FromDays(1).TotalMilliseconds);
        var endAt = now + TimeSpan.FromMilliseconds(endMilliseconds);
        var endValue = Math.Clamp(current + actualRate * endDays, 0D, 1D);
        return new UsageTrendProjection(
            [new PacingPoint(now, current), new PacingPoint(endAt, endValue)],
            endAt,
            alreadyBreached || breachDays is not null);
    }
}
