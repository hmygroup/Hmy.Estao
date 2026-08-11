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
        UsageSnapshot? oauthSnapshot = null;

        if (request.Source is ProviderSource.Auto or ProviderSource.OAuth)
        {
            var oauth = await TryFetchOAuthAsync(home, account?.Label, request.CancellationToken).ConfigureAwait(false);
            oauthSnapshot = oauth;
            if (request.Source is ProviderSource.OAuth)
            {
                return oauth ?? UsageSnapshot.Failure(Id, "oauth", "Codex auth.json is missing or does not contain an access token.");
            }

            if (oauth is not null && HasUsableRateLimits(oauth))
            {
                return oauth;
            }
        }

        if (request.Source is ProviderSource.Auto or ProviderSource.Cli)
        {
            var cli = await FetchCliAsync(home, account?.Label, request.CancellationToken).ConfigureAwait(false);
            return cli.Error is null || oauthSnapshot is null ? cli : oauthSnapshot;
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
        var initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"Estao\",\"version\":\"0.1\"}}}\n";
        var initialized = "{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}\n";
        var accountRead = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"account/read\",\"params\":{}}\n";
        var limitsRead = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"account/rateLimits/read\",\"params\":{}}\n";
        var output = await ProviderHelpers.RunProcessAsync(
            OperatingSystem.IsWindows() ? "codex.cmd" : "codex",
            "-s read-only -a untrusted app-server",
            home,
            new Dictionary<string, string?> { ["CODEX_HOME"] = home },
            initialize + initialized + accountRead + limitsRead,
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
            var parsed = ParseRateLimits(root, "cli", email);
            if (ProviderHelpers.FindProperty(root, "email") is { } emailElement && emailElement.ValueKind == JsonValueKind.String)
            {
                email = emailElement.GetString();
            }

            plan ??= parsed.Plan;
            credits ??= parsed.Credits;

            foreach (var window in ParseWindows(root))
            {
                windows.RemoveAll(existing => string.Equals(existing.Id, window.Id, StringComparison.OrdinalIgnoreCase));
                windows.Add(window);
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

    internal static UsageSnapshot ParseRateLimits(JsonElement root, string source, string? accountLabel)
    {
        var windows = ParseWindows(root);

        var email = ProviderHelpers.FirstString(root, "email", "accountEmail") ?? accountLabel;
        var rateLimits = FindRateLimits(root);
        var plan = ProviderHelpers.FirstString(rateLimits ?? root,
                "plan", "planType", "plan_type", "subscriptionType", "subscription_type")
            ?? ProviderHelpers.FirstString(root,
                "plan", "planType", "plan_type", "subscriptionType", "subscription_type");

        var creditsElement = rateLimits is { } limits
            ? ProviderHelpers.FindProperty(limits, "credits")
            : null;
        creditsElement ??= ProviderHelpers.FindProperty(root, "credits");
        var balance = creditsElement is { } credits
            ? ProviderHelpers.FirstNumber(credits, "balance", "credits")
            : ProviderHelpers.FirstNumber(root, "balance", "credits");
        var unlimited = creditsElement is { } creditObject
            ? FirstBoolean(creditObject, "unlimited")
            : null;
        var creditsSnapshot = balance is { } value
            ? new CreditsSnapshot(value, "credits", unlimited)
            : null;

        return new UsageSnapshot("codex", ProviderCatalog.DisplayName("codex"), source, DateTimeOffset.UtcNow,
            windows, email, plan, creditsSnapshot);
    }

    private static UsageSnapshot SnapshotFromWham(JsonElement root, string source, string? accountLabel)
        => ParseRateLimits(root, source, accountLabel);

    private static bool HasUsableRateLimits(UsageSnapshot snapshot)
    {
        return snapshot.Windows.Any(window => window.PercentUsed is not null || window.ResetAt is not null);
    }

    private static IReadOnlyList<RateWindow> ParseWindows(JsonElement root)
    {
        var rateLimits = FindRateLimits(root) ?? root;
        var windows = new List<RateWindow>();

        AddWindow(windows, rateLimits, "session", "Session",
            "primary", "primary_window", "primaryWindow", "five_hour", "fiveHour");
        AddWindow(windows, rateLimits, "weekly", "Weekly",
            "secondary", "secondary_window", "secondaryWindow", "weekly", "weekly_window", "weeklyWindow");

        return windows;
    }

    private static JsonElement? FindRateLimits(JsonElement root)
    {
        var byLimitId = ProviderHelpers.FindProperty(root, "rateLimitsByLimitId", "rate_limits_by_limit_id");
        if (byLimitId is { } limitsById &&
            ProviderHelpers.FindProperty(limitsById, "codex") is { } codex &&
            HasRateLimitData(codex))
        {
            return codex;
        }

        return ProviderHelpers.FindProperty(root, "rateLimits", "rate_limits", "rate_limit");
    }

    private static bool HasRateLimitData(JsonElement element)
    {
        foreach (var name in new[]
        {
            "primary", "secondary", "primary_window", "secondary_window",
            "primaryWindow", "secondaryWindow", "five_hour", "weekly"
        })
        {
            if (ProviderHelpers.FindProperty(element, name) is { } value && value.ValueKind == JsonValueKind.Object)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddWindow(List<RateWindow> windows, JsonElement rateLimits,
        string fallbackId, string fallbackTitle, params string[] names)
    {
        var element = ProviderHelpers.FindProperty(rateLimits, names);
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var durationMinutes = WindowDurationMinutes(element.Value);
        var isWeekly = durationMinutes is >= 1000;
        var isSession = durationMinutes is > 0 and < 1000;
        var id = isWeekly ? "weekly" : isSession ? "session" : fallbackId;
        var title = id == "weekly" ? "Weekly" : id == "session" ? "Session" : fallbackTitle;
        var window = new RateWindow(id, title,
            ProviderHelpers.PercentUsedFrom(element.Value), ProviderHelpers.ResetFrom(element.Value));

        windows.RemoveAll(existing => string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase));
        windows.Add(window);
    }

    private static double? WindowDurationMinutes(JsonElement element)
    {
        var minutes = ProviderHelpers.FirstNumber(element,
            "windowDurationMins", "window_duration_mins", "windowMinutes", "window_minutes");
        if (minutes is not null)
        {
            return minutes;
        }

        var seconds = ProviderHelpers.FirstNumber(element,
            "limitWindowSeconds", "limit_window_seconds", "windowDurationSeconds", "window_duration_seconds");
        return seconds / 60D;
    }

    private static bool? FirstBoolean(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return property.GetBoolean();
            }

            if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out var value))
            {
                return value;
            }
        }

        return null;
    }
}
