using Hmy.Estao.Core.Configuration;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal sealed class RefreshSettingsPanel : Panel
{
    private readonly ZarpaToggleSwitch _enabled = new() { Text = "Refresh usage automatically", Width = 260 };
    private readonly ZarpaComboBox _interval = new() { LabelText = "Every", DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };

    public RefreshSettingsPanel()
    {
        Dock = DockStyle.Top;
        Height = 104;
        Padding = new Padding(6, 8, 6, 10);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Text = "Refresh",
            TextAlign = ContentAlignment.MiddleLeft
        };
        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        options.Controls.Add(_enabled);
        options.Controls.Add(_interval);
        Controls.Add(new ZarpaSettingsSectionSeparator());
        Controls.Add(options);
        Controls.Add(title);

        foreach (var minutes in RefreshIntervalCatalog.Minutes)
            _interval.Items.Add($"{minutes} minutes");
    }

    public void LoadConfig(EstaoConfig config)
    {
        _enabled.Checked = config.Refresh.Enabled;
        var value = $"{config.Refresh.IntervalMinutes} minutes";
        _interval.SelectedIndex = Math.Max(0, _interval.Items.IndexOf(value));
    }

    public void Apply(RefreshConfig config)
    {
        config.Enabled = _enabled.Checked;
        var selected = _interval.SelectedItem?.ToString() ?? "15 minutes";
        config.IntervalMinutes = int.TryParse(selected.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], out var minutes)
            ? minutes
            : 15;
    }
}
