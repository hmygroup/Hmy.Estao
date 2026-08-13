using Hmy.Estao.Core.Configuration;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal sealed class UsageColorsSettingsPanel : ZarpaSettingsSection
{
    private readonly Dictionary<string, ProviderUsageColorRow> _rows = new(StringComparer.OrdinalIgnoreCase);

    public UsageColorsSettingsPanel() : base("Usage alerts",
        "Choose when each provider changes to warning or critical and which colors are shown.")
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = ProviderCatalog.InitialProviderIds.Length + 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 19F));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, ZarpaSettingsMetrics.TableHeaderHeight));
        foreach (var _ in ProviderCatalog.InitialProviderIds)
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, ZarpaSettingsMetrics.TableRowHeight));

        AddHeader(table, "Provider", 0);
        AddHeader(table, "Custom", 1);
        AddHeader(table, "Warning at", 2);
        AddHeader(table, "Warning color", 3);
        AddHeader(table, "Critical at", 4);
        AddHeader(table, "Critical color", 5);

        for (var index = 0; index < ProviderCatalog.InitialProviderIds.Length; index++)
        {
            var provider = ProviderCatalog.InitialProviderIds[index];
            var row = new ProviderUsageColorRow(provider);
            _rows[provider] = row;
            row.AddTo(table, index + 1);
        }

        AddContent(table, ZarpaSettingsMetrics.TableHeaderHeight +
            ProviderCatalog.InitialProviderIds.Length * ZarpaSettingsMetrics.TableRowHeight);
    }

    public void LoadConfig(EstaoConfig config)
    {
        foreach (var (provider, row) in _rows)
        {
            var configured = config.Providers.First(item =>
                string.Equals(ProviderCatalog.NormalizeId(item.Id), provider, StringComparison.OrdinalIgnoreCase));
            row.Load(configured.UsageColors);
        }
    }

    public void Apply(EstaoConfig config)
    {
        foreach (var (provider, row) in _rows)
        {
            var configured = config.Providers.First(item =>
                string.Equals(ProviderCatalog.NormalizeId(item.Id), provider, StringComparison.OrdinalIgnoreCase));
            row.Apply(configured.UsageColors);
        }
    }

    private static void AddHeader(TableLayoutPanel table, string text, int column) => table.Controls.Add(new Label
    {
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 8F, FontStyle.Bold),
        Text = text,
        TextAlign = ContentAlignment.BottomLeft,
        Margin = new Padding(5, 0, 5, 0)
    }, column, 0);
}

internal sealed class ProviderUsageColorRow
{
    private readonly string _provider;
    private readonly ProviderUsageColorIdentity _identity;
    private readonly ZarpaToggleSwitch _enabled = new() { Dock = DockStyle.Fill, Text = string.Empty };
    private readonly ZarpaNumericUpDown _warning = PercentageField();
    private readonly UsageColorButton _warningColor = new(UsageColorCatalog.DefaultWarningColor);
    private readonly ZarpaNumericUpDown _critical = PercentageField();
    private readonly UsageColorButton _criticalColor = new(UsageColorCatalog.DefaultCriticalColor);

    public ProviderUsageColorRow(string provider)
    {
        _provider = provider;
        _identity = new ProviderUsageColorIdentity(provider) { Dock = DockStyle.Fill };
        _warning.Maximum = (decimal)UsageColorCatalog.MaximumWarningPercent;
        _warning.ValueChanged += (_, _) =>
        {
            if (_critical.Value <= _warning.Value) _critical.Value = _warning.Value + 1;
            _critical.Minimum = _warning.Value + 1;
        };
        _enabled.CheckedChanged += (_, _) => UpdateEnabledState();
    }

    public void AddTo(TableLayoutPanel table, int row)
    {
        table.Controls.Add(_identity, 0, row);
        table.Controls.Add(_enabled, 1, row);
        table.Controls.Add(_warning, 2, row);
        table.Controls.Add(_warningColor, 3, row);
        table.Controls.Add(_critical, 4, row);
        table.Controls.Add(_criticalColor, 5, row);
    }

