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
    bool IsStale = false)
{
    public static UsageSnapshot Failure(string provider, string source, string error)
    {
        return new UsageSnapshot(provider, Configuration.ProviderCatalog.DisplayName(provider), source, DateTimeOffset.UtcNow, [], Error: error);
    }
}

public sealed record ProviderAccount(string Id, string Label, string? Secret = null, string? HomePath = null, string? WorkspaceId = null);

public sealed record FetchRequest(
    Configuration.ProviderConfig Config,
    ProviderAccount? Account,
    Configuration.ProviderSource Source,
    bool AllowBrowserImport,
    CancellationToken CancellationToken);
