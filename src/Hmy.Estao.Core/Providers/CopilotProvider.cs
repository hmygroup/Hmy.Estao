using System.Net.Http.Headers;
using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Core.Providers;

internal sealed class CopilotProvider(HttpClient httpClient) : IUsageProvider
{
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
        if (string.IsNullOrWhiteSpace(token))
        {
            return UsageSnapshot.Failure(Id, "api", "Copilot requires a GitHub OAuth token. Use settings or `estao config set-api-key --provider copilot --stdin`.");
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

        using var response = await httpClient.SendAsync(httpRequest, request.CancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return UsageSnapshot.Failure(Id, "api", $"Copilot usage API returned {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(request.CancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: request.CancellationToken).ConfigureAwait(false);
        return SnapshotFromJson(document.RootElement, account?.Label);
    }

    private static UsageSnapshot SnapshotFromJson(JsonElement root, string? accountLabel)
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
        return new UsageSnapshot("copilot", ProviderCatalog.DisplayName("copilot"), "api", DateTimeOffset.UtcNow, windows, account, plan);
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
