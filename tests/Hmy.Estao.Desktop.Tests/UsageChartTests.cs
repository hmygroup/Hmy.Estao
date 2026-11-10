using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using System.Reflection;
using System.Text.Json;

namespace Hmy.Estao.Desktop.Tests;

public sealed class UsageChartTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ChartsUseFullResetCyclesAndSeparateDifferentHorizons()
    {
        var sessionReset = Now.AddHours(2);
        var weeklyReset = Now.AddDays(4);
        var snapshot = new UsageSnapshot("claude", "Claude", "oauth", Now,
            [new("session", "Session", .4, sessionReset), new("weekly", "Weekly", .2, weeklyReset)]);
        var charts = UsageChartBuilder.Build(snapshot, [], new(), Now);
        Assert.Equal(2, charts.Count);
        Assert.Equal(sessionReset.AddHours(-5), charts[0].StartAt);
        Assert.Equal(sessionReset, charts[0].EndAt);
        Assert.Equal(weeklyReset.AddDays(-7), charts[1].StartAt);
        Assert.Equal(weeklyReset, charts[1].EndAt);
        Assert.Equal(new[] { "5h", "7d" }, charts.Select(c => c.Range));
        Assert.All(charts, c => Assert.Single(Assert.Single(c.Series, s => s.Kind == "usage").Points)); // No invented preview history or zero boundary.
    }

    [Fact]
    public void MatchingCyclesShareAChartAndPacingUsesTheSameCoreCalculatorsAsApp()
    {
        var reset = Now.AddHours(2);
        var snapshot = new UsageSnapshot("claude", "Claude", "oauth", Now,
            [new("session", "Session", .6, reset), new("five_hour_opus", "Opus", .3, reset)]);
        UsageHistoryPoint[] history = [new("claude", "session", Now.AddHours(-2), .1), new("claude", "session", Now.AddHours(-1), .3)];
        var chart = Assert.Single(UsageChartBuilder.Build(snapshot, history, new() { Enabled = true, DailyTargetPercent = 100 }, Now));
        Assert.Equal(2, chart.Series.Count(s => s.Kind == "usage"));
        var target = Assert.Single(chart.Series, s => s.Kind == "target");
        Assert.Equal(PacingCalculator.BuildTargetLine(chart.StartAt, reset, 100), target.Points);
        var forecast = UsageExhaustionCalculator.Compute(chart.Series.First(s => s.Kind == "usage" && s.Window == "session").Points, .6, TimeSpan.FromHours(5), Now, reset)!;
        Assert.Equal(forecast.EstimatedAt, chart.Forecast.At);
        Assert.False(chart.Forecast.ResetOccursFirst);
        var projection = Assert.Single(chart.Series, s => s.Kind == "projection");
        Assert.Equal(Now, projection.Points[0].Timestamp);
        Assert.True(projection.Points[^1].Timestamp <= reset);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 15)]
    [InlineData(true, 0)]
    public void FreshForecastProjectsWithoutAnEnabledDailyTarget(bool enabled, double target)
    {
        var reset = Now.AddHours(2);
        var snapshot = new UsageSnapshot("claude", "Claude", "oauth", Now,
            [new("session", "Session", .4, reset)]);
        UsageHistoryPoint[] history = [new("claude", "session", Now.AddHours(-2), .1), new("claude", "session", Now.AddHours(-1), .2)];

        var chart = Assert.Single(UsageChartBuilder.Build(snapshot, history,
            new() { Enabled = enabled, DailyTargetPercent = target }, Now));

        var projection = Assert.Single(chart.Series, s => s.Kind == "projection");
        Assert.Equal(Now, projection.Points[0].Timestamp);
        Assert.Equal(.4, projection.Points[0].Value);
        Assert.Equal(reset, projection.Points[^1].Timestamp);
        Assert.DoesNotContain(chart.Series, s => s.Kind == "target");
    }

    [Fact]
    public void ProjectionStopsAtExhaustionWhenItPrecedesReset()
    {
        var reset = Now.AddHours(4);
        var snapshot = new UsageSnapshot("claude", "Claude", "oauth", Now,
            [new("session", "Session", .8, reset)]);
        UsageHistoryPoint[] history = [new("claude", "session", Now.AddHours(-2), .1), new("claude", "session", Now.AddHours(-1), .2)];

        var chart = Assert.Single(UsageChartBuilder.Build(snapshot, history, new(), Now));
        var projection = Assert.Single(chart.Series, s => s.Kind == "projection");

        Assert.True(projection.Points[^1].Timestamp < reset);
        Assert.Equal(1D, projection.Points[^1].Value);
    }

    [Fact]
    public void NoRateOrNoFutureResetDoesNotProject()
    {
        var noRate = new UsageSnapshot("claude", "Claude", "oauth", Now,
            [new("session", "Session", 0D, Now.AddHours(2))]);
        var noRateChart = Assert.Single(UsageChartBuilder.Build(noRate, [], new(), Now));
        Assert.DoesNotContain(noRateChart.Series, s => s.Kind == "projection");

        var noReset = new UsageSnapshot("claude", "Claude", "oauth", Now,
            [new("session", "Session", .4, null)]);
        UsageHistoryPoint[] history = [new("claude", "session", Now.AddHours(-1), .2)];
        var noResetChart = Assert.Single(UsageChartBuilder.Build(noReset, history, new(), Now));
        Assert.DoesNotContain(noResetChart.Series, s => s.Kind == "projection");
    }

    [Fact]
    public void HistoryIsFilteredByProviderCycleAndFiniteValuesAndLatestReadingIsIncluded()
    {
        var reset = Now.AddHours(1);
        var snapshot = new UsageSnapshot("claude", "Claude", "auto", Now, [new("session", "Session", .5, reset)]);
        UsageHistoryPoint[] history = [new("codex", "session", Now.AddMinutes(-10), .9),
            new("claude", "session", Now.AddHours(-6), .2), new("claude", "session", Now.AddMinutes(2), .7),
            new("claude", "session", Now.AddMinutes(-20), double.NaN), new("claude", "session", Now.AddMinutes(-10), .4)];
        var chart = Assert.Single(UsageChartBuilder.Build(snapshot, history, new(), Now));
        var points = Assert.Single(chart.Series, s => s.Kind == "usage").Points;
        Assert.Equal(new[] { .4, .5 }, points.Select(p => p.Value));
        Assert.Equal(Now, points[^1].Timestamp);
    }

    [Fact]
    public void StaleReadingsKeepTheirTimestampAndHistoryWithoutAFreshProjection()
    {
        var updated = Now.AddHours(-1);
        var snapshot = new UsageSnapshot("claude", "Claude", "oauth", updated,
            [new("session", "Session", .5, Now.AddHours(1))], Error: "429", IsStale: true);
        var chart = Assert.Single(UsageChartBuilder.Build(snapshot, [], new() { Enabled = true, DailyTargetPercent = 10 }, Now));
        Assert.Equal(updated, chart.ObservedAt);
        Assert.Equal(Now, chart.NowAt);
        Assert.Equal(updated, chart.Series[0].Points[^1].Timestamp);
        Assert.Null(chart.Forecast.At);
        Assert.True(chart.Forecast.Unavailable);
        Assert.DoesNotContain(chart.Series, s => s.Kind == "projection");
    }

    [Fact]
    public void HistoryRemainsVisibleWhenProviderIsUnavailableAndUnknownResetUsesTrailingRange()
    {
        var snapshot = UsageSnapshot.Failure("copilot", "oauth", "offline") with { UpdatedAt = Now };
        var chart = Assert.Single(UsageChartBuilder.Build(snapshot,
            [new("copilot", "premium", Now.AddDays(-1), .1)], new(), Now));
        Assert.Equal(Now.AddDays(-30), chart.StartAt);
        Assert.Equal(Now, chart.EndAt);
        Assert.Single(Assert.Single(chart.Series).Points);
        Assert.Null(chart.Forecast.At);
    }

    [Theory]
    [InlineData("codex", "platform.openai.com", "status.openai.com")]
    [InlineData("claude", "claude.ai", "status.anthropic.com")]
    [InlineData("copilot", "github.com", "www.githubstatus.com")]
    [InlineData("opencode", "opencode.ai", "status.openai.com")]
    [InlineData("zai", "z.ai", "status.openai.com")]
    public void ProviderLinksMatchAppAndOnlyAllowKnownProviders(string provider, string dashboard, string status)
    {
        Assert.Equal(dashboard, ProviderLinks.Dashboard(provider).Host);
        Assert.Equal(status, ProviderLinks.Status(provider).Host);
        Assert.Equal("https", ProviderLinks.Dashboard(provider).Scheme);
        Assert.Throws<ArgumentException>(() => ProviderLinks.Dashboard("https://untrusted.invalid"));
        Assert.Throws<ArgumentException>(() => ProviderLinks.Status("https://untrusted.invalid"));
    }

    [Fact]
    public async Task NativeCommandsOpenOnlyTheSelectedProvidersApprovedLinks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "estao-links-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(Path.Combine(directory, "config.json"));
        var config = ConfigStore.CreateDefaultConfig();
        config.Refresh.Enabled = false;
        config.TaskbarOverlay.Enabled = false;
        foreach (var provider in config.Providers) provider.Enabled = false;
        await store.SaveAsync(config);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var language = DesktopLocalization.Language;
            try
            {
                using var context = new TrayApplicationContext(store);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var opened = new List<Uri>();
                typeof(TrayApplicationContext).GetField("_openBrowser", flags)!.SetValue(context, new Action<Uri>(opened.Add));
                void Command(string action, string provider) => ((Task)typeof(TrayApplicationContext)
                    .GetMethod("CommandAsync", flags)!.Invoke(context, [new DesktopCommand(action, provider)])!).GetAwaiter().GetResult();
                Command("usage-dashboard", "claude");
                Command("status-page", "copilot");
                Assert.Equal(new[] { ProviderLinks.Dashboard("claude"), ProviderLinks.Status("copilot") }, opened);
                Assert.Throws<ArgumentException>(() => Command("usage-dashboard", "https://untrusted.invalid"));
                Assert.Equal(2, opened.Count);
            }
            catch (Exception exception) { failure = exception; }
            finally { DesktopLocalization.SetLanguage(language); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { if (!thread.IsAlive && Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ChartPayloadUsesFrontendPropertyNamesAndStructuredTranslations()
    {
        var snapshot = new UsageSnapshot("claude", "Claude", "oauth", Now, [new("session", "Session", .5, Now.AddHours(2))]);
        var chart = Assert.Single(UsageChartBuilder.Build(snapshot, [], new(), Now));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(chart, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var series = json.RootElement.GetProperty("series")[0];
        Assert.Equal("usage", series.GetProperty("kind").GetString());
        Assert.Equal(.5, series.GetProperty("points")[0].GetProperty("value").GetDouble());
        Assert.Equal("window.session", series.GetProperty("label").GetProperty("code").GetString());
    }
}
