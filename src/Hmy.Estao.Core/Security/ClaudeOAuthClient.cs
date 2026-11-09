using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Core.Security;

public static class ClaudeOAuthClient
{
    // Claude Code's public native OAuth client, used as a compatibility flow.
    // This is not an independently registered/approved Estao client identity.
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string UsageScope = "user:profile";
    private const string TokenEndpoint = "https://platform.claude.com/v1/oauth/token";

    public static Task<OAuthToken> ExchangeAsync(HttpClient client, string code, string state, string verifier,
        string redirectUri, CancellationToken ct) => RequestAsync(client, new
        {
            grant_type = "authorization_code", code, state, client_id = ClientId,
            redirect_uri = redirectUri, code_verifier = verifier
        }, null, ct);

    public static Task<OAuthToken> RefreshAsync(HttpClient client, OAuthToken previous, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(previous.RefreshToken)) throw new InvalidOperationException("Claude OAuth needs sign-in again.")
            .WithUserMessage("signin.oauth_expired");
        return RequestAsync(client, new { grant_type = "refresh_token", refresh_token = previous.RefreshToken, client_id = ClientId }, previous, ct);
    }

    public static async Task ValidateUsageAsync(HttpClient client, OAuthToken token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Claude OAuth usage access failed (HTTP {(int)response.StatusCode}).")
            .WithUserMessage("signin.oauth_usage_failed", (int)response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Claude returned invalid usage data.");
    }

    private static async Task<OAuthToken> RequestAsync(HttpClient client, object payload, OAuthToken? previous, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(TokenEndpoint, payload, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Claude OAuth token request failed (HTTP {(int)response.StatusCode}).")
            .WithUserMessage("signin.oauth_failed", (int)response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = document.RootElement;
        var access = String(root, "access_token");
        var refresh = String(root, "refresh_token") ?? previous?.RefreshToken;
        var scope = String(root, "scope") ?? previous?.Scope ?? UsageScope;
        if (!scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(UsageScope, StringComparer.Ordinal))
            throw new InvalidOperationException("Claude did not grant usage access.").WithUserMessage("signin.oauth_scope_missing");
        if (string.IsNullOrWhiteSpace(access) || !root.TryGetProperty("expires_in", out var expires)
            || expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt32(out var seconds) || seconds <= 0 || seconds > 365 * 24 * 60 * 60
            || String(root, "token_type") is { } type && !type.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Claude returned an invalid OAuth response.");
        var label = root.TryGetProperty("account", out var account) ? String(account, "email_address") : null;
        return new(access, label ?? previous?.AccountLabel, refresh, DateTimeOffset.UtcNow.AddSeconds(seconds), scope);
    }

    private static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
