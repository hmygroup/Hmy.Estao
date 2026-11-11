using System.Text.Json;
using Hmy.Estao.Core.Providers;

namespace Hmy.Estao.Tests;

public sealed class CodexProviderTests
{
    [Fact]
    public async Task cli_waits_for_handshake_and_detects_signed_out_before_requesting_usage()
    {
        using var output = new StringReader("""
            {"id":1,"result":{}}
            {"id":2,"result":{"account":null,"requiresOpenaiAuth":true}}
            """);
        using var input = new StringWriter();
        var snapshot = await CodexProvider.ReadCliUsageAsync(output, input, "Default", CancellationToken.None);
        Assert.Contains("not signed in", snapshot.Error);
        Assert.Contains("account/read", input.ToString());
        Assert.DoesNotContain("account/rateLimits/read", input.ToString());
        Assert.DoesNotContain("account/login/start", input.ToString());
    }

    [Fact]
    public async Task cli_requests_usage_after_the_account_response()
    {
        using var output = new StringReader("""
            {"id":1,"result":{}}
            {"method":"account/updated","params":{"authMode":"chatgpt"}}
            {"id":2,"result":{"account":{"type":"chatgpt","email":"fixture@example.test","planType":"plus"}}}
            {"id":3,"result":{"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":300}}}}
            """);
        using var input = new StringWriter();
        var snapshot = await CodexProvider.ReadCliUsageAsync(output, input, "Default", CancellationToken.None);
        Assert.Null(snapshot.Error);
        Assert.Equal(.25, Assert.Single(snapshot.Windows).PercentUsed);
        Assert.Contains("account/rateLimits/read", input.ToString());
        Assert.DoesNotContain("jsonrpc", input.ToString());
    }

    [Fact]
    public async Task cli_displays_one_percent_weekly_usage_as_one_percent_not_one_hundred()
    {
        using var output = new StringReader("""
            {"id":1,"result":{}}
            {"id":2,"result":{"account":{"type":"chatgpt","email":"fixture@example.test","planType":"plus"}}}
            {"id":3,"result":{"rateLimits":{"primary":{"usedPercent":13,"windowDurationMins":300},"secondary":{"usedPercent":1,"windowDurationMins":10080}}}}
            """);
        using var input = new StringWriter();
        var snapshot = await CodexProvider.ReadCliUsageAsync(output, input, "Default", CancellationToken.None);

        Assert.Null(snapshot.Error);
        Assert.Equal(.13D, snapshot.Windows.Single(window => window.Id == "session").PercentUsed);
        Assert.Equal(.01D, snapshot.Windows.Single(window => window.Id == "weekly").PercentUsed);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(.5, .005)]
    [InlineData(1, .01)]
    [InlineData(2, .02)]
    [InlineData(100, 1)]
    public void cli_percentage_scale_is_consistent_below_at_and_above_one(double reported, double expected)
    {
        foreach (var field in new[] { "usedPercent", "percentUsed", "usagePercent" })
        {
            var window = new Dictionary<string, object> { [field] = reported, ["windowDurationMins"] = 10080 };
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                result = new { rateLimits = new { secondary = window } }
            }));
            var snapshot = CodexProvider.ParseRateLimits(document.RootElement, "cli", null);
            Assert.Equal(expected, Assert.Single(snapshot.Windows).PercentUsed);
        }
    }

    [Fact]
    public async Task cli_errors_do_not_expose_raw_server_details()
    {
        using var output = new StringReader("""
            {"id":1,"error":{"message":"sensitive details"}}
            """);
        using var input = new StringWriter();
        var snapshot = await CodexProvider.ReadCliUsageAsync(output, input, "Default", CancellationToken.None);
        Assert.NotNull(snapshot.Error);
        Assert.DoesNotContain("sensitive details", snapshot.Error);
    }

    [Fact]
    public void signed_out_cli_account_reports_sign_in_guidance()
    {
        var snapshot = CodexProvider.ParseCliOutput("""
            {"id":1,"result":{}}
            {"id":2,"result":{"account":null,"requiresOpenaiAuth":true}}
            {"id":3,"error":{"message":"not authenticated"}}
            """, "Default");
        Assert.Contains("not signed in", snapshot.Error);
        Assert.Contains("Settings", snapshot.Error);
        Assert.DoesNotContain("codex.cmd", snapshot.Error);
    }

    [Fact]
    public void signed_in_cli_output_still_returns_usage()
    {
        var snapshot = CodexProvider.ParseCliOutput("""
            {"id":1,"result":{}}
            {"id":2,"result":{"account":{"type":"chatgpt","email":"fixture@example.test","planType":"plus"}}}
            {"id":3,"result":{"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":300}}}}
            """, "Default");
        Assert.Null(snapshot.Error);
        Assert.Equal(.25, Assert.Single(snapshot.Windows).PercentUsed);
    }

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
    public void interprets_one_used_percent_from_wham_as_one_percent()
    {
        using var document = JsonDocument.Parse("""
        {
          "rate_limit": {
            "primary_window": { "used_percent": 1, "limit_window_seconds": 604800 }
          }
        }
        """);

        var snapshot = CodexProvider.ParseRateLimits(document.RootElement, "oauth", null);

        Assert.Equal(.01D, Assert.Single(snapshot.Windows).PercentUsed);
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
