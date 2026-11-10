using System.Net;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Desktop.Tests;

public sealed class CodexOAuthLoginTests
{
    [Fact]
    public async Task MissingCliCanUseBrowserOAuthCallbackWithoutLaunchingAnyCli()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var browser = new HttpClient();
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://auth.openai.com/oauth/token", request.RequestUri!.AbsoluteUri);
            var fields = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync());
            Assert.Equal(CodexOAuthClient.ClientId, fields["client_id"]);
            Assert.Equal("fixture-code", fields["code"]);
            Assert.False(string.IsNullOrWhiteSpace(fields["code_verifier"]));
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"fixture-access\",\"refresh_token\":\"fixture-refresh\",\"expires_in\":3600}") };
        }));
        Task<HttpResponseMessage>? callback = null;
        var token = await CodexOAuthLoginService.SignInAsync(uri =>
        {
            var values = System.Web.HttpUtility.ParseQueryString(uri.Query);
            Assert.Equal("auth.openai.com", uri.Host);
            Assert.Equal("S256", values["code_challenge_method"]);
            callback = browser.GetAsync(values["redirect_uri"] + "?code=fixture-code&state=" + Uri.EscapeDataString(values["state"]!), cancellation.Token);
        }, cancellation.Token, client, port: 0);
        using var response = await callback!;
        Assert.Equal("fixture-access", token.AccessToken);
        Assert.Equal("fixture-refresh", token.RefreshToken);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
