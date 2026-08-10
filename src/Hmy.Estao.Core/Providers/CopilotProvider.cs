using System.Net.Http.Headers;
using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Core.Providers;

internal sealed class CopilotProvider : IUsageProvider
{
    private readonly HttpClient _httpClient;
    private readonly IOAuthTokenStore _oauthTokenStore;

    public CopilotProvider(HttpClient httpClient, IOAuthTokenStore? oauthTokenStore = null)
    {
        _httpClient = httpClient;
        _oauthTokenStore = oauthTokenStore ?? new SecureOAuthTokenStore();
    }

    public string Id => "copilot";

    public IReadOnlyList<ProviderAccount> GetAccounts(ProviderConfig config)
    {
        var accounts = ProviderHelpers.TokenAccounts(config).ToList();
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            accounts.Insert(0, new ProviderAccount("api-key", "Configured token", config.ApiKey));
        }

        return accounts;
    }

    public async Task<UsageSnapshot> FetchAsync(FetchRequest request)
    {
        var account = request.Account ?? GetAccounts(request.Config).FirstOrDefault();
        var token = account?.Secret ?? request.Config.ApiKey;
        var source = "api";
        if (string.IsNullOrWhiteSpace(token) && request.Source is ProviderSource.Auto or ProviderSource.OAuth)
        {
            var oauth = await _oauthTokenStore.ReadAsync(Id, request.CancellationToken).ConfigureAwait(false);
            token = oauth?.AccessToken;
            account ??= oauth is null ? null : new ProviderAccount("oauth", oauth.AccountLabel ?? "GitHub OAuth", token);
            source = "oauth";
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            return UsageSnapshot.Failure(Id, source,
                "Copilot is not connected. In Settings, choose Sign in with OAuth and complete the GitHub device flow.");
        }

        var host = NormalizeEnterpriseHost(request.Config.EnterpriseHost);
        var apiHost = string.IsNullOrWhiteSpace(host) ? "api.github.com" : $"api.{host}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"https://{apiHost}/copilot_internal/user");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        httpRequest.Headers.Accept.ParseAdd("application/json");
        httpRequest.Headers.UserAgent.ParseAdd("GitHubCopilotChat/0.26.7");
        httpRequest.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.96.2");
        httpRequest.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.26.7");
        httpRequest.Headers.TryAddWithoutValidation("X-Github-Api-Version", "2025-04-01");

        using var response = await _httpClient.SendAsync(httpRequest, request.CancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return UsageSnapshot.Failure(Id, "api", $"Copilot usage API returned {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(request.CancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: request.CancellationToken).ConfigureAwait(false);
        return SnapshotFromJson(document.RootElement, account?.Label, source);
    }

    private static UsageSnapshot SnapshotFromJson(JsonElement root, string? accountLabel, string source)
    {
        var windows = new List<RateWindow>();
        var globalReset = ProviderHelpers.ResetFrom(root);

        // The current copilot_internal/user response uses quota_snapshots and a
        // top-level quota_reset_date (the same shape consumed by VS Code).
        if (ProviderHelpers.FindProperty(root, "quota_snapshots") is { } snapshots)
        {
            AddQuotaSnapshot(windows, snapshots, "premium_interactions", "Premium", globalReset);
            AddQuotaSnapshot(windows, snapshots, "chat", "Chat", globalReset);
        }

        // Older GitHub web responses expose quotas.remaining/limits and resetDate.
        if (windows.Count == 0 && ProviderHelpers.FindProperty(root, "quotas") is { } quotas)
        {
            var remaining = ProviderHelpers.FindProperty(quotas, "remaining");
            var limits = ProviderHelpers.FindProperty(quotas, "limits");
            var reset = ProviderHelpers.ResetFrom(quotas) ?? globalReset;
            AddLegacyQuota(windows, "premium", "Premium", remaining, limits, reset,
                ["premiumInteractionsPercentage", "premium_interactions_percentage"],
                ["premiumInteractions", "premium_interactions"]);
            AddLegacyQuota(windows, "chat", "Chat", remaining, limits, reset,
                ["chatPercentage"], ["chat"]);
        }

        // Keep compatibility with responses that place the quota objects at the
        // root rather than under quota_snapshots.
        if (windows.Count == 0)
        {
            AddQuotaObject(windows, root, "premiumInteractions", "premium_interactions", "Premium", globalReset);
            AddQuotaObject(windows, root, "chat", "chat", "Chat", globalReset);
        }

        var account = ProviderHelpers.FirstString(root, "login", "email", "username") ?? accountLabel;
        var plan = ProviderHelpers.FirstString(root, "copilotPlan", "plan", "licenseType");
        return new UsageSnapshot("copilot", ProviderCatalog.DisplayName("copilot"), source, DateTimeOffset.UtcNow, windows, account, plan);
    }

    private static void AddQuotaSnapshot(List<RateWindow> windows, JsonElement snapshots,
        string propertyName, string title, DateTimeOffset? globalReset)
    {
        if (ProviderHelpers.FindProperty(snapshots, propertyName) is not { } quota ||
            quota.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var percent = ProviderHelpers.PercentUsedFrom(quota);
        var hasValues = ProviderHelpers.FirstNumber(quota, "entitlement", "remaining", "percent_remaining") is not null;
        if (percent is not null || hasValues)
        {
            windows.Add(new RateWindow(propertyName, title, percent, ProviderHelpers.ResetFrom(quota) ?? globalReset));
        }
    }

    private static void AddQuotaObject(List<RateWindow> windows, JsonElement root,
        string camelName, string snakeName, string title, DateTimeOffset? globalReset)
    {
        var quota = ProviderHelpers.FindProperty(root, camelName, snakeName);
        if (quota is null || quota.Value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var value = quota.Value;
        windows.Add(new RateWindow(camelName, title, ProviderHelpers.PercentUsedFrom(value),
            ProviderHelpers.ResetFrom(value) ?? globalReset));
    }

    private static void AddLegacyQuota(List<RateWindow> windows, string id, string title,
        JsonElement? remaining, JsonElement? limits, DateTimeOffset? reset,
        string[] percentageNames, string[] valueNames)
    {
        if (remaining is null)
        {
            return;
        }

        var remainingElement = remaining.Value;
        var percentRemaining = ProviderHelpers.FirstNumber(remainingElement, percentageNames);
        double? percentUsed = percentRemaining is not null
            ? 1D - ProviderHelpers.NormalizePercent(percentRemaining.Value)
            : null;

        if (percentUsed is null && limits is not null)
        {
            var remainingValue = ProviderHelpers.FirstNumber(remainingElement, valueNames);
            var limitValue = ProviderHelpers.FirstNumber(limits.Value, valueNames);
            if (remainingValue is not null && limitValue is > 0)
            {
                percentUsed = Math.Clamp(1D - remainingValue.Value / limitValue.Value, 0D, 1D);
            }
        }

        if (percentUsed is not null || ProviderHelpers.FirstNumber(remainingElement, valueNames) is not null)
        {
            windows.Add(new RateWindow(id, title, percentUsed, reset));
        }
    }

    private static string? NormalizeEnterpriseHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var trimmed = host.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return trimmed.Trim('/');
    }
}
