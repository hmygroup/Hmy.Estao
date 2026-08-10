using System.Drawing.Drawing2D;
using Hmy.Estao.Core.Models;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal sealed record ZarpaUsageChartPoint(DateTimeOffset Timestamp, double Value);

internal sealed record ZarpaUsageChartSeries(string Label, Color Color, IReadOnlyList<ZarpaUsageChartPoint> Points);

/// <summary>
/// Compact, double-buffered usage graph. It only receives locally persisted
/// samples; no network call is made by this control.
/// </summary>
internal sealed class ZarpaUsageChart : Control, IZarpaThemeAware
{
    private readonly Font _titleFont = new("Segoe UI", 9.5F, FontStyle.Bold);
    private readonly Font _bodyFont = new("Segoe UI", 8F);
    private readonly Font _axisFont = new("Segoe UI", 7F);
    private ZarpaThemeTokens? _theme;
    private IReadOnlyList<ZarpaUsageChartSeries> _series = [];
    private bool _preview;
    private Bitmap? _renderCache;
    private bool _cacheDirty = true;

    public ZarpaUsageChart()
    {
        Height = 154;
        MinimumSize = new Size(220, 120);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = ZarpaPopoverPalette.SurfaceTop;
        AccessibleName = "Local usage history";
        AccessibleRole = AccessibleRole.Graphic;
    }

    public void SetData(IReadOnlyList<ZarpaUsageChartSeries> series, bool preview = false)
    {
        _series = series;
        _preview = preview;
        InvalidateCache();
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _theme = value;
        BackColor = value.Surface;
        InvalidateCache();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _axisFont.Dispose();
            _renderCache?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 0 || Height <= 0) return;
        if (_cacheDirty || _renderCache is null || _renderCache.Size != ClientSize)
        {
            _renderCache?.Dispose();
            _renderCache = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var cacheGraphics = Graphics.FromImage(_renderCache);
            Render(cacheGraphics);
            _cacheDirty = false;
        }

