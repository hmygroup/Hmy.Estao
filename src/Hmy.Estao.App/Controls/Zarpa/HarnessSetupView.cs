using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Harnesses;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal sealed class HarnessSetupView : UserControl, IZarpaThemeAware
{
    private readonly ConfigStore _configStore;
    private readonly HarnessCatalogRepository _catalog = new();
    private readonly HarnessEnvironmentSyncService _sync = new();
    private readonly ZarpaComboBox _environment = Combo(300);
    private readonly ZarpaButton _adopt = Button("Adopt environment", ZarpaButtonStyle.Secondary, 138);
    private readonly ZarpaButton _newEnvironment = Button("New...", ZarpaButtonStyle.Secondary, 84);
    private readonly ZarpaButton _refresh = Button("Refresh status", ZarpaButtonStyle.Secondary, 112);
    private readonly ListView _desired = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly ListView _preview = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly ListView _installed = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly FlowLayoutPanel _planCards = CardList();
    private readonly FlowLayoutPanel _installedCards = CardList();
    private readonly FlowLayoutPanel _previewCards = CardList();
    private readonly ZarpaButton _previewButton = Button("Preview", ZarpaButtonStyle.Secondary, 94);
    private readonly ZarpaButton _applyButton = Button("Apply", ZarpaButtonStyle.Primary, 94);
    private readonly ZarpaButton _removeButton = Button("Remove...", ZarpaButtonStyle.Secondary, 104);
    private readonly ZarpaButton _restoreButton = Button("Restore...", ZarpaButtonStyle.Secondary, 104);
    private readonly ZarpaButton _discardButton = Button("Discard plan", ZarpaButtonStyle.Secondary, 112);
    private readonly Label _environmentSummary = new()
    {
        Dock = DockStyle.Top,
        Height = 36,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 34,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };
    private readonly Label _planSummary = new() { Dock = DockStyle.Top, Height = 30, AutoEllipsis = true };
    private HarnessManagerConfig _manager = new();
    private HarnessEnvironmentDocument? _document;
    private IReadOnlyList<HarnessCatalogEntry> _catalogEntries = [];
    private readonly List<HarnessCatalogEntry> _staged = [];

    public HarnessSetupView(ConfigStore configStore)
    {
        _configStore = configStore;
        Dock = DockStyle.Fill;
        Padding = new Padding(18, 14, 18, 18);
        ConfigureLists();
        var header = new ZarpaSectionHeader
        {
            Dock = DockStyle.Top,
            TitleText = "My Setup",
            DescriptionText = "Stage changes, inspect drift, and synchronize one harness environment atomically.",
            IconKey = "ic_fluent_settings_24_regular"
        };
        var environmentBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 52,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 5, 0, 5)
        };
        environmentBar.Controls.AddRange([_environment, _adopt, _newEnvironment, _refresh]);
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.None
        };
        var desiredHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Text = "INSTALLATION PLAN · WHAT WILL CHANGE",
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var installedHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Text = "CURRENTLY INSTALLED",
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var previewHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Text = "PREVIEW AND DRIFT",
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 7)
        };
        actions.Controls.AddRange([_applyButton, _previewButton, _discardButton, _restoreButton, _removeButton]);
        var planSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 210 };
        planSplit.Panel1.Controls.Add(_planCards);
        planSplit.Panel1.Controls.Add(desiredHeader);
        planSplit.Panel1.Controls.Add(_planSummary);
        planSplit.Panel2.Controls.Add(_installedCards);
        planSplit.Panel2.Controls.Add(installedHeader);
        split.Panel1.Controls.Add(planSplit);
        split.Panel2.Controls.Add(_previewCards);
        split.Panel2.Controls.Add(previewHeader);
        split.Panel2.Controls.Add(actions);
        Controls.Add(split);
        Controls.Add(_environmentSummary);
        Controls.Add(environmentBar);
        Controls.Add(header);
        Controls.Add(_status);

        _environment.SelectedIndexChanged += async (_, _) => await SelectEnvironmentAsync().ConfigureAwait(true);
        _adopt.Click += async (_, _) => await AdoptAsync().ConfigureAwait(true);
        _newEnvironment.Click += async (_, _) => await NewEnvironmentAsync().ConfigureAwait(true);
        _refresh.Click += async (_, _) => await RefreshStatusAsync().ConfigureAwait(true);
        _previewButton.Click += async (_, _) => await PreviewAsync().ConfigureAwait(true);
        _applyButton.Click += async (_, _) => await ApplyAsync().ConfigureAwait(true);
        _removeButton.Click += async (_, _) => await RemoveAsync().ConfigureAwait(true);
        _restoreButton.Click += async (_, _) => await RestoreAsync().ConfigureAwait(true);
        _discardButton.Click += (_, _) => DiscardPlan();
        Load += (_, _) =>
        {
            if (split.Height >= 400) split.SplitterDistance = Math.Max(360, (int)(split.Height * .62));
            if (planSplit.Height >= 260) planSplit.SplitterDistance = Math.Max(230, (int)(planSplit.Height * .68));
        };
        Resize += (_, _) =>
        {
            if (split.Height >= 400) split.SplitterDistance = Math.Clamp((int)(split.Height * .62), 320, split.Height - 220);
            if (planSplit.Height >= 260) planSplit.SplitterDistance = Math.Clamp((int)(planSplit.Height * .68), 220, planSplit.Height - 100);
        };
    }

    public async Task LoadConfigAsync(HarnessManagerConfig manager)
    {
        _manager = manager;
        _catalogEntries = await _catalog.ListAsync(manager.Repositories).ConfigureAwait(true);
        var selectedId = SelectedEnvironmentConfig()?.Id;
        _environment.Items.Clear();
        foreach (var environment in manager.Environments)
            _environment.Items.Add(environment.Name);
        if (_environment.Items.Count == 0)
        {
            _document = null;
            UpdateEnabledState();
            return;
        }
        var index = manager.Environments.FindIndex(item => string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        _environment.SelectedIndex = Math.Max(0, index);
        await SelectEnvironmentAsync().ConfigureAwait(true);
    }

    public void Stage(HarnessCatalogEntry entry)
    {
        if (_document is null)
        {
            _status.Text = "Select an environment before adding an artifact.";
            return;
        }
        var candidates = entry.Manifest.Type is HarnessCatalogItemTypes.Collection or HarnessCatalogItemTypes.Snapshot
            ? entry.Manifest.References.Select(reference => _catalogEntries.FirstOrDefault(candidate =>
                string.Equals(candidate.RepositoryId, reference.RepositoryId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Manifest.Id, reference.ArtifactId, StringComparison.OrdinalIgnoreCase) &&
                (reference.Version.Length == 0 || string.Equals(candidate.Manifest.Version, reference.Version, StringComparison.OrdinalIgnoreCase))))
                .Where(candidate => candidate is not null).Cast<HarnessCatalogEntry>()
            : [entry];
        foreach (var candidate in candidates)
        {
            if (!candidate.Manifest.AllowedScopes.Contains(_document.Scope, StringComparer.OrdinalIgnoreCase)) continue;
            if (candidate.Manifest.Compatibility.GetValueOrDefault(_document.HarnessId,
                    HarnessCompatibilityStates.Unsupported) == HarnessCompatibilityStates.Unsupported) continue;
            if (_staged.All(existing => !SameArtifact(existing, candidate))) _staged.Add(candidate);
        }
        PopulateDesired();
        _status.Text = $"Staged {_staged.Count} artifact(s). Preview before applying.";
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        BackColor = value.Canvas;
        ForeColor = value.Text;
        foreach (var list in new[] { _desired, _installed, _preview })
        {
            list.BackColor = value.Surface;
            list.ForeColor = value.Text;
        }
        foreach (var list in new[] { _planCards, _installedCards, _previewCards })
        {
            list.BackColor = value.Canvas;
            list.ForeColor = value.Text;
        }
        _environmentSummary.ForeColor = value.TextMuted;
        _planSummary.ForeColor = value.TextMuted;
        _status.ForeColor = value.TextMuted;
    }

    private async Task SelectEnvironmentAsync()
    {
        var config = SelectedEnvironmentConfig();
        if (config is null)
        {
            _document = null;
            UpdateEnabledState();
            return;
        }
        var store = new HarnessEnvironmentStore(_configStore.Path);
        _document = await store.LoadAsync(config).ConfigureAwait(true) ?? HarnessEnvironmentStore.FromConfig(config);
        _staged.Clear();
        foreach (var reference in _document.Artifacts.Where(reference => reference.Enabled))
        {
            var entry = FindEntry(reference);
            if (entry is not null) _staged.Add(entry);
        }
        PopulateDesired();
        await RefreshStatusAsync().ConfigureAwait(true);
    }

    private async Task RefreshStatusAsync()
    {
        if (_document is null) return;
        _preview.Items.Clear();
        _previewCards.Controls.Clear();
        foreach (var drift in await _sync.DetectDriftAsync(_document).ConfigureAwait(true))
        {
            var item = new ListViewItem(drift.State);
            item.SubItems.Add(drift.Path);
            item.SubItems.Add("Local content differs from the last applied version");
            _preview.Items.Add(item);
            _previewCards.Controls.Add(SetupRow(drift.State, $"{drift.Path}\r\nLocal content differs from the last applied version.", "ic_fluent_warning_24_regular"));
        }
        var config = SelectedEnvironmentConfig();
        _environmentSummary.Text = $"{HarnessCatalog.Get(_document.HarnessId).DisplayName} · {_document.Scope} · {_document.RootPath}";
        _status.Text = config is { Managed: false }
            ? "Discovered environment — adopt it before Estao writes files."
            : _preview.Items.Count == 0 ? "Environment is synchronized with its last applied state." :
                $"{_preview.Items.Count} local drift item(s) require review.";
        UpdateEnabledState();
    }

    private async Task PreviewAsync()
    {
        if (_document is null) return;
        _preview.Items.Clear();
        _previewCards.Controls.Clear();
        foreach (ListViewItem desired in _desired.Items)
        {
            if (!desired.Checked || desired.Tag is not HarnessCatalogEntry entry) continue;
            try
            {
                var manifest = await HarnessHubService.ReadManifestAsync(entry.PackagePath).ConfigureAwait(true);
                foreach (var artifact in manifest.Artifacts)
                {
                    var item = new ListViewItem("install");
                    item.SubItems.Add(artifact.LogicalPath);
                    item.SubItems.Add($"{entry.Manifest.Name} · {entry.Manifest.Compatibility.GetValueOrDefault(_document.HarnessId)}");
                    _preview.Items.Add(item);
                    _previewCards.Controls.Add(SetupRow("Install", $"{entry.Manifest.Name} · {entry.Manifest.Version}\r\n{artifact.LogicalPath}", "ic_fluent_arrow_download_24_regular"));
                }
            }
            catch (InvalidDataException)
            {
                var item = new ListViewItem("reference");
                item.SubItems.Add(entry.Manifest.Name);
                item.SubItems.Add("Collection metadata");
                _preview.Items.Add(item);
                _previewCards.Controls.Add(SetupRow("Reference", $"{entry.Manifest.Name}\r\nCollection metadata", "ic_fluent_link_24_regular"));
            }
        }
        _status.Text = $"Preview contains {_preview.Items.Count} planned operation(s). No files have changed.";
    }

    private async Task ApplyAsync()
    {
        if (_document is null || SelectedEnvironmentConfig() is not { Managed: true }) return;
        var plan = _desired.Items.Cast<ListViewItem>().Where(item => item.Checked)
            .Select(item => new HarnessSyncPlanItem((HarnessCatalogEntry)item.Tag!)).ToList();
        if (plan.Count == 0)
        {
            _status.Text = "Select at least one artifact to apply.";
            return;
        }
        try
        {
            ToggleBusy(true);
            await PreviewAsync().ConfigureAwait(true);
            var result = await _sync.ApplyAtomicAsync(_document, plan).ConfigureAwait(true);
            await new HarnessEnvironmentStore(_configStore.Path).SaveAsync(_document).ConfigureAwait(true);
            await _sync.TrimRestorePointsAsync(_document, 10).ConfigureAwait(true);
            _status.Text = $"Applied {result.InstalledFiles.Count} file(s) atomically. {result.Warnings.Count} warning(s).";
            await RefreshStatusAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Synchronization was rolled back.\n\n{exception.Message}", "Harness Hub",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _status.Text = "Apply failed; completed changes were rolled back.";
        }
        finally
        {
            ToggleBusy(false);
        }
    }

    private async Task AdoptAsync()
    {
        var selected = SelectedEnvironmentConfig();
        if (selected is null) return;
        selected.Managed = true;
        var config = await _configStore.LoadAsync().ConfigureAwait(true);
        var persisted = config.HarnessManager.Environments.FirstOrDefault(item =>
            string.Equals(item.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
        if (persisted is not null) persisted.Managed = true;
        await _configStore.SaveAsync(config).ConfigureAwait(true);
        await new HarnessEnvironmentStore(_configStore.Path).SaveAsync(_document!).ConfigureAwait(true);
        _status.Text = "Environment adopted. Changes remain staged until Preview and Apply.";
        UpdateEnabledState();
    }

    private async Task NewEnvironmentAsync()
    {
        using var dialog = new HarnessEnvironmentDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Environment is null) return;
        var config = await _configStore.LoadAsync().ConfigureAwait(true);
        config.HarnessManager.Environments.Add(dialog.Environment);
        await _configStore.SaveAsync(config).ConfigureAwait(true);
        await LoadConfigAsync(config.HarnessManager).ConfigureAwait(true);
        _environment.SelectedIndex = _manager.Environments.Count - 1;
    }

    private async Task RemoveAsync()
    {
        if (_document is null || _desired.SelectedItems.Count == 0) return;
        var entry = _desired.SelectedItems[0].Tag as HarnessCatalogEntry;
        if (entry is null) return;
        var reference = _document.Artifacts.FirstOrDefault(item =>
            string.Equals(item.RepositoryId, entry.RepositoryId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.ArtifactId, entry.Manifest.Id, StringComparison.OrdinalIgnoreCase));
        if (reference is null)
        {
            _staged.RemoveAll(item => SameArtifact(item, entry));
            PopulateDesired();
            return;
        }
        var answer = MessageBox.Show(
            $"Remove {entry.Manifest.Name} files?\n\nYes: remove managed files and create a restore point.\nNo: keep files and stop managing them.\nCancel: make no changes.",
            "Remove managed artifact", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (answer == DialogResult.Cancel) return;
        var backup = await _sync.RemoveAsync(_document, reference, answer == DialogResult.Yes).ConfigureAwait(true);
        await new HarnessEnvironmentStore(_configStore.Path).SaveAsync(_document).ConfigureAwait(true);
        _staged.RemoveAll(item => SameArtifact(item, entry));
        PopulateDesired();
        _status.Text = backup is null ? "Stopped managing the artifact; local files were kept." :
            "Managed files removed. A restore point was created.";
    }

    private void DiscardPlan()
    {
        if (_staged.Count == 0) return;
        var answer = MessageBox.Show("Discard all pending installation changes? Currently installed capabilities will remain untouched.",
            "Discard installation plan", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        _staged.Clear();
        foreach (var reference in _document?.Artifacts.Where(item => item.Enabled) ?? [])
        {
            var installed = FindEntry(reference);
            if (installed is not null) _staged.Add(installed);
        }
        PopulateDesired();
        _status.Text = "Installation plan discarded. No files were changed.";
    }

    private async Task RestoreAsync()
    {
        if (_document is null) return;
        var profile = HarnessCatalog.CreateDefaultProfile(_document.HarnessId, _document.RootPath);
        profile.Scope = _document.Scope;
        var service = new HarnessRestoreService();
        var points = await service.ListAsync(profile).ConfigureAwait(true);
        if (points.Count == 0)
        {
            MessageBox.Show("No restore points are available for this environment.", "Harness Hub",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new HarnessRestoreDialog(points);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedPoint is null) return;
        var result = await service.RestoreAsync(dialog.SelectedPoint, profile).ConfigureAwait(true);
        _status.Text = $"Restored {result.RestoredFiles.Count} file(s) and removed {result.RemovedFiles.Count} created file(s).";
        await RefreshStatusAsync().ConfigureAwait(true);
    }

    private void PopulateDesired()
    {
        _desired.BeginUpdate();
        _installed.BeginUpdate();
        _desired.Items.Clear();
        _installed.Items.Clear();
        _planCards.Controls.Clear();
        _installedCards.Controls.Clear();
        var target = _document is null ? string.Empty :
            $"{HarnessCatalog.Get(_document.HarnessId).DisplayName} · {_document.Scope}";
        foreach (var entry in _staged)
        {
            var installed = _document?.Artifacts.FirstOrDefault(reference =>
                string.Equals(reference.RepositoryId, entry.RepositoryId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(reference.ArtifactId, entry.Manifest.Id, StringComparison.OrdinalIgnoreCase));
            if (installed is not null && string.Equals(installed.Version, entry.Manifest.Version, StringComparison.OrdinalIgnoreCase))
            {
                var current = new ListViewItem(entry.Manifest.Name) { Tag = entry };
                current.SubItems.Add(entry.Manifest.Type);
                current.SubItems.Add(installed.Version);
                current.SubItems.Add(target);
                _installed.Items.Add(current);
                continue;
            }
            var item = new ListViewItem(entry.Manifest.Name) { Tag = entry, Checked = true };
            item.SubItems.Add(entry.Manifest.Type);
            item.SubItems.Add(installed?.Version ?? "—");
            item.SubItems.Add(entry.Manifest.Version);
            item.SubItems.Add(target);
            item.SubItems.Add(installed is null ? "Install" : "Update");
            _desired.Items.Add(item);
            _planCards.Controls.Add(PlanRow(entry, installed, target));
        }
        foreach (ListViewItem item in _installed.Items)
            if (item.Tag is HarnessCatalogEntry installedEntry)
                _installedCards.Controls.Add(PlanRow(installedEntry, null, target));
        _desired.EndUpdate();
        _installed.EndUpdate();
        _planSummary.Text = _desired.Items.Count == 0
            ? "No pending changes. Select a capability from Catalog to prepare an installation plan."
            : $"{_desired.Items.Count} change(s) ready · target: {target} · review Preview before Apply.";
        UpdateEnabledState();
    }

    private void UpdateEnabledState()
    {
        var managed = SelectedEnvironmentConfig() is { Managed: true };
        _adopt.Enabled = _document is not null && !managed;
        _applyButton.Enabled = managed && _desired.Items.Count > 0;
        _applyButton.Text = _desired.Items.Count == 0 ? "Apply changes" : $"Apply {_desired.Items.Count} change(s)";
        _previewButton.Text = _desired.Items.Count == 0 ? "Review plan" : "Review plan";
        _previewButton.Enabled = _document is not null && _desired.Items.Count > 0;
        _removeButton.Enabled = managed && _desired.SelectedItems.Count > 0;
        _discardButton.Enabled = _desired.Items.Count > 0;
        _restoreButton.Enabled = managed;
    }

    private void ToggleBusy(bool busy)
    {
        _applyButton.Enabled = !busy;
        _previewButton.Enabled = !busy;
        _environment.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private HarnessEnvironmentConfig? SelectedEnvironmentConfig() =>
        _environment.SelectedIndex < 0 || _environment.SelectedIndex >= _manager.Environments.Count
            ? null : _manager.Environments[_environment.SelectedIndex];

    private HarnessCatalogEntry? FindEntry(HarnessArtifactReference reference) => _catalogEntries
        .Where(entry => string.Equals(entry.RepositoryId, reference.RepositoryId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(entry.Manifest.Id, reference.ArtifactId, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(entry => string.Equals(entry.Manifest.Version, reference.Version, StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(entry => Version.TryParse(entry.Manifest.Version, out var version) ? version : new Version())
        .FirstOrDefault();

    private static bool SameArtifact(HarnessCatalogEntry left, HarnessCatalogEntry right) =>
        string.Equals(left.RepositoryId, right.RepositoryId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Manifest.Id, right.Manifest.Id, StringComparison.OrdinalIgnoreCase);

    private void ConfigureLists()
    {
        _desired.Columns.Add("Artifact", 240);
        _desired.Columns.Add("Type", 110);
        _desired.Columns.Add("Current", 90);
        _desired.Columns.Add("Target", 90);
        _desired.Columns.Add("Destination", 220);
        _desired.Columns.Add("Action", 100);
        _installed.Columns.Add("Artifact", 240);
        _installed.Columns.Add("Type", 110);
        _installed.Columns.Add("Version", 90);
        _installed.Columns.Add("Harness / environment", 260);
        _preview.Columns.Add("Action", 100);
        _preview.Columns.Add("Path / setting", 380);
        _preview.Columns.Add("Details", 360);
        _desired.SelectedIndexChanged += (_, _) => UpdateEnabledState();
    }

    private static ZarpaComboBox Combo(int width) => new()
    {
        LabelText = string.Empty,
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = width
    };

    private static FlowLayoutPanel CardList() => new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Padding = new Padding(8)
    };

    private static ZarpaMultiSelectCard SetupRow(string title, string description, string iconKey) => new()
    {
        Width = 720,
        Height = 112,
        TitleText = title,
        DescriptionText = description,
        MetadataText = "Harness Hub · review in Preview before applying",
        BadgeText = title.StartsWith("Install", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith("Update", StringComparison.OrdinalIgnoreCase) ? "PLAN" : "ACTIVE",
        IconKey = iconKey,
        Selectable = false,
        Margin = new Padding(4)
    };

    private static ZarpaInstallPlanCard PlanRow(HarnessCatalogEntry entry, HarnessArtifactReference? item, string target) => new()
    {
        TitleText = entry.Manifest.Name,
        Operation = item is null ? "Installed" : string.Equals(item.Version, entry.Manifest.Version, StringComparison.OrdinalIgnoreCase) ? "Installed" : "Update",
        SourceVersion = item?.Version ?? entry.Manifest.Version,
        TargetVersion = entry.Manifest.Version,
        Source = entry.RepositoryName,
        Destination = target,
        Description = entry.Manifest.CapabilityDescription.Length == 0 ? entry.Manifest.Summary : entry.Manifest.CapabilityDescription,
        IconKey = item is null ? "ic_fluent_checkmark_circle_24_regular" : "ic_fluent_arrow_sync_24_regular",
        Installed = item is null,
        Margin = new Padding(4)
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
