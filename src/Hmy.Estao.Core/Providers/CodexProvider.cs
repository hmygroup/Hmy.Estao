using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Platform;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Core.Providers;

internal sealed class CodexProvider(HttpClient httpClient, IOAuthTokenStore? oauthStore = null) : IUsageProvider
{
    private readonly IOAuthTokenStore _oauth = oauthStore ?? new SecureOAuthTokenStore();
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
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

        if (request.Source == ProviderSource.Cli && !request.Config.CliAutoSignInDisabled)
        {
            var cli = await FetchCliAsync(home, account?.Label, request.CancellationToken).ConfigureAwait(false);
            if (cli.Error is null) return cli;
            if (await FetchOwnedOAuthAsync(request.CancellationToken).ConfigureAwait(false) is { } saved) return saved;
            return cli;
        }
        if (await FetchOwnedOAuthAsync(request.CancellationToken).ConfigureAwait(false) is { } owned) return owned;
        if (request.Config.CliAutoSignInDisabled) return SignedOut();

        if (request.Source is ProviderSource.Auto or ProviderSource.OAuth)
        {
            var oauth = await TryFetchOAuthAsync(home, account?.Label, request.CancellationToken).ConfigureAwait(false);
            oauthSnapshot = oauth;
            if (request.Source is ProviderSource.OAuth)
            {
                return oauth ?? UsageSnapshot.Failure(Id, "oauth", "Codex auth.json is missing or does not contain an access token.", new("error.auth", "Codex"));
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

        return UsageSnapshot.Failure(Id, request.Source.ToString().ToLowerInvariant(), "Codex MVP supports OAuth auth.json and codex app-server only.", new("error.unsupported_source", "Codex"));
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

        return await FetchOAuthTokenAsync(new(token, accountLabel), cancellationToken).ConfigureAwait(false);
    }

    private async Task<UsageSnapshot?> FetchOwnedOAuthAsync(CancellationToken ct)
    {
        var token = await _oauth.ReadAsync(Id, ct).ConfigureAwait(false);
        if (token is null) return null;
        try
        {
            await RefreshGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                token = await _oauth.ReadAsync(Id, ct).ConfigureAwait(false);
                if (token is null) return SignedOut();
                if (token.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
                {
                    var refreshed = await CodexOAuthClient.RefreshAsync(httpClient, token, ct).ConfigureAwait(false);
                    if (!await _oauth.TryReplaceAsync(Id, token, refreshed, ct).ConfigureAwait(false)) return SignedOut();
                    token = refreshed;
                }
            }
            finally { RefreshGate.Release(); }
            return await FetchOAuthTokenAsync(token, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return UsageSnapshot.Failure(Id, "oauth", exception.Message, UserMessage.FromException(exception)); }
    }

    private async Task<UsageSnapshot> FetchOAuthTokenAsync(OAuthToken token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        if (!string.IsNullOrWhiteSpace(token.AccountId)) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", token.AccountId);
        request.Headers.UserAgent.ParseAdd("Estao/0.1");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return UsageSnapshot.Failure(Id, "oauth", $"Codex usage API returned {(int)response.StatusCode}.", new("error.http", "Codex", (int)response.StatusCode));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return SnapshotFromWham(document.RootElement, "oauth", token.AccountLabel);
    }

    private async Task<UsageSnapshot> FetchCliAsync(string home, string? accountLabel, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(home)) return SignedOut();
        string executable;
        try { executable = CodexProcess.FindExecutable(); }
        catch (FileNotFoundException) { return SignedOut(); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            return await CodexProcess.RunAsync(CodexProcess.CreateStartInfo(executable, home, ["app-server"]),
                (output, input, ct) => ReadCliUsageAsync(output, input, accountLabel, ct), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return UsageSnapshot.Failure(Id, "cli", "Codex usage check timed out. Check your Codex sign-in and try again.", new("error.codex_timeout")); }
    }

    internal static async Task<UsageSnapshot> ReadCliUsageAsync(TextReader output, TextWriter input, string? accountLabel, CancellationToken ct)
    {
        await SendAsync(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "estao", version = "0.0.9" } } });
        var stage = 1;
        string? accountResponse = null;
        while (await output.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length > 1024 * 1024) return UsageSnapshot.Failure("codex", "cli", "Codex returned an oversized usage response.", new("error.invalid_data"));
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var requestId) || requestId != stage) continue;
                if (root.TryGetProperty("error", out _)) return UsageSnapshot.Failure("codex", "cli", "Codex could not read account usage. Check your sign-in or update Codex CLI.", new("error.codex_usage"));
                if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) continue;
                if (stage == 1)
                {
                    stage = 2;
                    await SendAsync(new { method = "initialized", @params = new { } });
                    await SendAsync(new { id = 2, method = "account/read", @params = new { refreshToken = false } });
                }
                else if (stage == 2)
                {
                    if (result.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Null) return SignedOut();
                    accountResponse = line;
                    stage = 3;
                    await SendAsync(new { id = 3, method = "account/rateLimits/read", @params = new { } });
                }
                else return ParseCliOutput(accountResponse + "\n" + line, accountLabel);
            }
        }
        return UsageSnapshot.Failure("codex", "cli", "Codex closed before returning account usage. Try again or update Codex CLI.", new("error.codex_usage"));

        async Task SendAsync<T>(T message)
        {
            await input.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct).ConfigureAwait(false);
            await input.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static UsageSnapshot SignedOut() => UsageSnapshot.Failure("codex", "cli",
        "Codex is not signed in. Open Settings > Providers > Codex and sign in.", new("error.auth", "Codex"));

    internal static UsageSnapshot ParseCliOutput(string output, string? accountLabel)
    {
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
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var requestId) && requestId == 2
                && root.TryGetProperty("result", out var accountResult) && accountResult.ValueKind == JsonValueKind.Object
                && accountResult.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Null)
                return SignedOut();
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
            return UsageSnapshot.Failure("codex", "cli", "codex app-server did not return rate-limit data.", new("error.usage_missing", "Codex"));
        }

        return new UsageSnapshot("codex", ProviderCatalog.DisplayName("codex"), "cli", DateTimeOffset.UtcNow, windows, email, plan, credits);
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
        // The app-server reports whole percentages, including 1 for 1%,
        // not normalized fractions. Keep other providers' legacy formats unchanged.
        var usedPercent = ProviderHelpers.FirstNumber(element.Value, "usedPercent", "percentUsed", "usagePercent");
        var window = new RateWindow(id, title,
            usedPercent is { } value ? ProviderHelpers.NormalizeWholePercent(value) : ProviderHelpers.PercentUsedFrom(element.Value),
            ProviderHelpers.ResetFrom(element.Value));

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
