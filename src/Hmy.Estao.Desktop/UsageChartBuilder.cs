using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Desktop;

internal sealed record UsageChartSeries(string Window, UserMessage Label, IReadOnlyList<PacingPoint> Points,
    string Kind = "usage", int ColorIndex = 0);
internal sealed record UsageChartForecast(UserMessage Label, DateTimeOffset? At, bool ResetOccursFirst, bool Unavailable);
internal sealed record UsageChart(string Provider, DateTimeOffset StartAt, DateTimeOffset EndAt,
    DateTimeOffset ObservedAt, DateTimeOffset NowAt, string Range, IReadOnlyList<UsageChartSeries> Series, UsageChartForecast Forecast);

internal static class UsageChartBuilder
{
    public static IReadOnlyList<UsageChart> Build(UsageSnapshot snapshot, IReadOnlyList<UsageHistoryPoint> history,
        PacingConfig pacing, DateTimeOffset now)
    {
        var stored = history.Where(p => p.Provider.Equals(snapshot.Provider, StringComparison.OrdinalIgnoreCase)
            && double.IsFinite(p.PercentUsed) && p.Timestamp <= now).ToArray();
        var windows = snapshot.Windows.ToList();
        foreach (var id in stored.Select(p => p.Window).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!windows.Any(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                windows.Add(new(id, id, null, null));

        var charts = new List<UsageChart>();
        foreach (var cycle in windows.GroupBy(w => (
            Range: UsageWindowCatalog.DisplayRange(snapshot.Provider, w.Id, w.Title),
            Reset: w.ResetAt is { } reset && reset > now ? (DateTimeOffset?)reset : null)))
        {
            var end = cycle.Key.Reset ?? now;
            var start = end - cycle.Key.Range;
            var series = new List<UsageChartSeries>();
            foreach (var window in cycle)
            {
                var points = stored.Where(p => p.Window.Equals(window.Id, StringComparison.OrdinalIgnoreCase)
                    && p.Timestamp >= start).Select(p => new PacingPoint(p.Timestamp, Math.Clamp(p.PercentUsed, 0, 1))).ToList();
                // Keep the reading's original timestamp, including during a stale-data retry.
                if (window.PercentUsed is { } used && double.IsFinite(used) && snapshot.UpdatedAt >= start && snapshot.UpdatedAt <= now)
                {
                    points.RemoveAll(p => p.Timestamp >= snapshot.UpdatedAt);
                    points.Add(new(snapshot.UpdatedAt, Math.Clamp(used, 0, 1)));
                }
                series.Add(new(window.Id, window.TitleMessage, points.OrderBy(p => p.Timestamp).ToArray(), ColorIndex: series.Count));
            }

            if (pacing.Enabled && pacing.DailyTargetPercent > 0)
                series.Add(new("target", new("chart.target"), PacingCalculator.BuildTargetLine(start, end, pacing.DailyTargetPercent), "target"));

            var reference = cycle.OrderByDescending(w => w.PercentUsed ?? 0).First();
            var actual = series.First(s => s.Window == reference.Id);
            // Do not move stale observations to now or invent a fresh depletion rate.
            var observedAt = snapshot.UpdatedAt < now ? snapshot.UpdatedAt : now;
            var forecast = snapshot.Error is null && !snapshot.IsStale && observedAt >= start
                ? UsageExhaustionCalculator.Compute(actual.Points, reference.PercentUsed, cycle.Key.Range, observedAt, reference.ResetAt)
                : null;
            if (forecast is not null && cycle.Key.Reset is { } resetAt)
            {
                var trend = UsageTrendCalculator.Compute(reference.PercentUsed ?? actual.Points.LastOrDefault().Value,
                    forecast.DailyConsumptionRate,
                    pacing.Enabled && pacing.DailyTargetPercent > 0 ? pacing.DailyTargetPercent : 0D,
                    start, observedAt, resetAt);
                if (trend is not null)
                    series.Add(new(reference.Id, new("chart.trend"), trend.Line, "projection"));
            }
            charts.Add(new(snapshot.Provider, start, end, observedAt, now, UsageWindowCatalog.DisplayLabel(cycle.Key.Range), series,
                new(reference.TitleMessage, forecast is null ? null : forecast.ResetOccursFirst ? forecast.ResetAt : forecast.EstimatedAt,
                    forecast?.ResetOccursFirst == true, snapshot.IsStale || snapshot.Error is not null)));
        }
        return charts;
    }
}
