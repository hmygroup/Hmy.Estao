using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Desktop.Tests;

public sealed class ClaudeOAuthLoginTests
{
    [Fact]
    public void UsesRandomPkceStateAndOnlyRequestsUsageScope()
    {
        var proof = ClaudeOAuthLoginService.CreateProof();
        Assert.Equal(43, proof.Verifier.Length);
        Assert.NotEqual(proof.State, ClaudeOAuthLoginService.CreateProof().State);
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(proof.Verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, proof.Challenge);
        var uri = ClaudeOAuthLoginService.AuthorizationUri("http://localhost:12345/callback", proof.Challenge, proof.State);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("claude.ai", uri.Host);
        Assert.Equal("user:profile", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(proof.State, query["state"]);
    }

    [Fact]
    public async Task CallbackRejectsForgedStateAndThenAcceptsTheRealCallbackWithoutEchoingSecrets()
    {
        using var listener = new ClaudeOAuthLoginService.CallbackListener();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = listener.WaitForCodeAsync("expected-state", deadline.Token);
        using var browser = new HttpClient();
        using var forged = await browser.GetAsync(listener.RedirectUri + "?code=forged&state=wrong", deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.False(pending.IsCompleted);
        using var accepted = await browser.GetAsync(listener.RedirectUri + "?code=private-code&state=expected-state", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("private-code", await pending);
        Assert.DoesNotContain("private-code", await accepted.Content.ReadAsStringAsync(deadline.Token));
    }

    [Fact]
    public async Task CallbackDenialDoesNotBecomeSuccessfulSignIn()
    {
        using var listener = new ClaudeOAuthLoginService.CallbackListener();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = listener.WaitForCodeAsync("expected-state", deadline.Token);
        using var browser = new HttpClient();
        using var result = await browser.GetAsync(listener.RedirectUri + "?error=access_denied&state=expected-state", deadline.Token);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal("signin.oauth_denied", UserMessage.FromException(failure).Code);
    }

    [Fact]
    public async Task CancellationStopsWaitingForTheCallback()
    {
        using var listener = new ClaudeOAuthLoginService.CallbackListener();
        using var cancellation = new CancellationTokenSource();
        var pending = listener.WaitForCodeAsync("state", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task CompleteFlowUsesBrowserCallbackPkceExchangeAndUsageVerificationWithoutCliOrCookies()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var browser = new HttpClient();
        string? expectedState = null, challenge = null, redirect = null;
        var calls = 0;
        using var client = new HttpClient(new Handler(async request =>
        {
            calls++;
            Assert.False(request.Headers.Contains("Cookie"));
            if (request.Method == HttpMethod.Post)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var root = json.RootElement;
                Assert.Equal("authorization_code", root.GetProperty("grant_type").GetString());
                Assert.Equal("fixture-code", root.GetProperty("code").GetString());
                Assert.Equal(expectedState, root.GetProperty("state").GetString());
                Assert.Equal(redirect, root.GetProperty("redirect_uri").GetString());
                var verifier = root.GetProperty("code_verifier").GetString()!;
                Assert.Equal(challenge, Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
                return Json("{\"access_token\":\"fixture-access\",\"refresh_token\":\"fixture-refresh\",\"expires_in\":3600,\"scope\":\"user:profile\",\"token_type\":\"Bearer\"}");
            }
            Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-access", request.Headers.Authorization?.Parameter);
            return Json("{\"five_hour\":{\"utilization\":20}}");
        }));
        Task<HttpResponseMessage>? callback = null;
        var token = await ClaudeOAuthLoginService.SignInAsync(uri =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            expectedState = query["state"]; challenge = query["code_challenge"]; redirect = query["redirect_uri"];
            callback = browser.GetAsync(redirect + "?code=fixture-code&state=" + Uri.EscapeDataString(expectedState!), deadline.Token);
        }, deadline.Token, client);
        using var response = await callback!;
        Assert.Equal("fixture-access", token.AccessToken);
        Assert.Equal("fixture-refresh", token.RefreshToken);
        Assert.NotNull(token.ExpiresAt);
        Assert.Equal(2, calls);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => run(request);
    }
}
