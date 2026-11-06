using System.Net;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Providers;
using Hmy.Estao.Core.Security;
using Hmy.Estao.Desktop;

namespace Hmy.Estao.Desktop.Tests;

public sealed class ProviderAuthenticationTests
{
    [Fact]
    public void SignInMethodsAreValidatedForTheSelectedProvider()
    {
        foreach (var (provider, source) in new[] { ("codex", "oauth"), ("claude", "web"), ("copilot", "api"), ("opencode", "web"), ("zai", "api") })
            ProviderAuthenticationService.ValidateSource(provider, source);
        Assert.Throws<ArgumentException>(() => ProviderAuthenticationService.ValidateSource("codex", "api"));
        Assert.Throws<ArgumentException>(() => ProviderAuthenticationService.ValidateSource("zai", "oauth"));
        Assert.Throws<ArgumentException>(() => ProviderAuthenticationService.ValidateSource("unknown", "auto"));
    }

    [Fact]
    public async Task CopilotSavedOAuthIsVerifiedUsingTheInjectedTokenStore()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("test-oauth", request.Headers.Authorization?.Parameter);
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        var cookies = new Cookies();
        var oauth = new OAuth { Value = new("test-oauth", "fixture") };
        var service = new ProviderAuthenticationService(cookies, oauth, new UsageProviderFactory(client, cookies, oauth));
        Assert.True((await service.CheckAsync(new ProviderConfig { Id = "copilot", Source = "oauth" }, CancellationToken.None)).SignedIn);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("copilot")]
    [InlineData("opencode")]
    [InlineData("zai")]
    public async Task MissingCredentialsAreSignedOutEvenWhenDisabled(string providerId)
    {
        using var client = new HttpClient(new Handler(_ => throw new Exception("No HTTP request should be made")));
        var cookies = new Cookies();
        var oauth = new OAuth();
        var factory = new UsageProviderFactory(client, cookies, oauth);
        var service = new ProviderAuthenticationService(cookies, oauth, factory, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var config = new ProviderConfig { Id = providerId, Enabled = false, CodexProfileHomePaths = [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))] };
        Assert.False((await service.CheckAsync(config, CancellationToken.None)).SignedIn);
    }

    [Theory]
    [InlineData("claude", "oauth")]
    [InlineData("copilot", "api")]
    [InlineData("opencode", "web")]
    [InlineData("zai", "api")]
    public async Task ValidCredentialsAreCheckedAndRecognizedForDisabledProviders(string id, string source)
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"rollingUsage\":{\"usedPercent\":20},\"data\":{\"limits\":[{\"type\":\"TOKENS_LIMIT\",\"percentage\":20}]}}") }));
        var cookies = new Cookies { Value = "session=test" };
        var oauth = new OAuth();
        var service = new ProviderAuthenticationService(cookies, oauth, new UsageProviderFactory(client, cookies, oauth));
        var config = new ProviderConfig { Id = id, Enabled = false, Source = source,
            ApiKey = id == "claude" ? "sk-ant-oat-test" : "test", WorkspaceId = "workspace" };
        Assert.True((await service.CheckAsync(config, CancellationToken.None)).SignedIn);
    }

    [Theory]
    [InlineData("claude", "oauth")]
    [InlineData("copilot", "api")]
    [InlineData("opencode", "web")]
    [InlineData("zai", "api")]
    public async Task RejectedCredentialsAreNotReportedAsSignedIn(string id, string source)
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var cookies = new Cookies { Value = "session=test" };
        var oauth = new OAuth();
        var service = new ProviderAuthenticationService(cookies, oauth, new UsageProviderFactory(client, cookies, oauth));
        var config = new ProviderConfig { Id = id, Source = source, ApiKey = id == "claude" ? "sk-ant-oat-test" : "test", WorkspaceId = "workspace" };
        Assert.False((await service.CheckAsync(config, CancellationToken.None)).SignedIn);
    }

    [Fact]
    public void NetworkErrorsDoNotClaimTheUserIsSignedOut()
    {
        var status = ProviderAuthenticationService.FromSnapshot(UsageSnapshot.Failure("copilot", "api", "Network unavailable"));
        Assert.Null(status.SignedIn);
    }

    [Fact]
    public void SignOutClearsOnlyCredentialsAndKeepsProviderPreferencesAndActiveCodexProfile()
    {
        var provider = new ProviderConfig { Id = "codex", Enabled = true, ApiKey = "secret", CookieHeader = "cookie",
            TokenAccounts = new() { Accounts = [new() { Token = "account-secret" }] }, ActiveAccountIndex = 1,
            CodexProfileHomePaths = ["personal", "work"], WorkspaceId = "workspace", Source = "oauth", Pacing = new() { Enabled = true } };
        ProviderAuthenticationService.ClearConfiguredCredentials(provider);
        Assert.Null(provider.ApiKey);
        Assert.Null(provider.CookieHeader);
        Assert.Null(provider.TokenAccounts);
        Assert.Equal(1, provider.ActiveAccountIndex);
        Assert.Equal(new[] { "personal", "work" }, provider.CodexProfileHomePaths);
        Assert.True(provider.Enabled);
        Assert.True(provider.Pacing.Enabled);
        Assert.Equal("workspace", provider.WorkspaceId);
    }

    [Fact]
    public async Task ClearingOneSavedOAuthTokenKeepsOtherProviders()
    {
        var directory = Path.Combine(Path.GetTempPath(), "estao-oauth-clear-" + Guid.NewGuid().ToString("N"));
        var store = new SecureOAuthTokenStore(Path.Combine(directory, "oauth.json"));
        try
        {
            await store.SaveAsync("copilot", new("copilot-test", "test"));
            await store.SaveAsync("github-harness", new("hub-test", "test"));
            await store.ClearAsync("copilot");
            Assert.Null(await store.ReadAsync("copilot"));
            Assert.Equal("hub-test", (await store.ReadAsync("github-harness"))!.AccessToken);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class Cookies : ICookieSecretStore
    {
        public string? Value { get; set; }
        public Task<string?> ReadCookieHeaderAsync(string providerId, CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveCookieHeaderAsync(string providerId, string cookieHeader, CancellationToken cancellationToken = default) { Value = cookieHeader; return Task.CompletedTask; }
        public Task ClearCookieHeaderAsync(string providerId, CancellationToken cancellationToken = default) { Value = null; return Task.CompletedTask; }
    }
    private sealed class OAuth : IOAuthTokenStore
    {
        public OAuthToken? Value { get; set; }
        public Task<OAuthToken?> ReadAsync(string providerId, CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(string providerId, OAuthToken token, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
