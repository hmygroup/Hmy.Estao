using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Core.Providers;

internal sealed class OpenCodeProvider(HttpClient httpClient, ICookieSecretStore cookieStore) : IUsageProvider
{
    private const string WorkspacesFunction = "def39973159c7f0483d8793a822b8dbb10d067e12c65455fcb4608459ba0234f";
    private const string SubscriptionFunction = "7abeebee372f304e050aaaf92be863f4a86490e382f8c79db68fd94040d691b4";

    public string Id => "opencode";

    public IReadOnlyList<ProviderAccount> GetAccounts(ProviderConfig config)
    {
        var accounts = ProviderHelpers.TokenAccounts(config).ToList();
        if (!string.IsNullOrWhiteSpace(config.WorkspaceId) || !string.IsNullOrWhiteSpace(config.CookieHeader))
        {
            accounts.Insert(0, new ProviderAccount("configured", "Configured workspace", config.CookieHeader, WorkspaceId: config.WorkspaceId));
        }

        return accounts;
    }

    public async Task<UsageSnapshot> FetchAsync(FetchRequest request)
    {
        var account = request.Account ?? GetAccounts(request.Config).FirstOrDefault();
        var cookie = await ResolveCookieAsync(request, account).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return UsageSnapshot.Failure(Id, "web", "OpenCode requires a saved or legacy manual cookie.");
        }

        var workspaceId = account?.WorkspaceId ?? request.Config.WorkspaceId;
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            workspaceId = await FetchWorkspaceIdAsync(cookie, request.CancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return UsageSnapshot.Failure(Id, "web", "OpenCode workspace id was not found.");
        }

        var body = await PostServerFunctionAsync(SubscriptionFunction, cookie, $"[\"{workspaceId}\"]", request.CancellationToken).ConfigureAwait(false);
        return SnapshotFromBody(body, account?.Label ?? workspaceId);
    }

    private async Task<string?> ResolveCookieAsync(FetchRequest request, ProviderAccount? account)
    {
        var source = ProviderHelpers.ParseCookieSource(request.Config.CookieSource);
        if (source is CookieSource.Off)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(account?.Secret))
        {
            return NormalizeCookieHeader(account.Secret);
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

    private static string NormalizeCookieHeader(string cookie)
    {
        return cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) ? cookie[7..].Trim() : cookie.Trim();
    }

    private async Task<string?> FetchWorkspaceIdAsync(string cookie, CancellationToken cancellationToken)
    {
        var body = await PostServerFunctionAsync(WorkspacesFunction, cookie, "[]", cancellationToken).ConfigureAwait(false);
        var match = Regex.Match(body, "wrk_[A-Za-z0-9_-]+", RegexOptions.CultureInvariant);
        return match.Success ? match.Value : null;
    }

    private async Task<string> PostServerFunctionAsync(string functionId, string cookie, string payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://opencode.ai/_server");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("Accept", "text/javascript, application/json");
        request.Headers.TryAddWithoutValidation("x-solidstart-server-function", functionId);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderException($"OpenCode API returned {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static UsageSnapshot SnapshotFromBody(string body, string? account)
    {
        var windows = new List<RateWindow>();
        foreach (Match match in ProviderHelpers.OpenCodeWindowRegex().Matches(body))
        {
            var id = match.Groups["name"].Value == "rollingUsage" ? "session" : "weekly";
            var title = id == "session" ? "Session" : "Weekly";
            var json = "{" + match.Groups["body"].Value + "}";
            using var document = JsonDocument.Parse(json);
            windows.Add(new RateWindow(id, title, ProviderHelpers.PercentUsedFrom(document.RootElement), ProviderHelpers.ResetFrom(document.RootElement)));
        }

        if (windows.Count == 0)
        {
            return UsageSnapshot.Failure("opencode", "web", "OpenCode usage fields were not found.");
        }

        return new UsageSnapshot("opencode", ProviderCatalog.DisplayName("opencode"), "web", DateTimeOffset.UtcNow, windows, account);
    }
}
