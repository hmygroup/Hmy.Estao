using Hmy.Estao.Core.Configuration;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal sealed class RefreshSettingsPanel : ZarpaSettingsSection
{
    private readonly ZarpaToggleSwitch _enabled = new() { Text = string.Empty, Width = 58 };
    private readonly ZarpaComboBox _interval = new() { LabelText = string.Empty, DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };

    public RefreshSettingsPanel() : base("Data refresh", "Control how often provider usage is updated in the background.")
    {
        foreach (var minutes in RefreshIntervalCatalog.Minutes)
            _interval.Items.Add($"{minutes} minutes");

        AddRow("Automatic refresh", "Keep usage data current without opening the popover.",
            ZarpaSettingsLayout.Inline(_enabled, _interval), 230);
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
