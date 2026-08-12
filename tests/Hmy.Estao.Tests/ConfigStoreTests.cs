using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Platform;
using System.Text.Json;

namespace Hmy.Estao.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public async Task load_returns_default_config_when_file_is_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json");
        var config = await new ConfigStore(path).LoadAsync();

        Assert.Contains(config.Providers, provider => provider.Id == "codex" && provider.Enabled == true);
        Assert.Contains(config.Providers, provider => provider.Id == "claude");
        Assert.True(config.TaskbarOverlay.MoveEnabled);
        Assert.All(config.Providers, provider => Assert.False(provider.UsageColors.Enabled));
    }

    [Fact]
    public async Task save_and_load_preserves_compatible_raw_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json");
        var store = new ConfigStore(path);
        await store.SaveAsync(new EstaoConfig
        {
            Theme = "NordFrost",
            Providers =
            [
                new ProviderConfig
                {
                    Id = "copilot",
                    Enabled = true,
                    Source = "api",
                    ApiKey = "secret",
                    EnterpriseHost = "github.example.com"
                }
            ]
        });

        var loaded = await store.LoadAsync();
        Assert.Equal("NordFrost", loaded.Theme);
        var copilot = Assert.Single(loaded.Providers, provider => provider.Id == "copilot");
        Assert.Equal("secret", copilot.ApiKey);
        Assert.Equal("github.example.com", copilot.EnterpriseHost);
        Assert.False(copilot.Pacing.Enabled);
    }

    [Fact]
    public async Task legacy_global_pacing_is_migrated_to_each_provider()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, """
            {
              "version": 1,
              "pacing": { "enabled": true, "dailyTargetPercent": 22, "notifyOnExceed": false },
              "providers": [
                { "id": "codex", "enabled": true },
                { "id": "claude", "enabled": true }
              ]
            }
            """);

        var config = await new ConfigStore(path).LoadAsync();

        Assert.Null(config.LegacyPacing);
        Assert.All(config.Providers.Where(provider => provider.Id is "codex" or "claude"), provider =>
        {
            Assert.True(provider.Pacing.Enabled);
            Assert.Equal(22D, provider.Pacing.DailyTargetPercent);
            Assert.False(provider.Pacing.NotifyOnExceed);
        });

        await new ConfigStore(path).SaveAsync(config);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.False(saved.RootElement.TryGetProperty("pacing", out _));
        Assert.All(saved.RootElement.GetProperty("providers").EnumerateArray(), provider =>
            Assert.True(provider.TryGetProperty("pacing", out _)));
    }

    [Fact]
    public async Task provider_pacing_takes_precedence_over_legacy_global_pacing()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, """
            {
              "version": 1,
              "pacing": { "enabled": true, "dailyTargetPercent": 22 },
              "providers": [
                {
                  "id": "codex",
                  "enabled": true,
                  "pacing": { "enabled": true, "dailyTargetPercent": 9 }
                }
              ]
            }
            """);

        var config = await new ConfigStore(path).LoadAsync();

        var codex = Assert.Single(config.Providers, provider => provider.Id == "codex");
        Assert.Equal(9D, codex.Pacing.DailyTargetPercent);
        var claude = Assert.Single(config.Providers, provider => provider.Id == "claude");
        Assert.Equal(22D, claude.Pacing.DailyTargetPercent);
    }

    [Fact]
    public async Task taskbar_overlay_custom_position_is_optional_and_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json");
        var store = new ConfigStore(path);
        var config = ConfigStore.CreateDefaultConfig();
        config.TaskbarOverlay.MoveEnabled = false;
        config.TaskbarOverlay.PositionX = 420;
        config.TaskbarOverlay.PositionY = 180;

        await store.SaveAsync(config);
        var loaded = await store.LoadAsync();

        Assert.Equal(420, loaded.TaskbarOverlay.PositionX);
        Assert.Equal(180, loaded.TaskbarOverlay.PositionY);
        Assert.False(loaded.TaskbarOverlay.MoveEnabled);

        loaded.TaskbarOverlay.PositionY = null;
        await store.SaveAsync(loaded);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var overlay = saved.RootElement.GetProperty("taskbarOverlay");
        Assert.False(overlay.TryGetProperty("positionX", out _));
        Assert.False(overlay.TryGetProperty("positionY", out _));
    }

    [Fact]
    public async Task provider_usage_colors_are_normalized_and_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json");
        var store = new ConfigStore(path);
        var config = ConfigStore.CreateDefaultConfig();
        var codex = config.Providers.Single(provider => provider.Id == "codex");
        codex.UsageColors.Enabled = true;
        codex.UsageColors.WarningPercent = 82;
        codex.UsageColors.WarningColor = "#a1b2c3";
        codex.UsageColors.CriticalPercent = 40;
        codex.UsageColors.CriticalColor = "not-a-color";

        await store.SaveAsync(config);
        var loaded = await store.LoadAsync();
        var colors = loaded.Providers.Single(provider => provider.Id == "codex").UsageColors;

        Assert.True(colors.Enabled);
        Assert.Equal(82D, colors.WarningPercent);
        Assert.Equal("#A1B2C3", colors.WarningColor);
        Assert.Equal(83D, colors.CriticalPercent);
        Assert.Equal(UsageColorCatalog.DefaultCriticalColor, colors.CriticalColor);
    }

    [Fact]
    public void provider_usage_color_thresholds_remain_distinct_at_the_upper_limit()
    {
        var config = ConfigStore.CreateDefaultConfig();
        var colors = config.Providers[0].UsageColors;
        colors.WarningPercent = 100;
        colors.CriticalPercent = 20;

        ConfigStore.Normalize(config);

        Assert.Equal(99D, colors.WarningPercent);
        Assert.Equal(100D, colors.CriticalPercent);
    }

    [Fact]
    public void resolves_hmy_estao_config_override()
    {
        var env = new FakeEnvironment("C:\\Users\\me", "D:\\configs\\estao.json");

        Assert.Equal("D:\\configs\\estao.json", EstaoPaths.ResolveConfigPath(env));
    }

    private sealed class FakeEnvironment(string profile, string? config) : IEnvironment
    {
        public string? GetEnvironmentVariable(string name) => name == "HMY_ESTAO_CONFIG" ? config : null;

        public string GetFolderPath(Environment.SpecialFolder folder) => folder switch
        {
            Environment.SpecialFolder.UserProfile => profile,
            Environment.SpecialFolder.ApplicationData => Path.Combine(profile, "AppData", "Roaming"),
            _ => string.Empty
        };
    }
}
