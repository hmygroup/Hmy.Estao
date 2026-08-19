using System.ComponentModel;
using System.Drawing.Drawing2D;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal sealed class HarnessMarketplaceHero : Control, IZarpaThemeAware
{
    private ZarpaThemeTokens? _theme;
    private int _artifactCount;
    private int _repositoryCount;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ArtifactCount
    {
        get => _artifactCount;
        set { _artifactCount = value; Invalidate(); }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int RepositoryCount
    {
        get => _repositoryCount;
        set { _repositoryCount = value; Invalidate(); }
    }

    public HarnessMarketplaceHero()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _theme = value;
        BackColor = value.Canvas;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e) =>
        e.Graphics.Clear(_theme?.Canvas ?? SystemColors.Control);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var theme = _theme;
        var canvas = theme?.Canvas ?? SystemColors.Control;
        var surface = theme?.Surface ?? SystemColors.Window;
        var accent = theme?.Accent ?? SystemColors.Highlight;
        var text = theme?.Text ?? SystemColors.WindowText;
        var muted = theme?.TextMuted ?? SystemColors.GrayText;
        var border = theme?.Border ?? SystemColors.ControlDark;
        var bounds = new Rectangle(0, 3, Width - 1, Height - 10);
        using var path = RoundedRectangle(bounds, Math.Max(12, theme?.GroupCornerRadius ?? 12));
        using (var fill = new SolidBrush(surface)) e.Graphics.FillPath(fill, path);
        using (var outline = new Pen(border)) e.Graphics.DrawPath(outline, path);
        using (var accentBrush = new SolidBrush(accent))
            e.Graphics.FillRectangle(accentBrush, bounds.Left, bounds.Top + 17, 4, bounds.Height - 34);

        using var eyebrowFont = new Font(theme?.FontFamily ?? "Segoe UI", 8F, FontStyle.Bold);
        using var titleFont = new Font(theme?.FontFamily ?? "Segoe UI", 18F, FontStyle.Bold);
        using var bodyFont = new Font(theme?.FontFamily ?? "Segoe UI", 9.5F);
        TextRenderer.DrawText(e.Graphics, "HARNESS HUB  /  TEAM MARKETPLACE", eyebrowFont,
            new Rectangle(22, 15, Width - 320, 18), accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, "Discover capabilities built by your team", titleFont,
            new Rectangle(20, 34, Width - 340, 32), text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, "Install versioned skills, MCPs and shared harness configuration with confidence.",
            bodyFont, new Rectangle(22, 67, Width - 360, 22), muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        DrawMetric(e.Graphics, new Rectangle(Width - 286, 25, 122, 54), _artifactCount.ToString(), "CAPABILITIES",
            surface, accent, text, muted, border, bodyFont, eyebrowFont);
        DrawMetric(e.Graphics, new Rectangle(Width - 154, 25, 132, 54), _repositoryCount.ToString(), "REPOSITORIES",
            surface, accent, text, muted, border, bodyFont, eyebrowFont);
        base.OnPaint(e);
    }

    private static void DrawMetric(Graphics graphics, Rectangle bounds, string value, string label,
        Color surface, Color accent, Color text, Color muted, Color border, Font valueFont, Font labelFont)
    {
        using var path = RoundedRectangle(bounds, 10);
        using var fill = new SolidBrush(surface);
        using var outline = new Pen(border);
        graphics.FillPath(fill, path);
        graphics.DrawPath(outline, path);
        using var dot = new SolidBrush(accent);
        graphics.FillEllipse(dot, bounds.Left + 12, bounds.Top + 12, 7, 7);
        TextRenderer.DrawText(graphics, value, valueFont,
            new Rectangle(bounds.Left + 26, bounds.Top + 5, bounds.Width - 34, 23), text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(graphics, label, labelFont,
            new Rectangle(bounds.Left + 12, bounds.Top + 29, bounds.Width - 20, 18), muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        if (radius <= 0)
        {
            var square = new GraphicsPath();
            square.AddRectangle(bounds);
            return square;
        }
        var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2);
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class HarnessMarketplaceSurface : Panel, IZarpaThemeAware
{
    public void ApplyTheme(ZarpaThemeTokens value)
    {
        BackColor = value.Surface;
        ForeColor = value.Text;
        foreach (Control child in Controls) child.ForeColor = value.Text;
        Invalidate();
    }
}
