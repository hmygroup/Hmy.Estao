using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Harnesses;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal sealed class HarnessCatalogView : UserControl, IZarpaThemeAware
{
    private readonly ConfigStore _configStore;
    private readonly HarnessCatalogRepository _repository = new();
    private readonly ZarpaToastManager _toastManager = new();
    private readonly Action _publish;
    private readonly Action<HarnessCatalogEntry> _addToSetup;
    private readonly Action _configureRepositories;
    private readonly ZarpaTextBox _search = new()
    {
        LabelText = string.Empty,
        Placeholder = "Search artifacts, teams, owners, or tags",
        Width = 330,
        Height = 34,
        MinimumSize = new Size(120, 34)
    };
    private readonly ZarpaComboBox _type = Combo(150);
    private readonly ZarpaComboBox _harness = Combo(150);
    private readonly ZarpaComboBox _team = Combo(150);
    private readonly ZarpaButton _cardsButton = Button("Cards", ZarpaButtonStyle.Secondary, 78);
    private readonly ZarpaButton _tableButton = Button("Table", ZarpaButtonStyle.Secondary, 78);
    private readonly ZarpaButton _refreshButton = Button("Refresh", ZarpaButtonStyle.Secondary, 92);
    private readonly ZarpaButton _publishButton = Button("Publish", ZarpaButtonStyle.Primary, 92);
    private readonly ZarpaButton _repositoriesButton = Button("Repositories", ZarpaButtonStyle.Subtle, 112);
    private readonly ZarpaCardPanel _filterSurface = new()
    {
        Dock = DockStyle.Top,
        Height = 180,
        Compact = false,
        TitleText = "Find capabilities",
        DescriptionText = "Search the team marketplace and narrow results by type, harness, or team.",
        IconKey = "ic_fluent_search_24_regular"
    };
    private readonly ListView _table = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        MultiSelect = true,
        HideSelection = false,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly FlowLayoutPanel _cards = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        Padding = new Padding(10)
    };
    private readonly Label _detailsTitle = new()
    {
        Dock = DockStyle.Top,
        Height = 42,
        Font = new Font("Segoe UI", 13F, FontStyle.Bold),
        TextAlign = ContentAlignment.BottomLeft,
        AutoEllipsis = true
    };
    private readonly Label _details = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        Padding = new Padding(0, 8, 0, 8)
    };
    private readonly ZarpaButton _addButton = Button("Install selected", ZarpaButtonStyle.Primary, 162);
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 30, TextAlign = ContentAlignment.MiddleLeft };
    private readonly SplitContainer _split = new()
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Vertical,
        FixedPanel = FixedPanel.Panel2
    };
    private HarnessManagerConfig _config = new();
    private IReadOnlyList<HarnessCatalogEntry> _entries = [];
    private HarnessCatalogEntry? _selected;
    private readonly HashSet<string> _selectedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _installedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _installedVersions = new(StringComparer.OrdinalIgnoreCase);
    private bool _populatingTable;
    private bool _cardMode = true;
    private ZarpaThemeTokens? _themeTokens;

    public HarnessCatalogView(ConfigStore configStore, Action publish, Action<HarnessCatalogEntry> addToSetup,
        Action configureRepositories)
    {
        _configStore = configStore;
        _publish = publish;
        _addToSetup = addToSetup;
        _configureRepositories = configureRepositories;
        Dock = DockStyle.Fill;
        Padding = new Padding(18, 14, 18, 18);
        ConfigureTable();
        foreach (var value in new[] { "All types" }.Concat(HarnessCatalogItemTypes.All.Select(HarnessFeatureIds.DisplayName)))
            _type.Items.Add(value);
        foreach (var value in new[] { "All harnesses" }.Concat(HarnessCatalog.All.Select(item => item.DisplayName)))
            _harness.Items.Add(value);
        _type.SelectedIndex = 0;
        _harness.SelectedIndex = 0;
        _team.Items.Add("All teams");
        _team.SelectedIndex = 0;

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 52,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 5, 0, 5)
        };
        toolbar.Controls.AddRange([_search, _cardsButton, _tableButton, _refreshButton, _publishButton,
            _repositoriesButton]);
        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 4)
        };
        filters.Controls.AddRange([FilterLabel("TYPE"), _type, FilterLabel("HARNESS"), _harness,
            FilterLabel("TEAM"), _team]);
        _filterSurface.Controls.Add(toolbar);
        _filterSurface.Controls.Add(filters);
        var cardsPanel = new ZarpaCardPanel
        {
            Dock = DockStyle.Fill,
            TitleText = "Available capabilities",
            DescriptionText = "Select a card to stage it for installation in My Setup.",
            IconKey = "ic_fluent_grid_24_regular",
            Compact = false,
            RoundContentCorners = true,
            Padding = new Padding(12, 58, 12, 12)
        };
        cardsPanel.Controls.Add(_cards);
        var detailActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 6, 0, 6)
        };
        detailActions.Controls.Add(_addButton);
        _split.Panel1.Controls.Add(cardsPanel);
        _split.Panel1.Controls.Add(_table);
        _split.Panel1.Controls.Add(_status);
        _split.Panel2.Padding = new Padding(16, 6, 4, 4);
        _split.Panel2.Controls.Add(_details);
        _split.Panel2.Controls.Add(_detailsTitle);
        _split.Panel2.Controls.Add(detailActions);
        Controls.Add(_split);
        Controls.Add(_filterSurface);

        _search.ValueChanged += (_, _) => ApplyFilters();
        _type.SelectedIndexChanged += (_, _) => ApplyFilters();
        _harness.SelectedIndexChanged += (_, _) => ApplyFilters();
        _team.SelectedIndexChanged += (_, _) => ApplyFilters();
        _cardsButton.Click += (_, _) => SetView(cardMode: true);
        _tableButton.Click += (_, _) => SetView(cardMode: false);
        _refreshButton.Click += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        _publishButton.Click += (_, _) => _publish();
        _repositoriesButton.Click += (_, _) => _configureRepositories();
        _addButton.Click += (_, _) => InstallSelected();
        _table.SelectedIndexChanged += (_, _) => SelectEntry(_table.SelectedItems.Count == 0
            ? null : _table.SelectedItems[0].Tag as HarnessCatalogEntry);
        _table.ItemChecked += (_, args) => TableItemChecked(args);
        Load += (_, _) => ResizeDetailPanel();
        Resize += (_, _) => ResizeDetailPanel();
        SetView(cardMode: true);
    }

    private void ResizeDetailPanel()
    {
        if (_split.Width < 640) return;
        _split.SplitterDistance = Math.Max(320, _split.Width - 300);
    }

    public void LoadConfig(HarnessManagerConfig config)
    {
        _config = config;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            _status.Text = "Refreshing catalog...";
            _entries = await _repository.ListAsync(_config.Repositories).ConfigureAwait(true);
            await LoadInstalledAsync().ConfigureAwait(true);
            _selectedKeys.RemoveWhere(key => _entries.All(entry => !string.Equals(
                EntryKey(entry), key, StringComparison.OrdinalIgnoreCase)));
            PopulateTeams();
            ApplyFilters();
            _status.Text = _config.Repositories.Count == 0
                ? "Configure at least one repository to use the catalog."
                : $"{_entries.Count} published artifact version(s) across {_config.Repositories.Count(item => item.Enabled)} repository(ies).";
        }
        catch (Exception exception)
        {
            _status.Text = $"Catalog unavailable: {exception.Message}";
        }
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _themeTokens = value;
        BackColor = value.Canvas;
        ForeColor = value.Text;
        _table.BackColor = value.Surface;
        _table.ForeColor = value.Text;
        _cards.BackColor = value.Canvas;
        _filterSurface.ApplyTheme(value);
        _detailsTitle.ForeColor = value.Text;
        _details.ForeColor = value.TextMuted;
        _status.ForeColor = value.TextMuted;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var search = _search.Value.Trim();
        var selectedType = _type.SelectedIndex <= 0 ? null : HarnessCatalogItemTypes.All[_type.SelectedIndex - 1];
        var selectedHarness = _harness.SelectedIndex <= 0 ? null : HarnessCatalog.All[_harness.SelectedIndex - 1].Id;
        var selectedTeam = _team.SelectedIndex <= 0 ? null : _team.Text;
        var filtered = LatestEntries(_entries).Where(entry =>
            (selectedType is null || string.Equals(entry.Manifest.Type, selectedType, StringComparison.OrdinalIgnoreCase)) &&
            (selectedHarness is null || entry.Manifest.Compatibility.TryGetValue(selectedHarness, out var state) &&
                !string.Equals(state, HarnessCompatibilityStates.Unsupported, StringComparison.OrdinalIgnoreCase)) &&
            (selectedTeam is null || string.Equals(entry.Manifest.Team, selectedTeam, StringComparison.OrdinalIgnoreCase)) &&
            (search.Length == 0 || SearchText(entry).Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
        PopulateTable(filtered);
        PopulateCards(filtered);
        if (_selected is not null && !filtered.Contains(_selected)) SelectEntry(null);
    }

    private void PopulateTable(IEnumerable<HarnessCatalogEntry> entries)
    {
        _populatingTable = true;
        _table.BeginUpdate();
        _table.Items.Clear();
        foreach (var entry in entries)
        {
            var item = new ListViewItem(entry.Manifest.Name)
            {
                Tag = entry,
                Checked = _selectedKeys.Contains(EntryKey(entry))
            };
            item.SubItems.Add(HarnessFeatureIds.DisplayName(entry.Manifest.Type));
            item.SubItems.Add(entry.Manifest.Version);
            item.SubItems.Add(entry.Manifest.Team);
            item.SubItems.Add(entry.Manifest.OwnerName);
            item.SubItems.Add(entry.Manifest.State);
            item.SubItems.Add(entry.RepositoryName);
            _table.Items.Add(item);
        }
        _table.EndUpdate();
        _populatingTable = false;
    }

    private void PopulateCards(IEnumerable<HarnessCatalogEntry> entries)
    {
        _cards.SuspendLayout();
        _cards.Controls.Clear();
        foreach (var entry in entries)
        {
            var selected = _selectedKeys.Contains(EntryKey(entry));
            var installed = _installedKeys.Contains(EntryKey(entry));
            var updateAvailable = _installedVersions.TryGetValue(ArtifactKey(entry), out var installedVersion) &&
                CompareVersions(entry.Manifest.Version, installedVersion) > 0;
            var card = new HarnessCapabilityCard(
                entry.Manifest.Name,
                HarnessFeatureIds.DisplayName(entry.Manifest.Type),
                entry.Manifest.Version,
                CatalogDescription(entry),
                $"{entry.Manifest.Team} · {entry.Manifest.OwnerName}")
            {
                Installed = installed && !updateAvailable,
                UpdateAvailable = updateAvailable,
                Selected = selected && !installed
            };
            card.SelectionChanged += (_, _) => SetSelected(entry, card.Selected);
            if (_themeTokens is not null) card.ApplyTheme(_themeTokens);
            _cards.Controls.Add(card);
        }
        _cards.ResumeLayout();
    }

    private void SelectEntry(HarnessCatalogEntry? entry)
    {
        _selected = entry;
        UpdateInstallAction();
        _detailsTitle.Text = entry?.Manifest.Name ?? "Select an artifact";
        _details.Text = entry is null ? "Choose a catalog item to inspect its compatibility and version details." :
            $"{CatalogDescription(entry)}\r\n\r\n{entry.Manifest.Description}\r\n\r\n" +
            $"Type: {HarnessFeatureIds.DisplayName(entry.Manifest.Type)}\r\nVersion: {entry.Manifest.Version}\r\n" +
            $"State: {entry.Manifest.State}\r\nOwner: {entry.Manifest.OwnerName} ({entry.Manifest.Team})\r\n" +
            $"Repository: {entry.RepositoryName}\r\nScopes: {string.Join(", ", entry.Manifest.AllowedScopes)}\r\n\r\n" +
            string.Join("\r\n", HarnessCatalog.All.Select(harness =>
                $"{harness.DisplayName}: {entry.Manifest.Compatibility.GetValueOrDefault(harness.Id, HarnessCompatibilityStates.Unsupported)}")) +
            (entry.Manifest.ChangeNotes.Length == 0 ? string.Empty : $"\r\n\r\nChanges\r\n{entry.Manifest.ChangeNotes}");
    }

    private void SetView(bool cardMode)
    {
        _cardMode = cardMode;
        _cards.Visible = cardMode;
        _table.Visible = !cardMode;
        if (cardMode) _cards.BringToFront(); else _table.BringToFront();
        _cardsButton.ButtonStyle = cardMode ? ZarpaButtonStyle.Primary : ZarpaButtonStyle.Secondary;
        _tableButton.ButtonStyle = cardMode ? ZarpaButtonStyle.Secondary : ZarpaButtonStyle.Primary;
        ApplyFilters();
    }

    private void PopulateTeams()
    {
        var selected = _team.Text;
        _team.Items.Clear();
        _team.Items.Add("All teams");
        foreach (var team in _entries.Select(entry => entry.Manifest.Team).Distinct(StringComparer.OrdinalIgnoreCase).Order())
            _team.Items.Add(team);
        _team.SelectedIndex = Math.Max(0, _team.Items.IndexOf(selected));
    }

    private void ConfigureTable()
    {
        _table.Columns.Add("Name", 220);
        _table.Columns.Add("Type", 110);
        _table.Columns.Add("Version", 80);
        _table.Columns.Add("Team", 110);
        _table.Columns.Add("Owner", 130);
        _table.Columns.Add("State", 90);
        _table.Columns.Add("Repository", 130);
    }

    private static string SearchText(HarnessCatalogEntry entry) => string.Join(' ',
        entry.Manifest.Name, entry.Manifest.Summary, entry.Manifest.Description,
        entry.Manifest.CapabilityDescription, entry.Manifest.Team,
        entry.Manifest.OwnerName, entry.Manifest.Type, string.Join(' ', entry.Manifest.Tags));

    private static string CompatibilitySummary(HarnessCatalogEntry entry) => string.Join(" · ",
        HarnessCatalog.All.Where(harness => entry.Manifest.Compatibility.TryGetValue(harness.Id, out var state) &&
            !string.Equals(state, HarnessCompatibilityStates.Unsupported, StringComparison.OrdinalIgnoreCase))
        .Select(harness => harness.DisplayName));

    private void SetSelected(HarnessCatalogEntry entry, bool selected)
    {
        if (_installedKeys.Contains(EntryKey(entry))) return;
        if (selected) _selectedKeys.Add(EntryKey(entry));
        else _selectedKeys.Remove(EntryKey(entry));
        SelectEntry(entry);
        UpdateInstallAction();
        if (selected) _toastManager.Show(this, "Capability selected", $"{entry.Manifest.Name} is ready to install.",
            ZarpaFeedbackKind.Success, 2400, string.Empty, _themeTokens);
    }

    private void TableItemChecked(ItemCheckedEventArgs args)
    {
        if (_populatingTable || args.Item.Tag is not HarnessCatalogEntry entry) return;
        if (args.Item.Checked) _selectedKeys.Add(EntryKey(entry));
        else _selectedKeys.Remove(EntryKey(entry));
        SelectEntry(entry);
        UpdateInstallAction();
    }

    private void InstallSelected()
    {
        var selected = _entries.Where(entry => _selectedKeys.Contains(EntryKey(entry))).ToList();
        if (selected.Count == 0) return;
        foreach (var entry in selected) _addToSetup(entry);
        _selectedKeys.Clear();
        UpdateInstallAction();
        ApplyFilters();
        _status.Text = $"Staged {selected.Count} selected capability(ies) in My Setup. Preview before applying.";
        _toastManager.Show(this, "Added to My Setup", $"{selected.Count} capability(ies) staged for preview.",
            ZarpaFeedbackKind.Success, 3000, string.Empty, _themeTokens);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toastManager.Dispose();
        base.Dispose(disposing);
    }

    private void UpdateInstallAction()
    {
        var count = _selectedKeys.Count;
        _addButton.Enabled = count > 0;
        _addButton.Text = count == 0 ? "Install selected" : $"Install selected ({count})";
    }

    private static string EntryKey(HarnessCatalogEntry entry) =>
        $"{entry.RepositoryId}|{entry.Manifest.Id}|{entry.Manifest.Version}";

    private static string ArtifactKey(HarnessCatalogEntry entry) =>
        $"{entry.RepositoryId}|{entry.Manifest.Id}";

    private static IReadOnlyList<HarnessCatalogEntry> LatestEntries(IEnumerable<HarnessCatalogEntry> entries) =>
        entries.GroupBy(ArtifactKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Manifest.Version,
                Comparer<string>.Create(CompareVersions)).First())
            .ToList();

    private static int CompareVersions(string left, string right)
    {
        static int[] Parts(string value) => value.Split('-', 2)[0].Split('.')
            .Select(part => int.TryParse(part, out var number) ? number : 0).ToArray();
        var a = Parts(left);
        var b = Parts(right);
        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var comparison = (index < a.Length ? a[index] : 0).CompareTo(index < b.Length ? b[index] : 0);
            if (comparison != 0) return comparison;
        }
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string CatalogDescription(HarnessCatalogEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.Manifest.CapabilityDescription)
            ? entry.Manifest.CapabilityDescription
            : !string.IsNullOrWhiteSpace(entry.Manifest.Summary)
                ? entry.Manifest.Summary
                : entry.Manifest.Description;

    private async Task LoadInstalledAsync()
    {
        _installedKeys.Clear();
        _installedVersions.Clear();
        var store = new HarnessEnvironmentStore(_configStore.Path);
        foreach (var environment in _config.Environments)
        {
            var document = await store.LoadAsync(environment).ConfigureAwait(true);
            if (document is null) continue;
            foreach (var artifact in document.Artifacts.Where(item => item.Enabled))
            {
                _installedKeys.Add($"{artifact.RepositoryId}|{artifact.ArtifactId}|{artifact.Version}");
                var key = $"{artifact.RepositoryId}|{artifact.ArtifactId}";
                if (!_installedVersions.TryGetValue(key, out var current) || CompareVersions(artifact.Version, current) > 0)
                    _installedVersions[key] = artifact.Version;
            }
        }
        _selectedKeys.RemoveWhere(key => _installedKeys.Contains(key));
    }

    private static Label FilterLabel(string text) => new()
    {
        AutoSize = false,
        Text = text,
        Width = 66,
        Height = 34,
        Margin = new Padding(12, 2, 0, 2),
        Font = new Font("Segoe UI", 8F, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static ZarpaComboBox Combo(int width) => new()
    {
        LabelText = string.Empty,
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = width
    };
    private static ZarpaButton Button(string text, ZarpaButtonStyle style, int width) => new()
    {
        Text = text,
        ButtonStyle = style,
        Width = width,
        Height = 34,
        Margin = new Padding(4, 2, 4, 2)
    };

}
