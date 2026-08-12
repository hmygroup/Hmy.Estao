using Hmy.Estao.Core;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Formatting;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Providers;
using Hmy.Estao.Core.Refresh;
using Hmy.Estao.App.Controls.Zarpa;
using Hmy.Estao.App.Services;
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
    private readonly System.Windows.Forms.Timer _hoverPopoverTimer;
    private readonly SynchronizationContext _uiContext;
    private readonly PacingStateStore _pacingStateStore;
    private EstaoConfig _config;
    private IReadOnlyList<UsageSnapshot> _snapshots = [];
    private UsagePopover? _popover;
    private bool _popoverOpenedFromOverlayHover;
    private long _popoverHoverLeftAt;

    public TrayApplicationContext(ConfigStore configStore, Func<ConfigStore, UsageRefreshService> serviceFactory)
    {
        _configStore = configStore;
        _config = _configStore.LoadAsync().GetAwaiter().GetResult();
        _pacingStateStore = new PacingStateStore(configStore.Path);
        ApplyConfiguredTheme(_config);
        _taskbarOverlay = new TaskbarUsageOverlay();
        _taskbarOverlay.PositionCommitted += SaveTaskbarOverlayPosition;
        _taskbarOverlay.ProviderHoverRequested += ShowHoveredUsagePopover;
        _hoverPopoverTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _hoverPopoverTimer.Tick += (_, _) => MonitorHoveredUsagePopover();
        _refreshService = serviceFactory(configStore);
        _refreshService.Refreshed += (_, snapshots) => PostMenuRebuild(snapshots);
        _refreshLoop = new AdaptiveRefreshLoop(
            _refreshService,
            () => SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline,
            ConfiguredRefreshDelay);
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
            _hoverPopoverTimer.Stop();
            _hoverPopoverTimer.Dispose();
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
        _taskbarOverlay.Update(snapshots, _refreshService.History, OverlayConfigForDisplay());
        if (_config.Providers.Any(provider => provider.Pacing is { Enabled: true, NotifyOnExceed: true }))
            _ = CheckPacingWarningsAsync(snapshots);

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
        var startup = new ToolStripMenuItem("Launch at sign-in") { Checked = StartupManager.IsStartupEnabled() };
        startup.Click += (_, _) =>
        {
            var enable = !StartupManager.IsStartupEnabled();
            var updated = enable ? StartupManager.EnableStartup() : StartupManager.DisableStartup();
            startup.Checked = StartupManager.IsStartupEnabled();
            if (!updated)
            {
                MessageBox.Show("Could not update the Windows startup setting.", EstaoConstants.DisplayName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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

        OpenUsagePopover(Cursor.Position, null, openedFromOverlayHover: false);
    }

    private void ShowHoveredUsagePopover(string provider, Point anchor)
    {
        if (_popover is { IsDisposed: false, Visible: true })
        {
            if (_popoverOpenedFromOverlayHover) _popover.ShowProvider(provider);
            return;
        }

        _refreshLoop.MarkInteraction();
        OpenUsagePopover(anchor, provider, openedFromOverlayHover: true);
    }

    private void OpenUsagePopover(Point anchor, string? provider, bool openedFromOverlayHover)
    {
        var popover = new UsagePopover(_snapshots, RefreshAsync, ShowSettings, ExitThread, _zarpaTheme.Preset,
            _refreshService.History, _config.Providers, _zarpaTheme.BackdropStyle, _zarpaTheme.BackdropOpacity);
        _popover = popover;
        _popoverOpenedFromOverlayHover = openedFromOverlayHover;
        _popoverHoverLeftAt = 0;
        popover.FormClosed += (_, _) =>
        {
            if (!ReferenceEquals(_popover, popover)) return;
            _popover = null;
            _popoverOpenedFromOverlayHover = false;
            _hoverPopoverTimer.Stop();
        };
        popover.ShowAt(anchor, provider);
        if (openedFromOverlayHover) _hoverPopoverTimer.Start();
    }

    private void MonitorHoveredUsagePopover()
    {
        if (!_popoverOpenedFromOverlayHover || _popover is not { IsDisposed: false, Visible: true } popover)
        {
            _hoverPopoverTimer.Stop();
            return;
        }

        var cursor = Cursor.Position;
        if (_taskbarOverlay.Bounds.Contains(cursor) || popover.Bounds.Contains(cursor))
        {
            _popoverHoverLeftAt = 0;
            return;
        }

        if (_popoverHoverLeftAt == 0)
        {
            _popoverHoverLeftAt = Environment.TickCount64;
            return;
        }

        if (Environment.TickCount64 - _popoverHoverLeftAt < 300) return;
        popover.Close();
    }

    private async Task RefreshAsync()
    {
        await _refreshService.RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Compares every window's latest usage against the configured daily
    /// pacing target and shows a one-time-per-day tray balloon the first time
    /// a window goes over pace. Windows that are back under pace (e.g. after
    /// a reset) are free to warn again on a later day.
    /// </summary>
    private async Task CheckPacingWarningsAsync(IReadOnlyList<UsageSnapshot> snapshots)
    {
        var history = _refreshService.History;
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.LocalDateTime);

        foreach (var snapshot in snapshots)
        {
            if (snapshot.Error is not null) continue;
            var provider = ProviderCatalog.NormalizeId(snapshot.Provider);
            var pacing = _config.Providers.FirstOrDefault(configured =>
                string.Equals(ProviderCatalog.NormalizeId(configured.Id), provider, StringComparison.OrdinalIgnoreCase))?.Pacing;
            if (pacing is not { Enabled: true, NotifyOnExceed: true }) continue;

            foreach (var window in snapshot.Windows)
            {
                var rangeStart = now - UsageWindowCatalog.DisplayRange(provider, window.Id, window.Title);
                var points = history
                    .Where(point => string.Equals(point.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(point.Window, window.Id, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(point => point.Timestamp)
                    .Select(point => new PacingPoint(point.Timestamp, point.PercentUsed))
                    .ToArray();
                if (points.Length == 0) continue;

                var result = PacingCalculator.Compute(points, pacing.DailyTargetPercent, rangeStart, now);
                if (result is null || !result.IsOverPace) continue;

                var alreadyNotifiedToday = !await _pacingStateStore
                    .TryMarkNotifiedAsync(provider, window.Id, today).ConfigureAwait(false);
                if (alreadyNotifiedToday) continue;

                _uiContext.Post(_ => ShowPacingBalloon(snapshot.DisplayName, window, result), null);
            }
        }
    }

    private void ShowPacingBalloon(string displayName, RateWindow window, PacingResult result)
    {
        var actual = (int)Math.Round(result.ActualPercentNow * 100D);
        var expected = (int)Math.Round(result.ExpectedPercentNow * 100D);
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
        _notifyIcon.BalloonTipTitle = $"{displayName}: {window.Title} ahead of pace";
        _notifyIcon.BalloonTipText =
            $"You've used {actual}% of the {window.Title.ToLowerInvariant()} limit, above your {expected}% daily target.";
        _notifyIcon.ShowBalloonTip(8000);
    }

    private TimeSpan ConfiguredRefreshDelay()
    {
        if (!_config.Refresh.Enabled) return TimeSpan.FromMinutes(1);
        return TimeSpan.FromMinutes(Math.Clamp(_config.Refresh.IntervalMinutes, 1, 60));
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_configStore, previewOverlay: PreviewOverlay);
        form.ShowDialog();
        _config = _configStore.LoadAsync().GetAwaiter().GetResult();
        ApplyConfiguredTheme(_config);
        _popover?.ApplyTheme(_zarpaTheme.Preset, _zarpaTheme.BackdropStyle, _zarpaTheme.BackdropOpacity);
        _taskbarOverlay.Update(_snapshots, _refreshService.History, OverlayConfigForDisplay());
        if (_popover is { IsDisposed: false }) _popover.UpdatePacing(_config.Providers);
        _refreshLoop.Restart();
    }

    private void PreviewOverlay(EstaoConfig previewConfig)
    {
        ApplyConfiguredTheme(previewConfig);
        _taskbarOverlay.Update(_snapshots, _refreshService.History, OverlayConfigForDisplay(previewConfig));
        if (_popover is { IsDisposed: false, Visible: true })
            _popover.ApplyTheme(_zarpaTheme.Preset, _zarpaTheme.BackdropStyle, _zarpaTheme.BackdropOpacity);
        else
            ShowUsagePopover();
    }

    private async void SaveTaskbarOverlayPosition(Point position)
    {
        _config.TaskbarOverlay.PositionX = position.X;
        _config.TaskbarOverlay.PositionY = position.Y;
        try
        {
            await _configStore.SaveAsync(_config).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Could not save the overlay position.\n\n{exception.Message}", EstaoConstants.DisplayName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplyConfiguredTheme(EstaoConfig config)
    {
        _zarpaTheme.Preset = ZarpaThemePreferences.Parse(config.Theme);
        _zarpaTheme.BackdropStyle = ZarpaThemePreferences.ParseBackdrop(config.BackdropStyle);
    }

    private TaskbarOverlayConfig OverlayConfigForDisplay(EstaoConfig? source = null)
    {
        var config = source ?? _config;
        var configured = new TaskbarOverlayConfig
        {
            Enabled = config.TaskbarOverlay.Enabled,
            ProviderIds = config.TaskbarOverlay.ProviderIds.ToList(),
            Controls = config.TaskbarOverlay.Controls.ToList(),
            DisplayMode = config.TaskbarOverlay.DisplayMode,
            Size = config.TaskbarOverlay.Size,
            MoveEnabled = config.TaskbarOverlay.MoveEnabled,
            PositionX = config.TaskbarOverlay.PositionX,
            PositionY = config.TaskbarOverlay.PositionY
        };
        if (configured.ProviderIds.Count == 0)
        {
            configured.ProviderIds = config.Providers
                .Where(provider => provider.Enabled == true && ProviderCatalog.IsSupported(provider.Id))
                .Select(provider => ProviderCatalog.NormalizeId(provider.Id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return configured;
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
