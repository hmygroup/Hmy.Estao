using Hmy.Estao.Core.Refresh;

namespace Hmy.Estao.Tests;

public sealed class RefreshTests
{
    [Fact]
    public void adaptive_delay_matches_conservative_policy()
    {
        var now = DateTimeOffset.Parse("2026-07-20T12:00:00Z");

        Assert.Equal(TimeSpan.FromMinutes(30), UsageRefreshService.AdaptiveDelay(now, null, false));
        Assert.Equal(TimeSpan.FromMinutes(2), UsageRefreshService.AdaptiveDelay(now, now.AddMinutes(-2), false));
        Assert.Equal(TimeSpan.FromMinutes(5), UsageRefreshService.AdaptiveDelay(now, now.AddMinutes(-30), false));
        Assert.Equal(TimeSpan.FromMinutes(15), UsageRefreshService.AdaptiveDelay(now, now.AddHours(-2), false));
        Assert.Equal(TimeSpan.FromMinutes(30), UsageRefreshService.AdaptiveDelay(now, now.AddMinutes(-1), true));
    }
}
