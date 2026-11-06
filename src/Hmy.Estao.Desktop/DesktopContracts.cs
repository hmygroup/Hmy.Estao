using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Shofia.WebHost.Messaging;

namespace Hmy.Estao.Desktop;

internal static class DesktopContracts
{
    public static readonly RequestType<EmptyRequest, DesktopState> State = new("estao.state");
    public static readonly RequestType<SettingsUpdate, EmptyRequest> Save = new("estao.settings.save");
    public static readonly RequestType<DesktopCommand, EmptyRequest> Command = new("estao.command");
}

internal sealed record EmptyRequest;
internal sealed record DesktopCommand(string Action, string? Provider = null, int? AccountIndex = null);
internal sealed record ProviderSettings(
    string Id, string Name, bool Enabled, string Source, string CookieSource,
    string? WorkspaceId, string? EnterpriseHost, string? Region, List<string>? CodexProfileHomePaths,
    PacingConfig Pacing, bool HasApiKey, bool HasCookie, string? ApiKey = null,
    string? NewCookie = null, bool ClearApiKey = false, bool ClearCookie = false);
internal sealed record SettingsUpdate(List<ProviderSettings> Providers, UsageColorConfig UsageAlerts,
    RefreshConfig Refresh, bool OverlayEnabled, List<string> OverlayProviderIds, bool LaunchAtSignIn, string Theme);
internal sealed record AccountChoice(int Index, string Label);
internal sealed record ProviderSignInState(string Provider, bool Pending, string? UserCode, string Message);
internal sealed record DesktopState(string View, string? SelectedProvider, List<ProviderSettings> Providers,
    UsageColorConfig UsageAlerts, RefreshConfig Refresh, bool OverlayEnabled, List<string> OverlayProviderIds,
    bool LaunchAtSignIn, IReadOnlyList<UsageSnapshot> Snapshots, IReadOnlyList<UsageHistoryPoint> History,
    Dictionary<string, List<AccountChoice>> Accounts, string? Status, string Theme, ProviderSignInState? ProviderSignIn = null);
