using System.Net.Http.Headers;
using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Core.Providers;

internal sealed class ZaiProvider(HttpClient httpClient) : IUsageProvider
{
    private const string CreditLimitType = "CREDIT_LIMIT";
    private const string TokensLimitType = "TOKENS_LIMIT";
    private const string TimeLimitType = "TIME_LIMIT";

    public string Id => "zai";

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
            return UsageSnapshot.Failure(Id, "api",
                "Z.ai is not connected. Save your z.ai API key in Settings (API Keys page at z.ai).", new("error.auth", "Z.ai"));
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.z.ai/api/monitor/usage/quota/limit");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue(token);
        httpRequest.Headers.Accept.ParseAdd("application/json");
        httpRequest.Headers.UserAgent.ParseAdd("Estao/0.1");

        using var response = await httpClient.SendAsync(httpRequest, request.CancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return UsageSnapshot.Failure(Id, "api", $"Z.ai usage API returned {(int)response.StatusCode}.", new("error.http", "Z.ai", (int)response.StatusCode));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(request.CancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: request.CancellationToken).ConfigureAwait(false);
        return ParseQuotaLimits(document.RootElement, account?.Label);
    }

    internal static UsageSnapshot ParseQuotaLimits(JsonElement root, string? accountLabel)
    {
        var data = ProviderHelpers.FindProperty(root, "data") is { } payload && payload.ValueKind == JsonValueKind.Object
            ? payload
            : root;
        var windows = new List<RateWindow>();
        if (ProviderHelpers.FindProperty(data, "limits") is { ValueKind: JsonValueKind.Array } limits)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                if (ParseLimit(limit) is not { } window) continue;
                windows.Add(window);
            }
        }

        // The popover, tray and overlay render the first window as the primary
        // usage bar, so the rolling 5-hour window always comes first.
        windows = windows
            .OrderBy(window => window.Id switch
            {
                "session" => 0,
                "weekly" => 1,
                _ => 2
            })
            .ToList();

        if (windows.Count == 0)
        {
            return UsageSnapshot.Failure("zai", "api", "Z.ai quota limits were not found.", new("error.usage_missing", "Z.ai"));
        }

        return new UsageSnapshot("zai", ProviderCatalog.DisplayName("zai"), "api", DateTimeOffset.UtcNow, windows,
            accountLabel, ProviderHelpers.FirstString(data, "level"));
    }

    private static RateWindow? ParseLimit(JsonElement limit)
    {
        if (limit.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = ProviderHelpers.FirstString(limit, "type");
        var percent = ProviderHelpers.FirstNumber(limit, "percentage");
        double? percentUsed = percent is null ? null : Math.Clamp(percent.Value / 100D, 0D, 1D);
        var reset = ResetFrom(limit);

        return type switch
        {
            // Quota rows are identified by unit/number: unit=3, number=5 is the
            // rolling 5-hour window and unit=6, number=1 the weekly one. The
            // current API reports CREDIT_LIMIT rows with used/total credits;
            // TOKENS_LIMIT is kept for older responses.
            CreditLimitType or TokensLimitType => QuotaWindow(limit, percentUsed, reset),
            TimeLimitType => new RateWindow("mcp-monthly", "MCP (1 Month)", percentUsed, reset),
            _ => null
        };
    }

    private static RateWindow QuotaWindow(JsonElement limit, double? percentUsed, DateTimeOffset? reset)
    {
        var used = ProviderHelpers.FirstNumber(limit, "currentValue");
        var total = ProviderHelpers.FirstNumber(limit, "usage");
        var unit = ProviderHelpers.FirstString(limit, "unitName") ?? "credits";

        return (ProviderHelpers.FirstNumber(limit, "unit"), ProviderHelpers.FirstNumber(limit, "number")) switch
        {
            ({ } unitValue, { } number) when unitValue == 6 && number == 1 =>
                new RateWindow("weekly", "Weekly", percentUsed, reset, used, total, unit),
            _ => new RateWindow("session", "5h", percentUsed, reset, used, total, unit) { TitleMessage = new("window.five_hours") }
        };
    }

    private static DateTimeOffset? ResetFrom(JsonElement limit)
    {
        if (ProviderHelpers.FirstNumber(limit, "nextResetTime") is not { } timestamp)
        {
            return null;
        }

        try
        {
            return timestamp > 100_000_000_000D
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp)
                : DateTimeOffset.FromUnixTimeSeconds((long)timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
