using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.App;

public sealed class SettingsForm : Form
{
    private readonly ConfigStore _configStore;
    private readonly ICookieSecretStore _cookieStore;
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false };
    private EstaoConfig _config = ConfigStore.CreateDefaultConfig();

    public SettingsForm(ConfigStore configStore, ICookieSecretStore? cookieStore = null)
    {
        _configStore = configStore;
        _cookieStore = cookieStore ?? new SecureCookieStore();
        Text = "Estao Settings";
        Width = 920;
        Height = 420;
        StartPosition = FormStartPosition.CenterScreen;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Provider", DataPropertyName = nameof(ProviderRow.DisplayName), ReadOnly = true, Width = 140 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Enabled", DataPropertyName = nameof(ProviderRow.Enabled), Width = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Source", DataPropertyName = nameof(ProviderRow.Source), Width = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Cookie Source", DataPropertyName = nameof(ProviderRow.CookieSource), Width = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Cookie Status", DataPropertyName = nameof(ProviderRow.CookieStatus), ReadOnly = true, Width = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "New Cookie Header", DataPropertyName = nameof(ProviderRow.NewCookieHeader), Width = 220 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "API Key / Token", DataPropertyName = nameof(ProviderRow.ApiKey), Width = 180 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Workspace / Host", DataPropertyName = nameof(ProviderRow.WorkspaceOrHost), Width = 180 });

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Save", Width = 100 };
        save.Click += async (_, _) => await SaveAsync().ConfigureAwait(false);
        var import = new Button { Text = "Import...", Width = 100 };
        import.Click += async (_, _) => await ImportAsync().ConfigureAwait(false);
        var cancel = new Button { Text = "Cancel", Width = 100 };
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(save);
        buttons.Controls.Add(import);
        buttons.Controls.Add(cancel);

        Controls.Add(_grid);
        Controls.Add(buttons);
        Load += async (_, _) => await LoadAsync().ConfigureAwait(false);
    }

    private async Task LoadAsync()
    {
        _config = await _configStore.LoadAsync().ConfigureAwait(true);
        var rows = new List<ProviderRow>();
        foreach (var provider in _config.Providers)
        {
            rows.Add(new ProviderRow(provider, await CookieStatusAsync(provider).ConfigureAwait(true)));
        }

        _grid.DataSource = rows;
    }

    private async Task SaveAsync()
    {
        _grid.EndEdit();
        var rows = _grid.DataSource as IEnumerable<ProviderRow> ?? [];
        foreach (var row in rows)
        {
            row.Apply();
            if (!string.IsNullOrWhiteSpace(row.NewCookieHeader))
            {
                await _cookieStore.SaveCookieHeaderAsync(row.ProviderId, row.NewCookieHeader).ConfigureAwait(true);
                row.ClearLegacyCookie();
            }
        }

        await _configStore.SaveAsync(_config).ConfigureAwait(true);
        Close();
    }

    private async Task<string> CookieStatusAsync(ProviderConfig provider)
    {
        var storedCookie = await _cookieStore.ReadCookieHeaderAsync(provider.Id).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(storedCookie))
        {
            return "Encrypted saved";
        }

        return string.IsNullOrWhiteSpace(provider.CookieHeader) ? "Not set" : "Legacy config saved";
    }

    private async Task ImportAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "JSON config (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await _configStore.ImportExplicitAsync(dialog.FileName).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
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
            NewCookieHeader = string.Empty;
            WorkspaceOrHost = provider.Id == "copilot" ? provider.EnterpriseHost ?? string.Empty : provider.WorkspaceId ?? string.Empty;
        }

        public string ProviderId { get; }

        public string DisplayName { get; }

        public bool Enabled { get; set; }

        public string Source { get; set; }

        public string CookieSource { get; set; }

        public string CookieStatus { get; }

        public string NewCookieHeader { get; set; }

        public string ApiKey { get; set; }

        public string WorkspaceOrHost { get; set; }

        public void Apply()
        {
            _provider.Enabled = Enabled;
            _provider.Source = string.IsNullOrWhiteSpace(Source) ? "auto" : Source.Trim();
            _provider.CookieSource = string.IsNullOrWhiteSpace(CookieSource) ? "auto" : CookieSource.Trim();
            _provider.ApiKey = EmptyToNull(ApiKey);
            if (_provider.Id == "copilot")
            {
                _provider.EnterpriseHost = EmptyToNull(WorkspaceOrHost);
            }
            else
            {
                _provider.WorkspaceId = EmptyToNull(WorkspaceOrHost);
            }
        }

        public void ClearLegacyCookie()
        {
            _provider.CookieHeader = null;
        }

        private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