        e.Graphics.DrawImageUnscaled(_renderCache, Point.Empty);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        InvalidateCache();
    }

    private void InvalidateCache()
    {
        _cacheDirty = true;
        Invalidate();
    }

    private void Render(Graphics graphics)
    {
        graphics.Clear(_theme?.Surface ?? ZarpaPopoverPalette.SurfaceTop);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var surface = _theme?.SurfaceRaised ?? ZarpaPopoverPalette.SurfaceTop;
        var border = _theme?.Border ?? ZarpaPopoverPalette.Border;
        var text = _theme?.Text ?? ZarpaPopoverPalette.Text;
        var muted = _theme?.TextMuted ?? ZarpaPopoverPalette.TextMuted;
        var plot = new Rectangle(12, 42, Math.Max(20, Width - 30), Math.Max(35, Height - 64));
        var card = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));

        ZarpaPopoverPaint.FillRounded(graphics, surface, card, 12);
        using (var outline = new Pen(border, 1F))
        using (var path = ZarpaPopoverPaint.RoundedPath(card, 12))
            graphics.DrawPath(outline, path);

        TextRenderer.DrawText(graphics, "Usage history", _titleFont,
            new Rectangle(12, 7, 120, 22), text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(graphics, _preview ? "Preview · local history" : "Stored locally · last 7 days", _bodyFont,
            new Rectangle(12, 25, 150, 16), muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        DrawLegend(graphics, text, muted);
        DrawGrid(graphics, plot, border, muted);

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(7);
        foreach (var series in _series)
        {
            var points = series.Points
                .Where(point => point.Timestamp >= cutoff)
                .OrderBy(point => point.Timestamp)
                .ToArray();
            DrawSeries(graphics, plot, series.Color, points);
        }

        if (_series.Count == 0 || _series.All(series => series.Points.Count == 0))
        {
            TextRenderer.DrawText(graphics, "Collecting local usage data…", _bodyFont,
                plot, muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        TextRenderer.DrawText(graphics, "7d ago", _bodyFont, new Rectangle(plot.Left, plot.Bottom + 5, 48, 15), muted,
            TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(graphics, "now", _bodyFont, new Rectangle(plot.Right - 30, plot.Bottom + 5, 30, 15), muted,
            TextFormatFlags.Right | TextFormatFlags.NoPadding);
    }

    private void DrawLegend(Graphics graphics, Color text, Color muted)
    {
        var x = Math.Max(155, Width - 164);
        foreach (var series in _series.Take(3))
        {
            using var brush = new SolidBrush(series.Color);
            graphics.FillEllipse(brush, new Rectangle(x, 14, 6, 6));
            TextRenderer.DrawText(graphics, series.Label, _bodyFont,
                new Rectangle(x + 10, 8, 48, 18), text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            x += 55;
            if (x >= Width - 25) break;
        }
    }

    private void DrawGrid(Graphics graphics, Rectangle plot, Color border, Color muted)
    {
        using var gridPen = new Pen(Color.FromArgb(75, border), 1F);
        gridPen.DashStyle = DashStyle.Dot;
        foreach (var level in new[] { 0D, .5D, 1D })
        {
            var y = plot.Bottom - (int)Math.Round(plot.Height * level);
            graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            TextRenderer.DrawText(graphics, $"{level:P0}", _axisFont,
                new Rectangle(plot.Right - 28, y - 10, 28, 16), muted,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }
    }

    private static void DrawSeries(Graphics graphics, Rectangle plot, Color color,
        IReadOnlyList<ZarpaUsageChartPoint> points)
    {
        if (points.Count == 0) return;

        var rangeStart = DateTimeOffset.UtcNow - TimeSpan.FromDays(7);
        var range = TimeSpan.FromDays(7).TotalSeconds;
        var mapped = points.Select(point =>
        {
            var x = plot.Left + (float)Math.Clamp((point.Timestamp - rangeStart).TotalSeconds / range, 0D, 1D) * plot.Width;
            var y = plot.Bottom - (float)Math.Clamp(point.Value, 0D, 1D) * plot.Height;
            return new PointF(x, y);
        }).ToArray();

        if (mapped.Length == 1)
        {
            using var dotBrush = new SolidBrush(color);
            graphics.FillEllipse(dotBrush, new RectangleF(mapped[0].X - 3, mapped[0].Y - 3, 6, 6));
            return;
        }

        using var linePath = SmoothPath(mapped);
        using var fillPath = (GraphicsPath)linePath.Clone();
        fillPath.AddLine(mapped[^1].X, plot.Bottom, mapped[0].X, plot.Bottom);
        fillPath.CloseFigure();
        using (var fill = new LinearGradientBrush(plot, Color.FromArgb(92, color), Color.FromArgb(8, color), 90F))
            graphics.FillPath(fill, fillPath);
        using (var pen = new Pen(color, 1.8F))
            graphics.DrawPath(pen, linePath);

        using var brush = new SolidBrush(color);
        foreach (var point in mapped)
            graphics.FillEllipse(brush, new RectangleF(point.X - 2.5F, point.Y - 2.5F, 5F, 5F));
    }

    private static GraphicsPath SmoothPath(IReadOnlyList<PointF> points)
    {
        var path = new GraphicsPath();
        path.StartFigure();
        path.AddLine(points[0], points[0]);
        for (var index = 0; index < points.Count - 1; index++)
        {
            var p0 = index == 0 ? points[index] : points[index - 1];
            var p1 = points[index];
            var p2 = points[index + 1];
            var p3 = index + 2 < points.Count ? points[index + 2] : p2;
            var c1 = new PointF(p1.X + (p2.X - p0.X) / 6F, p1.Y + (p2.Y - p0.Y) / 6F);
            var c2 = new PointF(p2.X - (p3.X - p1.X) / 6F, p2.Y - (p3.Y - p1.Y) / 6F);
            path.AddBezier(p1, c1, c2, p2);
        }

        return path;
    }
}
