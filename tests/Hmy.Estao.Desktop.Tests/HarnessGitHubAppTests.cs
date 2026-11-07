using System.Net;
using System.Reflection;
using System.Text;
using Hmy.Estao.App;

namespace Hmy.Estao.Desktop.Tests;

public sealed class HarnessGitHubAppTests
{
    [Fact]
    public async Task DeviceAuthorizationAndTokenExchangeUseTheHarnessGitHubApp()
    {
        const string clientId = "Iv23lilqu6xECmxSGO99";
        var requests = new List<(string Path, string Body)>();
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add((path, await request.Content!.ReadAsStringAsync(ct)));
            Assert.Equal("github.com", request.RequestUri.Host);
            Assert.Equal(HttpMethod.Post, request.Method);
            var json = path == "/login/device/code"
                ? "{\"device_code\":\"fixture-device\",\"user_code\":\"ABCD-EFGH\",\"verification_uri\":\"https://github.com/login/device\",\"expires_in\":60,\"interval\":1}"
                : "{\"access_token\":\"fixture-token\",\"token_type\":\"bearer\"}";
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }));

        var authorization = await OAuthLoginService.StartHarnessDeviceFlowAsync(CancellationToken.None, client);
        Assert.Equal("ABCD-EFGH", authorization.UserCode);
        Assert.Equal("fixture-token", await OAuthLoginService.CompleteHarnessDeviceFlowAsync(authorization, CancellationToken.None, client));
        Assert.Equal(new[] { "/login/device/code", "/login/oauth/access_token" }, requests.Select(r => r.Path));
        Assert.All(requests, request =>
        {
            Assert.Contains("client_id=" + clientId, request.Body);
            Assert.DoesNotContain("client_secret", request.Body);
            Assert.DoesNotContain("repo", request.Body);
        });
        Assert.Contains("device_code=fixture-device", requests[1].Body);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", requests[1].Body);
    }

    [Fact]
    public void CopilotKeepsItsSeparateOAuthClient()
    {
        var copilotClientId = typeof(OAuthLoginService).GetField("CopilotOAuthClientId", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue();
        Assert.Equal("Iv1.b507a08c87ecfe98", copilotClientId);
        Assert.NotEqual(copilotClientId, OAuthLoginService.HarnessOAuthClientId);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
}
