using Hmy.Estao.Core.Configuration;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal sealed class TaskbarOverlaySettingsPanel : Panel
{
    private readonly ZarpaToggleSwitch _enabled = new() { Text = "Show usage on the Windows taskbar", Width = 330 };
    private readonly ZarpaComboBox _displayMode = new() { LabelText = "Identity", DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly ZarpaComboBox _size = new() { LabelText = "Size", DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly FlowLayoutPanel _providers = new() { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = false, Margin = Padding.Empty };
    private readonly FlowLayoutPanel _controls = new() { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = false, Margin = Padding.Empty };
    private readonly Dictionary<string, ZarpaCheckBox> _providerChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ZarpaCheckBox> _controlChecks = new(StringComparer.OrdinalIgnoreCase);

    public TaskbarOverlaySettingsPanel()
    {
        Height = 360;
        Dock = DockStyle.Top;
        Padding = new Padding(6, 8, 6, 10);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 25,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Text = "Taskbar overlay",
            TextAlign = ContentAlignment.MiddleLeft
        };
        var helper = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Text = "Shown in the free taskbar area; hidden automatically when space is limited.",
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            AutoSize = false
        };
        var header = new Panel { Dock = DockStyle.Top, Height = 49 };
        header.Controls.Add(_enabled);

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 70,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        options.Controls.Add(_displayMode);
        options.Controls.Add(_size);

        var providersLabel = SectionLabel("Providers");
        var controlsLabel = SectionLabel("Modules");
        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        rows.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rows.Controls.Add(providersLabel, 0, 0);
        rows.Controls.Add(controlsLabel, 1, 0);
        rows.Controls.Add(_providers, 0, 1);
        rows.Controls.Add(_controls, 1, 1);

        Controls.Add(rows);
        Controls.Add(options);
        Controls.Add(header);
        Controls.Add(helper);
        Controls.Add(title);

        BuildChecks();
        foreach (var mode in TaskbarOverlayDisplayCatalog.DisplayModes) _displayMode.Items.Add(mode);
        foreach (var size in TaskbarOverlayDisplayCatalog.Sizes) _size.Items.Add(size);
    }

    public void LoadConfig(EstaoConfig config)
    {
        _enabled.Checked = config.TaskbarOverlay.Enabled;
        _displayMode.SelectedIndex = Math.Max(0, _displayMode.Items.IndexOf(config.TaskbarOverlay.DisplayMode));
        _size.SelectedIndex = Math.Max(0, _size.Items.IndexOf(config.TaskbarOverlay.Size));
        var configuredProviders = config.TaskbarOverlay.ProviderIds;
        foreach (var (id, check) in _providerChecks)
        {
            var providerIsEnabled = config.Providers.Any(provider =>
                string.Equals(ProviderCatalog.NormalizeId(provider.Id), id, StringComparison.OrdinalIgnoreCase) && provider.Enabled == true);
            check.Checked = configuredProviders.Count == 0 ? providerIsEnabled : configuredProviders.Contains(id, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var (id, check) in _controlChecks)
            check.Checked = config.TaskbarOverlay.Controls.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    public void Apply(TaskbarOverlayConfig config)
    {
        config.Enabled = _enabled.Checked;
        config.DisplayMode = _displayMode.SelectedItem?.ToString() ?? "icon-title";
        config.Size = _size.SelectedItem?.ToString() ?? "normal";
        config.ProviderIds = _providerChecks.Where(item => item.Value.Checked).Select(item => item.Key).ToList();
        config.Controls = _controlChecks.Where(item => item.Value.Checked).Select(item => item.Key).ToList();
        if (config.Controls.Count == 0)
            config.Controls = TaskbarOverlayControlCatalog.Default.ToList();
    }

    private void BuildChecks()
    {
        foreach (var provider in ProviderCatalog.InitialProviderIds)
        {
            var check = new ZarpaCheckBox
            {
                Text = ProviderCatalog.DisplayName(provider),
                Width = 150,
                Height = 31,
                Margin = new Padding(0, 0, 4, 0)
            };
            _providerChecks[provider] = check;
            _providers.Controls.Add(check);
        }

        foreach (var control in TaskbarOverlayControlCatalog.All)
        {
            var check = new ZarpaCheckBox
            {
                Text = TaskbarOverlayControlCatalog.DisplayName(control),
                Width = 126,
                Height = 31,
                Margin = new Padding(0, 0, 4, 0)
            };
            _controlChecks[control] = check;
            _controls.Controls.Add(check);
        }
    }

    private static Label SectionLabel(string text) => new()
    {
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 8F, FontStyle.Bold),
        Text = text,
        TextAlign = ContentAlignment.BottomLeft
    };
}
