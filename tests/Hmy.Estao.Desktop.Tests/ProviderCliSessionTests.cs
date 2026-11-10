using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Providers;
using Hmy.Estao.Core.Security;
using Hmy.Estao.Core.Refresh;
using System.Reflection;
using System.Net;

namespace Hmy.Estao.Desktop.Tests;

public sealed class ProviderCliSessionTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public async Task EnablingAndManualSignInActuallyRouteCliReuseBeforeBrowserOAuth(bool manual, bool reusable, bool direct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "estao-cli-routing-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(Path.Combine(directory, "config.json"));
        var config = ConfigStore.Normalize(ConfigStore.CreateDefaultConfig());
        config.Refresh.Enabled = false;
        config.TaskbarOverlay.Enabled = false;
        foreach (var p in config.Providers) { p.Enabled = false; p.Source = "oauth"; }
        config.Providers.First(p => p.Id == "claude").CliAutoSignInDisabled = direct;
        await store.SaveAsync(config);
        Exception? failure = null;
        var language = DesktopLocalization.Language;
        var thread = new Thread(() =>
        {
            try
            {
                using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"five_hour\":{\"utilization\":20}}") }));
                var factory = new UsageProviderFactory(client, oauthStore: new NoTokens(),
                    claudeCliCredentials: _ => Task.FromResult<OAuthToken?>(new("fixture-cli-access", null)));
                using var context = new TrayApplicationContext(store);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(TrayApplicationContext);
                type.GetField("_factory", flags)!.SetValue(context, factory);
                type.GetField("_service", flags)!.SetValue(context, new UsageRefreshService(store, factory));
                type.GetField("_cliSessions", flags)!.SetValue(context, new ProviderCliSessionService(factory,
                    _ => "fixture.exe", (_, _, _) => Task.FromResult<string?>(reusable ? "{\"loggedIn\":true}" : "{\"loggedIn\":false}"),
                    _ => Task.FromResult<OAuthToken?>(new("fixture-cli-access", null))));
                var browsers = 0;
                type.GetField("_openBrowser", flags)!.SetValue(context, new Action<Uri>(_ => { browsers++; throw new Exception("Browser OAuth must not run when CLI reuse succeeds."); }));
                var overlay = (ProviderOverlay)type.GetField("_overlay", flags)!.GetValue(context)!;
                overlay.BeginInvoke(new Action(async () =>
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        if (manual)
                        {
                            var change = new ProviderSettings("claude", "Claude", false, "oauth", "off", null, null, null, null, new(), false, false);
                            if (direct)
                                await (Task)type.GetMethod("SignInAsync", flags)!.Invoke(context, ["claude"])!;
                            else
                                await (Task)type.GetMethod("BeginProviderSignInAsync", flags)!.Invoke(context, [change])!;
                            while ((bool)type.GetField("_signingIn", flags)!.GetValue(context)!) await Task.Delay(10, deadline.Token);
                            Assert.Equal(!reusable, ((ProviderSignInState)type.GetField("_providerSignIn", flags)!.GetValue(context)!).IsError);
                        }
                        else
                        {
                            var current = await store.LoadAsync();
                            current.Providers.First(p => p.Id == "claude").Enabled = true;
                            await store.SaveAsync(current);
                            type.GetField("_config", flags)!.SetValue(context, current);
                            await (Task)type.GetMethod("ProbeEnabledCliSessionsAsync", flags)!.Invoke(context, [new[] { "claude" }])!;
                        }
                        Assert.Equal(reusable ? "auto" : "oauth", (await store.LoadAsync()).Providers.First(p => p.Id == "claude").Source);
                        Assert.False((await store.LoadAsync()).Providers.First(p => p.Id == "claude").CliAutoSignInDisabled);
                        var authentication = (Dictionary<string, CodexAccountStatus>)type.GetField("_providerAuthentication", flags)!.GetValue(context)!;
                        Assert.Equal(reusable, authentication.GetValueOrDefault("claude")?.SignedIn == true);
                        Assert.Equal(manual && !reusable ? 1 : 0, browsers);
                    }
                    catch (Exception exception) { failure = exception; }
                    finally { context.ExitThread(); }
                }));
                System.Windows.Forms.Application.Run(context);
            }
            catch (Exception exception) { failure = exception; }
            finally { DesktopLocalization.SetLanguage(language); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { if (!thread.IsAlive && Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("oauth")]
    [InlineData("auto")]
    [InlineData("web")]
    public async Task ClaudeDiscoveryDoesNotDependOnTheOldHiddenMode(string source)
    {
        var calls = 0;
        var service = new ProviderCliSessionService(new UsageProviderFactory(), _ => "fixture-claude.exe", (exe, args, ct) =>
        {
            calls++;
            Assert.Equal(new[] { "auth", "status" }, args);
            return Task.FromResult<string?>("{\"loggedIn\":true,\"authMethod\":\"claude.ai\"}");
        }, _ => Task.FromResult<OAuthToken?>(new("fixture-access", null)));
        var result = await service.TryReadAsync(new ProviderConfig { Id = "claude", Enabled = true, Source = source }, CancellationToken.None);
        Assert.Equal("auto", result!.Source);
        Assert.Null(result.Token); // CLI refresh credentials are never imported.
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"loggedIn\":false}")]
    [InlineData("{\"loggedIn\":true,\"authMethod\":\"api_key\"}")]
    [InlineData("not JSON")]
    public async Task UnusableClaudeCliSessionsFallBackWithoutStartingInteractiveLogin(string? status)
    {
        var service = new ProviderCliSessionService(new UsageProviderFactory(), _ => "fixture.exe",
            (_, _, _) => Task.FromResult(status), _ => Task.FromResult<OAuthToken?>(new("fixture", null)));
        Assert.Null(await service.TryReadAsync(new ProviderConfig { Id = "claude" }, CancellationToken.None));
    }

    [Fact]
    public async Task MissingCliAndExplicitSignOutDoNotReadOrReconnectCredentials()
    {
        var reads = 0;
        var service = new ProviderCliSessionService(new UsageProviderFactory(), _ => null,
            (_, _, _) => { reads++; return Task.FromResult<string?>(null); });
        Assert.Null(await service.TryReadAsync(new ProviderConfig { Id = "claude" }, CancellationToken.None));
        Assert.Null(await service.TryReadAsync(new ProviderConfig { Id = "codex", CliAutoSignInDisabled = true }, CancellationToken.None));
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CodexUsesOnlyAConfirmedExistingCliSession(bool signedIn)
    {
        var service = new ProviderCliSessionService(new UsageProviderFactory(), codexStatus:
            (_, _) => Task.FromResult(new CodexAccountStatus(signedIn, "fixture")));
        var result = await service.TryReadAsync(new ProviderConfig { Id = "codex", Source = "oauth" }, CancellationToken.None);
        Assert.Equal(signedIn ? "cli" : null, result?.Source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GitHubCliIsReusedOnlyWhenItsTokenCanAccessCopilot(bool accepted)
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("fixture-gh-token", request.Headers.Authorization?.Parameter);
            return new(accepted ? HttpStatusCode.OK : HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
        }));
        var service = new ProviderCliSessionService(new UsageProviderFactory(client, oauthStore: new NoTokens()),
            _ => "fixture-gh.exe", (_, args, _) =>
            {
                Assert.Equal(new[] { "auth", "token" }, args);
                return Task.FromResult<string?>("fixture-gh-token");
            });
        var session = await service.TryReadAsync(new ProviderConfig { Id = "copilot" }, CancellationToken.None);
        Assert.Equal(accepted, session is not null);
        if (accepted)
        {
            Assert.Equal("oauth", session!.Source);
            Assert.Equal("fixture-gh-token", session.Token!.AccessToken);
        }
    }

    [Fact]
    public async Task CancellationDoesNotTurnIntoAnOAuthFallback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new ProviderCliSessionService(new UsageProviderFactory(), _ => "fixture.exe",
            (_, _, ct) => Task.FromCanceled<string?>(ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TryReadAsync(new ProviderConfig { Id = "claude" }, cancellation.Token));
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
}
