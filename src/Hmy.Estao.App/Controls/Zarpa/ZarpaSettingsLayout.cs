using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

/// <summary>
/// Shared layout primitives for the settings window. Sections own their
/// spacing and ordering so feature panels only describe their content.
/// </summary>
internal static class ZarpaSettingsMetrics
{
    public const int SectionHorizontalPadding = 6;
    public const int SectionTopPadding = 10;
    public const int SectionBottomPadding = 14;
    public const int SectionHeaderHeight = 56;
    public const int SectionHeaderWithoutDescriptionHeight = 36;
    public const int StandardRowHeight = 68;
    public const int FieldRowHeight = 76;
    public const int CompactRowHeight = 52;
    public const int TableHeaderHeight = 28;
    public const int TableRowHeight = 62;
    public const int ContentGap = 8;
}

internal class ZarpaSettingsSection : Panel, IZarpaThemeAware
{
    private readonly TableLayoutPanel _layout;
    private readonly Label _title;
    private readonly Label _description;
    private int _nextRow;

    public ZarpaSettingsSection(string title, string description = "")
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = Padding.Empty;
        Padding = new Padding(
            ZarpaSettingsMetrics.SectionHorizontalPadding,
            ZarpaSettingsMetrics.SectionTopPadding,
            ZarpaSettingsMetrics.SectionHorizontalPadding,
            ZarpaSettingsMetrics.SectionBottomPadding);
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = title;

        _layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var header = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Height = string.IsNullOrWhiteSpace(description)
                ? ZarpaSettingsMetrics.SectionHeaderWithoutDescriptionHeight
                : ZarpaSettingsMetrics.SectionHeaderHeight
        };
        _title = new Label
        {
            Dock = DockStyle.Top,
            Height = 27,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Text = title,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _description = new Label
        {
            Dock = DockStyle.Fill,
            Text = description,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true
        };
        header.Controls.Add(_description);
        header.Controls.Add(_title);
        AddLayoutControl(header, header.Height);

        Controls.Add(_layout);
    }

    public void AddRow(string title, string description, Control editor, int editorWidth = 320) =>
        AddContent(new ZarpaSettingsRow(title, description, editor, editorWidth),
            string.IsNullOrWhiteSpace(description)
                ? ZarpaSettingsMetrics.CompactRowHeight
                : ZarpaSettingsMetrics.StandardRowHeight);

    public void AddContent(Control content, int height)
    {
        content.Dock = DockStyle.Fill;
        content.Margin = Padding.Empty;
        AddLayoutControl(content, height);
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        BackColor = value.Canvas;
        ForeColor = value.Text;
        _layout.BackColor = value.Canvas;
        _title.ForeColor = value.Text;
        _description.ForeColor = value.TextMuted;
    }

    private void AddLayoutControl(Control control, int height)
    {
        _layout.RowCount = _nextRow + 1;
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        _layout.Controls.Add(control, 0, _nextRow++);
    }
}

internal sealed class ZarpaSettingsRow : TableLayoutPanel, IZarpaThemeAware
{
    private readonly Label _title;
    private readonly Label _description;

    public ZarpaSettingsRow(string title, string description, Control editor, int editorWidth)
    {
        ColumnCount = 2;
        RowCount = 1;
        Margin = Padding.Empty;
        Padding = new Padding(0, 4, 0, 4);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, editorWidth));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var copy = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _title = new Label
        {
            Dock = string.IsNullOrWhiteSpace(description) ? DockStyle.Fill : DockStyle.Top,
            Height = 28,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Text = title,
            TextAlign = string.IsNullOrWhiteSpace(description)
                ? ContentAlignment.MiddleLeft
                : ContentAlignment.BottomLeft,
            AutoEllipsis = true
        };
        _description = new Label
        {
            Dock = DockStyle.Fill,
            Text = description,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true
        };
        if (!string.IsNullOrWhiteSpace(description)) copy.Controls.Add(_description);
        copy.Controls.Add(_title);

        editor.Dock = DockStyle.Fill;
        editor.Margin = new Padding(ZarpaSettingsMetrics.ContentGap, 2, 0, 2);
        Controls.Add(copy, 0, 0);
        Controls.Add(editor, 1, 0);
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        BackColor = value.Canvas;
        ForeColor = value.Text;
        _title.ForeColor = value.Text;
        _description.ForeColor = value.TextMuted;
    }
}

internal static class ZarpaSettingsLayout
{
    public static FlowLayoutPanel Inline(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        foreach (var control in controls)
        {
            control.Margin = new Padding(0, 2, ZarpaSettingsMetrics.ContentGap, 2);
            panel.Controls.Add(control);
        }
        return panel;
    }
}

internal sealed class ZarpaSettingsFooter : Panel, IZarpaThemeAware
{
    private Color _border = Color.FromArgb(67, 70, 78);

    public ZarpaSettingsFooter()
    {
        Dock = DockStyle.Bottom;
        Height = 70;
        Padding = new Padding(22, 12, 22, 12);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        BackColor = value.Surface;
        ForeColor = value.Text;
        _border = value.Border;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(_border);
        e.Graphics.DrawLine(pen, 0, 0, Math.Max(0, Width - 1), 0);
    }
}

internal sealed class ZarpaSettingsFooterActions : FlowLayoutPanel, IZarpaThemeAware
{
    public ZarpaSettingsFooterActions()
    {
        Dock = DockStyle.Right;
        Width = 360;
        FlowDirection = FlowDirection.RightToLeft;
        WrapContents = false;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        BackColor = value.Surface;
        ForeColor = value.Text;
    }
}
