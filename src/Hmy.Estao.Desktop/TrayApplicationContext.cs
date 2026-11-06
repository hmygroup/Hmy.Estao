using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Providers;
using Hmy.Estao.Core.Refresh;
using Hmy.Estao.Core.Security;
using Hmy.Estao.App;

namespace Hmy.Estao.Desktop;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ConfigStore _store;
    private readonly UsageRefreshService _service;
    private readonly ProviderOverlay _overlay = new();
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer _hoverMonitor = new() { Interval = 100 };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SecureCookieStore _cookies = new();
    private readonly UsageProviderFactory _factory = new();
    private readonly PacingStateStore _pacing;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private EstaoConfig _config;
    private DesktopWindow? _settings;
    private DesktopWindow? _popover;
    private string? _selectedProvider;
    private string? _status;
    private bool _signingIn;
    private ProviderSignInState? _providerSignIn;
    private CancellationTokenSource? _signInCancellation;
    private CancellationTokenSource? _codexCheckCancellation;
    private CodexAccountStatus? _codexAuthentication;
    private string? _codexCheckedProfile;
    private bool _checkingCodex;
    private DateTimeOffset _nextRefresh;
    private long _hoverLeftAt;

    public TrayApplicationContext(ConfigStore store)
    {
        _store = store;
        _config = ConfigStore.Normalize(store.LoadAsync().GetAwaiter().GetResult());
        _service = new UsageRefreshService(store);
        _pacing = new PacingStateStore(store.Path);
        // The overlay owns the UI dispatcher even when no web window has ever opened.
        _ = _overlay.Handle;
        _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? (Icon)SystemIcons.Application.Clone();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Usage", null, (_, _) => ShowPopover(null, Cursor.Position));
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
        _tray = new NotifyIcon { Icon = _icon, Text = "Estao", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (_popover is { IsDisposed: false, Visible: true }) _popover.Close();
            else ShowPopover(null, Cursor.Position);
        };
        _overlay.ProviderRequested += ShowPopover;
        _overlay.ProviderHoverChanged += provider =>
        {
            if (provider is null)
            {
                if (_popover is { IsDisposed: false, Visible: false }) _popover.Close();
                return;
            }
            if (_settings is { IsDisposed: false, Visible: true } || _popover is { Visible: true }) return;
            _selectedProvider = provider;
            EnsurePopover();
            _popover!.BeginInitialization();
        };
        _overlay.ProviderHoverRequested += (provider, point) =>
        {
            ShowPopover(provider, point);
            if (_popover is not null) { _hoverLeftAt = 0; _hoverMonitor.Start(); }
        };
        _hoverMonitor.Tick += (_, _) =>
        {
            if (_popover is null) { _hoverMonitor.Stop(); return; }
            if (_overlay.Bounds.Contains(Cursor.Position) || _popover.Bounds.Contains(Cursor.Position)) _hoverLeftAt = 0;
            else if (_hoverLeftAt == 0) _hoverLeftAt = Environment.TickCount64;
            else if (Environment.TickCount64 - _hoverLeftAt >= 300) _popover.Close();
        };
        _overlay.PositionCommitted += async point => await SavePositionAsync(point);
        _overlay.UpdateUsage(_config, []);
        _timer.Tick += async (_, _) =>
        {
            if (_config.Refresh.Enabled && DateTimeOffset.UtcNow >= _nextRefresh) await RefreshAsync();
        };
        _timer.Start();
        _overlay.BeginInvoke(async () => await RefreshAsync());
    }

    public void ShowSettings()
    {
        if (_settings is { IsDisposed: false }) { _settings.Activate(); return; }
        _popover?.Close();
        _settings = CreateWindow("settings");
        _settings.StartPosition = FormStartPosition.CenterScreen;
        _settings.FormClosed += (_, _) =>
        {
            _settings = null;
            _signInCancellation?.Cancel();
            _codexCheckCancellation?.Cancel();
            _codexCheckedProfile = null;
        };
        _settings.Show();
    }

    private DesktopWindow CreateWindow(string view) => new(view,
        () => StateAsync(view), SaveSettingsAsync, CommandAsync, _config.Theme, _store.Path);

    private void ShowPopover(string? provider, Point anchor)
    {
        _hoverMonitor.Stop();
        if (_settings is { IsDisposed: false, Visible: true }) return;
        _selectedProvider = provider;
        EnsurePopover();
        PositionPopover(provider, anchor);
        if (_popover!.Visible) _popover.Activate();
        else _popover.Show(); // Do not wait for browser initialization at the hover deadline.
    }

    private void EnsurePopover()
    {
        if (_popover is { IsDisposed: false }) return;
        var popover = CreateWindow("popover");
        _popover = popover;
        popover.FormClosed += (_, _) => { if (ReferenceEquals(_popover, popover)) _popover = null; _hoverMonitor.Stop(); };
        popover.Deactivate += (_, _) => popover.Close();
    }

    private void PositionPopover(string? provider, Point anchor)
    {
        if (_popover is null) return;
        // Overlay events identify the provider, but its individual cell is not
        // the positioning anchor: every provider shares the overlay's left edge.
        var anchorBounds = provider is not null ? _overlay.Bounds : new Rectangle(anchor, Size.Empty);
        var area = provider is not null ? Screen.FromRectangle(anchorBounds).WorkingArea : Screen.FromPoint(anchor).WorkingArea;
        _popover.MinimumSize = new Size(Math.Min(_popover.MinimumSize.Width, area.Width),
            Math.Min(_popover.MinimumSize.Height, area.Height));
        _popover.Bounds = PopoverPlacement.Calculate(_popover.Size, anchorBounds, area);
    }

    private async Task RefreshAsync()
    {
        _nextRefresh = DateTimeOffset.UtcNow.AddMinutes(_config.Refresh.IntervalMinutes);
        try
        {
            var snapshots = await _service.RefreshAsync(_lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            _overlay.UpdateUsage(_config, snapshots);
            await CheckPacingAsync(snapshots);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _status = $"Refresh failed: {exception.Message}"; }
    }

    private async Task<DesktopState> StateAsync(string view)
    {
        if (view == "settings") CheckCodexAccount();
        var providers = new List<ProviderSettings>();
        foreach (var p in _config.Providers.Where(p => ProviderCatalog.IsSupported(p.Id)))
            providers.Add(new ProviderSettings(p.Id, ProviderCatalog.DisplayName(p.Id), p.Enabled == true,
                p.Source ?? "auto", p.CookieSource ?? "auto", p.WorkspaceId, p.EnterpriseHost, p.Region,
                p.CodexProfileHomePaths, p.Pacing, !string.IsNullOrWhiteSpace(p.ApiKey),
                !string.IsNullOrWhiteSpace(p.CookieHeader) || !string.IsNullOrWhiteSpace(await _cookies.ReadCookieHeaderAsync(p.Id)),
                Authentication: p.Id == "codex" ? _codexAuthentication : null));
        var accounts = _config.Providers.Where(p => p.Enabled == true && ProviderCatalog.IsSupported(p.Id))
            .ToDictionary(p => p.Id, p => _factory.Create(p.Id).GetAccounts(p)
                .Select((a, i) => new AccountChoice(i, a.Label)).ToList());
        return new DesktopState(view, _selectedProvider, providers, _config.UsageAlerts!, _config.Refresh,
            _config.TaskbarOverlay.Enabled, _config.TaskbarOverlay.ProviderIds, StartupManager.Enabled,
            _service.LastSnapshots, view == "popover" ? _service.History : [], accounts, _status,
            DesktopThemeCatalog.Resolve(_config.Theme).Id, view == "settings" ? _providerSignIn : null);
    }

    private async Task SaveSettingsAsync(SettingsUpdate update)
    {
        if (!DesktopThemeCatalog.IsSupported(update.Theme)) throw new ArgumentException("Choose a supported desktop theme.");
        if (update.Providers.Select(p => p.Id).Distinct().Count() != update.Providers.Count
            || update.Providers.Any(p => !ProviderCatalog.IsSupported(p.Id)))
            throw new ArgumentException("Invalid provider list.");
        if (!double.IsFinite(update.UsageAlerts.WarningPercent) || !double.IsFinite(update.UsageAlerts.CriticalPercent)
            || update.UsageAlerts.WarningPercent < 0 || update.UsageAlerts.WarningPercent >= update.UsageAlerts.CriticalPercent
            || update.UsageAlerts.CriticalPercent > 100)
            throw new ArgumentException("Warning must be below critical, between 0 and 100%.");
        await _saveGate.WaitAsync();
        try
        {
            // Reload so an overlay drag, account switch, or untouched legacy section is never overwritten by a stale web draft.
            var config = await _store.LoadAsync();
            foreach (var change in update.Providers)
            {
                var p = config.Providers.First(p => p.Id == change.Id);
                p.Enabled = change.Enabled;
                p.Source = change.Source;
                p.CookieSource = change.CookieSource;
                p.WorkspaceId = change.WorkspaceId;
                p.EnterpriseHost = change.EnterpriseHost;
                p.Region = change.Region;
                p.CodexProfileHomePaths = change.CodexProfileHomePaths;
                p.Pacing = change.Pacing;
                if (change.ClearApiKey) p.ApiKey = null;
                else if (!string.IsNullOrWhiteSpace(change.ApiKey)) p.ApiKey = change.ApiKey.Trim();
            }
            config.UsageAlerts = update.UsageAlerts;
            config.Theme = DesktopThemeCatalog.Resolve(update.Theme).Id;
            config.Refresh = update.Refresh;
            config.TaskbarOverlay.Enabled = update.OverlayEnabled;
            config.TaskbarOverlay.ProviderIds = update.OverlayProviderIds;
            var errors = ConfigValidation.Validate(config);
            if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
            foreach (var change in update.Providers)
            {
                var p = config.Providers.First(p => p.Id == change.Id);
                if (change.ClearCookie) { await _cookies.ClearCookieHeaderAsync(p.Id); p.CookieHeader = null; }
                else if (!string.IsNullOrWhiteSpace(change.NewCookie))
                {
                    await _cookies.SaveCookieHeaderAsync(p.Id, change.NewCookie);
                    p.CookieHeader = null;
                }
            }
            if (StartupManager.Enabled != update.LaunchAtSignIn) StartupManager.Set(update.LaunchAtSignIn);
            await _store.SaveAsync(config);
            _config = config;
            _overlay.UpdateUsage(_config, _service.LastSnapshots);
            _status = "Settings saved";
        }
        finally { _saveGate.Release(); }
        await RefreshAsync();
    }

    private async Task SavePositionAsync(Point point)
    {
        await _saveGate.WaitAsync();
        try
        {
            var config = await _store.LoadAsync();
            config.TaskbarOverlay.PositionX = point.X;
            config.TaskbarOverlay.PositionY = point.Y;
            await _store.SaveAsync(config);
            _config = config;
        }
        catch (Exception exception) { _status = $"Could not save overlay position: {exception.Message}"; }
        finally { _saveGate.Release(); }
    }

    private async Task CommandAsync(DesktopCommand command)
    {
        switch (command.Action)
        {
            case "refresh": await RefreshAsync(); break;
            case "settings": _overlay.BeginInvoke(ShowSettings); break;
            case "close": _overlay.BeginInvoke(() => _popover?.Close()); break;
            case "account":
                await _saveGate.WaitAsync();
                try
                {
                    var config = await _store.LoadAsync();
                    var p = config.Providers.First(p => p.Id == command.Provider);
                    var count = _factory.Create(p.Id).GetAccounts(p).Count;
                    if (command.AccountIndex is not int index || index < 0 || index >= count) throw new ArgumentException("Invalid account.");
                    p.ActiveAccountIndex = index;
                    await _store.SaveAsync(config);
                    _config = config;
                }
                finally { _saveGate.Release(); }
                await RefreshAsync();
                break;
            case "signin":
                if (_signingIn) throw new InvalidOperationException("A sign-in is already in progress.");
                _ = SignInAsync(command.Provider);
                break;
            case "copy-signin-code":
                if (_providerSignIn is not { Pending: true, UserCode: not null } active)
                    throw new InvalidOperationException("Start GitHub sign-in to get a new code.");
                Clipboard.SetText(active.UserCode);
                break;
            case "open-signin-page":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/login/device") { UseShellExecute = true });
                break;
            case "cancel-signin": _signInCancellation?.Cancel(); break;
            case "check-codex-signin": _codexCheckedProfile = null; CheckCodexAccount(); break;
            default: throw new ArgumentException("Unknown command.");
        }
    }

    private async Task SignInAsync(string? provider)
    {
        _signingIn = true;
        _codexCheckCancellation?.Cancel();
        _codexCheckedProfile = null;
        _providerSignIn = new(provider ?? "", true, null, "Starting sign-in…");
        _signInCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            switch (provider)
            {
                case "codex":
                    var config = await _store.LoadAsync(_signInCancellation.Token);
                    var configured = config.Providers.First(p => p.Id == "codex");
                    var profile = CodexLoginService.ActiveProfile(configured, _factory.Create("codex").GetAccounts(configured));
                    await CodexLoginService.SignInAsync(profile,
                        uri => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }),
                        new Progress<string>(message =>
                        {
                            if (_providerSignIn is { Provider: "codex", Pending: true } current)
                                _providerSignIn = current with { Message = message };
                        }), _signInCancellation.Token);
                    _status = "Codex sign-in complete.";
                    _codexAuthentication = new(true, "Signed in with ChatGPT");
                    _codexCheckedProfile = profile;
                    _providerSignIn = new("codex", false, null, _status);
                    await RefreshAsync();
                    break;
                case "claude": OAuthLoginService.StartClaudeLogin(); _status = "Complete Claude sign-in in the terminal, then refresh."; break;
                case "copilot":
                    var login = await OAuthLoginService.SignInToCopilotAsync(new Progress<string>(s =>
                    {
                        if (_providerSignIn is { Pending: true } current) _providerSignIn = current with { Message = s };
                    }), _signInCancellation.Token, code =>
                    {
                        _providerSignIn = new("copilot", true, code, "Enter this code on GitHub to finish signing in. This is not an API key.");
                    });
                    await new SecureOAuthTokenStore().SaveAsync("copilot", new OAuthToken(login.AccessToken, login.AccountLabel));
                    _status = "GitHub sign-in complete. Choose OAuth as the usage source and save.";
                    await RefreshAsync();
                    break;
                default: throw new ArgumentException("This provider does not support sign-in.");
            }
        }
        catch (OperationCanceledException) { _status = "Sign-in cancelled."; }
        catch (Exception exception) { _status = $"Sign-in failed: {exception.Message}"; }
        finally
        {
            _signingIn = false;
            _providerSignIn = new(provider ?? "", false, null, _status ?? "Sign-in finished.");
            _signInCancellation.Dispose();
            _signInCancellation = null;
        }
    }

    private void CheckCodexAccount()
    {
        if (_checkingCodex || _signingIn || _lifetime.IsCancellationRequested) return;
        try
        {
            var configured = _config.Providers.First(p => p.Id == "codex");
            var profile = CodexLoginService.ActiveProfile(configured, _factory.Create("codex").GetAccounts(configured));
            if (profile == _codexCheckedProfile) return;
            _codexCheckedProfile = profile;
            _codexAuthentication = new(null, "Checking Codex sign-in…");
            _codexCheckCancellation?.Dispose();
            _codexCheckCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _checkingCodex = true;
            _ = ReadAsync(profile, _codexCheckCancellation.Token);
        }
        catch (Exception) { _codexAuthentication = new(null, "Could not check the saved Codex profile. Check its path and try again."); }

        async Task ReadAsync(string profile, CancellationToken ct)
        {
            try
            {
                var status = await CodexLoginService.ReadAccountAsync(profile, ct);
                if (!ct.IsCancellationRequested) _codexAuthentication = status;
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (!ct.IsCancellationRequested) _codexAuthentication = new(null,
                    exception is InvalidOperationException or TimeoutException ? exception.Message : "Could not check Codex sign-in. Try Check sign-in again.");
            }
            finally { _checkingCodex = false; }
        }
    }

    private async Task CheckPacingAsync(IReadOnlyList<UsageSnapshot> snapshots)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var snapshot in snapshots.Where(s => s.Error is null))
        {
            var pacing = _config.Providers.FirstOrDefault(p => p.Id == snapshot.Provider)?.Pacing;
            if (pacing is not { Enabled: true, NotifyOnExceed: true }) continue;
            foreach (var w in snapshot.Windows)
            {
                var points = _service.History.Where(p => p.Provider == snapshot.Provider && p.Window == w.Id)
                    .OrderBy(p => p.Timestamp).Select(p => new PacingPoint(p.Timestamp, p.PercentUsed)).ToArray();
                var result = PacingCalculator.Compute(points, pacing.DailyTargetPercent,
                    now - UsageWindowCatalog.DisplayRange(snapshot.Provider, w.Id, w.Title), now);
                if (result is not { IsOverPace: true } || !await _pacing.TryMarkNotifiedAsync(snapshot.Provider, w.Id, DateOnly.FromDateTime(now.LocalDateTime))) continue;
                _tray.ShowBalloonTip(8000, $"{snapshot.DisplayName}: {w.Title} ahead of pace",
                    $"You've used {result.ActualPercentNow:P0}, above your {result.ExpectedPercentNow:P0} daily target.", ToolTipIcon.Warning);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetime.Cancel();
            _timer.Dispose();
            _hoverMonitor.Dispose();
            _settings?.Dispose();
            _popover?.Dispose();
            _overlay.Dispose();
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
