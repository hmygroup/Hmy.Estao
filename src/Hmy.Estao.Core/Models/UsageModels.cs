namespace Hmy.Estao.Core.Models;

public sealed record RateWindow(
    string Id,
    string Title,
    double? PercentUsed,
    DateTimeOffset? ResetAt,
    double? Used = null,
    double? Limit = null,
    string? Unit = null)
{
    public double? PercentRemaining => PercentUsed is null ? null : Math.Clamp(1 - PercentUsed.Value, 0, 1);
    public UserMessage? UnitMessage => Unit switch
    {
        "credits" => new("credits"),
        "tokens" => new("window.tokens"),
        "requests" => new("window.requests"),
        _ => null
    };
    private UserMessage? _titleMessage;
    public UserMessage TitleMessage
    {
        get => _titleMessage ?? (Id switch
        {
            "session" or "weekly" or "daily" or "monthly" or "chat" or "tools" or "tokens" or "requests" => new UserMessage($"window.{Id}"),
            "premium" or "premium_interactions" or "premiumInteractions" => new("window.premium"),
            "mcp-monthly" => new("window.mcp_monthly"),
            _ => new("window.named", Title)
        });
        init => _titleMessage = value;
    }
}

public sealed record CreditsSnapshot(double? Balance, string? Unit, bool? Unlimited = null);

public sealed record UsageSnapshot(
    string Provider,
    string DisplayName,
    string Source,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<RateWindow> Windows,
    string? Account = null,
    string? Plan = null,
    CreditsSnapshot? Credits = null,
    string? Error = null,
    bool IsStale = false,
    UserMessage? ErrorMessage = null,
    UserMessage? AccountMessage = null)
{
    public static UsageSnapshot Failure(string provider, string source, string error, UserMessage? message = null)
    {
        return new UsageSnapshot(provider, Configuration.ProviderCatalog.DisplayName(provider), source, DateTimeOffset.UtcNow, [], Error: error,
            ErrorMessage: message ?? new UserMessage("error.provider", Configuration.ProviderCatalog.DisplayName(provider)));
    }
}

/// <summary>
/// A locally persisted usage sample. This is deliberately independent from the
/// provider API response so the usage chart can be rendered from the computer's
/// own history when a provider is offline.
/// </summary>
public sealed record UsageHistoryPoint(
    string Provider,
    string Window,
    DateTimeOffset Timestamp,
    double PercentUsed);

public sealed record ProviderAccount(string Id, string Label, string? Secret = null, string? HomePath = null, string? WorkspaceId = null)
{
    public UserMessage? LabelMessage => Id switch
    {
        "api-key" => new("account.token"),
        "manual-cookie" => new("account.cookie"),
        "configured" => new("account.workspace"),
        _ => null
    };
}

public sealed record FetchRequest(
    Configuration.ProviderConfig Config,
    ProviderAccount? Account,
    Configuration.ProviderSource Source,
    CancellationToken CancellationToken);
