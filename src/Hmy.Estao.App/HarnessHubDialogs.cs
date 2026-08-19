using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Harnesses;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App;

internal sealed class HarnessRepositoriesDialog : ZarpaModernForm
{
    private readonly HarnessManagerConfig _target;
    private readonly List<HarnessRepositoryConfig> _repositories;
    private readonly ListBox _list = new() { Dock = DockStyle.Fill };
    private readonly ZarpaTextBox _name = Field("Company Hub");
    private readonly ZarpaTextBox _path = Field("\\\\server\\team\\harness-hub");

    public HarnessRepositoriesDialog(HarnessManagerConfig target)
    {
        _target = target;
        _repositories = target.Repositories.Select(item => new HarnessRepositoryConfig
        {
            Id = item.Id,
            Name = item.Name,
            Path = item.Path,
            Enabled = item.Enabled
        }).ToList();
        Text = "Harness Hub Repositories";
        ContextText = "Catalog sources";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(720, 460);
        MinimumSize = new Size(640, 420);
        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14) };
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _name.LabelText = "Repository name";
        _path.LabelText = "Network or local folder";
        editor.Controls.Add(_name, 0, 0);
        editor.Controls.Add(_path, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        var add = Button("Add / update", ZarpaButtonStyle.Primary, 112);
        var remove = Button("Remove", ZarpaButtonStyle.Secondary, 88);
        var browse = Button("Browse...", ZarpaButtonStyle.Secondary, 92);
        buttons.Controls.AddRange([add, remove, browse]);
        editor.Controls.Add(buttons, 0, 2);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 220 };
        split.Panel1.Padding = new Padding(12);
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(editor);
        Controls.Add(split);
        Controls.Add(Footer(() => SaveAndClose()));
        _list.SelectedIndexChanged += (_, _) => LoadSelected();
        add.Click += (_, _) => AddOrUpdate();
        remove.Click += (_, _) => RemoveSelected();
        browse.Click += (_, _) => Browse();
        RefreshList();
    }

    private void LoadSelected()
    {
        if (_list.SelectedIndex < 0) return;
        _name.Value = _repositories[_list.SelectedIndex].Name;
        _path.Value = _repositories[_list.SelectedIndex].Path;
    }

    private void AddOrUpdate()
    {
        if (string.IsNullOrWhiteSpace(_name.Value) || string.IsNullOrWhiteSpace(_path.Value)) return;
        var repository = _list.SelectedIndex >= 0 ? _repositories[_list.SelectedIndex] : new HarnessRepositoryConfig();
        repository.Name = _name.Value.Trim();
        repository.Path = _path.Value.Trim();
        repository.Id = Slug(repository.Id.Length == 0 ? repository.Name : repository.Id);
        repository.Enabled = true;
        if (_list.SelectedIndex < 0) _repositories.Add(repository);
        RefreshList();
        _list.SelectedIndex = _repositories.IndexOf(repository);
    }

    private void RemoveSelected()
    {
        if (_list.SelectedIndex < 0) return;
        _repositories.RemoveAt(_list.SelectedIndex);
        RefreshList();
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select a Harness Hub repository folder" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _path.Value = dialog.SelectedPath;
    }

    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (var repository in _repositories) _list.Items.Add(repository.Name);
    }

    private void SaveAndClose()
    {
        _target.Repositories = _repositories;
        _target.DefaultRepositoryId = _repositories.FirstOrDefault()?.Id ?? string.Empty;
        _target.HubPath = _repositories.FirstOrDefault()?.Path ?? string.Empty;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string Slug(string value) => string.Join('-', new string(value.Trim().ToLowerInvariant()
        .Select(character => char.IsLetterOrDigit(character) ? character : '-')
        .ToArray()).Split('-', StringSplitOptions.RemoveEmptyEntries));

    private static ZarpaTextBox Field(string placeholder) => new() { Placeholder = placeholder, Dock = DockStyle.Fill };
    internal static ZarpaButton Button(string text, ZarpaButtonStyle style, int width) => new()
    {
        Text = text,
        ButtonStyle = style,
        Width = width,
        Height = 34,
        Margin = new Padding(4)
    };
    internal static Control Footer(Action save)
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(10) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 220, FlowDirection = FlowDirection.RightToLeft };
        var saveButton = Button("Save", ZarpaButtonStyle.Primary, 92);
        var cancel = Button("Cancel", ZarpaButtonStyle.Subtle, 92);
        saveButton.Click += (_, _) => save();
        cancel.Click += (_, _) => ((Form)footer.FindForm()!).Close();
        actions.Controls.AddRange([saveButton, cancel]);
        footer.Controls.Add(actions);
        return footer;
    }
}

