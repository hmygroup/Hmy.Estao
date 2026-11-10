using System.Text.Json;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Core.Security;

public static class CodexOAuthClient
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public static Task<OAuthToken> ExchangeAsync(HttpClient client, string code, string verifier, string redirect, CancellationToken ct)
        => RequestAsync(client, new() { ["grant_type"] = "authorization_code", ["code"] = code,
            ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["client_id"] = ClientId }, null, ct);

    public static Task<OAuthToken> RefreshAsync(HttpClient client, OAuthToken previous, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(previous.RefreshToken)) throw new InvalidOperationException("Codex needs sign-in again.").WithUserMessage("signin.codex_oauth_expired");
        return RequestAsync(client, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = previous.RefreshToken, ["client_id"] = ClientId }, previous, ct);
    }

    private static async Task<OAuthToken> RequestAsync(HttpClient client, Dictionary<string, string> fields, OAuthToken? previous, CancellationToken ct)
    {
        using var response = await client.PostAsync("https://auth.openai.com/oauth/token", new FormUrlEncodedContent(fields), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Codex OAuth failed (HTTP {(int)response.StatusCode}).")
            .WithUserMessage("signin.codex_oauth_failed", (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("access_token", out var accessValue)
            || accessValue.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid Codex OAuth response.");
        var access = accessValue.GetString();
        if (string.IsNullOrWhiteSpace(access) || !root.TryGetProperty("expires_in", out var expiry)
            || expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt32(out var seconds)
            || seconds <= 0 || seconds > 365 * 24 * 60 * 60
            || root.TryGetProperty("token_type", out var type) && !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid Codex OAuth response.");
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? previous?.RefreshToken : previous?.RefreshToken;
        var idToken = root.TryGetProperty("id_token", out var id) ? id.GetString() ?? access : access;
        string? account = previous?.AccountId, email = previous?.AccountLabel;
        try
        {
            var payload = idToken!.Split('.')[1].Replace('-', '+').Replace('_', '/');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            if (claims.RootElement.TryGetProperty("email", out var e)) email = e.GetString();
            if (claims.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth)
                && auth.TryGetProperty("chatgpt_account_id", out var a)) account = a.GetString();
        }
        catch (Exception exception) when (exception is JsonException or FormatException or IndexOutOfRangeException or InvalidOperationException) { }
        return new(access, email, refresh, DateTimeOffset.UtcNow.AddSeconds(seconds), "openid profile email offline_access", account);
    }
}
