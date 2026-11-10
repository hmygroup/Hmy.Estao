using System.Net;
using System.Text;
using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Providers;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Tests;

public sealed class CodexOAuthTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedOAuthUsageAndRefreshWorkWithoutAnyCliProfile(bool expired)
    {
        var tokens = new Tokens { Value = new("original-access", "fixture-user", "original-refresh",
            DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1), AccountId: "fixture-account") };
        var calls = 0;
        using var client = new HttpClient(new Handler(async request =>
        {
            calls++;
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("auth.openai.com", request.RequestUri!.Host);
                var values = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync());
                Assert.Equal("original-refresh", values["refresh_token"]);
                Assert.Equal(CodexOAuthClient.ClientId, values["client_id"]);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"rotated-access\",\"refresh_token\":\"rotated-refresh\",\"expires_in\":3600}") };
            }
            Assert.Equal(expired ? "rotated-access" : "original-access", request.Headers.Authorization?.Parameter);
            Assert.Equal("fixture-account", Assert.Single(request.Headers.GetValues("ChatGPT-Account-Id")));
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"rate_limit\":{\"primary_window\":{\"used_percent\":12}}}") };
        }));
        var provider = new UsageProviderFactory(client, oauthStore: tokens).Create("codex");
        var result = await provider.FetchAsync(new(new ProviderConfig { Id = "codex", Source = "oauth",
            CodexProfileHomePaths = [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))] }, null, ProviderSource.OAuth, CancellationToken.None));
        Assert.Null(result.Error);
        Assert.Equal(0.12, Assert.Single(result.Windows).PercentUsed);
        Assert.Equal(expired ? 2 : 1, calls);
        Assert.Equal(expired ? "rotated-refresh" : "original-refresh", tokens.Value!.RefreshToken);
    }

    [Fact]
    public async Task OAuthExchangeExtractsAccountRoutingAndDoesNotExposeTokensInMetadata()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["email"] = "fixture@example.invalid",
            ["https://api.openai.com/auth"] = new { chatgpt_account_id = "fixture-account" }
        }))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { access_token = "private-access", refresh_token = "private-refresh", expires_in = 3600, id_token = "header." + payload + ".signature" }))
        })));
        var token = await CodexOAuthClient.ExchangeAsync(client, "code", "verifier", "http://localhost:1455/auth/callback", CancellationToken.None);
        Assert.Equal("fixture-account", token.AccountId);
        Assert.Equal("fixture@example.invalid", token.AccountLabel);
    }

    private sealed class Tokens : IOAuthTokenStore
    {
        public OAuthToken? Value;
        public Task<OAuthToken?> ReadAsync(string providerId, CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(string providerId, OAuthToken token, CancellationToken cancellationToken = default) { Value = token; return Task.CompletedTask; }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
