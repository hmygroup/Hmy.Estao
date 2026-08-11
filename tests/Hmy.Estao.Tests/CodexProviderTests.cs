using System.Text.Json;
using Hmy.Estao.Core.Providers;

namespace Hmy.Estao.Tests;

public sealed class CodexProviderTests
{
    [Fact]
    public void parses_current_app_server_rate_limits_and_maps_by_duration()
    {
        using var document = JsonDocument.Parse("""
        {
          "id": 3,
          "result": {
            "rateLimits": {
              "planType": "pro",
              "primary": { "usedPercent": 25, "windowDurationMins": 300, "resetsAt": 1779459394 },
              "secondary": { "usedPercent": 18, "windowDurationMins": 10080, "resetsAt": 1779826837 },
              "credits": { "unlimited": false, "balance": "766.76" }
            }
          }
        }
        """);

        var snapshot = CodexProvider.ParseRateLimits(document.RootElement, "cli", "fallback@example.com");

        Assert.Equal("pro", snapshot.Plan);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal(.25D, snapshot.Windows.Single(window => window.Id == "session").PercentUsed);
        Assert.Equal(.18D, snapshot.Windows.Single(window => window.Id == "weekly").PercentUsed);
        Assert.Equal(766.76D, snapshot.Credits?.Balance);
    }

    [Fact]
    public void parses_legacy_wham_rate_limits()
    {
        using var document = JsonDocument.Parse("""
        {
          "plan_type": "pro",
          "rate_limit": {
            "primary_window": { "used_percent": 0, "limit_window_seconds": 18000, "reset_at": 1762147153 },
            "secondary_window": { "used_percent": 100, "limit_window_seconds": 604800, "reset_at": 1762650589 }
          }
        }
        """);

        var snapshot = CodexProvider.ParseRateLimits(document.RootElement, "oauth", null);

        Assert.Equal("pro", snapshot.Plan);
        Assert.Equal(0D, snapshot.Windows.Single(window => window.Id == "session").PercentUsed);
        Assert.Equal(1D, snapshot.Windows.Single(window => window.Id == "weekly").PercentUsed);
    }

    [Fact]
    public void ignores_null_secondary_when_primary_is_the_weekly_window()
    {
        using var document = JsonDocument.Parse("""
        {
          "result": {
            "rateLimits": {
              "primary": { "usedPercent": 2, "windowDurationMins": 10080, "resetsAt": 1787030441 },
              "secondary": null,
              "planType": "plus"
            }
          }
        }
        """);

        var snapshot = CodexProvider.ParseRateLimits(document.RootElement, "cli", null);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal("weekly", window.Id);
        Assert.Equal(.02D, window.PercentUsed);
        Assert.NotNull(window.ResetAt);
    }

    [Fact]
    public void prefers_codex_entry_from_rate_limits_by_limit_id()
    {
        using var document = JsonDocument.Parse("""
        {
          "result": {
            "rateLimits": { "primary": null, "secondary": null, "planType": "plus" },
            "rateLimitsByLimitId": {
              "codex": {
                "primary": { "usedPercent": 42, "windowDurationMins": 10080, "resetsAt": 1779826837 }
              }
            }
          }
        }
        """);

        var snapshot = CodexProvider.ParseRateLimits(document.RootElement, "cli", null);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal("weekly", window.Id);
        Assert.Equal(.42D, window.PercentUsed);
        Assert.NotNull(window.ResetAt);
    }
}
