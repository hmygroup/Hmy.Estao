using System.Net;
using System.Text;
using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Providers;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Tests;

public sealed class ClaudeOAuthTests
{
    [Theory]
    [InlineData(1, 0.01)]
    [InlineData(0.5, 0.005)]
    [InlineData(100, 1)]
    public async Task ClaudeUtilizationIsAWholePercentage(double used, double expected)
    {
        var store = new Tokens { Value = new("access", null) };
        using var client = new HttpClient(new Handler(_ => Task.FromResult(Json(JsonSerializer.Serialize(new { five_hour = new { utilization = used } })))));
        var provider = new UsageProviderFactory(client, oauthStore: store).Create("claude");
        var snapshot = await provider.FetchAsync(new(new ProviderConfig { Id = "claude", Source = "oauth" }, null, ProviderSource.OAuth, CancellationToken.None));
        Assert.Equal(expected, Assert.Single(snapshot.Windows).PercentUsed);
    }

    [Fact]
    public async Task LateRefreshCannotUndoSignOutOrOverwriteANewerLogin()
    {
        var directory = Path.Combine(Path.GetTempPath(), "estao-oauth-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SecureOAuthTokenStore(Path.Combine(directory, "oauth.json"));
            var old = new OAuthToken("old", null, "old-refresh");
            var refreshed = new OAuthToken("refreshed", null, "refreshed-refresh");
            await store.SaveAsync("claude", old);
            await store.ClearAsync("claude");
            Assert.False(await store.TryReplaceAsync("claude", old, refreshed));
            Assert.Null(await store.ReadAsync("claude"));
            var newer = new OAuthToken("newer-login", null, "newer-refresh");
            await store.SaveAsync("claude", newer);
            Assert.False(await store.TryReplaceAsync("claude", old, refreshed));
            Assert.Equal(newer, await store.ReadAsync("claude"));
            Assert.True(await store.TryReplaceAsync("claude", newer, refreshed));
            Assert.Equal(refreshed, await store.ReadAsync("claude"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OwnedExpiredTokensAreRefreshedAndRotatedWithoutCliCredentials()
    {
        var store = new Tokens { Value = new("old-access", "fixture-account", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-5), "user:profile") };
        var calls = 0;
        using var client = new HttpClient(new Handler(async request =>
        {
            calls++;
            if (request.Method == HttpMethod.Post)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("old-refresh", json.RootElement.GetProperty("refresh_token").GetString());
                Assert.Equal(ClaudeOAuthClient.ClientId, json.RootElement.GetProperty("client_id").GetString());
                return Json("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600,\"scope\":\"user:profile\"}");
            }
            Assert.Equal("new-access", request.Headers.Authorization!.Parameter);
            return Json("{\"five_hour\":{\"utilization\":20}}");
        }));
        var provider = new UsageProviderFactory(client, oauthStore: store).Create("claude");
        var snapshot = await provider.FetchAsync(new(new ProviderConfig { Id = "claude", Source = "oauth" }, null, ProviderSource.OAuth, CancellationToken.None));
        Assert.Null(snapshot.Error);
        Assert.Equal(0.2, Assert.Single(snapshot.Windows).PercentUsed);
        Assert.Equal("new-refresh", store.Value!.RefreshToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task MissingUsageScopeIsRejected()
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(Json("{\"access_token\":\"access\",\"expires_in\":3600,\"scope\":\"user:inference\"}"))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ClaudeOAuthClient.ExchangeAsync(client, "code", "state", "verifier", "http://localhost:12345/callback", CancellationToken.None));
        Assert.Equal("signin.oauth_scope_missing", UserMessage.FromException(error).Code);
    }

    [Fact]
    public async Task BothTokensAreEncryptedAndExpirySurvivesStorage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "estao-claude-oauth-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "oauth.json");
        try
        {
            var store = new SecureOAuthTokenStore(path);
            var token = new OAuthToken("private-access-token", "fixture", "private-refresh-token", DateTimeOffset.UtcNow.AddHours(1), "user:profile");
            await store.SaveAsync("claude", token);
            Assert.Equal(token, await store.ReadAsync("claude"));
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(token.AccessToken, json);
            Assert.DoesNotContain(token.RefreshToken!, json);
            await store.SaveAsync("copilot", new("copilot-token", null));
            await store.ClearAsync("claude");
            Assert.Null(await store.ReadAsync("claude"));
            Assert.NotNull(await store.ReadAsync("copilot"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class Tokens : IOAuthTokenStore
    {
        public OAuthToken? Value;
        public Task<OAuthToken?> ReadAsync(string providerId, CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(string providerId, OAuthToken token, CancellationToken cancellationToken = default) { Value = token; return Task.CompletedTask; }
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => run(request);
    }
}
