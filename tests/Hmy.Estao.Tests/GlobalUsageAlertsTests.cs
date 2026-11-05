using System.Text.Json;
using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Tests;

public sealed class GlobalUsageAlertsTests
{
    [Fact]
    public void OldConfigPromotesActiveCustomRuleWithoutChangingProviderRules()
    {
        var disabled = new ProviderConfig { Id = "codex", Enabled = false,
            UsageColors = new() { Enabled = true, WarningPercent = 50 } };
        var active = new ProviderConfig { Id = "claude", Enabled = true,
            UsageColors = new() { Enabled = true, WarningPercent = 65, CriticalPercent = 85, WarningColor = "#ABCDEF" } };
        var config = ConfigStore.Normalize(new EstaoConfig { Providers = [disabled, active] });

        Assert.Equal(65, config.UsageAlerts!.WarningPercent);
        Assert.Equal(85, config.UsageAlerts.CriticalPercent);
        Assert.Equal("#ABCDEF", config.UsageAlerts.WarningColor);
        config.UsageAlerts.WarningPercent = 70;
        Assert.Equal(65, active.UsageColors.WarningPercent);
        Assert.Equal(50, disabled.UsageColors.WarningPercent);
    }

    [Fact]
    public void ExistingGlobalRuleWinsIncludingWhenDisabled()
    {
        var config = ConfigStore.Normalize(new EstaoConfig
        {
            UsageAlerts = new() { Enabled = false, WarningPercent = 60 },
            Providers = [new() { Id = "codex", Enabled = true, UsageColors = new() { Enabled = true, WarningPercent = 20 } }]
        });
        Assert.False(config.UsageAlerts!.Enabled);
        Assert.Equal(60, config.UsageAlerts.WarningPercent);
    }

    [Fact]
    public void DefaultsAndThresholdNormalizationMatchExistingColors()
    {
        var defaults = ConfigStore.Normalize(ConfigStore.CreateDefaultConfig());
        Assert.Equal(UsageColorCatalog.DefaultWarningColor, defaults.UsageAlerts!.WarningColor);
        Assert.Equal(UsageColorCatalog.DefaultCriticalColor, defaults.UsageAlerts.CriticalColor);
        Assert.False(defaults.UsageAlerts.Enabled);
        defaults.UsageAlerts.WarningPercent = 150;
        defaults.UsageAlerts.CriticalPercent = 10;
        defaults.UsageAlerts.WarningColor = "not-a-color";
        ConfigStore.Normalize(defaults);
        Assert.Equal(99, defaults.UsageAlerts.WarningPercent);
        Assert.Equal(100, defaults.UsageAlerts.CriticalPercent);
        Assert.Equal(UsageColorCatalog.DefaultWarningColor, defaults.UsageAlerts.WarningColor);
    }

    [Fact]
    public async Task GlobalRuleRoundTripsAlongsideExistingProviderAndHarnessConfiguration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"estao-alerts-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConfigStore(path);
            var config = ConfigStore.Normalize(ConfigStore.CreateDefaultConfig());
            config.UsageAlerts = new() { Enabled = true, WarningPercent = 60, CriticalPercent = 80 };
            config.Providers[0].ApiKey = "existing-token";
            config.Providers[0].TokenAccounts = new() { Accounts = [new() { Label = "Work", Token = "saved-account" }] };
            config.TaskbarOverlay.PositionX = 123;
            config.TaskbarOverlay.PositionY = 456;
            var harness = JsonSerializer.Serialize(config.HarnessManager);
            await store.SaveAsync(config);
            var loaded = await store.LoadAsync();
            Assert.Equal(60, loaded.UsageAlerts!.WarningPercent);
            Assert.Equal("existing-token", loaded.Providers[0].ApiKey);
            Assert.Equal("saved-account", loaded.Providers[0].TokenAccounts!.Accounts[0].Token);
            Assert.Equal(123, loaded.TaskbarOverlay.PositionX);
            Assert.Equal(harness, JsonSerializer.Serialize(loaded.HarnessManager));
            Assert.Equal(UsageColorLevel.Warning, UsageColorCatalog.LevelFor(loaded.UsageAlerts, .7));
            Assert.Equal(UsageColorLevel.Critical, UsageColorCatalog.LevelFor(loaded.UsageAlerts, .9));
        }
        finally { File.Delete(path); }
    }
}
