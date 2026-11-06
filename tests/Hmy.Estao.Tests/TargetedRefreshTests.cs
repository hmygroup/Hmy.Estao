using System.Net;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Providers;
using Hmy.Estao.Core.Refresh;

namespace Hmy.Estao.Tests;

public sealed class TargetedRefreshTests
{
    [Fact]
    public async Task RefreshingOneProviderDoesNotFetchOrReplaceTheOthers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "estao-target-refresh-" + Guid.NewGuid().ToString("N"));
        try
        {
            var requests = new List<string>();
            using var client = new HttpClient(new Handler(request =>
            {
                requests.Add(request.RequestUri!.Host);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"quota_snapshots\":{\"premium_interactions\":{\"entitlement\":300,\"remaining\":200,\"percent_remaining\":66}},\"data\":{\"limits\":[{\"type\":\"TOKENS_LIMIT\",\"percentage\":20}]}}") };
            }));
            var store = new ConfigStore(Path.Combine(directory, "config.json"));
            var config = ConfigStore.Normalize(ConfigStore.CreateDefaultConfig());
            foreach (var provider in config.Providers)
            {
                provider.Enabled = provider.Id is "copilot" or "zai";
                provider.Source = "api";
                provider.ApiKey = "fixture-token";
            }
            await store.SaveAsync(config);
            var service = new UsageRefreshService(store, new UsageProviderFactory(client));
            var first = await service.RefreshAsync();
            Assert.Equal(2, requests.Count);
            var copilot = first.Single(s => s.Provider == "copilot");
            var historyBefore = service.History.Where(h => h.Provider == "copilot").ToArray();
            requests.Clear();

            var refreshed = await service.RefreshProviderAsync("zai");
            Assert.Equal("api.z.ai", Assert.Single(requests));
            Assert.Same(copilot, refreshed.Single(s => s.Provider == "copilot"));
            Assert.Equal(historyBefore, service.History.Where(h => h.Provider == "copilot"));
            Assert.Equal(first.Select(s => s.Provider), refreshed.Select(s => s.Provider));

            config.Providers.First(p => p.Id == "zai").Enabled = false;
            await store.SaveAsync(config);
            requests.Clear();
            var disabled = await service.RefreshProviderAsync("zai");
            Assert.Empty(requests);
            Assert.Same(copilot, Assert.Single(disabled));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task TargetedRefreshWaitsForAnInFlightRefreshInsteadOfDroppingTheCredentialChange()
    {
        var directory = Path.Combine(Path.GetTempPath(), "estao-refresh-gate-" + Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var store = new ConfigStore(Path.Combine(directory, "config.json"));
            var config = ConfigStore.Normalize(ConfigStore.CreateDefaultConfig());
            foreach (var provider in config.Providers) provider.Enabled = provider.Id == "copilot";
            var copilot = config.Providers.First(p => p.Id == "copilot");
            copilot.Source = "api";
            copilot.ApiKey = "before-sign-in";
            await store.SaveAsync(config);
            var tokens = new List<string>();
            using var client = new HttpClient(new AsyncHandler(async request =>
            {
                tokens.Add(request.Headers.Authorization!.Parameter!);
                if (tokens.Count == 1) { started.TrySetResult(); await release.Task; }
                return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }));
            var service = new UsageRefreshService(store, new UsageProviderFactory(client));
            var initial = service.RefreshAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            copilot.ApiKey = "after-sign-in";
            await store.SaveAsync(config);
            var targeted = service.RefreshProviderAsync("copilot");
            Assert.False(targeted.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(initial, targeted).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "before-sign-in", "after-sign-in" }, tokens);
        }
        finally
        {
            release.TrySetResult();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
}
