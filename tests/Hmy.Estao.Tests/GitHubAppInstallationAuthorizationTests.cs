using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hmy.Estao.Core.Harnesses.Installer;

namespace Hmy.Estao.Tests;

public sealed class GitHubAppInstallationAuthorizationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "estao-app-auth-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _rsa = RSA.Create(2048);
    private string KeyPath => Path.Combine(_directory, "private-key.pem");

    public GitHubAppInstallationAuthorizationTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(KeyPath, _rsa.ExportRSAPrivateKeyPem());
    }

    [Fact]
    public async Task SignsJwtDiscoversInstallationAndRestrictsToken()
    {
        var clock = new Clock();
        var handler = new Handler(async request =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var parts = request.Headers.Authorization.Parameter!.Split('.');
            Assert.True(_rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
                Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var payload = JsonDocument.Parse(Decode(parts[1]));
            Assert.Equal("1246331", payload.RootElement.GetProperty("iss").GetString());
            Assert.Equal(clock.Now.AddSeconds(-60).ToUnixTimeSeconds(), payload.RootElement.GetProperty("iat").GetInt64());
            Assert.Equal(clock.Now.AddMinutes(9).ToUnixTimeSeconds(), payload.RootElement.GetProperty("exp").GetInt64());
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/repos/hmygroup/hmy-prompt-engineer/installation", request.RequestUri.AbsolutePath);
                return Json(new { id = 42 });
            }
            Assert.Equal("/app/installations/42/access_tokens", request.RequestUri.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("hmy-prompt-engineer", body.RootElement.GetProperty("repositories")[0].GetString());
            Assert.Equal("read", body.RootElement.GetProperty("permissions").GetProperty("contents").GetString());
            return Json(new { token = "installation-test-token", expires_at = clock.Now.AddHours(1) });
        });
        using var client = new HttpClient(handler);
        var auth = new GitHubAppInstallationAuthorization(client, KeyPath, clock);
        Assert.True(auth.IsConfigured);
        Assert.Equal("installation-test-token", await auth.GetTokenAsync());
        Assert.Equal("installation-test-token", await auth.GetTokenAsync());
        Assert.Equal(2, handler.Calls);
        clock.Now = clock.Now.AddMinutes(56);
        Assert.Equal("installation-test-token", await auth.GetTokenAsync());
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneTokenExchange()
    {
        var clock = new Clock();
        var handler = new Handler(_ => Task.FromResult(Json(new
        {
            id = 42, token = "test-token", expires_at = clock.Now.AddHours(1)
        })));
        using var client = new HttpClient(handler);
        var auth = new GitHubAppInstallationAuthorization(client, KeyPath, clock);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => auth.GetTokenAsync()));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task FailureDoesNotExposeResponseBodyOrFallBackToLogin()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("sensitive-server-content")
        }));
        using var client = new HttpClient(handler);
        var auth = new GitHubAppInstallationAuthorization(client, KeyPath);
        var error = await Assert.ThrowsAsync<GitHubAccessException>(() => auth.GetTokenAsync());
        Assert.Equal(HttpStatusCode.Forbidden, error.Status);
        Assert.DoesNotContain("sensitive-server-content", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RejectsExpiredTokenAndDoesNotCacheIt()
    {
        var clock = new Clock();
        var handler = new Handler(_ => Task.FromResult(Json(new
        {
            id = 42, token = "test-token", expires_at = clock.Now.AddSeconds(-1)
        })));
        using var client = new HttpClient(handler);
        var auth = new GitHubAppInstallationAuthorization(client, KeyPath, clock);
        await Assert.ThrowsAsync<InvalidDataException>(() => auth.GetTokenAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => auth.GetTokenAsync());
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task MissingKeyIsNotConfiguredAndCancellationMakesNoRequests()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Unexpected network request"));
        using var client = new HttpClient(handler);
        var auth = new GitHubAppInstallationAuthorization(client, Path.Combine(_directory, "missing.pem"));
        Assert.False(auth.IsConfigured);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.GetTokenAsync(cancellation.Token));
        Assert.Equal(0, handler.Calls);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
    private static byte[] Decode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return respond(request);
        }
    }
    public void Dispose()
    {
        _rsa.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
