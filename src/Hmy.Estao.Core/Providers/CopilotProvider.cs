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
        if (ProviderHelpers.FindProperty(root, "premiumInteractions", "premium_interactions") is { } premium)
        {
            windows.Add(new RateWindow("premium", "Premium", ProviderHelpers.PercentUsedFrom(premium), null));
        }

        if (ProviderHelpers.FindProperty(root, "chat") is { } chat)
        {
            windows.Add(new RateWindow("chat", "Chat", ProviderHelpers.PercentUsedFrom(chat), null));
        }

        var account = ProviderHelpers.FirstString(root, "login", "email", "username") ?? accountLabel;
        var plan = ProviderHelpers.FirstString(root, "copilotPlan", "plan");
        return new UsageSnapshot("copilot", ProviderCatalog.DisplayName("copilot"), source, DateTimeOffset.UtcNow, windows, account, plan);
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
