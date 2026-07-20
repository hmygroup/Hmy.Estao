using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Core.Providers;

internal sealed class CodexProvider(HttpClient httpClient) : IUsageProvider
{
    public string Id => "codex";

    public IReadOnlyList<ProviderAccount> GetAccounts(ProviderConfig config)
    {
        var accounts = new List<ProviderAccount>();
        var homes = config.CodexProfileHomePaths is { Count: > 0 }
            ? config.CodexProfileHomePaths
            : [Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(ProviderHelpers.UserHome(), ".codex")];

        foreach (var rawHome in homes.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var home = ProviderHelpers.ExpandHome(rawHome);
            var label = Path.GetFileName(home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            accounts.Add(new ProviderAccount(home, string.IsNullOrWhiteSpace(label) ? "Default" : label, HomePath: home));
        }

        return accounts;
    }

    public async Task<UsageSnapshot> FetchAsync(FetchRequest request)
    {
        var account = request.Account ?? GetAccounts(request.Config).FirstOrDefault();
        var home = account?.HomePath ?? Path.Combine(ProviderHelpers.UserHome(), ".codex");

        if (request.Source is ProviderSource.Auto or ProviderSource.OAuth)
        {
            var oauth = await TryFetchOAuthAsync(home, account?.Label, request.CancellationToken).ConfigureAwait(false);
            if (oauth is not null || request.Source is ProviderSource.OAuth)
            {
                return oauth ?? UsageSnapshot.Failure(Id, "oauth", "Codex auth.json is missing or does not contain an access token.");
            }
        }

        if (request.Source is ProviderSource.Auto or ProviderSource.Cli)
        {
            return await FetchCliAsync(home, account?.Label, request.CancellationToken).ConfigureAwait(false);
        }

        return UsageSnapshot.Failure(Id, request.Source.ToString().ToLowerInvariant(), "Codex MVP supports OAuth auth.json and codex app-server only.");
    }

    private async Task<UsageSnapshot?> TryFetchOAuthAsync(string home, string? accountLabel, CancellationToken cancellationToken)
    {
        var authPath = Path.Combine(home, "auth.json");
        if (!File.Exists(authPath))
        {
            return null;
        }

        using var auth = JsonDocument.Parse(await File.ReadAllTextAsync(authPath, cancellationToken).ConfigureAwait(false));
        var token = ProviderHelpers.FirstString(auth.RootElement, "access_token", "accessToken");
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Estao/0.1");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return UsageSnapshot.Failure(Id, "oauth", $"Codex usage API returned {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return SnapshotFromWham(document.RootElement, "oauth", accountLabel);
    }

    private async Task<UsageSnapshot> FetchCliAsync(string home, string? accountLabel, CancellationToken cancellationToken)
    {
        var initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"client\":\"Estao\"}}\n";
        var accountRead = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"account/read\",\"params\":{}}\n";
        var limitsRead = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"account/rateLimits/read\",\"params\":{}}\n";
        var output = await ProviderHelpers.RunProcessAsync(
            "codex",
            "-s read-only -a untrusted app-server",
            home,
            new Dictionary<string, string?> { ["CODEX_HOME"] = home },
            initialize + accountRead + limitsRead,
            cancellationToken).ConfigureAwait(false);

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var windows = new List<RateWindow>();
        string? email = accountLabel;
        string? plan = null;
        CreditsSnapshot? credits = null;

        foreach (var line in lines)
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (ProviderHelpers.FindProperty(root, "email") is { } emailElement && emailElement.ValueKind == JsonValueKind.String)
            {
                email = emailElement.GetString();
            }

            plan ??= ProviderHelpers.FirstString(root, "plan", "planType", "subscriptionType");

            if (ProviderHelpers.FindProperty(root, "primary_window", "primaryWindow") is { } primary)
            {
                windows.Add(Window("session", "Session", primary));
            }

            if (ProviderHelpers.FindProperty(root, "secondary_window", "secondaryWindow") is { } secondary)
            {
                windows.Add(Window("weekly", "Weekly", secondary));
            }

            var balance = ProviderHelpers.FirstNumber(root, "balance", "credits");
            if (balance is not null)
            {
                credits = new CreditsSnapshot(balance, "credits");
            }
        }

        if (windows.Count == 0 && credits is null)
        {
            return UsageSnapshot.Failure(Id, "cli", "codex app-server did not return rate-limit data.");
        }

        return new UsageSnapshot(Id, ProviderCatalog.DisplayName(Id), "cli", DateTimeOffset.UtcNow, windows, email, plan, credits);
    }

    private static UsageSnapshot SnapshotFromWham(JsonElement root, string source, string? accountLabel)
    {
        var windows = new List<RateWindow>();
        if (ProviderHelpers.FindProperty(root, "primary_window", "primaryWindow", "five_hour") is { } primary)
        {
            windows.Add(Window("session", "Session", primary));
        }

        if (ProviderHelpers.FindProperty(root, "secondary_window", "secondaryWindow", "weekly") is { } secondary)
        {
            windows.Add(Window("weekly", "Weekly", secondary));
        }

        var email = ProviderHelpers.FirstString(root, "email", "accountEmail") ?? accountLabel;
        var plan = ProviderHelpers.FirstString(root, "plan", "planType", "subscriptionType");
        var credits = ProviderHelpers.FirstNumber(root, "balance", "credits") is { } balance ? new CreditsSnapshot(balance, "credits") : null;
        return new UsageSnapshot("codex", ProviderCatalog.DisplayName("codex"), source, DateTimeOffset.UtcNow, windows, email, plan, credits);
    }

    private static RateWindow Window(string id, string title, JsonElement element)
    {
        return new RateWindow(id, title, ProviderHelpers.PercentUsedFrom(element), ProviderHelpers.ResetFrom(element));
    }
}
