using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hmy.Estao.Core.Harnesses.Installer;

// Possession of the key (including an embedded copy) grants the App's repository access.
// Never send keys or installation credentials to the frontend.
public sealed class GitHubAppInstallationAuthorization(HttpClient client, string keyPath, TimeProvider? clock = null,
    Func<CancellationToken, Task<string>>? bundledKeyReader = null)
{
    public const string AppId = "1246331";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public bool IsConfigured => bundledKeyReader is not null || File.Exists(keyPath);

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && _expiresAt > _clock.GetUtcNow().AddMinutes(5)) return _token;
            var jwt = await CreateJwtAsync(ct).ConfigureAwait(false);
            using var installation = await SendAsync(HttpMethod.Get,
                $"repos/{GitHubRepositorySource.Repository}/installation", jwt, null, ct).ConfigureAwait(false);
            var id = installation.RootElement.GetProperty("id").GetInt64();
            if (id <= 0) throw new InvalidDataException("GitHub returned an invalid installation ID.");
            // Narrow even a more broadly installed App to this repository and read-only content.
            using var result = await SendAsync(HttpMethod.Post, $"app/installations/{id}/access_tokens", jwt,
                new { repositories = new[] { GitHubRepositorySource.Repository.Split('/')[1] },
                    permissions = new { contents = "read" } }, ct).ConfigureAwait(false);
            var token = result.RootElement.GetProperty("token").GetString();
            var expiresAt = result.RootElement.GetProperty("expires_at").GetDateTimeOffset();
            if (string.IsNullOrWhiteSpace(token) || expiresAt <= _clock.GetUtcNow().AddMinutes(5))
                throw new InvalidDataException("GitHub returned an invalid installation token.");
            _token = token;
            _expiresAt = expiresAt;
            return token;
        }
        finally { _gate.Release(); }
    }

    private async Task<string> CreateJwtAsync(CancellationToken ct)
    {
        var pem = bundledKeyReader is not null
            ? await bundledKeyReader(ct).ConfigureAwait(false)
            : await File.ReadAllTextAsync(keyPath, ct).ConfigureAwait(false);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var now = _clock.GetUtcNow();
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(), exp = now.AddMinutes(9).ToUnixTimeSeconds(), iss = AppId
        }));
        var unsigned = header + "." + payload;
        var signature = rsa.SignData(System.Text.Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return unsigned + "." + Encode(signature);
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string jwt, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, "https://api.github.com/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Headers.UserAgent.ParseAdd("Hmy.Estao");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new GitHubAccessException($"GitHub App authentication failed (HTTP {(int)response.StatusCode}). Check the App installation and Contents read permission.", response.StatusCode);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
