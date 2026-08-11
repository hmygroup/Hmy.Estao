using Hmy.Estao.App.Controls.Zarpa;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Security;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App;

public sealed class SettingsForm : ZarpaModernForm
{
    private readonly ConfigStore _configStore;
    private readonly ICookieSecretStore _cookieStore;
    private readonly ZarpaThemeManager _theme = new() { Preset = ZarpaThemePreset.Graphite };
    private readonly Panel _providersHost = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoScroll = false, Padding = new Padding(22, 8, 22, 18) };
    private readonly ZarpaSettingsScrollHost _providersViewport = new() { Dock = DockStyle.Fill };
    private readonly TaskbarOverlaySettingsPanel _overlaySettings = new();
    private readonly RefreshSettingsPanel _refreshSettings = new();
    private readonly Panel _appearanceSettings = new() { Dock = DockStyle.Top, Height = 178, Padding = new Padding(6, 8, 6, 10) };
    private readonly Label _providerCount = new() { AutoSize = true };
    private readonly ZarpaComboBox _themePicker = new() { LabelText = "Theme", DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly ZarpaComboBox _backdropPicker = new() { LabelText = "Backdrop", DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly ZarpaButton _previewButton = new() { Text = "Preview", ButtonStyle = ZarpaButtonStyle.Secondary, Width = 92 };
    private readonly ZarpaButton _saveButton = new() { Text = "Save changes", ButtonStyle = ZarpaButtonStyle.Primary, Width = 126 };
    private readonly List<ProviderSettingsRow> _providerRows = [];
    private EstaoConfig _config = ConfigStore.CreateDefaultConfig();
    private readonly IOAuthTokenStore _oauthTokenStore = new SecureOAuthTokenStore();

    private readonly Action<EstaoConfig>? _previewOverlay;
    private readonly Action<EstaoConfig, Action<Point>>? _beginMoveOverlay;
    private readonly Action? _endMoveOverlay;
    private bool _movingOverlay;

    public SettingsForm(ConfigStore configStore, ICookieSecretStore? cookieStore = null,
        Action<EstaoConfig>? previewOverlay = null,
        Action<EstaoConfig, Action<Point>>? beginMoveOverlay = null,
        Action? endMoveOverlay = null)
    {
        _configStore = configStore;
        _cookieStore = cookieStore ?? new SecureCookieStore();
        _previewOverlay = previewOverlay;
        _beginMoveOverlay = beginMoveOverlay;
        _endMoveOverlay = endMoveOverlay;

        Text = "Estao Settings";
        ContextText = "Providers";
        TitleIconKey = string.Empty;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(900, 700);
        MaximizeBox = true;

        var header = BuildHeader();
        var footer = BuildFooter();
        BuildAppearanceSection();
        _providersHost.Controls.Add(_overlaySettings);
        _providersHost.Controls.Add(_refreshSettings);
        _providersHost.Controls.Add(_appearanceSettings);
        _providersViewport.Content = _providersHost;
        Controls.Add(_providersViewport);
        Controls.Add(footer);
        Controls.Add(header);

        _previewButton.Click += (_, _) => PreviewOverlay();
        _saveButton.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        _overlaySettings.SetMovementAvailable(_beginMoveOverlay is not null);
        _overlaySettings.MoveModeChanged += SetOverlayMoveMode;
        _overlaySettings.ResetPositionRequested += ResetOverlayPosition;
        foreach (var preset in ZarpaThemePreferences.Available) _themePicker.Items.Add(ZarpaThemePreferences.DisplayName(preset));
        foreach (var backdrop in ZarpaThemePreferences.AvailableBackdrops) _backdropPicker.Items.Add(backdrop.ToString());
        _themePicker.SelectedIndexChanged += (_, _) =>
            _theme.Preset = ZarpaThemePreferences.Parse(_themePicker.Text);
        _backdropPicker.SelectedIndexChanged += (_, _) =>
            _theme.BackdropStyle = ZarpaThemePreferences.ParseBackdrop(_backdropPicker.Text);
        Load += async (_, _) => await LoadAsync().ConfigureAwait(true);
        _theme.Theme.MotionEnabled = false;
        _theme.Attach(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopMovingOverlay();
            _theme.Dispose();
        }
        base.Dispose(disposing);
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 104, Padding = new Padding(24, 16, 24, 10) };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 180,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 0, 0),
            WrapContents = false
        };
        var import = new ZarpaButton { Text = "Import config", ButtonStyle = ZarpaButtonStyle.Subtle, Width = 126 };
        import.Click += async (_, _) => await ImportAsync().ConfigureAwait(true);
        actions.Controls.Add(import);

        var title = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            Height = 35,
            Text = "Providers",
            TextAlign = ContentAlignment.MiddleLeft
        };
        _providerCount.Dock = DockStyle.Top;
        _providerCount.Height = 24;
        _providerCount.AutoSize = false;
        _providerCount.Text = "Loading provider configuration…";
        var copy = new Panel { Dock = DockStyle.Fill };
        copy.Controls.Add(_providerCount);
        copy.Controls.Add(title);
        header.Controls.Add(copy);
        header.Controls.Add(actions);
        return header;
    }

    private Control BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(22, 14, 22, 14) };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 360,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var cancel = new ZarpaButton { Text = "Cancel", ButtonStyle = ZarpaButtonStyle.Subtle, Width = 100 };
        cancel.Click += (_, _) => Close();
        actions.Controls.Add(_previewButton);
        actions.Controls.Add(_saveButton);
        actions.Controls.Add(cancel);
        footer.Controls.Add(actions);
        return footer;
    }

    private void BuildAppearanceSection()
    {
        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Text = "Appearance",
            TextAlign = ContentAlignment.MiddleLeft
        };
        _themePicker.Dock = DockStyle.Top;
        _themePicker.Width = 220;
        _themePicker.Height = 76;
        _backdropPicker.Dock = DockStyle.Top;
        _backdropPicker.Width = 220;
        _backdropPicker.Height = 76;
        _appearanceSettings.Controls.Add(new ZarpaSettingsSectionSeparator());
        _appearanceSettings.Controls.Add(_backdropPicker);
        _appearanceSettings.Controls.Add(_themePicker);
        _appearanceSettings.Controls.Add(title);
    }

    private async Task LoadAsync()
    {
        _saveButton.Loading = true;
        try
        {
            _config = await _configStore.LoadAsync().ConfigureAwait(true);
            _overlaySettings.LoadConfig(_config);
            _refreshSettings.LoadConfig(_config);
            var selectedTheme = ZarpaThemePreferences.DisplayName(ZarpaThemePreferences.Parse(_config.Theme));
            _themePicker.SelectedIndex = Math.Max(0, _themePicker.Items.IndexOf(selectedTheme));
            var selectedBackdrop = ZarpaThemePreferences.ParseBackdrop(_config.BackdropStyle).ToString();
            _backdropPicker.SelectedIndex = Math.Max(0, _backdropPicker.Items.IndexOf(selectedBackdrop));
            var statuses = await Task.WhenAll(_config.Providers.Select(CookieStatusAsync)).ConfigureAwait(true);
            DisposeProviderRows();

            for (var index = _config.Providers.Count - 1; index >= 0; index--)
            {
                var model = new ProviderRow(_config.Providers[index], statuses[index]);
                var row = new ProviderSettingsRow(model, SignInProviderAsync) { Dock = DockStyle.Top };
                _providerRows.Insert(0, row);
                _providersHost.Controls.Add(row);
                _providersHost.Controls.SetChildIndex(row, 0);
            }
            _providersHost.Controls.SetChildIndex(_appearanceSettings, 0);
            _providersHost.Controls.SetChildIndex(_refreshSettings, 1);
            _providersHost.Controls.SetChildIndex(_overlaySettings, 2);

            var enabled = _providerRows.Count(row => row.EnabledProvider);
            _providerCount.Text = $"{enabled} of {_providerRows.Count} enabled  ·  Credentials stay encrypted on this device";
        }
        finally
        {
            _saveButton.Loading = false;
        }
    }

    private async Task SaveAsync()
    {
        _saveButton.Loading = true;
        try
        {
            foreach (var row in _providerRows)
            {
                row.Apply();
                if (!string.IsNullOrWhiteSpace(row.NewCookieHeader))
                {
                    await _cookieStore.SaveCookieHeaderAsync(row.ProviderId, row.NewCookieHeader).ConfigureAwait(true);
                    row.ClearLegacyCookie();
                }
            }

            _overlaySettings.Apply(_config.TaskbarOverlay);
            _refreshSettings.Apply(_config.Refresh);
            _config.Theme = ZarpaThemePreferences.Parse(_themePicker.Text).ToString();
            _config.BackdropStyle = ZarpaThemePreferences.ParseBackdrop(_backdropPicker.Text).ToString();
            await _configStore.SaveAsync(_config).ConfigureAwait(true);
            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            if (!IsDisposed) _saveButton.Loading = false;
        }
    }

    private void PreviewOverlay()
    {
        foreach (var row in _providerRows) row.Apply();
        _overlaySettings.Apply(_config.TaskbarOverlay);
        _config.Theme = ZarpaThemePreferences.Parse(_themePicker.Text).ToString();
        _config.BackdropStyle = ZarpaThemePreferences.ParseBackdrop(_backdropPicker.Text).ToString();
        _previewOverlay?.Invoke(_config);
    }

    private void SetOverlayMoveMode(bool enabled)
    {
        if (!enabled)
        {
            StopMovingOverlay();
            return;
        }

        ApplyOverlayEditorValues();
        _movingOverlay = true;
        _beginMoveOverlay?.Invoke(_config, OnOverlayPositionChanged);
    }

    private void ResetOverlayPosition()
    {
        _config.TaskbarOverlay.PositionX = null;
        _config.TaskbarOverlay.PositionY = null;
        _overlaySettings.ShowPosition(null, null);

        var keepMoving = _movingOverlay;
        ApplyOverlayEditorValues();
        _beginMoveOverlay?.Invoke(_config, OnOverlayPositionChanged);
        if (!keepMoving) _endMoveOverlay?.Invoke();
    }

    private void OnOverlayPositionChanged(Point position)
    {
        if (IsDisposed) return;
        _config.TaskbarOverlay.PositionX = position.X;
        _config.TaskbarOverlay.PositionY = position.Y;
        _overlaySettings.ShowPosition(position.X, position.Y);
    }

    private void ApplyOverlayEditorValues()
    {
        foreach (var row in _providerRows) row.Apply();
        _overlaySettings.Apply(_config.TaskbarOverlay);
        _config.Theme = ZarpaThemePreferences.Parse(_themePicker.Text).ToString();
        _config.BackdropStyle = ZarpaThemePreferences.ParseBackdrop(_backdropPicker.Text).ToString();
    }

    private void StopMovingOverlay()
    {
        if (!_movingOverlay) return;
        _movingOverlay = false;
        _overlaySettings.SetMoving(false);
        _endMoveOverlay?.Invoke();
    }

    private async Task<string> CookieStatusAsync(ProviderConfig provider)
    {
        var storedCookie = await _cookieStore.ReadCookieHeaderAsync(provider.Id).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(storedCookie)) return "Encrypted credential saved";
        return string.IsNullOrWhiteSpace(provider.CookieHeader) ? "No cookie stored" : "Legacy credential saved";
    }

    private async Task SignInProviderAsync(string providerId, IProgress<string> progress)
    {
        switch (ProviderCatalog.NormalizeId(providerId))
        {
            case "codex":
                progress.Report("Opening Codex sign-in…");
                OAuthLoginService.StartCodexLogin();
                await EnableProviderAsync(providerId).ConfigureAwait(true);
                progress.Report("Finish Codex sign-in in the terminal, then refresh usage.");
                break;
            case "claude":
                progress.Report("Opening Claude Code sign-in…");
                OAuthLoginService.StartClaudeLogin();
                await EnableProviderAsync(providerId).ConfigureAwait(true);
                progress.Report("Finish Claude sign-in in the terminal, then refresh usage.");
                break;
            case "copilot":
                var login = await OAuthLoginService.SignInToCopilotAsync(progress).ConfigureAwait(true);
                await _oauthTokenStore.SaveAsync("copilot", new OAuthToken(login.AccessToken, login.AccountLabel)).ConfigureAwait(true);
                await EnableProviderAsync(providerId, "oauth").ConfigureAwait(true);
                progress.Report("Copilot connected. Refresh usage now.");
                break;
        }

        if (!IsDisposed)
            BeginInvoke(new MethodInvoker(async () => await LoadAsync().ConfigureAwait(true)));
    }

    private async Task EnableProviderAsync(string providerId, string source = "auto")
    {
        var config = await _configStore.LoadAsync().ConfigureAwait(true);
        var provider = config.Providers.First(item => ProviderCatalog.NormalizeId(item.Id) == ProviderCatalog.NormalizeId(providerId));
        provider.Enabled = true;
        provider.Source = source;
        await _configStore.SaveAsync(config).ConfigureAwait(true);
    }

    private async Task ImportAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "JSON config (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await _configStore.ImportExplicitAsync(dialog.FileName).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    private void DisposeProviderRows()
    {
        foreach (var row in _providerRows) row.Dispose();
        _providerRows.Clear();
        for (var index = _providersHost.Controls.Count - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(_providersHost.Controls[index], _overlaySettings) &&
                !ReferenceEquals(_providersHost.Controls[index], _refreshSettings) &&
                !ReferenceEquals(_providersHost.Controls[index], _appearanceSettings))
                _providersHost.Controls[index].Dispose();
        }
    }

    private sealed class ProviderSettingsRow : Panel
    {
        private const int CollapsedHeight = 74;
        private readonly ProviderRow _model;
        private readonly Label _summary;
        private readonly ZarpaToggleSwitch _enabled;
        private readonly ZarpaButton _expand;
        private readonly Panel _details;
        private readonly ProviderBrandIcon _providerIcon;
        private readonly ZarpaComboBox _source;
        private readonly ZarpaComboBox _cookieSource;
        private readonly ZarpaTextBox _cookieStatus;
        private readonly ZarpaTextBox _workspace;
        private readonly ZarpaTextBox _newCookie;
        private readonly ZarpaTextBox _apiKey;
        private readonly ZarpaToggleSwitch _pacingEnabled;
        private readonly ZarpaNumericUpDown _pacingTarget;
        private readonly ZarpaToggleSwitch _pacingNotify;
        private readonly Func<string, IProgress<string>, Task> _signInAction;
        private readonly ProviderSignInButton? _signInButton;
        private readonly int _expandedHeight;
        private bool _expanded;

        public ProviderSettingsRow(ProviderRow model, Func<string, IProgress<string>, Task> signInAction)
        {
            _model = model;
            _signInAction = signInAction;
            Height = CollapsedHeight;
            MinimumSize = new Size(480, CollapsedHeight);
            Padding = new Padding(0, 0, 0, 8);

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 66,
                ColumnCount = 4,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = new Padding(6, 4, 6, 4)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _providerIcon = new ProviderBrandIcon(model.ProviderId)
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(2, 7, 6, 7),
                Status = model.Status
            };
            var copy = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var title = new Label
            {
                Dock = DockStyle.Top,
                Height = 27,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Text = model.DisplayName,
                TextAlign = ContentAlignment.BottomLeft
            };
            _summary = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                AutoEllipsis = true
            };
            copy.Controls.Add(_summary);
            copy.Controls.Add(title);

            _expand = new ZarpaButton
            {
                Dock = DockStyle.Fill,
                IconKey = "ic_fluent_chevron_down_24_regular",
                ButtonStyle = ZarpaButtonStyle.Subtle,
                Margin = new Padding(1, 12, 1, 12),
                AccessibleName = "Expand provider settings"
            };
            _expand.Click += (_, _) => ToggleExpanded();
            _enabled = new ZarpaToggleSwitch { Dock = DockStyle.Fill, Text = string.Empty, Checked = model.Enabled, Margin = new Padding(2, 10, 2, 10) };
            _enabled.CheckedChanged += (_, _) =>
            {
                _model.Enabled = _enabled.Checked;
                UpdateSummary();
            };
            header.Controls.Add(_providerIcon, 0, 0);
            header.Controls.Add(copy, 1, 0);
            header.Controls.Add(_expand, 2, 0);
            header.Controls.Add(_enabled, 3, 0);

            _details = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(48, 4, 8, 4) };
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            _source = ComboField("Usage source", ["auto", "web", "cli", "oauth", "api"], model.Source);
            _cookieSource = ComboField("Cookie source", ["auto", "manual", "off"], model.CookieSource);
            _cookieStatus = TextField("Cookie status", model.CookieStatus, "Stored credentials are encrypted with Windows DPAPI.");
            _cookieStatus.ReadOnly = true;
            var workspaceLabel = model.ProviderId switch
            {
                "codex" => "Codex profile homes",
                "copilot" => "Enterprise host",
                _ => "Workspace"
            };
            var workspaceHelper = model.ProviderId == "codex"
                ? "Separate multiple profile directories with semicolons."
                : "Optional provider-specific workspace or host.";
            _workspace = TextField(workspaceLabel, model.WorkspaceOrHost, workspaceHelper);
            _newCookie = TextField("New cookie header", string.Empty, "Leave blank to keep the current credential.");
            _newCookie.PasswordChar = '●';
            _apiKey = TextField("API key / token", model.ApiKey, "Leave blank when authentication is automatic.");
            _apiKey.PasswordChar = '●';
            _pacingEnabled = new ZarpaToggleSwitch
            {
                Dock = DockStyle.Fill,
                Text = "Enable pacing for this provider",
                Checked = model.Pacing.Enabled,
                Margin = new Padding(6, 12, 6, 3)
            };
            _pacingTarget = new ZarpaNumericUpDown
            {
                Dock = DockStyle.Fill,
                LabelText = "Daily pacing target",
                Minimum = (decimal)PacingCatalog.MinDailyTargetPercent,
                Maximum = (decimal)PacingCatalog.MaxDailyTargetPercent,
                Increment = 1M,
                DecimalPlaces = 0,
                Suffix = "%/day",
                Value = (decimal)Math.Clamp(model.Pacing.DailyTargetPercent,
                    PacingCatalog.MinDailyTargetPercent, PacingCatalog.MaxDailyTargetPercent),
                Margin = new Padding(6, 3, 6, 3)
            };
            _pacingNotify = new ZarpaToggleSwitch
            {
                Dock = DockStyle.Fill,
                Text = "Notify once a day when over pace",
                Checked = model.Pacing.NotifyOnExceed,
                Margin = new Padding(6, 12, 6, 3)
            };

            if (SupportsOAuth(model.ProviderId))
            {
                _signInButton = new ProviderSignInButton(model.ProviderId)
                {
                    Text = "Sign in",
                    ButtonStyle = ZarpaButtonStyle.Secondary,
                    Width = 126,
                    Height = 34,
                    Margin = new Padding(6, 8, 6, 3)
                };
                _signInButton.Click += async (_, _) => await SignInAsync().ConfigureAwait(true);
            }

            var providerFields = ProviderFields(model.ProviderId);
            var fieldRows = (providerFields.Count + 1) / 2;
            fields.RowCount = fieldRows;
            for (var row = 0; row < fieldRows; row++)
                fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / fieldRows));
            for (var index = 0; index < providerFields.Count; index++)
                fields.Controls.Add(providerFields[index], index % 2, index / 2);
            _expandedHeight = CollapsedHeight + fieldRows * 78 + 12;
            _details.Controls.Add(fields);

            Controls.Add(_details);
            Controls.Add(header);
            UpdateSummary();
        }

        public bool EnabledProvider => _enabled.Checked;
        public string ProviderId => _model.ProviderId;
        public string NewCookieHeader => _newCookie.Value;

        public void Apply()
        {
            _model.Enabled = _enabled.Checked;
            _model.Source = _source.Text;
            _model.CookieSource = _cookieSource.Text;
            _model.WorkspaceOrHost = _workspace.Value;
            _model.NewCookieHeader = _newCookie.Value;
            _model.ApiKey = _apiKey.Value;
            _model.Pacing.Enabled = _pacingEnabled.Checked;
            _model.Pacing.DailyTargetPercent = (double)_pacingTarget.Value;
            _model.Pacing.NotifyOnExceed = _pacingNotify.Checked;
            _model.Apply();
        }

        public void ClearLegacyCookie() => _model.ClearLegacyCookie();

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var separator = new Pen(Color.FromArgb(67, 70, 78));
            e.Graphics.DrawLine(separator, 6, Height - 1, Width - 6, Height - 1);
        }

        private void ToggleExpanded()
        {
            _expanded = !_expanded;
            _details.Visible = _expanded;
            _expand.IconKey = _expanded
                ? "ic_fluent_chevron_up_24_regular"
                : "ic_fluent_chevron_down_24_regular";
            _expand.AccessibleName = _expanded ? "Collapse provider settings" : "Expand provider settings";
            Height = _expanded ? _expandedHeight : CollapsedHeight;
            Parent?.PerformLayout();
        }

        private IReadOnlyList<Control> ProviderFields(string provider) => WithPacing(provider switch
        {
            "codex" => WithSignIn([_source, _workspace]),
            "claude" => WithSignIn([_source, _cookieSource, _cookieStatus, _newCookie, _apiKey]),
            "copilot" => WithSignIn([_source, _workspace, _apiKey]),
            "opencode" => [_source, _cookieSource, _cookieStatus, _workspace, _newCookie],
            _ => [_source, _cookieSource, _cookieStatus, _workspace, _newCookie, _apiKey]
        });

        private IReadOnlyList<Control> WithPacing(IReadOnlyList<Control> fields) =>
            [.. fields, _pacingEnabled, _pacingTarget, _pacingNotify];

        private IReadOnlyList<Control> WithSignIn(IReadOnlyList<Control> fields) => _signInButton is null
            ? fields
            : [.. fields, _signInButton];

        private async Task SignInAsync()
        {
            if (_signInButton is null) return;
            _signInButton.Loading = true;
            try
            {
                var progress = new Progress<string>(message => _summary.Text = message);
                await _signInAction(_model.ProviderId, progress).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                _summary.Text = $"Sign-in failed: {exception.Message}";
            }
            finally
            {
                if (!_signInButton.IsDisposed) _signInButton.Loading = false;
            }
        }

        private static bool SupportsOAuth(string provider) => provider is "codex" or "claude" or "copilot";

        private void UpdateSummary()
        {
            var state = _enabled.Checked ? "Enabled" : "Disabled";
            var pacing = _model.Pacing.Enabled
                ? $"Pacing: {_model.Pacing.DailyTargetPercent:0}%/day"
                : "Pacing off";
            _providerIcon.Status = _model.Status;
            _summary.Text = $"{state}  ·  {pacing}  ·  {_model.CookieStatus}  ·  Source: {_model.Source}";
        }

        private static ZarpaComboBox ComboField(string label, IEnumerable<string> values, string selected)
        {
            var field = new ZarpaComboBox
            {
                Dock = DockStyle.Fill,
                LabelText = label,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(6, 3, 6, 3)
            };
            foreach (var value in values) field.Items.Add(value);
            field.SelectedIndex = Math.Max(0, field.Items.IndexOf(selected));
            return field;
        }

        private static ZarpaTextBox TextField(string label, string value, string helper) => new()
        {
            Dock = DockStyle.Fill,
            LabelText = label,
            Value = value,
            HelperText = helper,
            Margin = new Padding(6, 3, 6, 3)
        };
    }

    private sealed class ProviderBrandIcon(string provider) : Control
    {
        private ProviderStatus status;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public ProviderStatus Status
        {
            get => status;
            set { status = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var size = Math.Min(28, Math.Min(Width, Height));
            var iconBounds = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
            ZarpaProviderIconCatalog.TryDraw(e.Graphics, provider, iconBounds, ForeColor);
            var dotColor = status switch
            {
                ProviderStatus.Ready => Color.FromArgb(35, 193, 118),
                ProviderStatus.NeedsSetup => Color.FromArgb(224, 157, 54),
                _ => Color.FromArgb(120, 127, 140)
            };
            var dot = new Rectangle(iconBounds.Left - 2, iconBounds.Top - 2, 9, 9);
            using var outline = new SolidBrush(BackColor);
            using var dotBrush = new SolidBrush(dotColor);
            e.Graphics.FillEllipse(outline, dot);
            e.Graphics.FillEllipse(dotBrush, new Rectangle(dot.Left + 1, dot.Top + 1, dot.Width - 2, dot.Height - 2));
        }
    }

    private enum ProviderStatus
    {
        Disabled,
        NeedsSetup,
        Ready
    }

    private sealed class ProviderSignInButton(string provider) : ZarpaButton
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var size = Math.Min(18, Math.Min(Width, Height) - 10);
            ZarpaProviderIconCatalog.TryDraw(e.Graphics, provider,
                new Rectangle(9, (Height - size) / 2, size, size), ForeColor);
        }
    }

    private sealed class ProviderRow
    {
        private readonly ProviderConfig _provider;

        public ProviderRow(ProviderConfig provider, string cookieStatus)
        {
            _provider = provider;
            ProviderId = provider.Id;
            DisplayName = ProviderCatalog.DisplayName(provider.Id);
            Enabled = provider.Enabled == true;
            Source = provider.Source ?? "auto";
            CookieSource = provider.CookieSource ?? "auto";
            CookieStatus = cookieStatus;
            ApiKey = provider.ApiKey ?? string.Empty;
            WorkspaceOrHost = provider.Id switch
            {
                "codex" => string.Join("; ", provider.CodexProfileHomePaths ?? []),
                "copilot" => provider.EnterpriseHost ?? string.Empty,
                _ => provider.WorkspaceId ?? string.Empty
            };
        }

        public string ProviderId { get; }
        public string DisplayName { get; }
        public bool Enabled { get; set; }
        public string Source { get; set; }
        public string CookieSource { get; set; }
        public string CookieStatus { get; }
        public string NewCookieHeader { get; set; } = string.Empty;
        public string ApiKey { get; set; }
        public string WorkspaceOrHost { get; set; }
        public PacingConfig Pacing => _provider.Pacing;
        public ProviderStatus Status => !Enabled
            ? ProviderStatus.Disabled
            : IsConfigured ? ProviderStatus.Ready : ProviderStatus.NeedsSetup;

        private bool IsConfigured => ProviderId == "codex" ||
            !string.Equals(CookieStatus, "No cookie stored", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(ApiKey) ||
            ProviderId == "copilot" && !string.IsNullOrWhiteSpace(WorkspaceOrHost);

        public void Apply()
        {
            _provider.Enabled = Enabled;
            _provider.Source = string.IsNullOrWhiteSpace(Source) ? "auto" : Source.Trim();
            _provider.CookieSource = string.IsNullOrWhiteSpace(CookieSource) ? "auto" : CookieSource.Trim();
            _provider.ApiKey = EmptyToNull(ApiKey);
            if (_provider.Id == "codex")
                _provider.CodexProfileHomePaths = WorkspaceOrHost
                    .Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            else if (_provider.Id == "copilot") _provider.EnterpriseHost = EmptyToNull(WorkspaceOrHost);
            else _provider.WorkspaceId = EmptyToNull(WorkspaceOrHost);
        }

        public void ClearLegacyCookie() => _provider.CookieHeader = null;
        private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
