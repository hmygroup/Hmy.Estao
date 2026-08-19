using Hmy.Estao.App.Controls.Zarpa;
using Hmy.Estao.Core.Configuration;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App;

public sealed class HarnessHubForm : ZarpaModernForm
{
    private readonly ConfigStore _configStore;
    private readonly ZarpaThemeManager _theme = new()
    {
        Preset = ZarpaThemePreset.Graphite,
        Density = ZarpaDensity.Comfortable
    };
    private readonly ZarpaNavigationView _navigation = new()
    {
        Dock = DockStyle.Left,
        HeaderText = "HARNESS HUB",
        ExpandedWidth = 214,
        CompactWidth = 56
    };
    private readonly HarnessCatalogView _catalog;
    private readonly HarnessSetupView _setup;
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private EstaoConfig _config = ConfigStore.CreateDefaultConfig();

    public HarnessHubForm(ConfigStore configStore)
    {
        _configStore = configStore;
        _catalog = new HarnessCatalogView(configStore, OpenPublish, AddToSetup, ConfigureRepositories);
        _setup = new HarnessSetupView(configStore);
        Text = "Estao Harness Hub";
        ContextText = "Team catalog and harness configuration";
        TitleIconKey = "ic_fluent_apps_list_detail_24_regular";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1080, 700);
        Size = new Size(1440, 900);
        MaximizeBox = true;

        _navigation.Items.Add(new ZarpaNavigationItem
        {
            Key = "catalog",
            Text = "Catalog",
            IconKey = "ic_fluent_grid_24_regular"
        });
        _navigation.Items.Add(new ZarpaNavigationItem
        {
            Key = "setup",
            Text = "My Setup",
            IconKey = "ic_fluent_settings_24_regular"
        });
        _navigation.SelectedItemChanged += (_, _) => ShowSelectedWorkspace();
        var body = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        body.Controls.Add(_content);
        body.Controls.Add(_navigation);
        Controls.Add(body);
        Load += async (_, _) => await LoadHubAsync().ConfigureAwait(true);
        _theme.Attach(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _theme.Dispose();
        base.Dispose(disposing);
    }

    private async Task LoadHubAsync()
    {
        _config = await _configStore.LoadAsync().ConfigureAwait(true);
        _theme.Preset = ZarpaThemePreferences.Parse(_config.Theme);
        // The Hub is an operational workspace. Keep it opaque so the host
        // Settings window never bleeds through the installation plan.
        _theme.BackdropStyle = ZarpaBackdropStyle.None;
        _theme.BackdropOpacity = 100;
        _catalog.LoadConfig(_config.HarnessManager);
        await _setup.LoadConfigAsync(_config.HarnessManager).ConfigureAwait(true);
        _navigation.SelectedIndex = 0;
    }

    private void ShowSelectedWorkspace()
    {
        _content.Controls.Clear();
        var workspace = _navigation.SelectedItem?.Key == "setup" ? (Control)_setup : _catalog;
        workspace.Dock = DockStyle.Fill;
        _content.Controls.Add(workspace);
        workspace.BringToFront();
        if (ReferenceEquals(workspace, _catalog)) _ = _catalog.RefreshAsync();
    }

    private void AddToSetup(Hmy.Estao.Core.Harnesses.HarnessCatalogEntry entry)
    {
        _navigation.SelectedIndex = 1;
        _setup.Stage(entry);
    }

    private async void OpenPublish()
    {
        using var dialog = new HarnessPublishDialog(_config.HarnessManager);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await _catalog.RefreshAsync().ConfigureAwait(true);
    }

    private async void ConfigureRepositories()
    {
        using var dialog = new HarnessRepositoriesDialog(_config.HarnessManager);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await _configStore.SaveAsync(_config).ConfigureAwait(true);
        _catalog.LoadConfig(_config.HarnessManager);
        await _setup.LoadConfigAsync(_config.HarnessManager).ConfigureAwait(true);
    }
}
