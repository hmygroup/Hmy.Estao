using System.Text.Json;
using Hmy.Estao.Core.Providers;

namespace Hmy.Estao.Tests;

public sealed class ZaiProviderTests
{
    [Fact]
    public void parses_credit_limits_from_the_current_api_shape()
    {
        using var document = JsonDocument.Parse("""
        {
          "code": 200,
          "msg": "Operation successful",
          "success": true,
          "data": {
            "level": "lite",
            "limits": [
              { "type": "CREDIT_LIMIT", "unit": 3, "number": 5, "usage": 2000, "currentValue": 163, "remaining": 1836, "percentage": 8, "nextResetTime": 1788178718479 },
              { "type": "CREDIT_LIMIT", "unit": 6, "number": 1, "usage": 10000, "currentValue": 163, "remaining": 9836, "percentage": 1, "nextResetTime": 1788765392998 }
            ]
          }
        }
        """);

        var snapshot = ZaiProvider.ParseQuotaLimits(document.RootElement, "api-key");

        Assert.Equal("lite", snapshot.Plan);
        Assert.Equal(2, snapshot.Windows.Count);
        var session = snapshot.Windows.Single(window => window.Id == "session");
        Assert.Equal(.08D, session.PercentUsed);
        Assert.Equal(163D, session.Used);
        Assert.Equal(2000D, session.Limit);
        Assert.Equal("credits", session.Unit);
        var weekly = snapshot.Windows.Single(window => window.Id == "weekly");
        Assert.Equal(.01D, weekly.PercentUsed);
        Assert.Equal(10000D, weekly.Limit);
    }

    [Fact]
    public void parses_legacy_token_limits_into_five_hour_weekly_and_mcp_windows()
    {
        using var document = JsonDocument.Parse("""
        {
          "success": true,
          "data": {
            "level": "pro",
            "limits": [
              { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 42, "nextResetTime": 1779459394000 },
              { "type": "TOKENS_LIMIT", "unit": 6, "number": 1, "percentage": 12.5, "nextResetTime": 1779826837000 },
              { "type": "TIME_LIMIT", "percentage": 33, "currentValue": 33, "usage": 100 }
            ]
          }
        }
        """);

        var snapshot = ZaiProvider.ParseQuotaLimits(document.RootElement, "api-key");

        Assert.Equal("pro", snapshot.Plan);
        Assert.Equal(3, snapshot.Windows.Count);
        Assert.Equal("session", snapshot.Windows[0].Id);
        Assert.Equal(.42D, snapshot.Windows.Single(window => window.Id == "session").PercentUsed);
        Assert.Equal(.125D, snapshot.Windows.Single(window => window.Id == "weekly").PercentUsed);
        Assert.Equal(.33D, snapshot.Windows.Single(window => window.Id == "mcp-monthly").PercentUsed);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1779459394000),
            snapshot.Windows.Single(window => window.Id == "session").ResetAt);
        Assert.Null(snapshot.Windows.Single(window => window.Id == "mcp-monthly").ResetAt);
    }

    [Fact]
    public void token_limits_without_unit_hints_default_to_the_five_hour_window()
    {
        using var document = JsonDocument.Parse("""
        {
          "success": true,
          "data": {
            "limits": [
              { "type": "TOKENS_LIMIT", "percentage": 4 }
            ]
          }
        }
        """);

        var snapshot = ZaiProvider.ParseQuotaLimits(document.RootElement, null);

        var session = Assert.Single(snapshot.Windows);
        Assert.Equal("session", session.Id);
        Assert.Equal(.04D, session.PercentUsed);
    }

    [Fact]
    public void orders_session_before_weekly_windows()
    {
        using var document = JsonDocument.Parse("""
        {
          "success": true,
          "data": {
            "limits": [
              { "type": "TIME_LIMIT", "percentage": 1 },
              { "type": "TOKENS_LIMIT", "unit": 6, "number": 1, "percentage": 2 },
              { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 3 }
            ]
          }
        }
        """);

        var snapshot = ZaiProvider.ParseQuotaLimits(document.RootElement, null);

        Assert.Equal(["session", "weekly", "mcp-monthly"], snapshot.Windows.Select(window => window.Id).ToArray());
    }

    [Fact]
    public void missing_quota_limits_returns_failure()
    {
        using var document = JsonDocument.Parse("""{ "success": true, "data": {} }""");

        var snapshot = ZaiProvider.ParseQuotaLimits(document.RootElement, null);

        Assert.NotNull(snapshot.Error);
        Assert.Empty(snapshot.Windows);
    }
}
