using System.Net.Http.Headers;
using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Core.Providers;

internal sealed class ClaudeProvider(HttpClient httpClient, ICookieSecretStore cookieStore) : IUsageProvider
{
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
            var token = account?.Secret?.StartsWith("sk-ant-oat", StringComparison.Ordinal) == true
                ? account.Secret
                : await TryReadOAuthTokenAsync(request.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return await FetchOAuthAsync(token, account?.Label, request.CancellationToken).ConfigureAwait(false);
            }

            if (source is ProviderSource.OAuth)
            {
                return UsageSnapshot.Failure(Id, "oauth", "Claude OAuth credentials were not found.");
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
                return UsageSnapshot.Failure(Id, "web", "Claude cookie was not saved or configured.");
            }
        }

        return UsageSnapshot.Failure(Id, source.ToString().ToLowerInvariant(), "Claude MVP supports OAuth credentials and web cookies only.");
    }

    private static async Task<string?> TryReadOAuthTokenAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(ProviderHelpers.UserHome(), ".claude", ".credentials.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        return ProviderHelpers.FirstString(document.RootElement, "access_token", "accessToken");
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

    private async Task<UsageSnapshot> FetchOAuthAsync(string token, string? accountLabel, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return UsageSnapshot.Failure(Id, "oauth", $"Claude OAuth usage API returned {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return SnapshotFromUsage(document.RootElement, "oauth", accountLabel);
    }

    private async Task<UsageSnapshot> FetchWebAsync(string cookie, string? accountLabel, CancellationToken cancellationToken)
    {
        var orgRequest = new HttpRequestMessage(HttpMethod.Get, "https://claude.ai/api/organizations");
        orgRequest.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var orgResponse = await httpClient.SendAsync(orgRequest, cancellationToken).ConfigureAwait(false);
        if (!orgResponse.IsSuccessStatusCode)
        {
            return UsageSnapshot.Failure(Id, "web", $"Claude organizations API returned {(int)orgResponse.StatusCode}.");
        }

        await using var orgStream = await orgResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var orgDocument = await JsonDocument.ParseAsync(orgStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var orgId = ProviderHelpers.FirstString(orgDocument.RootElement, "uuid", "id");
        if (string.IsNullOrWhiteSpace(orgId))
        {
            return UsageSnapshot.Failure(Id, "web", "Claude organization id was not found.");
        }

        var usageRequest = new HttpRequestMessage(HttpMethod.Get, $"https://claude.ai/api/organizations/{orgId}/usage");
        usageRequest.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var usageResponse = await httpClient.SendAsync(usageRequest, cancellationToken).ConfigureAwait(false);
        if (!usageResponse.IsSuccessStatusCode)
        {
            return UsageSnapshot.Failure(Id, "web", $"Claude usage API returned {(int)usageResponse.StatusCode}.");
        }

        await using var usageStream = await usageResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var usageDocument = await JsonDocument.ParseAsync(usageStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return SnapshotFromUsage(usageDocument.RootElement, "web", accountLabel);
    }

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