internal sealed class HarnessEnvironmentDialog : ZarpaModernForm
{
    private readonly ZarpaTextBox _name = new() { LabelText = "Environment name", Dock = DockStyle.Top };
    private readonly ZarpaComboBox _harness = new() { LabelText = "Harness", Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ZarpaComboBox _scope = new() { LabelText = "Scope", Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ZarpaTextBox _root = new() { LabelText = "Root folder", Dock = DockStyle.Top };
    public HarnessEnvironmentConfig? Environment { get; private set; }

    public HarnessEnvironmentDialog()
    {
        Text = "New Harness Environment";
        ContextText = "Harness and target root";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(560, 430);
        foreach (var harness in HarnessCatalog.All) _harness.Items.Add(harness.DisplayName);
        _scope.Items.AddRange(["Personal", "Project"]);
        _harness.SelectedIndex = 0;
        _scope.SelectedIndex = 0;
        _root.Value = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var fields = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24) };
        fields.Controls.Add(_root);
        fields.Controls.Add(_scope);
        fields.Controls.Add(_harness);
        fields.Controls.Add(_name);
        Controls.Add(fields);
        Controls.Add(HarnessRepositoriesDialog.Footer(Save));
    }

    private void Save()
    {
        if (_harness.SelectedIndex < 0 || string.IsNullOrWhiteSpace(_root.Value)) return;
        var harness = HarnessCatalog.All[_harness.SelectedIndex];
        var scope = _scope.SelectedIndex == 1 ? "project" : "personal";
        var name = string.IsNullOrWhiteSpace(_name.Value) ? $"{harness.DisplayName} — {scope}" : _name.Value.Trim();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Environment = new HarnessEnvironmentConfig
        {
            Id = $"{harness.Id}-{scope}-{suffix}",
            Name = name,
            HarnessId = harness.Id,
            Scope = scope,
            RootPath = _root.Value.Trim(),
            Managed = true
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class HarnessRestoreDialog : ZarpaModernForm
{
    private readonly ListBox _points = new() { Dock = DockStyle.Fill };
    private readonly IReadOnlyList<HarnessRestorePoint> _source;
    public HarnessRestorePoint? SelectedPoint { get; private set; }

    public HarnessRestoreDialog(IReadOnlyList<HarnessRestorePoint> points)
    {
        _source = points;
        Text = "Restore Harness Environment";
        ContextText = "Latest restore points";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(620, 420);
        foreach (var point in points.Take(10))
            _points.Items.Add($"{point.Manifest.CreatedUtc.LocalDateTime:g} · {point.Manifest.PackageName} {point.Manifest.PackageVersion}");
        if (_points.Items.Count > 0) _points.SelectedIndex = 0;
        Controls.Add(_points);
        Controls.Add(HarnessRepositoriesDialog.Footer(Save));
    }

    private void Save()
    {
        if (_points.SelectedIndex < 0) return;
        SelectedPoint = _source[_points.SelectedIndex];
        DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class HarnessPublishDialog : ZarpaModernForm
{
    private readonly HarnessManagerConfig _manager;
    private readonly HarnessCatalogRepository _catalog = new();
    private readonly ZarpaComboBox _mode = Combo("Source", ["Publish local", "Import package", "Create collection", "Create snapshot"]);
    private readonly ZarpaComboBox _repository = Combo("Repository", []);
    private readonly ZarpaComboBox _harness = Combo("Source harness", []);
    private readonly ZarpaButton _scan = HarnessRepositoriesDialog.Button("Scan local", ZarpaButtonStyle.Secondary, 108);
    private readonly ZarpaTextBox _id = Field("Artifact ID", "team-skill");
    private readonly ZarpaTextBox _name = Field("Name", "Team skill");
    private readonly ZarpaTextBox _summary = Field("Summary", "What this artifact provides");
    private readonly ZarpaTextBox _version = Field("Version", "1.0.0");
    private readonly ZarpaTextBox _team = Field("Team", "company");
    private readonly ZarpaTextBox _changes = Field("Change notes", "Initial version");
    private readonly CheckedListBox _references = new() { Dock = DockStyle.Fill, CheckOnClick = true };
    private readonly ListView _localCandidates = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 30, TextAlign = ContentAlignment.MiddleLeft };
    private IReadOnlyList<HarnessPublishCandidate> _candidates = [];
    private IReadOnlyList<HarnessCatalogEntry> _entries = [];
    private IReadOnlyList<HarnessCatalogEntry> _referenceEntries = [];

    public HarnessPublishDialog(HarnessManagerConfig manager)
    {
        _manager = manager;
        Text = "Publish to Harness Hub";
        ContextText = "Local, imported, and composed artifacts";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(780, 690);
        MinimumSize = new Size(700, 620);
        foreach (var repository in manager.Repositories.Where(item => item.Enabled)) _repository.Items.Add(repository.Name);
        foreach (var harness in HarnessCatalog.All) _harness.Items.Add(harness.DisplayName);
        if (_repository.Items.Count > 0) _repository.SelectedIndex = 0;
        _harness.SelectedIndex = 0;
        _localCandidates.Columns.Add("Local artifact", 280);
        _localCandidates.Columns.Add("Type", 140);
        _localCandidates.Columns.Add("Files", 70);
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, Height = 380, ColumnCount = 2, Padding = new Padding(18) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        Add(fields, _mode, 0, 0); Add(fields, _repository, 1, 0);
        Add(fields, _harness, 0, 1); Add(fields, _scan, 1, 1);
        Add(fields, _id, 0, 2); Add(fields, _version, 1, 2);
        Add(fields, _name, 0, 3); Add(fields, _team, 1, 3);
        Add(fields, _summary, 0, 4); Add(fields, _changes, 1, 4);
        var referenceHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 0, 18, 8) };
        referenceHost.Controls.Add(_references);
        referenceHost.Controls.Add(_localCandidates);
        Controls.Add(referenceHost);
        Controls.Add(fields);
        Controls.Add(_status);
        Controls.Add(HarnessRepositoriesDialog.Footer(Publish));
        _mode.SelectedIndexChanged += async (_, _) => await ModeChangedAsync().ConfigureAwait(true);
        _harness.SelectedIndexChanged += async (_, _) => await LoadCandidatesAsync().ConfigureAwait(true);
        _scan.Click += async (_, _) => await LoadCandidatesAsync().ConfigureAwait(true);
        _localCandidates.SelectedIndexChanged += (_, _) => UseSelectedCandidateMetadata();
        Load += async (_, _) =>
        {
            try
            {
                _entries = await _catalog.ListAsync(manager.Repositories).ConfigureAwait(true);
                PopulateReferences();
                await ModeChangedAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                _status.Text = $"Could not initialize publishing: {exception.Message}";
            }
        };
    }

    private void UseSelectedCandidateMetadata()
    {
        if (_localCandidates.SelectedItems.Count == 0 ||
            _localCandidates.SelectedItems[0].Tag is not HarnessPublishCandidate candidate) return;
        _id.Value = candidate.Name;
        _name.Value = candidate.Name;
        _summary.Value = $"Shared {HarnessFeatureIds.DisplayName(candidate.Feature).ToLowerInvariant()} artifact.";
    }

    private async Task ModeChangedAsync()
    {
        var local = _mode.SelectedIndex == 0;
        var composed = _mode.SelectedIndex is 2 or 3;
        _harness.Enabled = local;
        _scan.Enabled = local;
        _localCandidates.Visible = local;
        _references.Visible = composed;
        if (local) _localCandidates.BringToFront();
        if (composed) _references.BringToFront();
        _status.Text = _mode.SelectedIndex switch
        {
            0 => "Scanning local harness artifacts...",
            1 => "Publish imports a previously exported v3 artifact package.",
            2 => "Select artifacts to reference from an immutable collection.",
            _ => "Select exact artifact versions for an immutable snapshot."
        };
        if (local) await LoadCandidatesAsync().ConfigureAwait(true);
    }

    private async Task LoadCandidatesAsync()
    {
        if (_harness.SelectedIndex < 0) return;
        var profile = _manager.Profiles.First(item => string.Equals(item.Id,
            HarnessCatalog.All[_harness.SelectedIndex].Id, StringComparison.OrdinalIgnoreCase));
        try
        {
            _scan.Loading = true;
            _status.Text = $"Scanning {HarnessCatalog.Get(profile.Id).DisplayName} under {profile.BasePath}...";
            _candidates = await _catalog.DiscoverCandidatesAsync(profile).ConfigureAwait(true);
            _localCandidates.BeginUpdate();
            _localCandidates.Items.Clear();
            foreach (var candidate in _candidates)
            {
                var item = new ListViewItem(candidate.Name) { Tag = candidate };
                item.SubItems.Add(HarnessFeatureIds.DisplayName(candidate.Feature));
                item.SubItems.Add(candidate.Files.Count.ToString());
                _localCandidates.Items.Add(item);
            }
            if (_localCandidates.Items.Count > 0) _localCandidates.Items[0].Selected = true;
            _status.Text = _candidates.Count == 0
                ? $"No publishable artifacts found. Scanned {profile.BasePath} as {profile.Scope} {HarnessCatalog.Get(profile.Id).DisplayName}."
                : $"Found {_candidates.Count} publishable local artifact(s) in {profile.BasePath}.";
        }
        catch (Exception exception)
        {
            _candidates = [];
            _localCandidates.Items.Clear();
            _status.Text = $"Local scan failed for {profile.BasePath}: {exception.Message}";
        }
        finally
        {
            _localCandidates.EndUpdate();
            _scan.Loading = false;
        }
    }

    private async void Publish()
    {
        if (_repository.SelectedIndex < 0) return;
        var repository = _manager.Repositories.Where(item => item.Enabled).ElementAt(_repository.SelectedIndex);
        try
        {
            HarnessCatalogEntry published;
            if (_mode.SelectedIndex == 1)
            {
                using var picker = new OpenFileDialog { Filter = "Estao artifact (*.estao)|*.estao|All files (*.*)|*.*" };
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                published = await _catalog.ImportAsync(repository, picker.FileName).ConfigureAwait(true);
            }
            else
            {
                var draft = new HarnessCatalogDraft(_id.Value, _name.Value, _summary.Value, _summary.Value,
                    _version.Value, _team.Value, _changes.Value, [], ["personal", "project"]);
                if (_mode.SelectedIndex == 0)
                {
                    if (_harness.SelectedIndex < 0 || _localCandidates.SelectedItems.Count == 0 ||
                        _localCandidates.SelectedItems[0].Tag is not HarnessPublishCandidate candidate) return;
                    var profile = _manager.Profiles.First(item => string.Equals(item.Id,
                        HarnessCatalog.All[_harness.SelectedIndex].Id, StringComparison.OrdinalIgnoreCase));
                    published = await _catalog.PublishLocalAsync(repository, profile,
                        candidate.Key, draft, _manager.Author).ConfigureAwait(true);
                }
                else
                {
                    var references = _references.CheckedIndices.Cast<int>().Select(index => _referenceEntries[index])
                        .Select(entry => new HarnessArtifactReference
                        {
                            RepositoryId = entry.RepositoryId,
                            ArtifactId = entry.Manifest.Id,
                            Version = entry.Manifest.Version,
                            Enabled = true
                        }).ToList();
                    published = await _catalog.PublishCollectionAsync(repository, draft, _manager.Author, references,
                        snapshot: _mode.SelectedIndex == 3).ConfigureAwait(true);
                }
            }
            MessageBox.Show($"Published {published.Manifest.Name} {published.Manifest.Version}.", "Harness Hub",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Publish failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PopulateReferences()
    {
        _references.Items.Clear();
        _referenceEntries = _entries.Where(entry => entry.Manifest.Type is not
            (HarnessCatalogItemTypes.Collection or HarnessCatalogItemTypes.Snapshot)).ToList();
        foreach (var entry in _referenceEntries)
            _references.Items.Add($"{entry.Manifest.Name} · {entry.Manifest.Version} · {entry.RepositoryName}");
    }

    private static void Add(TableLayoutPanel panel, Control control, int column, int row)
    {
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(6);
        panel.Controls.Add(control, column, row);
    }
    private static ZarpaTextBox Field(string label, string placeholder) => new() { LabelText = label, Placeholder = placeholder };
    private static ZarpaComboBox Combo(string label, IEnumerable<string> values)
    {
        var combo = new ZarpaComboBox { LabelText = label, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var value in values) combo.Items.Add(value);
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        return combo;
    }
}
