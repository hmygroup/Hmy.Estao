using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal sealed class ZarpaSettingsSectionSeparator : Control, IZarpaThemeAware
{
    private Color _line = Color.FromArgb(67, 70, 78);

    public ZarpaSettingsSectionSeparator()
    {
        Dock = DockStyle.Bottom;
        Height = 1;
        Margin = Padding.Empty;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _line = value.Border;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(_line);
        e.Graphics.DrawLine(pen, 0, 0, Math.Max(0, Width - 1), 0);
    }
}
