using System.Net.Http.Headers;
using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Core.Providers;

internal sealed class ClaudeProvider(HttpClient httpClient, ICookieSecretStore cookieStore, IOAuthTokenStore? oauthStore = null,
    ClaudeUsageRetryGate? retryGate = null, Func<CancellationToken, Task<OAuthToken?>>? readCli = null) : IUsageProvider
{
    private readonly IOAuthTokenStore _oauth = oauthStore ?? new SecureOAuthTokenStore();
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private readonly ClaudeUsageRetryGate _retry = retryGate ?? new(TimeProvider.System);
    public string Id => "claude";

    public IReadOnlyList<ProviderAccount> GetAccounts(ProviderConfig config)
    {
        var accounts = ProviderHelpers.TokenAccounts(config).ToList();
        if (!string.IsNullOrWhiteSpace(config.CookieHeader))
        {
            accounts.Insert(0, new ProviderAccount("manual-cookie", "Manual cookie", config.CookieHeader));
        }

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            accounts.Insert(0, new ProviderAccount("api-key", "Configured token", config.ApiKey));
        }

        return accounts;
    }

    public async Task<UsageSnapshot> FetchAsync(FetchRequest request)
    {
        var account = request.Account ?? GetAccounts(request.Config).FirstOrDefault();
        var source = request.Source;

        if (source is ProviderSource.Auto or ProviderSource.OAuth)
        {
            if (source == ProviderSource.Auto && !request.Config.CliAutoSignInDisabled
                && await (readCli ?? (ct => ClaudeCliCredentials.ReadAsync(ct)))(request.CancellationToken).ConfigureAwait(false) is { } cli)
            {
                var cliUsage = await FetchOAuthAsync(cli.AccessToken, cli.AccountLabel, request.CancellationToken).ConfigureAwait(false);
                if (cliUsage.ErrorMessage is not { Code: "error.http", Arguments.Length: > 1 } failure || failure.Arguments[1] is not (401 or 403))
                    return cliUsage;
                // A revoked CLI credential must not mask Estao's own valid OAuth login.
            }
            var owned = await _oauth.ReadAsync(Id, request.CancellationToken).ConfigureAwait(false);
            if (owned is not null)
            {
                if (_retry.GetDeferred("oauth:" + owned.AccessToken) is { } deferred) return deferred;
                try
                {
                    owned = await CurrentTokenAsync(owned, false, request.CancellationToken).ConfigureAwait(false);
                    var snapshot = await FetchOAuthAsync(owned.AccessToken, owned.AccountLabel, request.CancellationToken).ConfigureAwait(false);
                    if (snapshot.ErrorMessage is { Code: "error.http", Arguments.Length: > 1 } failure
                        && failure.Arguments[1] is 401 && !string.IsNullOrWhiteSpace(owned.RefreshToken))
                    {
                        owned = await CurrentTokenAsync(owned, true, request.CancellationToken).ConfigureAwait(false);
                        snapshot = await FetchOAuthAsync(owned.AccessToken, owned.AccountLabel, request.CancellationToken).ConfigureAwait(false);
                    }
                    return snapshot;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { return UsageSnapshot.Failure(Id, "oauth", exception.Message, UserMessage.FromException(exception)); }
            }
            var token = account?.Secret?.StartsWith("sk-ant-oat", StringComparison.Ordinal) == true
                ? account.Secret
                : null;
            if (!string.IsNullOrWhiteSpace(token))
            {
                return await FetchOAuthAsync(token, account?.Label, request.CancellationToken).ConfigureAwait(false);
            }

            if (source is ProviderSource.OAuth)
            {
                return UsageSnapshot.Failure(Id, "oauth", "Claude OAuth credentials were not found.", new("error.auth", "Claude"));
            }
        }

        if (source is ProviderSource.Auto or ProviderSource.Web)
        {
            var cookie = await ResolveCookieAsync(request, account).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                return await FetchWebAsync(cookie, account?.Label, request.CancellationToken).ConfigureAwait(false);
            }

            if (source is ProviderSource.Web)
            {
                return UsageSnapshot.Failure(Id, "web", "Claude cookie was not saved or configured.", new("error.auth", "Claude"));
            }
        }

        return source == ProviderSource.Auto ? UsageSnapshot.Failure(Id, "auto", "Claude is not signed in.", new("error.auth", "Claude"))
            : UsageSnapshot.Failure(Id, source.ToString().ToLowerInvariant(), "Claude MVP supports OAuth credentials and web cookies only.", new("error.unsupported_source", "Claude"));
    }

    private async Task<OAuthToken> CurrentTokenAsync(OAuthToken observed, bool force, CancellationToken ct)
    {
        await RefreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await _oauth.ReadAsync(Id, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Claude was signed out.").WithUserMessage("signin.oauth_expired");
            if (current.AccessToken != observed.AccessToken) return current;
            if (!force && (current.ExpiresAt is null || current.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))) return current;
            var refreshed = await ClaudeOAuthClient.RefreshAsync(httpClient, current, ct).ConfigureAwait(false);
            if (!await _oauth.TryReplaceAsync(Id, current, refreshed, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Claude authorization changed during refresh.").WithUserMessage("signin.oauth_expired");
            return refreshed;
        }
        finally { RefreshGate.Release(); }
    }

    private async Task<string?> ResolveCookieAsync(FetchRequest request, ProviderAccount? account)
    {
        var cookieSource = ProviderHelpers.ParseCookieSource(request.Config.CookieSource);
        if (cookieSource is CookieSource.Off)
        {
            return null;
        }

        if (account?.Secret?.Contains("sessionKey=", StringComparison.OrdinalIgnoreCase) == true)
        {
            return account.Secret;
        }

        var storedCookie = await cookieStore.ReadCookieHeaderAsync(Id, request.CancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(storedCookie))
        {
            return NormalizeCookieHeader(storedCookie);
        }

        if (!string.IsNullOrWhiteSpace(request.Config.CookieHeader))
        {
            return NormalizeCookieHeader(request.Config.CookieHeader);
        }

        return null;
    }

    private Task<UsageSnapshot> FetchOAuthAsync(string token, string? accountLabel, CancellationToken cancellationToken)
        => _retry.ExecuteAsync("oauth:" + token, () => FetchOAuthCoreAsync(token, accountLabel, cancellationToken), cancellationToken);

    private async Task<UsageSnapshot> FetchOAuthCoreAsync(string token, string? accountLabel, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return _retry.Failure(response, "oauth");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return SnapshotFromUsage(document.RootElement, "oauth", accountLabel);
    }

    private Task<UsageSnapshot> FetchWebAsync(string cookie, string? accountLabel, CancellationToken cancellationToken)
        => _retry.ExecuteAsync("web:" + cookie, () => FetchWebCoreAsync(cookie, accountLabel, cancellationToken), cancellationToken);

    private async Task<UsageSnapshot> FetchWebCoreAsync(string cookie, string? accountLabel, CancellationToken cancellationToken)
    {
        var orgRequest = new HttpRequestMessage(HttpMethod.Get, "https://claude.ai/api/organizations");
        orgRequest.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var orgResponse = await httpClient.SendAsync(orgRequest, cancellationToken).ConfigureAwait(false);
        if (!orgResponse.IsSuccessStatusCode)
        {
            if ((int)orgResponse.StatusCode == 429) return _retry.Failure(orgResponse, "web");
            return UsageSnapshot.Failure(Id, "web", $"Claude organizations API returned {(int)orgResponse.StatusCode}.",
                WebFailureMessage((int)orgResponse.StatusCode));
        }

        await using var orgStream = await orgResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var orgDocument = await JsonDocument.ParseAsync(orgStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var orgId = ProviderHelpers.FirstString(orgDocument.RootElement, "uuid", "id");
        if (string.IsNullOrWhiteSpace(orgId))
        {
            return UsageSnapshot.Failure(Id, "web", "Claude organization id was not found.", new("error.organization_missing"));
        }

        var usageRequest = new HttpRequestMessage(HttpMethod.Get, $"https://claude.ai/api/organizations/{orgId}/usage");
        usageRequest.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var usageResponse = await httpClient.SendAsync(usageRequest, cancellationToken).ConfigureAwait(false);
        if (!usageResponse.IsSuccessStatusCode)
        {
            if ((int)usageResponse.StatusCode == 429) return _retry.Failure(usageResponse, "web");
            return UsageSnapshot.Failure(Id, "web", $"Claude usage API returned {(int)usageResponse.StatusCode}.",
                WebFailureMessage((int)usageResponse.StatusCode));
        }

        await using var usageStream = await usageResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var usageDocument = await JsonDocument.ParseAsync(usageStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return SnapshotFromUsage(usageDocument.RootElement, "web", accountLabel);
    }

    private static UserMessage WebFailureMessage(int status) => status == 403
        ? new("error.claude_web_forbidden") : new("error.http", "Claude", status);

    private static UsageSnapshot SnapshotFromUsage(JsonElement root, string source, string? accountLabel)
    {
        var windows = new List<RateWindow>();
        if (ProviderHelpers.FindProperty(root, "five_hour", "fiveHour", "session") is { } session)
        {
            windows.Add(new RateWindow("session", "Session", ProviderHelpers.PercentUsedFrom(session), ProviderHelpers.ResetFrom(session)));
        }

        if (ProviderHelpers.FindProperty(root, "seven_day", "sevenDay", "weekly") is { } weekly)
        {
            windows.Add(new RateWindow("weekly", "Weekly", ProviderHelpers.PercentUsedFrom(weekly), ProviderHelpers.ResetFrom(weekly)));
        }

        var account = ProviderHelpers.FirstString(root, "email", "accountEmail") ?? accountLabel;
        var plan = ProviderHelpers.FirstString(root, "subscriptionType", "rate_limit_tier", "plan");
        return new UsageSnapshot("claude", ProviderCatalog.DisplayName("claude"), source, DateTimeOffset.UtcNow, windows, account, plan);
    }

    private static string NormalizeCookieHeader(string cookie)
    {
        return cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) ? cookie[7..].Trim() : cookie.Trim();
    }
}
