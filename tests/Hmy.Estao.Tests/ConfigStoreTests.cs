using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Platform;

namespace Hmy.Estao.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public async Task load_returns_default_config_when_file_is_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json");
        var config = await new ConfigStore(path).LoadAsync();

        Assert.Contains(config.Providers, provider => provider.Id == "codex" && provider.Enabled == false);
        Assert.Contains(config.Providers, provider => provider.Id == "claude");
    }

    [Fact]
    public async Task save_and_load_preserves_compatible_raw_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json");
        var store = new ConfigStore(path);
        await store.SaveAsync(new EstaoConfig
        {
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
        var copilot = Assert.Single(loaded.Providers, provider => provider.Id == "copilot");
        Assert.Equal("secret", copilot.ApiKey);
        Assert.Equal("github.example.com", copilot.EnterpriseHost);
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
