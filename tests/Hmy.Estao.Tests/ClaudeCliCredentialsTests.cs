using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Providers;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Tests;

public sealed class ClaudeCliCredentialsTests
{
    [Fact]
    public async Task ReadsCurrentCliAccessTokenWithoutImportingOrModifyingItsRefreshToken()
    {
        var file = Path.Combine(Path.GetTempPath(), "estao-cli-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var content = JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = "first-access", refreshToken = "private-cli-refresh",
                expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() }, mcpOAuth = new { accessToken = "wrong-token" } });
            await File.WriteAllTextAsync(file, content);
            var token = await ClaudeCliCredentials.ReadAsync(CancellationToken.None, file, () => true);
            Assert.Equal("first-access", token!.AccessToken);
            Assert.Null(token.RefreshToken);
            Assert.Equal(content, await File.ReadAllTextAsync(file));
            await File.WriteAllTextAsync(file, content.Replace("first-access", "rotated-access"));
            Assert.Equal("rotated-access", (await ClaudeCliCredentials.ReadAsync(CancellationToken.None, file, () => true))!.AccessToken);
            Assert.Null(await ClaudeCliCredentials.ReadAsync(CancellationToken.None, file, () => false));
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = "expired-access",
                expiresAt = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds() } }));
            Assert.Null(await ClaudeCliCredentials.ReadAsync(CancellationToken.None, file, () => true));
            await File.WriteAllTextAsync(file, "{\"mcpOAuth\":{\"accessToken\":\"not-a-subscription\"}}");
            Assert.Null(await ClaudeCliCredentials.ReadAsync(CancellationToken.None, file, () => true));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task AutoUsagePrefersCliAndSignOutSuppressionUsesOnlyOwnedOAuth()
    {
        var seen = new List<string?>();
        using var client = new HttpClient(new Handler(request =>
        {
            seen.Add(request.Headers.Authorization?.Parameter);
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"five_hour\":{\"utilization\":20}}") };
        }));
        var factory = new UsageProviderFactory(client, oauthStore: new Tokens(),
            claudeCliCredentials: _ => Task.FromResult<OAuthToken?>(new("cli-access", null)));
        var config = new ProviderConfig { Id = "claude", Source = "auto" };
        await factory.Create("claude").FetchAsync(new(config, null, ProviderSource.Auto, CancellationToken.None));
        config.CliAutoSignInDisabled = true;
        await factory.Create("claude").FetchAsync(new(config, null, ProviderSource.Auto, CancellationToken.None));
        Assert.Equal(new[] { "cli-access", "owned-access" }, seen);
    }

    private sealed class Tokens : IOAuthTokenStore
    {
        public Task<OAuthToken?> ReadAsync(string providerId, CancellationToken cancellationToken = default) => Task.FromResult<OAuthToken?>(new("owned-access", null));
        public Task SaveAsync(string providerId, OAuthToken token, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }
}