    public void Load(UsageColorConfig config)
    {
        _enabled.Checked = config.Enabled;
        _warning.Value = (decimal)Math.Clamp(config.WarningPercent, 0D, UsageColorCatalog.MaximumWarningPercent);
        _critical.Minimum = _warning.Value + 1;
        _critical.Value = (decimal)Math.Clamp(config.CriticalPercent, (double)_critical.Minimum, 100D);
        _warningColor.HexColor = config.WarningColor;
        _criticalColor.HexColor = config.CriticalColor;
        UpdateEnabledState();
    }

    public void Apply(UsageColorConfig config)
    {
        config.Enabled = _enabled.Checked;
        config.WarningPercent = (double)_warning.Value;
        config.WarningColor = _warningColor.HexColor;
        config.CriticalPercent = (double)_critical.Value;
        config.CriticalColor = _criticalColor.HexColor;
    }

    private void UpdateEnabledState()
    {
        _warning.Enabled = _enabled.Checked;
        _warningColor.Enabled = _enabled.Checked;
        _critical.Enabled = _enabled.Checked;
        _criticalColor.Enabled = _enabled.Checked;
    }

    private static ZarpaNumericUpDown PercentageField() => new()
    {
        Dock = DockStyle.Fill,
        LabelText = string.Empty,
        Minimum = 0,
        Maximum = 100,
        Increment = 1,
        DecimalPlaces = 0,
        Suffix = "%",
        Margin = new Padding(5, 5, 5, 5)
    };
}

internal sealed class ProviderUsageColorIdentity(string provider) : Control, IZarpaThemeAware
{
    private ZarpaThemeTokens? _theme;

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _theme = value;
        BackColor = value.Canvas;
        ForeColor = value.Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var iconSize = Math.Min(20, Height - 12);
        var icon = new Rectangle(5, (Height - iconSize) / 2, iconSize, iconSize);
        ZarpaProviderIconCatalog.TryDraw(e.Graphics, provider, icon, _theme?.Text ?? ForeColor);
        TextRenderer.DrawText(e.Graphics, ProviderCatalog.DisplayName(provider), Font,
            new Rectangle(icon.Right + 6, 0, Math.Max(1, Width - icon.Right - 8), Height),
            _theme?.Text ?? ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class UsageColorButton : Control, IZarpaThemeAware
{
    private ZarpaThemeTokens? _theme;
    private bool _hot;
    private string _hexColor;

    public UsageColorButton(string color)
    {
        _hexColor = UsageColorCatalog.NormalizeColor(color, UsageColorCatalog.DefaultWarningColor);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        TabStop = true;
        Dock = DockStyle.Fill;
        Margin = new Padding(5, 9, 5, 9);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string HexColor
    {
        get => _hexColor;
        set
        {
            _hexColor = UsageColorCatalog.NormalizeColor(value, _hexColor);
            AccessibleName = $"Select color {_hexColor}";
            Invalidate();
        }
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _theme = value;
        BackColor = value.Canvas;
        ForeColor = value.Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var surface = _theme?.SurfaceRaised ?? SystemColors.Control;
        var border = _hot || Focused
            ? _theme?.Accent ?? SystemColors.Highlight
            : _theme?.Border ?? SystemColors.ControlDark;
        var text = Enabled ? _theme?.Text ?? SystemColors.ControlText : _theme?.TextMuted ?? SystemColors.GrayText;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        ZarpaPopoverPaint.FillRounded(e.Graphics, surface, bounds, 7);
        using (var outline = new Pen(border))
        using (var path = ZarpaPopoverPaint.RoundedPath(bounds, 7))
            e.Graphics.DrawPath(outline, path);
        var swatchSize = Math.Min(20, Height - 10);
        var swatch = new Rectangle(7, (Height - swatchSize) / 2, swatchSize, swatchSize);
        using (var brush = new SolidBrush(ColorTranslator.FromHtml(_hexColor))) e.Graphics.FillEllipse(brush, swatch);
        TextRenderer.DrawText(e.Graphics, _hexColor, Font,
            new Rectangle(swatch.Right + 6, 0, Math.Max(1, Width - swatch.Right - 9), Height), text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Enabled) Focus(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (Enabled && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) PickColor();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled || e.KeyCode is not (Keys.Enter or Keys.Space)) return;
        PickColor();
        e.Handled = true;
    }

    private void PickColor()
    {
        using var dialog = new ColorDialog { Color = ColorTranslator.FromHtml(_hexColor), FullOpen = true };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        HexColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }
}
