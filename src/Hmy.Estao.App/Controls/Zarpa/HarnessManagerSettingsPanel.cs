using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Harnesses;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal sealed class HarnessManagerSettingsPanel : Panel, IZarpaThemeAware
{
    public event EventHandler? OpenHubRequested;
    private readonly HarnessHubService _hubService = new();
    private readonly HarnessPackageInstaller _installer = new();
    private readonly HarnessRestoreService _restoreService = new();
    private readonly ZarpaSettingsSection _hubSection = new("Department hub",
        "Use a shared network folder as a versioned catalog. Packages never include known literal credentials.");
    private readonly ZarpaSettingsSection _workspaceSection = new("Harness Hub workspace",
        "Open the dedicated catalog and My Setup workspace for artifact discovery, publishing, drift review, and synchronization.");
    private readonly ZarpaSettingsSection _profilesSection = new("Harness profiles",
        "Each harness has its own enable switch, scope, base path and feature checkboxes. Save changes to apply the profile.");
    private readonly ZarpaSettingsSection _publishSection = new("Publish a configuration",
        "Create an immutable .estao package from the enabled features of one harness profile.");
    private readonly ZarpaSettingsSection _packagesSection = new("Available packages",
        "Select a package to see every artifact and its install action. Then choose the target harness and features before installing.");
    private readonly ZarpaTextBox _hubPath = Field("\\\\server\\department\\estao-hub");
    private readonly ZarpaTextBox _author = Field(Environment.UserName);
    private readonly ZarpaComboBox _sourceHarness = Combo();
    private readonly ZarpaTextBox _packageId = Field("team-engineering-kit");
    private readonly ZarpaTextBox _packageName = Field("Team engineering kit");
    private readonly ZarpaTextBox _packageVersion = Field("1.0.0");
    private readonly ZarpaTextBox _packageDescription = Field("Shared instructions, skills and integrations");
    private readonly ZarpaComboBox _targetHarness = Combo();
    private readonly ZarpaToggleSwitch _targetEnabled = new() { Width = 150, Text = "Enabled" };
    private readonly ZarpaButton _publish = Button("Publish", ZarpaButtonStyle.Primary, 104);
    private readonly ZarpaButton _previewSource = Button("Scan source", ZarpaButtonStyle.Secondary, 112);
    private readonly ZarpaButton _refresh = Button("Refresh hub", ZarpaButtonStyle.Secondary, 112);
    private readonly ZarpaButton _download = Button("Download...", ZarpaButtonStyle.Secondary, 112);
    private readonly ZarpaButton _install = Button("Install", ZarpaButtonStyle.Primary, 100);
    private readonly ZarpaButton _restoreLast = Button("Restore...", ZarpaButtonStyle.Secondary, 112);
    private readonly ListView _packageList = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly ListView _sourcePreview = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly ListView _packageDetails = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly FlowLayoutPanel _targetFeatureList = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        AutoScroll = true,
        Margin = Padding.Empty,
        Padding = new Padding(2, 2, 2, 2)
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 42,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(4, 0, 4, 0)
    };
    private readonly Label _sourceStatus = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(4, 0, 4, 0),
        Text = "Scan the source to verify what will be published."
    };
    private readonly Dictionary<string, HarnessProfileEditor> _profileEditors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _targetFeatureChecks = new(StringComparer.OrdinalIgnoreCase);
    private string? _loadedTargetId;
    private ZarpaThemeTokens? _theme;

    public HarnessManagerSettingsPanel()
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = Padding.Empty;
        Padding = Padding.Empty;

        var openHub = Button("Open Harness Hub", ZarpaButtonStyle.Primary, 154);
        openHub.Click += (_, _) => OpenHubRequested?.Invoke(this, EventArgs.Empty);
        _workspaceSection.AddRow("Team workspace", "Catalog and desired-state management open in a resizable Zarpa window.", openHub, 240);

        var hubPathEditor = ZarpaSettingsLayout.Inline(_hubPath, BrowseButton(_hubPath));
        _hubSection.AddRow("Shared repository", "UNC paths and mapped network drives are supported.", hubPathEditor, 500);
        _hubSection.AddRow("Published by", "Shown in the package catalog so teammates know who owns the configuration.", _author, 360);

        foreach (var definition in HarnessCatalog.All)
        {
            var editor = new HarnessProfileEditor(definition);
            _profileEditors[definition.Id] = editor;
            _profilesSection.AddContent(editor, definition.SupportedFeatures.Count > 7 ? 178 : 154);
            _sourceHarness.Items.Add(definition.DisplayName);
            _targetHarness.Items.Add(definition.DisplayName);
        }
        _sourceHarness.SelectedIndex = 0;
        _targetHarness.SelectedIndex = Math.Max(0, _targetHarness.Items.IndexOf("GitHub Copilot"));
        ConfigureTargetFeatureList();

        _publishSection.AddRow("Source harness", "Only enabled feature types are collected.", _sourceHarness, 280);
        _publishSection.AddRow("Package identity", "The ID becomes the stable folder name in the shared catalog.",
            ZarpaSettingsLayout.Inline(_packageId, _packageVersion), 430);
        _publishSection.AddRow("Name", "A human-friendly catalog title.", _packageName, 430);
        _publishSection.AddRow("Description", "Explain the intended team, project or workflow.", _packageDescription, 430);
        var publishActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 8),
            Margin = Padding.Empty
        };
        publishActions.Controls.Add(_publish);
        publishActions.Controls.Add(_previewSource);
        _publishSection.AddContent(_sourcePreview, 220);
        _publishSection.AddContent(_sourceStatus, 38);
        _publishSection.AddContent(publishActions, 54);

        ConfigurePackageList();
        _packagesSection.AddRow("Install into", "Choose the destination harness. The Enabled switch and feature toggles below apply to this installation.",
            ZarpaSettingsLayout.Inline(_targetHarness, _targetEnabled), 420);
        _packagesSection.AddRow("Install features", "Only checked feature types will be written. Uncheck MCP, hooks or settings when you only want instructions and skills.",
            _targetFeatureList, 520);
        var packageBrowser = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 0) };
        var packageActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 4),
            Margin = Padding.Empty
        };
        packageActions.Controls.Add(_refresh);
        packageActions.Controls.Add(_download);
        packageActions.Controls.Add(_install);
        packageActions.Controls.Add(_restoreLast);
        var packageSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 150,
            FixedPanel = FixedPanel.Panel1,
            IsSplitterFixed = false,
            BorderStyle = BorderStyle.FixedSingle
        };
        packageSplit.Panel1.Controls.Add(_packageList);
        packageSplit.Panel2.Controls.Add(_packageDetails);
        packageBrowser.Controls.Add(packageSplit);
        packageBrowser.Controls.Add(_status);
        packageBrowser.Controls.Add(packageActions);
        _packagesSection.AddContent(packageBrowser, 480);

        Controls.Add(_packagesSection);
        Controls.Add(new ZarpaSettingsSectionSeparator());
        Controls.Add(_publishSection);
        Controls.Add(new ZarpaSettingsSectionSeparator());
        Controls.Add(_profilesSection);
        Controls.Add(new ZarpaSettingsSectionSeparator());
        Controls.Add(_hubSection);
        Controls.Add(new ZarpaSettingsSectionSeparator());
        Controls.Add(_workspaceSection);

        _publish.Click += async (_, _) => await PublishAsync().ConfigureAwait(true);
        _previewSource.Click += async (_, _) => await PreviewSourceAsync().ConfigureAwait(true);
        _refresh.Click += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        _download.Click += async (_, _) => await DownloadAsync().ConfigureAwait(true);
        _install.Click += async (_, _) => await InstallAsync().ConfigureAwait(true);
        _restoreLast.Click += async (_, _) => await RestoreLastAsync().ConfigureAwait(true);
        _packageList.SelectedIndexChanged += (_, _) => UpdatePackageSelection();
        _sourceHarness.SelectedIndexChanged += (_, _) =>
        {
            _sourcePreview.Items.Clear();
            _sourceStatus.Text = "Source changed. Scan again before publishing.";
        };
        _targetHarness.SelectedIndexChanged += (_, _) => LoadTargetFeatureList();
        _targetEnabled.CheckedChanged += TargetEnabledChanged;
        UpdatePackageSelection();
    }

    public void LoadConfig(EstaoConfig config)
    {
        _hubPath.Value = config.HarnessManager.HubPath;
        _author.Value = config.HarnessManager.Author;
        foreach (var profile in config.HarnessManager.Profiles)
            if (_profileEditors.TryGetValue(profile.Id, out var editor)) editor.Load(profile);
        LoadTargetFeatureList();
        _status.Text = string.IsNullOrWhiteSpace(_hubPath.Value)
            ? "Configure a shared folder, save Settings, then refresh the catalog."
            : "Ready to refresh the department catalog.";
    }

    public void Apply(HarnessManagerConfig config)
    {
        ApplyTargetFeatureList();
        config.HubPath = _hubPath.Value.Trim();
        config.Author = _author.Value.Trim();
        config.Profiles = HarnessCatalog.All.Select(item => _profileEditors[item.Id].ToConfig()).ToList();
        if (!string.IsNullOrWhiteSpace(config.HubPath))
        {
            var repository = config.Repositories.FirstOrDefault(item => string.Equals(
                item.Id, config.DefaultRepositoryId, StringComparison.OrdinalIgnoreCase))
                ?? config.Repositories.FirstOrDefault();
            if (repository is null)
            {
                repository = new HarnessRepositoryConfig
                {
                    Id = "company",
                    Name = "Company Hub",
                    Enabled = true
                };
                config.Repositories.Add(repository);
                config.DefaultRepositoryId = repository.Id;
            }
            repository.Path = config.HubPath;
        }
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _theme = value;
        BackColor = value.Canvas;
        ForeColor = value.Text;
        _packageList.BackColor = value.Surface;
        _packageList.ForeColor = value.Text;
        _sourcePreview.BackColor = value.Surface;
        _sourcePreview.ForeColor = value.Text;
        _packageDetails.BackColor = value.Surface;
        _packageDetails.ForeColor = value.Text;
        _status.BackColor = value.Canvas;
        _status.ForeColor = value.TextMuted;
        _sourceStatus.BackColor = value.Canvas;
        _sourceStatus.ForeColor = value.TextMuted;
        _targetFeatureList.BackColor = value.Canvas;
        _targetFeatureList.ForeColor = value.Text;
        foreach (var check in _targetFeatureChecks.Values)
        {
            check.BackColor = value.Canvas;
            check.ForeColor = value.Text;
        }
        foreach (var editor in _profileEditors.Values) editor.ApplyTheme(value);
    }

    private async Task PreviewSourceAsync()
    {
        var source = SelectedProfile(_sourceHarness);
        _previewSource.Loading = true;
        try
        {
            _status.Text = $"Scanning {HarnessCatalog.Get(source.Id).DisplayName} configuration...";
            var artifacts = await _hubService.PreviewAsync(source).ConfigureAwait(true);
            _sourcePreview.BeginUpdate();
            try
            {
                _sourcePreview.Items.Clear();
                foreach (var artifact in artifacts)
                {
                    var item = new ListViewItem(HarnessFeatureIds.DisplayName(artifact.Feature));
                    item.SubItems.Add(artifact.LogicalPath);
                    item.SubItems.Add(artifact.Size < 1024 ? $"{artifact.Size} B" : $"{artifact.Size / 1024D:0.#} KB");
                    item.SubItems.Add(artifact.Redacted ? "Secrets replaced" : "Included");
                    _sourcePreview.Items.Add(item);
                }
            }
            finally
            {
                _sourcePreview.EndUpdate();
            }

            var counts = string.Join(", ", artifacts.GroupBy(item => item.Feature)
                .Select(group => $"{HarnessFeatureIds.DisplayName(group.Key)}: {group.Count()}"));
            var settingsNote = source.Features.Contains(HarnessFeatureIds.Settings, StringComparer.OrdinalIgnoreCase)
                ? string.Empty
                : " Raw Settings are off, so model/UI/project preferences are not included.";
            _sourceStatus.Text = artifacts.Count == 0
                ? $"No publishable artifacts found.{settingsNote}"
                : $"Source preview: {artifacts.Count} artifact(s) · {counts}.{settingsNote}";
            _status.Text = _sourceStatus.Text;
        }
        catch (Exception exception)
        {
            _sourceStatus.Text = $"Source scan failed: {exception.Message}";
            ShowError("Source scan failed", exception);
        }
        finally
        {
            if (!IsDisposed) _previewSource.Loading = false;
        }
    }

    private async Task PublishAsync()
    {
        var source = SelectedProfile(_sourceHarness);
        _publish.Loading = true;
        try
        {
            _status.Text = $"Collecting {HarnessCatalog.Get(source.Id).DisplayName} artifacts...";
            var package = await _hubService.PublishAsync(_hubPath.Value, source,
                new HarnessPackageDraft(_packageId.Value, _packageName.Value, _packageDescription.Value,
                    _packageVersion.Value, _author.Value)).ConfigureAwait(true);
            _status.Text = $"Published {package.Manifest.Name} {package.Manifest.PackageVersion} with {package.Manifest.Artifacts.Count} artifacts.";
            await RefreshAsync(selectPath: package.Path).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowError("Publish failed", exception);
        }
        finally
        {
            if (!IsDisposed) _publish.Loading = false;
        }
    }

    private async Task RefreshAsync(string? selectPath = null)
    {
        _refresh.Loading = true;
        try
        {
            _status.Text = "Reading package manifests from the shared repository...";
            var packages = await _hubService.ListAsync(_hubPath.Value).ConfigureAwait(true);
            _packageList.BeginUpdate();
            try
            {
                _packageList.Items.Clear();
                foreach (var package in packages)
                {
                    var item = new ListViewItem(package.Manifest.Name) { Tag = package };
                    item.SubItems.Add(package.Manifest.PackageVersion);
                    item.SubItems.Add(HarnessCatalog.Get(package.Manifest.SourceHarness).DisplayName);
                    item.SubItems.Add(package.Manifest.Author);
                    item.SubItems.Add(package.Manifest.Artifacts.Count.ToString());
                    item.SubItems.Add(package.Manifest.PublishedUtc.LocalDateTime.ToString("g"));
                    _packageList.Items.Add(item);
                    if (string.Equals(package.Path, selectPath, StringComparison.OrdinalIgnoreCase)) item.Selected = true;
                }
            }
            finally
            {
                _packageList.EndUpdate();
            }
            _status.Text = packages.Count == 0 ? "The hub contains no valid packages yet." : $"{packages.Count} package(s) available.";
            UpdatePackageSelection();
        }
        catch (Exception exception)
        {
            ShowError("Refresh failed", exception);
        }
        finally
        {
            if (!IsDisposed) _refresh.Loading = false;
        }
    }

    private async Task DownloadAsync()
    {
        if (SelectedPackage() is not { } package) return;
        using var dialog = new SaveFileDialog
        {
            Filter = "Estao harness package (*.estao)|*.estao|All files (*.*)|*.*",
            FileName = Path.GetFileName(package.Path),
            Title = "Download harness package"
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        _download.Loading = true;
        try
        {
            await HarnessHubService.DownloadAsync(package, dialog.FileName).ConfigureAwait(true);
            _status.Text = $"Downloaded to {dialog.FileName}";
        }
        catch (Exception exception)
        {
            ShowError("Download failed", exception);
        }
        finally
        {
            if (!IsDisposed) _download.Loading = false;
        }
    }

    private async Task InstallAsync()
    {
        if (SelectedPackage() is not { } package) return;
        var target = SelectedTargetProfile();
        _install.Loading = true;
        try
        {
            _status.Text = $"Downloading and installing into {HarnessCatalog.Get(target.Id).DisplayName}...";
            var cached = await HarnessHubService.DownloadAsync(package).ConfigureAwait(true);
            var result = await _installer.InstallAsync(cached, target).ConfigureAwait(true);
            var summary = $"Installed {result.InstalledFiles.Count}; skipped {result.SkippedArtifacts.Count}; warnings {result.Warnings.Count}.";
            if (result.BackupDirectory is not null) summary += $" Backup: {result.BackupDirectory}";
            _status.Text = summary;
            MessageBox.Show(FindForm(), BuildInstallReport(result), "Harness package installed",
                MessageBoxButtons.OK, result.Warnings.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception exception)
        {
            ShowError("Install failed", exception);
        }
        finally
        {
            if (!IsDisposed) _install.Loading = false;
        }
    }

    private async Task RestoreLastAsync()
    {
        var target = SelectedTargetProfile();
        _restoreLast.Loading = true;
        try
        {
            var points = await _restoreService.ListAsync(target).ConfigureAwait(true);
            if (points.Count == 0)
            {
                MessageBox.Show(FindForm(),
                    $"No complete restore point exists yet for {HarnessCatalog.Get(target.Id).DisplayName}.\n\n" +
                    "Install a package with this version of Estao to create one.",
                    "Nothing to restore", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var point = ChooseRestorePoint(points);
            if (point is null) return;

            var overwritten = point.Manifest.Entries.Count(item => item.Existed);
            var created = point.Manifest.Entries.Count - overwritten;
            var legacyWarning = point.IsComplete
                ? string.Empty
                : "\n\nThis is a legacy partial backup. It can restore overwritten files, but cannot identify files that the old installation created.";
            var confirmation =
                $"Restore the state from before {point.Manifest.PackageName} {point.Manifest.PackageVersion}?\n\n" +
                $"Files restored: {overwritten}\n" +
                $"Files created by that installation and removed: {created}\n" +
                $"Created: {point.Manifest.CreatedUtc.LocalDateTime:g}{legacyWarning}";
            if (MessageBox.Show(FindForm(), confirmation, "Restore previous harness configuration",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            _status.Text = $"Restoring {HarnessCatalog.Get(target.Id).DisplayName}...";
            var result = await _restoreService.RestoreAsync(point, target).ConfigureAwait(true);
            _status.Text = $"Restore complete: {result.RestoredFiles.Count} restored, {result.RemovedFiles.Count} removed.";
            MessageBox.Show(FindForm(),
                $"Previous configuration restored.\n\nRestored files: {result.RestoredFiles.Count}\nRemoved installed files: {result.RemovedFiles.Count}",
                "Harness configuration restored", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            ShowError("Restore failed", exception);
        }
        finally
        {
            if (!IsDisposed) _restoreLast.Loading = false;
        }
    }

    private HarnessRestorePoint? ChooseRestorePoint(IReadOnlyList<HarnessRestorePoint> points)
    {
        using var dialog = new Form
        {
            Text = "Choose restore point",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            Width = 680,
            Height = 340,
            BackColor = _theme?.Canvas ?? BackColor,
            ForeColor = _theme?.Text ?? ForeColor
        };
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            Font = new Font("Segoe UI", 9F),
            BackColor = _theme?.Surface ?? SystemColors.Window,
            ForeColor = _theme?.Text ?? SystemColors.WindowText
        };
        foreach (var point in points)
            list.Items.Add($"{point.Manifest.CreatedUtc.LocalDateTime:g}   {point.Manifest.PackageName} {point.Manifest.PackageVersion}" +
                           (point.IsComplete ? string.Empty : "   (legacy partial backup)"));
        list.SelectedIndex = 0;
        var description = new Label
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(12, 10, 12, 4),
            Text = "Newest restore points are shown first. Complete points restore overwritten files and remove files created by that installation."
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
            WrapContents = false
        };
        var restore = new Button { Text = "Restore", DialogResult = DialogResult.OK, Width = 100, Height = 32 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 100, Height = 32 };
        actions.Controls.Add(restore);
        actions.Controls.Add(cancel);
        dialog.Controls.Add(list);
        dialog.Controls.Add(actions);
        dialog.Controls.Add(description);
        dialog.AcceptButton = restore;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(FindForm()) == DialogResult.OK && list.SelectedIndex >= 0
            ? points[list.SelectedIndex]
            : null;
    }

    private HarnessProfileConfig SelectedProfile(ZarpaComboBox picker)
    {
        var displayName = picker.SelectedItem?.ToString() ?? picker.Text;
        var definition = HarnessCatalog.All.FirstOrDefault(item => item.DisplayName == displayName) ?? HarnessCatalog.All[0];
        return _profileEditors[definition.Id].ToConfig();
    }

    private HarnessProfileConfig SelectedTargetProfile()
    {
        ApplyTargetFeatureList();
        return SelectedProfile(_targetHarness);
    }

    private HarnessHubPackage? SelectedPackage() => _packageList.SelectedItems.Count == 0
        ? null
        : _packageList.SelectedItems[0].Tag as HarnessHubPackage;

    private void UpdatePackageSelection()
    {
        var package = SelectedPackage();
        _download.Enabled = package is not null;
        _install.Enabled = package is not null && _targetEnabled.Checked && _targetFeatureChecks.Values.Any(check => check.Checked);
        _packageDetails.BeginUpdate();
        try
        {
            _packageDetails.Items.Clear();
            if (package is null)
            {
                _status.Text = "Select a package to review its contents before installing.";
                return;
            }

            foreach (var artifact in package.Manifest.Artifacts)
            {
                var item = new ListViewItem(HarnessFeatureIds.DisplayName(artifact.Feature))
                {
                    ToolTipText = artifact.LogicalPath
                };
                item.SubItems.Add(artifact.LogicalPath);
                item.SubItems.Add(DescribeInstallAction(package.Manifest, artifact));
                _packageDetails.Items.Add(item);
            }

            var source = HarnessCatalog.Get(package.Manifest.SourceHarness).DisplayName;
            var target = _targetHarness.SelectedItem?.ToString() ?? "target harness";
            var enabled = _targetFeatureChecks.Values.Count(check => check.Checked);
            _status.Text = $"{package.Manifest.Name} {package.Manifest.PackageVersion} · from {source} · {package.Manifest.Artifacts.Count} artifacts · {package.Size / 1024D:0.#} KB · {enabled} target feature(s) enabled for {target}.";
        }
        finally
        {
            _packageDetails.EndUpdate();
        }
    }

    private string DescribeInstallAction(HarnessPackageManifest manifest, HarnessPackageArtifact artifact)
    {
        var target = HarnessCatalog.All.FirstOrDefault(item => item.DisplayName == _targetHarness.SelectedItem?.ToString())?.Id
            ?? string.Empty;
        if (artifact.Redacted) return "Review secret placeholder";
        if (string.Equals(manifest.SourceHarness, target, StringComparison.OrdinalIgnoreCase)) return "Copy to target";
        return artifact.Feature switch
        {
            HarnessFeatureIds.Agents or HarnessFeatureIds.Mcp => "Convert to target format",
            HarnessFeatureIds.Prompts or HarnessFeatureIds.Instructions => "Rename / adapt for target",
            HarnessFeatureIds.Skills => "Copy portable skill",
            HarnessFeatureIds.Rules or HarnessFeatureIds.Plugins => "Skip outside Codex",
            HarnessFeatureIds.Hooks or HarnessFeatureIds.Settings => "Skip across harnesses",
            _ => "Review before install"
        };
    }

    private void ShowError(string title, Exception exception)
    {
        _status.Text = $"{title}: {exception.Message}";
        MessageBox.Show(FindForm(), exception.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static string BuildInstallReport(HarnessInstallResult result)
    {
        var lines = new List<string>
        {
            $"Installed files: {result.InstalledFiles.Count}",
            $"Skipped artifacts: {result.SkippedArtifacts.Count}"
        };
        if (result.BackupDirectory is not null) lines.Add($"Backup: {result.BackupDirectory}");
        if (result.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Review notes:");
            lines.AddRange(result.Warnings.Select(warning => $"• {warning}"));
        }
        if (result.SkippedArtifacts.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Skipped:");
            lines.AddRange(result.SkippedArtifacts.Take(8).Select(item => $"• {item}"));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void ConfigurePackageList()
    {
        _sourcePreview.Columns.Add("Feature", 130);
        _sourcePreview.Columns.Add("Source artifact", 430);
        _sourcePreview.Columns.Add("Size", 80);
        _sourcePreview.Columns.Add("Status", 130);
        _packageList.Columns.Add("Package", 220);
        _packageList.Columns.Add("Version", 82);
        _packageList.Columns.Add("Source", 115);
        _packageList.Columns.Add("Author", 130);
        _packageList.Columns.Add("Items", 55);
        _packageList.Columns.Add("Published", 125);
        _packageDetails.Columns.Add("Feature", 130);
        _packageDetails.Columns.Add("Artifact path", 420);
        _packageDetails.Columns.Add("Handling", 160);
    }

    private void ConfigureTargetFeatureList()
    {
        foreach (var feature in HarnessFeatureIds.All)
        {
            var check = new CheckBox
            {
                Text = HarnessFeatureIds.DisplayName(feature),
                AutoSize = true,
                Checked = !string.Equals(feature, HarnessFeatureIds.Settings, StringComparison.Ordinal),
                Margin = new Padding(0, 2, 16, 2)
            };
            check.CheckedChanged += (_, _) => UpdatePackageSelection();
            _targetFeatureChecks[feature] = check;
            _targetFeatureList.Controls.Add(check);
        }
    }

    private void LoadTargetFeatureList()
    {
        if (_loadedTargetId is not null) ApplyTargetFeatureList();
        if (_targetHarness.SelectedItem is not string displayName) return;
        var definition = HarnessCatalog.All.FirstOrDefault(item => item.DisplayName == displayName);
        if (definition is null || !_profileEditors.TryGetValue(definition.Id, out var editor)) return;

        _targetEnabled.CheckedChanged -= TargetEnabledChanged;
        _targetEnabled.Checked = editor.IsEnabled;
        _targetEnabled.CheckedChanged += TargetEnabledChanged;
        foreach (var feature in HarnessFeatureIds.All)
        {
            if (_targetFeatureChecks.TryGetValue(feature, out var check))
            {
                check.Enabled = HarnessCatalog.Supports(definition.Id, feature);
                check.Checked = editor.IsFeatureEnabled(feature);
            }
        }
        _loadedTargetId = definition.Id;
        UpdatePackageSelection();
    }

    private void ApplyTargetFeatureList()
    {
        var selectedId = _loadedTargetId;
        if (selectedId is null && _targetHarness.SelectedItem is string displayName)
            selectedId = HarnessCatalog.All.FirstOrDefault(item => item.DisplayName == displayName)?.Id;
        if (selectedId is null) return;
        var definition = HarnessCatalog.All.FirstOrDefault(item => item.Id == selectedId);
        if (definition is null || !_profileEditors.TryGetValue(definition.Id, out var editor)) return;
        editor.SetEnabled(_targetEnabled.Checked);
        editor.SetFeatures(_targetFeatureChecks.Where(item => item.Value.Checked).Select(item => item.Key));
    }

    private void TargetEnabledChanged(object? sender, EventArgs e) => UpdatePackageSelection();

    private static ZarpaButton BrowseButton(ZarpaTextBox target)
    {
        var button = Button("Browse...", ZarpaButtonStyle.Secondary, 92);
        button.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select folder",
                SelectedPath = Directory.Exists(Environment.ExpandEnvironmentVariables(target.Value))
                    ? Environment.ExpandEnvironmentVariables(target.Value)
                    : string.Empty,
                ShowNewFolderButton = true
            };
            if (dialog.ShowDialog() == DialogResult.OK) target.Value = dialog.SelectedPath;
        };
        return button;
    }

    private static ZarpaTextBox Field(string placeholder) => new()
    {
        LabelText = string.Empty,
        Placeholder = placeholder,
        Width = 300,
        Margin = new Padding(0, 2, 8, 2)
    };

    private static ZarpaComboBox Combo() => new()
    {
        LabelText = string.Empty,
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 260
    };

    private static ZarpaButton Button(string text, ZarpaButtonStyle style, int width) => new()
    {
        Text = text,
        ButtonStyle = style,
        Width = width,
        Height = 34,
        Margin = new Padding(0, 2, 8, 2)
    };

    private sealed class HarnessProfileEditor : Panel, IZarpaThemeAware
    {
        private readonly HarnessDefinition _definition;
        private readonly Label _title;
        private readonly Label _description;
        private readonly ZarpaToggleSwitch _enabled = new() { Width = 58, Text = string.Empty };
        private readonly ZarpaComboBox _scope = Combo();
        private readonly ZarpaTextBox _basePath = Field("C:\\Users\\name or repository root");
        private readonly Dictionary<string, CheckBox> _features = new(StringComparer.OrdinalIgnoreCase);

        public HarnessProfileEditor(HarnessDefinition definition)
        {
            _definition = definition;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            Padding = new Padding(0, 5, 0, 5);
            _scope.Width = 128;
            _scope.Items.Add("Personal");
            _scope.Items.Add("Project");
            _scope.SelectedIndex = 0;
            _basePath.Width = 430;

            var header = new Panel { Dock = DockStyle.Top, Height = 45, Margin = Padding.Empty };
            _title = new Label
            {
                Dock = DockStyle.Top,
                Height = 23,
                Text = definition.DisplayName,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                TextAlign = ContentAlignment.BottomLeft
            };
            _description = new Label
            {
                Dock = DockStyle.Fill,
                Text = definition.Description,
                TextAlign = ContentAlignment.TopLeft,
                AutoEllipsis = true
            };
            var toggleHost = new Panel { Dock = DockStyle.Right, Width = 68, Padding = new Padding(5, 7, 5, 4) };
            toggleHost.Controls.Add(_enabled);
            header.Controls.Add(_description);
            header.Controls.Add(_title);
            header.Controls.Add(toggleHost);

            var pathRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 48,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = new Padding(0, 3, 0, 3)
            };
            pathRow.Controls.Add(_scope);
            pathRow.Controls.Add(_basePath);
            pathRow.Controls.Add(BrowseButton(_basePath));

            var featureRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = Padding.Empty,
                Padding = new Padding(2, 7, 0, 0)
            };
            foreach (var feature in definition.SupportedFeatures)
            {
                var check = new CheckBox
                {
                    Text = HarnessFeatureIds.DisplayName(feature),
                    AutoSize = true,
                    Checked = !string.Equals(feature, HarnessFeatureIds.Settings, StringComparison.Ordinal),
                    Margin = new Padding(0, 2, 16, 2)
                };
                _features[feature] = check;
                featureRow.Controls.Add(check);
            }

            Controls.Add(featureRow);
            Controls.Add(pathRow);
            Controls.Add(header);
        }

        public void Load(HarnessProfileConfig profile)
        {
            _enabled.Checked = profile.Enabled;
            _scope.SelectedIndex = string.Equals(profile.Scope, "project", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _basePath.Value = profile.BasePath;
            foreach (var feature in _features)
                feature.Value.Checked = profile.Features.Contains(feature.Key, StringComparer.OrdinalIgnoreCase);
        }

        public bool IsEnabled => _enabled.Checked;

        public bool IsFeatureEnabled(string feature) =>
            _features.TryGetValue(feature, out var check) && check.Checked;

        public void SetEnabled(bool enabled) => _enabled.Checked = enabled;

        public void SetFeatures(IEnumerable<string> enabledFeatures)
        {
            var enabled = enabledFeatures.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var feature in _features)
                feature.Value.Checked = enabled.Contains(feature.Key);
        }

        public HarnessProfileConfig ToConfig() => new()
        {
            Id = _definition.Id,
            Enabled = _enabled.Checked,
            Scope = _scope.SelectedIndex == 1 ? "project" : "personal",
            BasePath = _basePath.Value.Trim(),
            Features = _features.Where(item => item.Value.Checked).Select(item => item.Key).ToList()
        };

        public void ApplyTheme(ZarpaThemeTokens value)
        {
            BackColor = value.Canvas;
            ForeColor = value.Text;
            _title.ForeColor = value.Text;
            _description.ForeColor = value.TextMuted;
            foreach (var check in _features.Values)
            {
                check.BackColor = value.Canvas;
                check.ForeColor = value.Text;
            }
        }
    }
}
