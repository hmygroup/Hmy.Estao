using Hmy.Estao.Core;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Formatting;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Providers;
using Hmy.Estao.Core.Refresh;
using Microsoft.Win32;

namespace Hmy.Estao.App;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ConfigStore _configStore;
    private readonly UsageRefreshService _refreshService;
    private readonly UsageProviderFactory _providerFactory = new();
    private readonly AdaptiveRefreshLoop _refreshLoop;
    private readonly NotifyIcon _notifyIcon;
    private readonly SynchronizationContext _uiContext;

    public TrayApplicationContext(ConfigStore configStore, Func<ConfigStore, UsageRefreshService> serviceFactory)
    {
        _configStore = configStore;
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
                _notifyIcon.ContextMenuStrip?.Show(Cursor.Position);
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
        }

        base.Dispose(disposing);
    }

    private void PostMenuRebuild(IReadOnlyList<UsageSnapshot> snapshots)
    {
        _uiContext.Post(_ => RebuildMenu(snapshots), null);
    }

    private void RebuildMenu(IReadOnlyList<UsageSnapshot> snapshots)
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => _refreshLoop.MarkInteraction();
        if (snapshots.Count == 0)
        {
            menu.Items.Add("Loading usage...").Enabled = false;
        }
        else
        {
            foreach (var snapshot in snapshots)
            {
                var item = new ToolStripMenuItem(Summary(snapshot));
                item.Enabled = false;
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync(allowBrowserImport: false).ConfigureAwait(false));
        menu.Items.Add("Refresh and import Chrome/Edge cookies", null, async (_, _) => await RefreshAsync(allowBrowserImport: true).ConfigureAwait(false));
        AddAccountMenu(menu);
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        var startup = new ToolStripMenuItem("Launch at sign-in") { Checked = StartupRegistration.IsEnabled() };
        startup.Click += (_, _) =>
        {
            StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled());
            startup.Checked = StartupRegistration.IsEnabled();
        };
        menu.Items.Add(startup);
        menu.Items.Add("Quit", null, (_, _) => ExitThread());

        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.Text = snapshots.FirstOrDefault(snapshot => snapshot.Error is null)?.DisplayName ?? EstaoConstants.DisplayName;
    }

    private async Task RefreshAsync(bool allowBrowserImport)
    {
        await _refreshService.RefreshAsync(allowBrowserImport).ConfigureAwait(false);
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_configStore);
        form.ShowDialog();
        _ = RefreshAsync(allowBrowserImport: false);
    }

    private void AddAccountMenu(ContextMenuStrip menu)
    {
        var config = _configStore.LoadAsync().GetAwaiter().GetResult();
        var accountsRoot = new ToolStripMenuItem("Accounts");
        foreach (var providerConfig in config.Providers.Where(provider => provider.Enabled == true && ProviderCatalog.IsSupported(provider.Id)))
        {
            var provider = _providerFactory.Create(providerConfig.Id);
            var accounts = provider.GetAccounts(providerConfig);
            if (accounts.Count <= 1)
            {
                continue;
            }

            var providerItem = new ToolStripMenuItem(ProviderCatalog.DisplayName(providerConfig.Id));
            var activeIndex = providerConfig.ActiveAccountIndex ?? providerConfig.TokenAccounts?.ActiveIndex ?? 0;
            for (var index = 0; index < accounts.Count; index++)
            {
                var capturedIndex = index;
                var accountItem = new ToolStripMenuItem(accounts[index].Label)
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
        await RefreshAsync(allowBrowserImport: false).ConfigureAwait(false);
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
