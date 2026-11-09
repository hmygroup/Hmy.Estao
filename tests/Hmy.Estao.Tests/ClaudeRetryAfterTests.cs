using System.Net;
using System.Net.Http.Headers;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Providers;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Security;
using Hmy.Estao.Core.Refresh;

namespace Hmy.Estao.Tests;

public sealed class ClaudeRetryAfterTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RateLimitsPreserveOnlyTheSameCredentialsLastSuccessfulReadingAndRecoveryReplacesIt(bool initialSuccess, bool changeCredentials)
    {
        var directory = Path.Combine(Path.GetTempPath(), "estao-claude-stale-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock();
            var succeed = initialSuccess;
            var utilization = 42;
            using var client = new HttpClient(new Handler(_ =>
            {
                if (succeed) return new(HttpStatusCode.OK) { Content = new StringContent(
                    $"{{\"five_hour\":{{\"utilization\":{utilization},\"resets_at\":\"2026-11-09T17:00:00Z\"}}}}") };
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(123));
                return response;
            }));
            var factory = new UsageProviderFactory(client, oauthStore: new NoTokens(), timeProvider: clock);
            var store = new ConfigStore(Path.Combine(directory, "config.json"));
            var config = ConfigStore.Normalize(ConfigStore.CreateDefaultConfig());
            foreach (var p in config.Providers) p.Enabled = p.Id == "claude";
            var claude = config.Providers.Single(p => p.Id == "claude");
            claude.Source = "oauth";
            claude.ApiKey = "sk-ant-oat-fixture";
            await store.SaveAsync(config);
            var service = new UsageRefreshService(store, factory);
            var initial = Assert.Single(await service.RefreshAsync());
            var history = service.History.ToArray();
            succeed = false;
            if (changeCredentials)
            {
                claude.ApiKey = "sk-ant-oat-different-account";
                await store.SaveAsync(config);
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var failed = Assert.Single(await service.RefreshProviderAsync("claude"));
                Assert.Equal("error.claude_rate_limited", failed.ErrorMessage!.Code);
                Assert.Equal(clock.Now.AddSeconds(123), failed.RetryAt);
                Assert.Equal(initialSuccess && !changeCredentials, failed.IsStale);
                if (failed.IsStale)
                {
                    Assert.Equal(initial.Windows, failed.Windows);
                    Assert.Equal(initial.UpdatedAt, failed.UpdatedAt);
                    Assert.Equal(0.42, Assert.Single(failed.Windows).PercentUsed);
                    Assert.Equal(initial.Windows[0].ResetAt, failed.Windows[0].ResetAt);
                }
                else Assert.Empty(failed.Windows);
                Assert.Equal(history, service.History);
            }
            clock.Now = clock.Now.AddSeconds(123);
            succeed = true;
            utilization = 55;
            var recovered = Assert.Single(await service.RefreshProviderAsync("claude"));
            Assert.False(recovered.IsStale);
            Assert.Null(recovered.Error);
            Assert.Null(recovered.RetryAt);
            Assert.Equal(0.55, Assert.Single(recovered.Windows).PercentUsed);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FactorySharesCooldownAcrossNewProviderInstancesWithoutDelayingOtherProviders()
    {
        var clock = new Clock();
        var claudeCalls = 0;
        var copilotCalls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "api.anthropic.com")
            {
                claudeCalls++;
                if (claudeCalls == 1)
                {
                    var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(123));
                    return limited;
                }
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"five_hour\":{\"utilization\":20}}") };
            }
            copilotCalls++;
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        var factory = new UsageProviderFactory(client, oauthStore: new NoTokens(), timeProvider: clock);
        Task<UsageSnapshot> Claude() => factory.Create("claude").FetchAsync(new(
            new ProviderConfig { Id = "claude", Source = "oauth", ApiKey = "sk-ant-oat-fixture" }, null, ProviderSource.OAuth, CancellationToken.None));
        var limited = await Claude();
        Assert.Equal(clock.Now.AddSeconds(123), factory.ClaudeRetryAt);
        Assert.Same(limited, await Claude());
        Assert.Equal(1, claudeCalls);
        await factory.Create("copilot").FetchAsync(new(new ProviderConfig { Id = "copilot", Source = "api", ApiKey = "fixture" }, null,
            ProviderSource.Api, CancellationToken.None));
        Assert.Equal(1, copilotCalls);
        clock.Now = clock.Now.AddSeconds(123);
        Assert.True(factory.TakeDueClaudeRetry());
        Assert.Null((await Claude()).Error);
        Assert.Equal(2, claudeCalls);
        Assert.Null(factory.ClaudeRetryAt);
    }

    private sealed class NoTokens : IOAuthTokenStore
    {
        public Task<OAuthToken?> ReadAsync(string providerId, CancellationToken cancellationToken = default) => Task.FromResult<OAuthToken?>(null);
        public Task SaveAsync(string providerId, OAuthToken token, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-09T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task DeltaSecondsSuppressesAllRequestsUntilTheExactDeadline()
    {
        var clock = new Clock();
        var gate = new ClaudeUsageRetryGate(clock);
        var calls = 0;
        Task<UsageSnapshot> Fetch()
        {
            calls++;
            using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(123));
            return Task.FromResult(gate.Failure(response, "oauth"));
        }
        var first = await gate.ExecuteAsync("same-token", Fetch, CancellationToken.None);
        Assert.Equal(clock.Now.AddSeconds(123), first.RetryAt);
        clock.Now = clock.Now.AddSeconds(122);
        Assert.Same(first, await gate.ExecuteAsync("same-token", Fetch, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.False(gate.TakeDueRetry());
        clock.Now = clock.Now.AddSeconds(1);
        Assert.True(gate.TakeDueRetry());
        Assert.False(gate.TakeDueRetry());
        await gate.ExecuteAsync("same-token", Fetch, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task HttpDateCanBeExtendedByAnother429AndSuccessClearsTheRetry()
    {
        var clock = new Clock();
        var gate = new ClaudeUsageRetryGate(clock);
        var firstDeadline = clock.Now.AddMinutes(7);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(firstDeadline);
        await gate.ExecuteAsync("token", () => Task.FromResult(gate.Failure(response, "oauth")), CancellationToken.None);
        Assert.Equal(firstDeadline, gate.RetryAt);
        clock.Now = firstDeadline;
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(19));
        await gate.ExecuteAsync("token", () => Task.FromResult(gate.Failure(response, "oauth")), CancellationToken.None);
        Assert.Equal(clock.Now.AddSeconds(19), gate.RetryAt);
        clock.Now = clock.Now.AddSeconds(19);
        await gate.ExecuteAsync("token", () => Task.FromResult(new UsageSnapshot("claude", "Claude", "oauth", clock.Now, [])), CancellationToken.None);
        Assert.Null(gate.RetryAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("invalid")]
    [InlineData("Mon, 09 Nov 2026 11:59:00 GMT")]
    public void MissingZeroMalformedOrPastHeadersDoNotInventARetryDeadline(string? header)
    {
        var gate = new ClaudeUsageRetryGate(new Clock());
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (header is not null) response.Headers.TryAddWithoutValidation("Retry-After", header);
        var snapshot = gate.Failure(response, "oauth");
        Assert.Null(snapshot.RetryAt);
        Assert.Equal("error.claude_rate_limited", snapshot.ErrorMessage!.Code);
    }

    [Fact]
    public void RetryAfterOnOtherStatusCodesDoesNotScheduleARateLimitRetry()
    {
        var gate = new ClaudeUsageRetryGate(new Clock());
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(123));
        Assert.Null(gate.Failure(response, "oauth").RetryAt);
    }

    [Fact]
    public async Task ConcurrentRequestsObserveTheSameCooldownAndChangedCredentialsAreIndependent()
    {
        var clock = new Clock();
        var gate = new ClaudeUsageRetryGate(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<UsageSnapshot> Fetch()
        {
            calls++;
            entered.TrySetResult();
            await release.Task;
            using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return gate.Failure(response, "oauth");
        }
        var one = gate.ExecuteAsync("old-token", Fetch, CancellationToken.None);
        await entered.Task;
        var two = gate.ExecuteAsync("old-token", Fetch, CancellationToken.None);
        release.SetResult();
        await Task.WhenAll(one, two);
        Assert.Equal(1, calls);
        await gate.ExecuteAsync("new-token", () => Task.FromResult(new UsageSnapshot("claude", "Claude", "oauth", clock.Now, [])), CancellationToken.None);
        Assert.Null(gate.RetryAt);
        await gate.ExecuteAsync("old-token", Fetch, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.NotNull(gate.RetryAt);
        gate.Clear();
        Assert.Null(gate.RetryAt);
    }
}
