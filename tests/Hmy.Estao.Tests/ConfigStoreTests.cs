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

        Assert.Equal(96, config.BackdropOpacity);
        Assert.Contains(config.Providers, provider => provider.Id == "codex" && provider.Enabled == true);
        Assert.Contains(config.Providers, provider => provider.Id == "claude");
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
        config.TaskbarOverlay.PositionX = 420;
        config.TaskbarOverlay.PositionY = 180;

        await store.SaveAsync(config);
        var loaded = await store.LoadAsync();

        Assert.Equal(420, loaded.TaskbarOverlay.PositionX);
        Assert.Equal(180, loaded.TaskbarOverlay.PositionY);

        loaded.TaskbarOverlay.PositionY = null;
        await store.SaveAsync(loaded);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var overlay = saved.RootElement.GetProperty("taskbarOverlay");
        Assert.False(overlay.TryGetProperty("positionX", out _));
        Assert.False(overlay.TryGetProperty("positionY", out _));
    }

    [Fact]
    public async Task backdrop_opacity_round_trips_and_is_normalized()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json");
        var store = new ConfigStore(path);
        var config = ConfigStore.CreateDefaultConfig();
        config.BackdropOpacity = 68;

        await store.SaveAsync(config);
        var loaded = await store.LoadAsync();

        Assert.Equal(68, loaded.BackdropOpacity);

        loaded.BackdropOpacity = 140;
        await store.SaveAsync(loaded);
        Assert.Equal(100, (await store.LoadAsync()).BackdropOpacity);
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
