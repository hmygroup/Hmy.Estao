using Hmy.Estao.Core.Configuration;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal sealed class PacingSettingsPanel : Panel
{
    private readonly ZarpaToggleSwitch _enabled = new() { Text = "Warn when I burn a rate limit faster than my daily target", Width = 420 };
    private readonly ZarpaNumericUpDown _dailyTarget = new()
    {
        LabelText = "Daily target",
        Minimum = (decimal)PacingCatalog.MinDailyTargetPercent,
        Maximum = (decimal)PacingCatalog.MaxDailyTargetPercent,
        Increment = 1M,
        DecimalPlaces = 0,
        Suffix = "%/day",
        Width = 150
    };
    private readonly ZarpaToggleSwitch _notify = new() { Text = "Show a tray notification the first time I go over", Width = 420 };

    public PacingSettingsPanel()
    {
        Dock = DockStyle.Top;
        Height = 168;
        Padding = new Padding(6, 8, 6, 10);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Text = "Daily pacing",
            TextAlign = ContentAlignment.MiddleLeft
        };
        var helper = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = "Draws a dashed target line on the usage chart based on a steady daily budget.",
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            AutoSize = false
        };
        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 78,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        options.Controls.Add(_enabled);
        options.Controls.Add(_dailyTarget);

        var notifyRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        notifyRow.Controls.Add(_notify);

        Controls.Add(new ZarpaSettingsSectionSeparator());
        Controls.Add(notifyRow);
        Controls.Add(options);
        Controls.Add(helper);
        Controls.Add(title);
    }

    public void LoadConfig(EstaoConfig config)
    {
        _enabled.Checked = config.Pacing.Enabled;
        _dailyTarget.Value = (decimal)Math.Clamp(config.Pacing.DailyTargetPercent,
            PacingCatalog.MinDailyTargetPercent, PacingCatalog.MaxDailyTargetPercent);
        _notify.Checked = config.Pacing.NotifyOnExceed;
    }

    public void Apply(PacingConfig config)
    {
        config.Enabled = _enabled.Checked;
        config.DailyTargetPercent = (double)_dailyTarget.Value;
        config.NotifyOnExceed = _notify.Checked;
    }
}
