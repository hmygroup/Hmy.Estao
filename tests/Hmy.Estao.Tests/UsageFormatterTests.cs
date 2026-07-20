using Hmy.Estao.Core.Formatting;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Tests;

public sealed class UsageFormatterTests
{
    [Fact]
    public void text_output_includes_provider_window_and_account()
    {
        var snapshot = new UsageSnapshot(
            "codex",
            "Codex",
            "oauth",
            DateTimeOffset.UtcNow,
            [new RateWindow("session", "Session", 0.25, DateTimeOffset.Parse("2026-07-20T13:00:00Z"))],
            Account: "user@example.com",
            Plan: "Pro");

        var text = UsageFormatter.ToText([snapshot]);

        Assert.Contains("== Codex (oauth) ==", text);
        Assert.Contains("Session: 75", text);
        Assert.Contains("Account: user@example.com", text);
    }
}
