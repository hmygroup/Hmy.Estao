using Hmy.Estao.Core;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Formatting;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Providers;
using Hmy.Estao.Core.Refresh;
using Hmy.Estao.App.Controls.Zarpa;
using Microsoft.Win32;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ConfigStore _configStore;
    private readonly UsageRefreshService _refreshService;
    private readonly UsageProviderFactory _providerFactory = new();
    private readonly AdaptiveRefreshLoop _refreshLoop;
    private readonly NotifyIcon _notifyIcon;
    private readonly ZarpaThemeManager _zarpaTheme = new() { Preset = ZarpaThemePreset.Graphite };
    private readonly TaskbarUsageOverlay _taskbarOverlay;
    private readonly SynchronizationContext _uiContext;
    private EstaoConfig _config;
    private IReadOnlyList<UsageSnapshot> _snapshots = [];
    private UsagePopover? _popover;

    public TrayApplicationContext(ConfigStore configStore, Func<ConfigStore, UsageRefreshService> serviceFactory)
    {
        _configStore = configStore;
        _config = _configStore.LoadAsync().GetAwaiter().GetResult();
        _zarpaTheme.Preset = ZarpaThemePreferences.Parse(_config.Theme);
        _taskbarOverlay = new TaskbarUsageOverlay(_zarpaTheme.Preset);
        _refreshService = serviceFactory(configStore);
        _refreshService.Refreshed += (_, snapshots) => PostMenuRebuild(snapshots);
        _refreshLoop = new AdaptiveRefreshLoop(_refreshService, () => SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline);
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = EstaoConstants.DisplayName,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                _refreshLoop.MarkInteraction();
                ShowUsagePopover();
            }
        };
        RebuildMenu([]);
        _refreshLoop.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshLoop.Dispose();
            _notifyIcon.Dispose();
            _taskbarOverlay.Dispose();
            _zarpaTheme.Dispose();
        }

        base.Dispose(disposing);
    }

    private void PostMenuRebuild(IReadOnlyList<UsageSnapshot> snapshots)
    {
        _uiContext.Post(_ => RebuildMenu(snapshots), null);
    }

    private void RebuildMenu(IReadOnlyList<UsageSnapshot> snapshots)
    {
        _snapshots = snapshots;
        if (_popover is { IsDisposed: false }) _popover.UpdateSnapshots(snapshots, _refreshService.History);
        _taskbarOverlay.SetTheme(_zarpaTheme.Preset);
        _taskbarOverlay.Update(snapshots, _refreshService.History, _config.TaskbarOverlay);

        var menu = new ZarpaContextMenu();
        menu.ApplyTheme(_zarpaTheme.Theme);
        menu.Opening += (_, _) => _refreshLoop.MarkInteraction();
        if (snapshots.Count == 0)
        {
            menu.Items.Add("Loading usage...").Enabled = false;
        }
        else
        {
            foreach (var snapshot in snapshots)
            {
                var item = new ZarpaMenuItem(Summary(snapshot), ProviderIcon(snapshot.Provider), null);
                item.Enabled = false;
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ZarpaMenuItem("Refresh now", "ic_fluent_arrow_sync_24_regular", async (_, _) => await RefreshAsync().ConfigureAwait(false)));
        AddAccountMenu(menu);
        menu.Items.Add(new ZarpaMenuItem("Settings...", "ic_fluent_settings_24_regular", (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripSeparator());
        var startup = new ToolStripMenuItem("Launch at sign-in") { Checked = StartupRegistration.IsEnabled() };
        startup.Click += (_, _) =>
        {
            StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled());
            startup.Checked = StartupRegistration.IsEnabled();
        };
        menu.Items.Add(startup);
        menu.Items.Add(new ZarpaMenuItem("Quit", "ic_fluent_dismiss_circle_24_regular", (_, _) => ExitThread()) { Tone = ZarpaMenuItemTone.Danger });

        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.Text = snapshots.FirstOrDefault(snapshot => snapshot.Error is null)?.DisplayName ?? EstaoConstants.DisplayName;
    }

    private void ShowUsagePopover()
    {
        if (_popover is { IsDisposed: false, Visible: true })
        {
            _popover.Close();
            return;
        }

        _popover = new UsagePopover(_snapshots, RefreshAsync, ShowSettings, ExitThread, _zarpaTheme.Preset,
            _refreshService.History);
        _popover.FormClosed += (_, _) => _popover = null;
        _popover.ShowAt(Cursor.Position);
    }

    private async Task RefreshAsync()
    {
        await _refreshService.RefreshAsync().ConfigureAwait(false);
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_configStore);
        form.ShowDialog();
        _config = _configStore.LoadAsync().GetAwaiter().GetResult();
        _zarpaTheme.Preset = ZarpaThemePreferences.Parse(_config.Theme);
        _taskbarOverlay.SetTheme(_zarpaTheme.Preset);
        _taskbarOverlay.Update(_snapshots, _refreshService.History, _config.TaskbarOverlay);
        _ = RefreshAsync();
    }

    private void AddAccountMenu(ContextMenuStrip menu)
    {
        var config = _config;
        var accountsRoot = new ZarpaMenuItem("Accounts", "ic_fluent_people_24_regular", null);
        foreach (var providerConfig in config.Providers.Where(provider => provider.Enabled == true && ProviderCatalog.IsSupported(provider.Id)))
        {
            var provider = _providerFactory.Create(providerConfig.Id);
            var accounts = provider.GetAccounts(providerConfig);
            if (accounts.Count <= 1)
            {
                continue;
            }

            var providerItem = new ZarpaMenuItem(ProviderCatalog.DisplayName(providerConfig.Id), ProviderIcon(providerConfig.Id), null);
            var activeIndex = providerConfig.ActiveAccountIndex ?? providerConfig.TokenAccounts?.ActiveIndex ?? 0;
            for (var index = 0; index < accounts.Count; index++)
            {
                var capturedIndex = index;
                var accountItem = new ZarpaMenuItem(accounts[index].Label, "ic_fluent_person_24_regular", null)
                {
                    Checked = capturedIndex == activeIndex
                };
                accountItem.Click += async (_, _) => await SelectAccountAsync(providerConfig.Id, capturedIndex).ConfigureAwait(false);
                providerItem.DropDownItems.Add(accountItem);
            }

            accountsRoot.DropDownItems.Add(providerItem);
        }

        if (accountsRoot.DropDownItems.Count > 0)
        {
            menu.Items.Add(accountsRoot);
        }
    }

    private async Task SelectAccountAsync(string providerId, int activeIndex)
    {
        var config = await _configStore.LoadAsync().ConfigureAwait(false);
        var provider = config.Providers.FirstOrDefault(item => item.Id == providerId);
        if (provider is null)
        {
            return;
        }

        provider.ActiveAccountIndex = activeIndex;
        await _configStore.SaveAsync(config).ConfigureAwait(false);
        _config = config;
        await RefreshAsync().ConfigureAwait(false);
    }

    private static string Summary(UsageSnapshot snapshot)
    {
        if (snapshot.Error is not null)
        {
            return $"{snapshot.DisplayName}: {snapshot.Error}";
        }

        var primary = snapshot.Windows.FirstOrDefault();
        if (primary?.PercentRemaining is null)
        {
            return $"{snapshot.DisplayName}: usage unavailable";
        }

        var account = string.IsNullOrWhiteSpace(snapshot.Account) ? string.Empty : $" - {snapshot.Account}";
        return $"{snapshot.DisplayName}: {primary.PercentRemaining.Value:P0} left{account}";
    }

    private static string ProviderIcon(string provider) => ProviderCatalog.NormalizeId(provider) switch
    {
        "codex" => "ic_fluent_bot_24_regular",
        "claude" => "ic_fluent_sparkle_24_regular",
        "copilot" => "ic_fluent_people_24_regular",
        "opencode" => "ic_fluent_code_24_regular",
        _ => "ic_fluent_apps_24_regular"
    };

}

internal static class StartupRegistration
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(EstaoConstants.DisplayName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(EstaoConstants.DisplayName, Application.ExecutablePath);
        }
        else
        {
            key.DeleteValue(EstaoConstants.DisplayName, throwOnMissingValue: false);
        }
    }
}
