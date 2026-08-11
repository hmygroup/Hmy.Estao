using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Tests;

public sealed class UsageWindowCatalogTests
{
    [Theory]
    [InlineData("codex", "session", "Session", 5)]
    [InlineData("claude", "five_hour", "5 hour", 5)]
    [InlineData("codex", "weekly", "Weekly", 168)]
    [InlineData("copilot", "premium_interactions", "Premium", 720)]
    [InlineData("copilot", "chat", "Chat", 720)]
    public void selects_the_display_horizon_for_each_rate_window(
        string provider, string window, string title, double expectedHours)
    {
        var range = UsageWindowCatalog.DisplayRange(provider, window, title);

        Assert.Equal(expectedHours, range.TotalHours);
    }
}
