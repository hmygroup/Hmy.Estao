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
    private readonly ProviderRetryTimer _claudeRetryTimer;
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
    private UserMessage? _statusMessage;
    private bool _signingIn;
    private ProviderSignInState? _providerSignIn;
    private CancellationTokenSource? _signInCancellation;
    private CancellationTokenSource? _codexCheckCancellation;
    private CodexAccountStatus? _codexAuthentication;
    private string? _codexCheckedProfile;
    private bool _checkingCodex;
    private readonly Dictionary<string, CodexAccountStatus> _providerAuthentication = [];
    private readonly HashSet<string> _checkingProviders = [];
    private readonly Dictionary<string, int> _providerCheckVersions = [];
    private CancellationTokenSource? _providerCheckCancellation;
    private readonly Func<ProviderConfig, CancellationToken, Task<CodexAccountStatus>> _checkAuthentication;
    private readonly ProviderCliSessionService _cliSessions;
    private readonly Action<Uri> _openBrowser = uri => System.Diagnostics.Process.Start(
        new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    private readonly Dictionary<(string Provider, int Version), Task<ProviderCliSession?>> _cliProbes = [];
    private DateTimeOffset _nextRefresh;
    private long _hoverLeftAt;

    public TrayApplicationContext(ConfigStore store,
        Func<ProviderConfig, CancellationToken, Task<CodexAccountStatus>>? checkAuthentication = null)
    {
        _store = store;
        _checkAuthentication = checkAuthentication ?? ((provider, ct) => AuthenticationService().CheckAsync(provider, ct));
        _config = ConfigStore.Normalize(store.LoadAsync().GetAwaiter().GetResult());
        DesktopLocalization.SetLanguage(_config.Language);
        _service = new UsageRefreshService(store, _factory);
        _cliSessions = new(_factory);
        _claudeRetryTimer = new(() =>
        {
            if (_config.Refresh.Enabled && _config.Providers.Any(p => p.Id == "claude" && p.Enabled == true)
                && _factory.TakeDueClaudeRetry()) _ = RefreshAsync("claude");
        });
        _factory.ClaudeRetryChanged += OnClaudeRetryChanged;
        _pacing = new PacingStateStore(store.Path);
        // The overlay owns the UI dispatcher even when no web window has ever opened.
        _ = _overlay.Handle;
        _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? (Icon)SystemIcons.Application.Clone();
        var menu = new ContextMenuStrip();
        menu.Items.Add(DesktopLocalization.Text("usage"), null, (_, _) => ShowPopover(null, Cursor.Position));
        menu.Items.Add(DesktopLocalization.Text("tray.refresh"), null, async (_, _) => await RefreshAsync());
        menu.Items.Add(DesktopLocalization.Text("tray.settings"), null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(DesktopLocalization.Text("tray.quit"), null, (_, _) => ExitThread());
        _tray = new NotifyIcon { Icon = _icon, Text = DesktopBranding.DisplayName, ContextMenuStrip = menu, Visible = true };
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
        _overlay.BeginInvoke(async () =>
        {
            await ProbeEnabledCliSessionsAsync();
            await RefreshAsync();
        });
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
            _providerCheckCancellation?.Cancel();
            _providerAuthentication.Clear();
            _checkingProviders.Clear();
            _codexCheckedProfile = null;
        };
        _settings.Show();
    }

    private DesktopWindow CreateWindow(string view) => new(view,
        () => StateAsync(view), SaveSettingsAsync, CommandAsync, _config.Theme, _store.Path, BeginProviderSignInAsync);

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

    private async Task RefreshAsync(string? provider = null)
    {
        if (provider is null) _nextRefresh = DateTimeOffset.UtcNow.AddMinutes(_config.Refresh.IntervalMinutes);
        try
        {
            var snapshots = provider is null ? await _service.RefreshAsync(_lifetime.Token)
                : await _service.RefreshProviderAsync(provider, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            foreach (var snapshot in snapshots.Where(s => s.Error is not null))
            {
                var authentication = ProviderAuthenticationService.FromSnapshot(snapshot);
                if (authentication.SignedIn != false) continue;
                if (snapshot.Provider == "codex") _codexAuthentication = authentication;
                else _providerAuthentication[snapshot.Provider] = authentication;
            }
            _overlay.UpdateUsage(_config, snapshots);
            ArmClaudeRetry();
            await CheckPacingAsync(snapshots);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _status = $"Refresh failed: {exception.Message}"; _statusMessage = new("settings.refresh_failed", UserMessage.FromException(exception)); }
    }

    private void OnClaudeRetryChanged(object? sender, EventArgs e)
    {
        if (_lifetime.IsCancellationRequested || _overlay.IsDisposed) return;
        try { _overlay.BeginInvoke(ArmClaudeRetry); }
        catch (InvalidOperationException) when (_lifetime.IsCancellationRequested || _overlay.IsDisposed) { }
    }

    private void ArmClaudeRetry() => _claudeRetryTimer.Schedule(
        !_lifetime.IsCancellationRequested && _config.Refresh.Enabled && _config.Providers.Any(p => p.Id == "claude" && p.Enabled == true)
            ? _factory.ClaudeRetryAt : null);

    private async Task<DesktopState> StateAsync(string view)
    {
        if (view == "settings") { CheckCodexAccount(); CheckOtherProviderAccounts(); }
        var providers = new List<ProviderSettings>();
        foreach (var p in _config.Providers.Where(p => ProviderCatalog.IsSupported(p.Id)))
            providers.Add(new ProviderSettings(p.Id, ProviderCatalog.DisplayName(p.Id), p.Enabled == true,
                p.Source ?? "auto", p.CookieSource ?? "auto", p.WorkspaceId, p.EnterpriseHost, p.Region,
                p.CodexProfileHomePaths, p.Pacing, !string.IsNullOrWhiteSpace(p.ApiKey),
                !string.IsNullOrWhiteSpace(p.CookieHeader) || !string.IsNullOrWhiteSpace(await _cookies.ReadCookieHeaderAsync(p.Id)),
                Authentication: p.Id == "codex" ? _codexAuthentication : _providerAuthentication.GetValueOrDefault(p.Id)));
        var accounts = _config.Providers.Where(p => p.Enabled == true && ProviderCatalog.IsSupported(p.Id))
            .ToDictionary(p => p.Id, p => _factory.Create(p.Id).GetAccounts(p)
                .Select((a, i) => new AccountChoice(i, a.Label, a.LabelMessage)).ToList());
        return new DesktopState(view, _selectedProvider, providers, _config.UsageAlerts!, _config.Refresh,
            _config.TaskbarOverlay.Enabled, _config.TaskbarOverlay.ProviderIds, StartupManager.Enabled,
            _service.LastSnapshots, view == "popover" ? _service.History : [], accounts, _status,
            DesktopThemeCatalog.Resolve(_config.Theme).Id, view == "settings" ? _providerSignIn : null, DesktopLocalization.Preference(_config.Language), _statusMessage, DesktopLocalization.Language,
            view == "popover" ? _service.LastSnapshots.SelectMany(s => UsageChartBuilder.Build(s, _service.History,
                _config.Providers.First(p => p.Id == s.Provider).Pacing, DateTimeOffset.UtcNow)).ToArray() : null);
    }

    private async Task SaveSettingsAsync(SettingsUpdate update)
    {
        var newlyEnabled = new List<string>();
        if (!DesktopLocalization.IsSupported(update.Language)) throw new ArgumentException("Choose a supported language.").WithUserMessage("settings.language_invalid");
        if (!DesktopThemeCatalog.IsSupported(update.Theme)) throw new ArgumentException("Choose a supported desktop theme.").WithUserMessage("settings.theme_invalid");
        if (update.Providers.Select(p => p.Id).Distinct().Count() != update.Providers.Count
            || update.Providers.Any(p => !ProviderCatalog.IsSupported(p.Id)))
            throw new ArgumentException("Invalid provider list.").WithUserMessage("settings.providers_invalid");
        if (!double.IsFinite(update.UsageAlerts.WarningPercent) || !double.IsFinite(update.UsageAlerts.CriticalPercent)
            || update.UsageAlerts.WarningPercent < 0 || update.UsageAlerts.WarningPercent >= update.UsageAlerts.CriticalPercent
            || update.UsageAlerts.CriticalPercent > 100)
            throw new ArgumentException("Warning must be below critical, between 0 and 100%.").WithUserMessage("settings.threshold_invalid");
        await _saveGate.WaitAsync();
        try
        {
            // Reload so an overlay drag, account switch, or untouched legacy section is never overwritten by a stale web draft.
            var config = await _store.LoadAsync();
            foreach (var change in update.Providers)
            {
                var p = config.Providers.First(p => p.Id == change.Id);
                if (change.Enabled && p.Enabled != true)
                {
                    newlyEnabled.Add(p.Id);
                    p.CliAutoSignInDisabled = false;
                }
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
            config.Language = update.Language;
            config.Refresh = update.Refresh;
            config.TaskbarOverlay.Enabled = update.OverlayEnabled;
            config.TaskbarOverlay.ProviderIds = update.OverlayProviderIds;
            var errors = ConfigValidation.Validate(config);
            if (errors.Count > 0)
            {
                throw new ArgumentException(string.Join("\n", errors)).WithUserMessage("message.list", ConfigValidation.ValidateMessages(config).Cast<object?>().ToArray());
            }
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
            ApplyLanguage();
            _overlay.UpdateUsage(_config, _service.LastSnapshots);
            _status = "Settings saved";
            _statusMessage = new("saved");
        }
        finally { _saveGate.Release(); }
        InvalidateAuthentication();
        await ProbeEnabledCliSessionsAsync(newlyEnabled);
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
        catch (Exception exception) { _status = $"Could not save overlay position: {exception.Message}"; _statusMessage = new("settings.position_failed", UserMessage.FromException(exception)); }
        finally { _saveGate.Release(); }
    }

    private async Task CommandAsync(DesktopCommand command)
    {
        switch (command.Action)
        {
            case "refresh": await RefreshAsync(); break;
            case "settings": _overlay.BeginInvoke(ShowSettings); break;
            case "usage-dashboard": _openBrowser(ProviderLinks.Dashboard(command.Provider ?? "")); break;
            case "status-page": _openBrowser(ProviderLinks.Status(command.Provider ?? "")); break;
            case "quit": _overlay.BeginInvoke(ExitThread); break;
            case "close": _overlay.BeginInvoke(() => _popover?.Close()); break;
            case "account":
                await _saveGate.WaitAsync();
                try
                {
                    var config = await _store.LoadAsync();
                    var p = config.Providers.First(p => p.Id == command.Provider);
                    var count = _factory.Create(p.Id).GetAccounts(p).Count;
                    if (command.AccountIndex is not int index || index < 0 || index >= count) throw new ArgumentException("Invalid account.").WithUserMessage("settings.account_invalid");
                    p.ActiveAccountIndex = index;
                    await _store.SaveAsync(config);
                    _config = config;
                }
                finally { _saveGate.Release(); }
                await RefreshAsync();
                break;
            case "signin":
                if (_signingIn) throw new InvalidOperationException("A sign-in is already in progress.").WithUserMessage("signin.already_pending");
                _ = SignInAsync(command.Provider);
                break;
            case "copy-signin-code":
                if (_providerSignIn is not { Pending: true, UserCode: not null } active)
                    throw new InvalidOperationException("Start GitHub sign-in to get a new code.").WithUserMessage("signin.need_code");
                Clipboard.SetText(active.UserCode);
                break;
            case "open-signin-page":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/login/device") { UseShellExecute = true });
                break;
            case "cancel-signin": _signInCancellation?.Cancel(); break;
            case "check-codex-signin": _codexCheckedProfile = null; CheckCodexAccount(); break;
            case "signout":
                if (_signingIn) throw new InvalidOperationException("A sign-in or sign-out is already in progress.").WithUserMessage("signin.already_pending");
                if (!ProviderCatalog.IsSupported(command.Provider ?? "")) throw new ArgumentException("Unknown provider.").WithUserMessage("settings.provider_unknown");
                _ = SignOutAsync(command.Provider!);
                break;
            default: throw new ArgumentException("Unknown command.").WithUserMessage("settings.command_unknown");
        }
    }

    private async Task SignInAsync(string? provider)
    {
        _signingIn = true;
        if (provider == "codex") { _codexCheckCancellation?.Cancel(); _codexCheckedProfile = null; }
        var failed = false;
        _providerSignIn = new(provider ?? "", true, null, "Starting sign-in…", MessageInfo: new("signin.start"));
        _signInCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            if (provider is not null)
            {
                InvalidateAuthentication(provider);
                await _saveGate.WaitAsync(_signInCancellation.Token);
                try
                {
                    var config = await _store.LoadAsync(_signInCancellation.Token);
                    var configured = config.Providers.First(p => p.Id == provider);
                    if (configured.CliAutoSignInDisabled)
                    {
                        configured.CliAutoSignInDisabled = false;
                        await _store.SaveAsync(config, _signInCancellation.Token);
                    }
                    _config = config;
                }
                finally { _saveGate.Release(); }
            }
            if (provider is not null && await TryUseCliSessionAsync(provider, true, _signInCancellation.Token))
            {
                await RefreshAsync(provider);
                var reading = _service.LastSnapshots.FirstOrDefault(s => s.Provider == provider);
                var rejected = reading?.ErrorMessage is { Code: "error.auth" }
                    || reading?.ErrorMessage is { Code: "error.http", Arguments.Length: > 1 } http && http.Arguments[1] is 401 or 403;
                if (!rejected)
                {
                    _status = $"{ProviderCatalog.DisplayName(provider)} connected using the existing CLI session.";
                    _statusMessage = new("signin.complete", ProviderCatalog.DisplayName(provider));
                    return;
                }
            }
            switch (provider)
            {
                case "codex":
                    _providerSignIn = new("codex", true, null, "Complete sign-in in your browser.", MessageInfo: new("signin.browser"));
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_signInCancellation.Token))
                    {
                        timeout.CancelAfter(TimeSpan.FromMinutes(5));
                        var token = await CodexOAuthLoginService.SignInAsync(_openBrowser, timeout.Token);
                        await _saveGate.WaitAsync(timeout.Token);
                        try
                        {
                            var config = await _store.LoadAsync(timeout.Token);
                            var codex = config.Providers.First(p => p.Id == "codex");
                            codex.Source = "oauth";
                            codex.CliAutoSignInDisabled = false;
                            await new SecureOAuthTokenStore().SaveAsync("codex", token, timeout.Token);
                            await _store.SaveAsync(config, timeout.Token);
                            _config = config;
                            _codexAuthentication = new(true, "Signed in", new("connection.signed_in"));
                            _codexCheckedProfile = CodexLoginService.ActiveProfile(codex, _factory.Create("codex").GetAccounts(codex));
                        }
                        finally { _saveGate.Release(); }
                    }
                    _status = "Codex sign-in complete.";
                    _statusMessage = new("signin.complete", "Codex");
                    _providerSignIn = new("codex", false, null, _status, MessageInfo: _statusMessage);
                    await RefreshAsync("codex");
                    break;
                case "claude":
                    _providerSignIn = new("claude", true, null, "Complete Claude OAuth sign-in in your browser.", MessageInfo: new("signin.browser"));
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_signInCancellation.Token))
                    {
                        timeout.CancelAfter(TimeSpan.FromMinutes(5));
                        var token = await ClaudeOAuthLoginService.SignInAsync(_openBrowser, timeout.Token);
                        await _saveGate.WaitAsync(timeout.Token);
                        try
                        {
                            var webConfig = await _store.LoadAsync(timeout.Token);
                            var claude = webConfig.Providers.First(p => p.Id == "claude");
                            claude.Source = "oauth";
                            claude.CliAutoSignInDisabled = false;
                            claude.CookieSource = "off";
                            claude.ApiKey = null;
                            claude.TokenAccounts = null;
                            claude.CookieHeader = null;
                            await new SecureOAuthTokenStore().SaveAsync("claude", token, timeout.Token);
                            await _cookies.ClearCookieHeaderAsync("claude", timeout.Token);
                            await _store.SaveAsync(webConfig, timeout.Token);
                            _config = webConfig;
                            _factory.ClearClaudeRetry();
                            _providerAuthentication["claude"] = new(true, "Signed in", new("connection.signed_in"));
                        }
                        finally { _saveGate.Release(); }
                    }
                    _status = "Claude sign-in complete.";
                    _statusMessage = new("signin.complete", "Claude");
                    await RefreshAsync("claude");
                    break;
                case "copilot":
                    var copilotConfig = _config.Providers.First(p => p.Id == "copilot");
                    if (copilotConfig.Source == "api")
                    {
                        await VerifyProviderSignInAsync(copilotConfig, _signInCancellation.Token);
                        _status = "GitHub Copilot sign-in complete.";
                        _statusMessage = new("signin.complete", "GitHub Copilot");
                        await RefreshAsync("copilot");
                        break;
                    }
                    var login = await OAuthLoginService.SignInToCopilotAsync(null, _signInCancellation.Token, code =>
                    {
                        _providerSignIn = new("copilot", true, code, "Enter this code on GitHub to finish signing in.", MessageInfo: new("signin.enter_code"));
                    }, info =>
                    {
                        if (_providerSignIn is { Pending: true } current) _providerSignIn = current with { MessageInfo = info };
                    });
                    await new SecureOAuthTokenStore().SaveAsync("copilot", new OAuthToken(login.AccessToken, login.AccountLabel));
                    await VerifyProviderSignInAsync(copilotConfig, _signInCancellation.Token);
                    _status = "GitHub Copilot sign-in complete.";
                    _statusMessage = new("signin.complete", "GitHub Copilot");
                    await RefreshAsync("copilot");
                    break;
                case "opencode":
                case "zai":
                    await VerifyProviderSignInAsync(_config.Providers.First(p => p.Id == provider), _signInCancellation.Token);
                    _status = $"{ProviderCatalog.DisplayName(provider)} sign-in complete.";
                    _statusMessage = new("signin.complete", ProviderCatalog.DisplayName(provider));
                    await RefreshAsync(provider);
                    break;
                default: throw new ArgumentException("This provider does not support sign-in.").WithUserMessage("signin.unsupported");
            }
        }
        catch (OperationCanceledException) { failed = !_signInCancellation.IsCancellationRequested; _status = failed ? "Sign-in timed out. Start sign-in again." : "Sign-in cancelled."; _statusMessage = new(failed ? "signin.timeout" : "signin.cancelled"); }
        catch (Exception exception) { failed = true; _status = $"Sign-in failed: {exception.Message}"; _statusMessage = new("signin.failed", UserMessage.FromException(exception)); }
        finally
        {
            _signingIn = false;
            _providerSignIn = new(provider ?? "", false, null, _status ?? "Sign-in finished.", failed, _statusMessage ?? new("signin.finished"));
            _signInCancellation.Dispose();
            _signInCancellation = null;
            CheckOtherProviderAccounts();
        }
    }

    private async Task ProbeEnabledCliSessionsAsync(IEnumerable<string>? ids = null)
    {
        try
        {
            var enabled = _config.Providers.Where(p => p.Enabled == true && (ids is null || ids.Contains(p.Id))).Select(p => p.Id).ToArray();
            await Task.WhenAll(enabled.Select(id => TryUseCliSessionAsync(id, false, _lifetime.Token)));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _status = "Could not save CLI sign-in preferences.";
            _statusMessage = new("connection.unknown");
        }
    }

    private async Task<bool> TryUseCliSessionAsync(string id, bool manual, CancellationToken ct)
    {
        var provider = _config.Providers.First(p => p.Id == id);
        if (provider.CliAutoSignInDisabled || (!manual && (_signingIn || provider.Enabled != true))) return false;
        var version = _providerCheckVersions.GetValueOrDefault(id);
        var key = (id, version);
        if (!_cliProbes.TryGetValue(key, out var probe))
            _cliProbes[key] = probe = Task.Run(() => _cliSessions.TryReadAsync(provider, ct), ct);
        ProviderCliSession? session;
        try { session = await probe; }
        finally { if (_cliProbes.TryGetValue(key, out var current) && ReferenceEquals(current, probe)) _cliProbes.Remove(key); }
        if (session is null || ct.IsCancellationRequested || version != _providerCheckVersions.GetValueOrDefault(id)) return false;
        await _saveGate.WaitAsync(ct);
        try
        {
            var config = await _store.LoadAsync(ct);
            var saved = config.Providers.First(p => p.Id == id);
            if (saved.CliAutoSignInDisabled || (!manual && (_signingIn || saved.Enabled != true))
                || version != _providerCheckVersions.GetValueOrDefault(id)) return false;
            var changed = saved.Source != session.Source;
            saved.Source = session.Source;
            if (session.Token is not null)
            {
                saved.ApiKey = null;
                saved.TokenAccounts = null;
                changed = true;
                var tokens = new SecureOAuthTokenStore();
                if (await tokens.ReadAsync(id, ct) != session.Token) await tokens.SaveAsync(id, session.Token, ct);
            }
            if (changed) await _store.SaveAsync(config, ct);
            _config = config;
            var signedIn = new CodexAccountStatus(true, "Signed in", new("connection.signed_in"));
            if (id == "codex")
            {
                _codexAuthentication = signedIn;
                _codexCheckedProfile = CodexLoginService.ActiveProfile(saved, _factory.Create(id).GetAccounts(saved));
            }
            else _providerAuthentication[id] = signedIn;
            return true;
        }
        finally { _saveGate.Release(); }
    }

    private ProviderAuthenticationService AuthenticationService() => new(_cookies, new SecureOAuthTokenStore(), _factory);

    private async Task VerifyProviderSignInAsync(ProviderConfig provider, CancellationToken ct)
    {
        var status = await AuthenticationService().CheckAsync(provider, ct);
        _providerAuthentication[provider.Id] = status;
        if (status.SignedIn != true) throw new InvalidOperationException(status.SignedIn == false
            ? "Sign-in was not accepted. Check your credentials and try again." : status.Message)
            .WithUserMessage(status.SignedIn == false ? "signin.rejected" : status.MessageInfo?.Code ?? "connection.verify");
    }

    private async Task BeginProviderSignInAsync(ProviderSettings change)
    {
        if (_signingIn) throw new InvalidOperationException("A sign-in or sign-out is already in progress.").WithUserMessage("signin.already_pending");
        if (!ProviderCatalog.IsSupported(change.Id)) throw new ArgumentException("Unknown provider.").WithUserMessage("settings.provider_unknown");
        // CLI reuse is attempted first; these sources are the browser fallback.
        if (change.Id == "claude") change = change with { Source = "oauth", CookieSource = "off", ApiKey = null, NewCookie = null };
        ProviderAuthenticationService.ValidateSource(change.Id, change.Source);
        _signingIn = true;
        _providerSignIn = new(change.Id, true, null, "Starting sign-in…", MessageInfo: new("signin.start"));
        try
        {
            await _saveGate.WaitAsync();
            try
            {
                var config = await _store.LoadAsync();
                var provider = config.Providers.First(p => p.Id == change.Id);
                provider.CliAutoSignInDisabled = false;
                provider.Source = change.Source;
                provider.CookieSource = change.CookieSource;
                provider.CodexProfileHomePaths = change.CodexProfileHomePaths;
                provider.EnterpriseHost = change.EnterpriseHost;
                provider.WorkspaceId = change.WorkspaceId;
                if (change.Id == "claude" || (change.Id == "copilot" && change.Source == "oauth"))
                {
                    provider.ApiKey = null;
                    provider.TokenAccounts = null;
                }
                if (!string.IsNullOrWhiteSpace(change.ApiKey)) provider.ApiKey = change.ApiKey.Trim();
                var errors = ConfigValidation.Validate(config);
                if (errors.Count != 0)
                {
                    throw new ArgumentException(string.Join("\n", errors)).WithUserMessage("message.list", ConfigValidation.ValidateMessages(config).Cast<object?>().ToArray());
                }
                if (change.Id == "zai" && string.IsNullOrWhiteSpace(provider.ApiKey)) throw new ArgumentException("Enter your Z.ai API key.").WithUserMessage("signin.enter_key", "Z.ai");
                if (change.Id == "copilot" && change.Source == "api" && string.IsNullOrWhiteSpace(provider.ApiKey)) throw new ArgumentException("Enter your GitHub Copilot API token.").WithUserMessage("signin.enter_key", "GitHub Copilot");
                if (change.Id != "claude" && (change.Id == "opencode" || change.Source == "web") && string.IsNullOrWhiteSpace(change.NewCookie)
                    && string.IsNullOrWhiteSpace(provider.CookieHeader)
                    && string.IsNullOrWhiteSpace(await _cookies.ReadCookieHeaderAsync(change.Id))) throw new ArgumentException("Enter your provider cookie header.").WithUserMessage("signin.enter_cookie_header");
                if (!string.IsNullOrWhiteSpace(change.NewCookie))
                {
                    await _cookies.SaveCookieHeaderAsync(change.Id, change.NewCookie);
                    provider.CookieHeader = null;
                }
                await _store.SaveAsync(config);
                _config = config;
                InvalidateAuthentication(change.Id);
            }
            finally { _saveGate.Release(); }
            _ = SignInAsync(change.Id);
        }
        catch (Exception exception)
        {
            _signingIn = false;
            _providerSignIn = new(change.Id, false, null, exception.Message, true, UserMessage.FromException(exception));
            throw;
        }
    }

    private async Task SignOutAsync(string providerId)
    {
        _signingIn = true;
        _providerSignIn = new(providerId, true, null, "Signing out…", MessageInfo: new("signout.pending"), Operation: "signout");
        InvalidateAuthentication(providerId);
        var failed = false;
        try
        {
            await _saveGate.WaitAsync(_lifetime.Token);
            try
            {
                var config = await _store.LoadAsync(_lifetime.Token);
                var provider = config.Providers.First(p => p.Id == providerId);
                if (providerId == "codex" && provider.Source == "cli")
                    await CodexLoginService.SignOutAsync(CodexLoginService.ActiveProfile(provider, _factory.Create(providerId).GetAccounts(provider)), _lifetime.Token);
                await _cookies.ClearCookieHeaderAsync(providerId, _lifetime.Token);
                await new SecureOAuthTokenStore().ClearAsync(providerId, _lifetime.Token);
                if (providerId == "claude") _factory.ClearClaudeRetry();
                ProviderAuthenticationService.ClearConfiguredCredentials(provider);
                provider.CliAutoSignInDisabled = true;
                if (providerId is "codex" or "claude") provider.Source = "oauth";
                await _store.SaveAsync(config, _lifetime.Token);
                _config = config;
            }
            finally { _saveGate.Release(); }
            if (providerId == "codex") _codexAuthentication = new(false, "Not signed in", new("connection.signed_out"));
            else _providerAuthentication[providerId] = new(false, "Not signed in", new("connection.signed_out"));
            _status = $"Signed out of {ProviderCatalog.DisplayName(providerId)}.";
            _statusMessage = new("signout.complete", ProviderCatalog.DisplayName(providerId));
            await RefreshAsync(providerId);
        }
        catch (OperationCanceledException) { _status = "Sign-out cancelled."; _statusMessage = new("signout.cancelled"); }
        catch (Exception exception) { failed = true; _status = $"Sign-out failed: {exception.Message}"; _statusMessage = new("signout.failed", UserMessage.FromException(exception)); }
        finally
        {
            _signingIn = false;
            _providerSignIn = new(providerId, false, null, _status ?? "Sign-out finished.", failed, _statusMessage ?? new("signout.finished"), "signout");
            if (providerId == "codex") _codexCheckedProfile = null;
        }
    }

    private void InvalidateAuthentication(string? provider = null)
    {
        if (provider is null or "codex")
        {
            _codexCheckCancellation?.Cancel();
            _codexCheckedProfile = null;
        }
        if (provider is not null)
        {
            _providerCheckVersions[provider] = _providerCheckVersions.GetValueOrDefault(provider) + 1;
            _providerAuthentication.Remove(provider);
            _checkingProviders.Remove(provider);
            return;
        }
        _providerCheckCancellation?.Cancel();
        _providerCheckCancellation?.Dispose();
        _providerCheckCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        foreach (var p in _config.Providers) _providerCheckVersions[p.Id] = _providerCheckVersions.GetValueOrDefault(p.Id) + 1;
        _providerAuthentication.Clear();
        _checkingProviders.Clear();
    }

    private void CheckOtherProviderAccounts()
    {
        if (_settings is not { IsDisposed: false } || _signingIn || _lifetime.IsCancellationRequested) return;
        if (_providerCheckCancellation is null || _providerCheckCancellation.IsCancellationRequested)
            _providerCheckCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var ct = _providerCheckCancellation.Token;
        foreach (var provider in _config.Providers.Where(p => p.Id != "codex" && ProviderCatalog.IsSupported(p.Id)))
        {
            if (_providerAuthentication.ContainsKey(provider.Id) || !_checkingProviders.Add(provider.Id)) continue;
            _providerAuthentication[provider.Id] = new(null, "Checking sign-in…", new("connection.checking"), true);
            _ = ReadAsync(provider, _providerCheckVersions.GetValueOrDefault(provider.Id));
        }
        async Task ReadAsync(ProviderConfig provider, int version)
        {
            bool Current() => !ct.IsCancellationRequested && _providerCheckVersions.GetValueOrDefault(provider.Id) == version;
            try
            {
                // Credential decryption, CLI discovery and process startup can block
                // before the first await. Only apply the result back on the UI thread.
                if (provider.Enabled == true && await TryUseCliSessionAsync(provider.Id, false, ct)) return;
                var status = await Task.Run(() => _checkAuthentication(provider, ct), ct);
                if (Current()) _providerAuthentication[provider.Id] = status;
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (Current()) _providerAuthentication[provider.Id] = new(null, "Could not check sign-in.", new("connection.unknown")); }
            finally { if (Current()) _checkingProviders.Remove(provider.Id); }
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
            _codexAuthentication = new(null, "Checking Codex sign-in…", new("connection.checking"), true);
            _codexCheckCancellation?.Dispose();
            _codexCheckCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _checkingCodex = true;
            _ = ReadAsync(configured, _codexCheckCancellation.Token);
        }
        catch (Exception) { _codexAuthentication = new(null, "Could not check the saved Codex profile. Check its path and try again.", new("connection.verify")); }

        async Task ReadAsync(ProviderConfig provider, CancellationToken ct)
        {
            try
            {
                if (provider.Enabled == true && await TryUseCliSessionAsync(provider.Id, false, ct)) return;
                var status = await Task.Run(() => _checkAuthentication(provider, ct), ct);
                if (!ct.IsCancellationRequested) _codexAuthentication = status;
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (!ct.IsCancellationRequested) _codexAuthentication = new(null,
                    exception is InvalidOperationException or TimeoutException ? exception.Message : "Could not check Codex sign-in. Try Check sign-in again.", UserMessage.FromException(exception));
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
                _tray.ShowBalloonTip(8000, DesktopLocalization.Text("native.pacing_title", snapshot.DisplayName, DesktopLocalization.Format(w.TitleMessage)),
                    DesktopLocalization.Text("native.pacing_body", result.ActualPercentNow.ToString("P0", DesktopLocalization.Culture), result.ExpectedPercentNow.ToString("P0", DesktopLocalization.Culture)), ToolTipIcon.Warning);
            }
        }
    }

    private void ApplyLanguage()
    {
        DesktopLocalization.SetLanguage(_config.Language);
        var items = _tray.ContextMenuStrip!.Items;
        items[0].Text = DesktopLocalization.Text("usage");
        items[1].Text = DesktopLocalization.Text("tray.refresh");
        items[2].Text = DesktopLocalization.Text("tray.settings");
        items[4].Text = DesktopLocalization.Text("tray.quit");
        _settings?.UpdateLanguage();
        _popover?.UpdateLanguage();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetime.Cancel();
            _factory.ClaudeRetryChanged -= OnClaudeRetryChanged;
            _claudeRetryTimer.Dispose();
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
