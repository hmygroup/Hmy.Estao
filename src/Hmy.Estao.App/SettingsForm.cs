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
    private readonly Panel _providersHost = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22, 8, 22, 18) };
    private readonly TaskbarOverlaySettingsPanel _overlaySettings = new();
    private readonly Label _providerCount = new() { AutoSize = true };
    private readonly ZarpaComboBox _themePicker = new() { LabelText = "Theme", DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly ZarpaButton _saveButton = new() { Text = "Save changes", ButtonStyle = ZarpaButtonStyle.Primary, Width = 126 };
    private readonly List<ProviderSettingsRow> _providerRows = [];
    private EstaoConfig _config = ConfigStore.CreateDefaultConfig();

    public SettingsForm(ConfigStore configStore, ICookieSecretStore? cookieStore = null)
    {
        _configStore = configStore;
        _cookieStore = cookieStore ?? new SecureCookieStore();

        Text = "Estao Settings";
        ContextText = "Providers";
        TitleIconKey = string.Empty;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(900, 700);
        MaximizeBox = true;

        var header = BuildHeader();
        var footer = BuildFooter();
        _providersHost.Controls.Add(_overlaySettings);
        Controls.Add(_providersHost);
        Controls.Add(footer);
        Controls.Add(header);

        _saveButton.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        foreach (var preset in ZarpaThemePreferences.Available) _themePicker.Items.Add(preset.ToString());
        _themePicker.SelectedIndexChanged += (_, _) =>
            _theme.Preset = ZarpaThemePreferences.Parse(_themePicker.Text);
        Load += async (_, _) => await LoadAsync().ConfigureAwait(true);
        _theme.Theme.MotionEnabled = false;
        _theme.Attach(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _theme.Dispose();
        base.Dispose(disposing);
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 104, Padding = new Padding(24, 16, 24, 10) };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 510,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 0, 0),
            WrapContents = false
        };
        var oauth = new ZarpaButton { Text = "Sign in with OAuth", ButtonStyle = ZarpaButtonStyle.Secondary, Width = 150 };
        oauth.Click += async (_, _) =>
        {
            using var form = new OAuthLoginForm(_configStore);
            form.ShowDialog(this);
            await LoadAsync().ConfigureAwait(true);
        };
        var import = new ZarpaButton { Text = "Import config", ButtonStyle = ZarpaButtonStyle.Subtle, Width = 126 };
        import.Click += async (_, _) => await ImportAsync().ConfigureAwait(true);
        actions.Controls.Add(oauth);
        actions.Controls.Add(import);
        actions.Controls.Add(_themePicker);

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
            Width = 250,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var cancel = new ZarpaButton { Text = "Cancel", ButtonStyle = ZarpaButtonStyle.Subtle, Width = 100 };
        cancel.Click += (_, _) => Close();
        actions.Controls.Add(_saveButton);
        actions.Controls.Add(cancel);
        footer.Controls.Add(actions);
        return footer;
    }

    private async Task LoadAsync()
    {
        _saveButton.Loading = true;
        try
        {
            _config = await _configStore.LoadAsync().ConfigureAwait(true);
            _overlaySettings.LoadConfig(_config);
            var selectedTheme = ZarpaThemePreferences.Parse(_config.Theme).ToString();
            _themePicker.SelectedIndex = Math.Max(0, _themePicker.Items.IndexOf(selectedTheme));
            var statuses = await Task.WhenAll(_config.Providers.Select(CookieStatusAsync)).ConfigureAwait(true);
            DisposeProviderRows();

            for (var index = _config.Providers.Count - 1; index >= 0; index--)
            {
                var model = new ProviderRow(_config.Providers[index], statuses[index]);
                var row = new ProviderSettingsRow(model) { Dock = DockStyle.Top };
                _providerRows.Insert(0, row);
                _providersHost.Controls.Add(row);
                _providersHost.Controls.SetChildIndex(row, 0);
            }
            _providersHost.Controls.SetChildIndex(_overlaySettings, 0);

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
            _config.Theme = ZarpaThemePreferences.Parse(_themePicker.Text).ToString();
            await _configStore.SaveAsync(_config).ConfigureAwait(true);
            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            if (!IsDisposed) _saveButton.Loading = false;
        }
    }

    private async Task<string> CookieStatusAsync(ProviderConfig provider)
    {
        var storedCookie = await _cookieStore.ReadCookieHeaderAsync(provider.Id).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(storedCookie)) return "Encrypted credential saved";
        return string.IsNullOrWhiteSpace(provider.CookieHeader) ? "No cookie stored" : "Legacy credential saved";
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
            if (!ReferenceEquals(_providersHost.Controls[index], _overlaySettings))
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
        private readonly ZarpaComboBox _source;
        private readonly ZarpaComboBox _cookieSource;
        private readonly ZarpaTextBox _cookieStatus;
        private readonly ZarpaTextBox _workspace;
        private readonly ZarpaTextBox _newCookie;
        private readonly ZarpaTextBox _apiKey;
        private readonly int _expandedHeight;
        private bool _expanded;

        public ProviderSettingsRow(ProviderRow model)
        {
            _model = model;
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

            var icon = new ProviderBrandIcon(model.ProviderId) { Dock = DockStyle.Fill, Margin = new Padding(2, 7, 6, 7) };
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

            _expand = new ZarpaButton { Dock = DockStyle.Fill, Text = "⌄", ButtonStyle = ZarpaButtonStyle.Subtle, Margin = new Padding(3, 10, 3, 10) };
            _expand.Click += (_, _) => ToggleExpanded();
            _enabled = new ZarpaToggleSwitch { Dock = DockStyle.Fill, Text = string.Empty, Checked = model.Enabled, Margin = new Padding(2, 10, 2, 10) };
            _enabled.CheckedChanged += (_, _) => UpdateSummary();
            header.Controls.Add(icon, 0, 0);
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
            _expand.Text = _expanded ? "⌃" : "⌄";
            Height = _expanded ? _expandedHeight : CollapsedHeight;
            Parent?.PerformLayout();
        }

        private IReadOnlyList<Control> ProviderFields(string provider) => provider switch
        {
            "codex" => [_source, _workspace],
            "claude" => [_source, _cookieSource, _cookieStatus, _newCookie, _apiKey],
            "copilot" => [_source, _workspace, _apiKey],
            "opencode" => [_source, _cookieSource, _cookieStatus, _workspace, _newCookie],
            _ => [_source, _cookieSource, _cookieStatus, _workspace, _newCookie, _apiKey]
        };

        private void UpdateSummary()
        {
            var state = _enabled.Checked ? "Enabled" : "Disabled";
            _summary.Text = $"{state}  ·  {_model.CookieStatus}  ·  Source: {_model.Source}";
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
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var size = Math.Min(28, Math.Min(Width, Height));
            ZarpaProviderIconCatalog.TryDraw(e.Graphics, provider,
                new Rectangle((Width - size) / 2, (Height - size) / 2, size, size), ForeColor);
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
